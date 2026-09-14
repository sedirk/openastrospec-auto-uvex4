"""Explicit Star Focuser / PHD2 optical-hysteresis commissioning, not autofocus.

Uses NINA's existing focuser owner and PHD2's existing capture owner. Temporarily
zeros only NINA's Overshoot amounts, restores them, and returns to the starting
reported motor position. Does not open/close equipment, slew, guide, or adopt a fit.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

import numpy as np
from astropy.io import fits

import commission_focus as owners
from focus_metrics import measure

ROOT = owners.ROOT


def json_ready(value):
    """Optional SEP diagnostics may be NaN; store absence, never zero/quality."""
    if isinstance(value, dict):
        return {key: json_ready(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_ready(item) for item in value]
    if isinstance(value, np.generic):
        value = value.item()
    if isinstance(value, float) and not np.isfinite(value):
        return None
    return value


def validate_plan(origin, positions):
    # This explicit station commissioning envelope is intentionally narrower
    # than the operator's 0..10000 UI range. Never expand it automatically.
    if origin != 5000 or not positions or len(positions) > 40:
        raise ValueError('This measurement requires the verified 5000-step origin')
    if any(not 4700 <= p <= 5300 for _, p in positions):
        raise ValueError('Requested position exceeds the 4700..5300 envelope')


def observation_snapshot():
    command = ('$r=& scripts/invoke-observation-automation-bridge.ps1 -Snapshot '
               '-ResponseTimeoutMilliseconds 5000; '
               '$r.snapshot | Select-Object processId,runState,mainFocusBusy | ConvertTo-Json -Compress')
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                        '-Command', command], cwd=ROOT, capture_output=True, text=True, timeout=15)
    if r.returncode:
        raise RuntimeError('Cannot establish coordinator idle state')
    state = json.loads(r.stdout)
    if state['runState'] not in ('Idle', 'Completed', 'Cancelled') or state['mainFocusBusy']:
        raise RuntimeError('Observation or main focus is active; do not interfere')
    return state


class ObservationLease:
    """Same FileShare.None lease held by the production host; no contents changed."""
    def __enter__(self):
        from ctypes import wintypes
        self.api = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                        ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.api.CreateFileW.restype = wintypes.HANDLE
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        path = Path(os.environ['LOCALAPPDATA'])/'UVEX-ADV/observations/control/real-observation-owner.lock'
        # Existing production lock only: no guessed directory creation.
        self.handle = self.api.CreateFileW(str(path), 0xC0000000, 0, None, 3, 0x80, None)
        if self.handle == ctypes.c_void_p(-1).value:
            raise OSError(ctypes.get_last_error(), 'Production observation lease unavailable')
        return self

    def __exit__(self, *args):
        self.api.CloseHandle(self.handle)


def focus_profile():
    p = owners.api('/profile/show?active=true')
    # Never store unrelated profile fields or network credentials.
    return dict(id=p['Id'], focuser=p['FocuserSettings'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--confirm-hardware', action='store_true', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mode', choices=['bilateral', 'reverse-check'], default='bilateral')
    args = parser.parse_args()
    up = [4800, 4900, 4950, 5000, 5050, 5100, 5200, 5300]
    down = [5275, 5250, 5225, 5200, 5150, 5100, 5050, 5000, 4950, 4900, 4800, 4700]
    up_repeat = [4725, 4750, 4775, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5200, 5300]
    points = [('up1', p) for p in up] + [('down1', p) for p in down]
    if args.mode == 'bilateral':
        points += [('up2', p) for p in up_repeat]
    validate_plan(5000, points)
    args.output.mkdir(parents=True, exist_ok=False)

    def save(name, value):
        encoded = json.dumps(json_ready(value), ensure_ascii=False, indent=2, allow_nan=False)
        with (args.output/name).open('x', encoding='utf-8') as f:
            f.write(encoded)

    def event(kind, **values):
        row = json_ready(dict(utc=datetime.now(timezone.utc).isoformat(), kind=kind, **values))
        with (args.output/'events.jsonl').open('a', encoding='utf-8') as f:
            f.write(json.dumps(row, ensure_ascii=False, allow_nan=False)+'\n')
            f.flush()
        print(json.dumps(row, ensure_ascii=False), flush=True)

    with ObservationLease():
        start_snapshot = observation_snapshot()
        initial = owners.physical()
        original = focus_profile()
        original_settings = original['focuser']
        if (original_settings['BacklashCompensationModel'] != 'OVERSHOOT'
                or original_settings['Id'] != 'ASCOM.StarFocuserPro.Focuser'
                or original_settings['BacklashIn'] != 100 or original_settings['BacklashOut'] != 0):
            raise RuntimeError('Expected existing Overshoot In=100 Out=0 was changed; no actions')
        if owners.phd('get_profile')['id'] != 2:
            raise RuntimeError('PHD2 profile changed')
        equipment = owners.phd('get_current_equipment')
        if equipment['camera']['name'] != 'G3M2210M' or not equipment['camera']['connected']:
            raise RuntimeError('PHD2 camera owner changed')
        origin = initial['focuser']['Position']
        validate_plan(origin, points)
        expected = origin
        expected_in = 100
        pier = initial['mount']['SideOfPier']
        deadline = time.monotonic()+1500
        reference = None
        samples = []
        manifest = dict(route='NINA-PHD2-SEP-optical-backlash-component-commissioning',
                        original=original, initial=initial, plan=points, origin=origin,
                        complete=False, return_confirmed=False, compensation_restored=False,
                        exposure_ms=10000, frames_per_point=2, settle_seconds=5,
                        measurement='Effective optical hysteresis, not a mechanical encoder calibration')
        save('plan.json', manifest)

        def check(restoring=False):
            if not restoring and ((args.output/'STOP').exists() or time.monotonic() > deadline):
                raise RuntimeError('Cancelled or bounded duration exhausted')
            snapshot = observation_snapshot()
            if snapshot['processId'] != start_snapshot['processId']:
                raise RuntimeError('NINA restarted; do not apply old moves')
            state = owners.physical(pier)
            if state['focuser']['Position'] != expected:
                raise RuntimeError('External focuser movement; do not overwrite operator state')
            profile = focus_profile()
            wanted = dict(original_settings, BacklashIn=expected_in)
            if profile['id'] != original['id'] or profile['focuser'] != wanted:
                raise RuntimeError('Focuser profile/settings changed externally')
            # Tracking may vary slightly; no new slew/target accepted.
            for key, tolerance in [('RightAscension', .003), ('Declination', .02)]:
                if key in state['mount'] and abs(state['mount'][key]-initial['mount'][key]) > tolerance:
                    raise RuntimeError('Mount pointing changed; stop measurement')
            return state

        def set_in(value):
            nonlocal expected_in
            if value not in (0, 100):
                raise ValueError('Only temporary zero and restoration are authorized')
            p = focus_profile()
            if p['id'] != original['id'] or p['focuser'] != dict(original_settings, BacklashIn=expected_in):
                raise RuntimeError('Do not overwrite changed focus configuration')
            event('compensation-intent', old=expected_in, new=value)
            owners.api('/profile/change-value?settingpath=FocuserSettings-BacklashIn&newValue='+str(value))
            expected_in = value  # Treat a received command as potentially applied.
            p = focus_profile()
            if p['id'] != original['id'] or p['focuser'] != dict(original_settings, BacklashIn=value):
                raise RuntimeError('Compensation readback mismatch')
            event('compensation-confirmed', value=value)

        def move(target, restoring=False):
            nonlocal expected
            if not 4700 <= target <= 5300:
                raise ValueError('Out of measurement envelope')
            check(restoring)
            while expected != target:
                check(restoring)
                step = expected + int(np.clip(target-expected, -150, 150))
                before = expected
                event('move-intent', before=before, target=step, restoring=restoring)
                expected = step
                owners.api('/equipment/focuser/move?position='+str(step))
                until = time.monotonic()+40
                while True:
                    f = owners.api('/equipment/focuser/info')
                    if not f['Connected'] or f['DeviceId'] != original_settings['Id']:
                        raise RuntimeError('Focuser owner lost')
                    if not f['IsMoving'] and not f['IsSettling'] and f['Position'] == step:
                        break
                    if time.monotonic() > until:
                        owners.api('/equipment/focuser/stop-move')
                        raise RuntimeError('Focuser timeout; stop requested, return not assumed')
                    time.sleep(.3)
                time.sleep(5)
                check(restoring)
                event('move-confirmed', position=step, restoring=restoring)

        def capture(group, position, frame, index):
            nonlocal reference
            state = check()
            path = (args.output/f'{index:02d}-{group}-{position}-f{frame}.fit').resolve()
            command = [str(ROOT/'.dotnet/dotnet.exe'),
                       str(ROOT/'src/UvexAdv.StarDetection.FocusHost/bin/Release/net8.0-windows/UvexAdv.StarDetection.FocusHost.dll'),
                       'capture', '--confirm-hardware', str(path), '10000', '100', '2', 'G3M2210M']
            event('capture-intent', group=group, position=position, frame=frame)
            r = subprocess.run(command, capture_output=True, text=True, timeout=55)
            if r.returncode:
                raise RuntimeError('PHD2 frame capture failed: '+r.stderr[-1000:])
            check()
            data, header = fits.getdata(path, header=True)
            if data.shape != (1080, 1920) or abs(float(header['EXPOSURE'])-10) > .001 or header['GAIN'] != 100:
                raise RuntimeError('Fresh frame geometry/exposure mismatch')
            result = measure(data, reference)
            if reference is None:
                reference = result['reference']
            # Preserve star IDs while following measured frame-to-frame drift.
            dx, dy = result['shift']
            reference = [dict(s, x=s['x']+dx, y=s['y']+dy) for s in reference]
            record = dict(group=group, position=position, frame=frame, index=index,
                          utc=datetime.now(timezone.utc).isoformat(), path=str(path),
                          sha256=hashlib.sha256(path.read_bytes()).hexdigest(), measurement=result,
                          focuser=state['focuser'])
            save(path.stem+'.json', record)
            samples.append(record)
            stars = result['stars']
            event('capture-complete', group=group, position=position, frame=frame,
                  count=len(stars), r50=float(np.median([s['r50'] for s in stars])) if stars else None)
            if len(stars) < 3:
                raise RuntimeError('Fewer than three usable stars; no unobserved moves')

        try:
            check()
            # Baseline also proves the image pipeline before changing compensation.
            for frame in range(2):
                capture('baseline', origin, frame, 0)
            set_in(0)
            move(4700)
            for index, (group, position) in enumerate(points, 1):
                move(position)
                for frame in range(2):
                    capture(group, position, frame, index)
            manifest['complete'] = True
        except BaseException as ex:
            manifest['error'] = str(ex)
            event('measurement-stopped', reason=str(ex))
        finally:
            try:
                # Restore original compensation first, then normal owner-mediated
                # return. Position readback does not prove restored optical focus.
                if expected_in != 100:
                    set_in(100)
                manifest['compensation_restored'] = focus_profile() == original
            except Exception as ex:
                manifest['configuration_restore_error'] = str(ex)
                event('configuration-restore-not-confirmed', reason=str(ex))
            try:
                move(origin, restoring=True)
                manifest['return_confirmed'] = True
                manifest['final'] = owners.physical(pier)
            except Exception as ex:
                manifest['return_error'] = str(ex)
                event('return-not-confirmed', reason=str(ex))
            manifest['samples'] = samples
            save('result.json', manifest)
            event('finished', complete=manifest['complete'], returned=manifest['return_confirmed'],
                  restored=manifest['compensation_restored'], frames=len(samples))


if __name__ == '__main__':
    main()
