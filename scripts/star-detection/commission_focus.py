"""Explicit, bounded main-focuser commissioning via existing NINA/PHD2 owners.

Not an automatic-observation entry point. No mount, roof, UVEX or camera SDK
commands. Uses NINA's native MoveFocuser (including configured backlash).
Every sweep returns to the original focus for analysis; --leave-position is an
explicit separately evaluated validation, never a fitted-point auto-command.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import time
from urllib.request import urlopen
import numpy as np
from astropy.io import fits
from focus_metrics import measure

ROOT = Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:1888/v2/api'


def api(path):
    with urlopen(BASE + path, timeout=5) as r:
        value = json.load(r)
    if not value.get('Success'):
        raise RuntimeError(value.get('Error', 'NINA API failure'))
    return value['Response']


def phd(method):
    with socket.create_connection(('127.0.0.1', 4400), timeout=5) as s:
        s.settimeout(5)
        stream = s.makefile('rwb')
        stream.write((json.dumps(dict(method=method, id=913)) + '\n').encode())
        stream.flush()
        for _ in range(100):
            result = json.loads(stream.readline())
            if result.get('id') == 913:
                if 'error' in result:
                    raise RuntimeError(result['error'])
                return result['result']
        raise RuntimeError('PHD2 RPC response missing')


def observation_idle():
    command = "$r=& scripts/invoke-observation-automation-bridge.ps1 -Snapshot -ResponseTimeoutMilliseconds 5000; $r.snapshot.runState"
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], cwd=ROOT,
                       capture_output=True, text=True, timeout=15)
    if r.returncode or r.stdout.strip() not in ['Completed', 'Idle', 'Cancelled']:
        raise RuntimeError('Observation not idle: ' + r.stdout[-500:] + r.stderr[-700:])


def physical(pier=None):
    f, m, c, d, camera, safe = [api('/equipment/'+k+'/info') for k in
                               ['focuser', 'mount', 'flatdevice', 'dome', 'camera', 'safetymonitor']]
    if not f['Connected'] or f['DeviceId'] != 'ASCOM.StarFocuserPro.Focuser' or f['IsMoving'] or f['IsSettling'] or f['TempComp']:
        raise RuntimeError('Exact main focuser must be stationary, connected, temp compensation off')
    if not m['Connected'] or m['Slewing'] or m['IsPulseGuiding'] or not m['TrackingEnabled'] or m['AtPark'] or m['Altitude'] < 42:
        raise RuntimeError('Mount must be stationary, tracking, unparked and above 42 degrees')
    if m['SideOfPier'] not in ['pierEast', 'pierWest'] or (pier and m['SideOfPier'] != pier):
        raise RuntimeError('Mount side changed')
    if not c['Connected'] or c['CoverState'] != 'Open' or c['LightOn'] or not d['Connected'] or d['ShutterStatus'] != 'ShutterOpen':
        raise RuntimeError('Cover/roof must be open and panel unlit')
    if camera['IsExposing'] or (safe['Connected'] and not safe['IsSafe']):
        raise RuntimeError('Science exposure or explicit unsafe indication')
    if phd('get_app_state') != 'Stopped':
        raise RuntimeError('PHD2 must be stopped; do not interrupt another owner operation')
    return dict(focuser=f, mount=m, cover=c, roof=d, safety=safe)


def validate_positions(positions, origin, leave=None):
    if not positions or len(positions) > 12 or not 4500 <= origin <= 5500:
        raise ValueError('Invalid bounded sweep')
    for p in positions + [origin] + ([] if leave is None else [leave]):
        if not 4600 <= p <= 5500 or abs(p-origin) > 400:
            raise ValueError('Position outside commissioning envelope including native 100-step inward overshoot')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--confirm-hardware', action='store_true', required=True)
    p.add_argument('--positions', type=int, nargs='+', required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--exposure-ms', type=int, default=3000)
    p.add_argument('--frames', type=int, default=3)
    p.add_argument('--leave-position', type=int)
    p.add_argument('--phd-profile', type=int, required=True)
    p.add_argument('--phd-camera', required=True)
    args = p.parse_args()
    if not 500 <= args.exposure_ms <= 10000 or not 1 <= args.frames <= 5:
        raise ValueError('Invalid capture bounds')
    observation_idle()
    initial = physical()
    origin = initial['focuser']['Position']
    pier = initial['mount']['SideOfPier']
    validate_positions(args.positions, origin, args.leave_position)
    args.output.mkdir(parents=True, exist_ok=False)
    deadline = time.monotonic()+900
    manifest = dict(route='component-commissioning-not-native-autofocus', initial=initial,
                    origin=origin, positions=args.positions, exposure_ms=args.exposure_ms,
                    frames=args.frames, native_backlash='NINA existing Overshoot In=100 Out=0',
                    samples=[], moves=[], warnings=[], complete=False)
    reference = None
    expected = origin

    def save(name, value):
        with (args.output/name).open('x', encoding='utf-8') as f:
            json.dump(value, f, ensure_ascii=False, indent=2, allow_nan=False)

    def event(kind, **values):
        data = dict(utc=datetime.now(timezone.utc).isoformat(), kind=kind, **values)
        with (args.output/'events.jsonl').open('a', encoding='utf-8') as f:
            f.write(json.dumps(data, ensure_ascii=False, allow_nan=False)+'\n')

    def move(target, restoring=False):
        nonlocal expected
        observation_idle()
        before = physical(pier)
        current = before['focuser']['Position']
        if current != expected:
            raise RuntimeError('Unexpected focus change; leave operator state untouched')
        while current != target:
            if not restoring and ((args.output/'STOP').exists() or time.monotonic() > deadline):
                raise RuntimeError('Commissioning cancelled or time limit reached')
            next_pos = current + int(np.clip(target-current, -150, 150))
            intent = dict(before=current, target=next_pos, restoring=restoring)
            event('move-intent', **intent)
            api('/equipment/focuser/move?position='+str(next_pos))
            expected = next_pos
            expires = time.monotonic()+30
            while True:
                f = api('/equipment/focuser/info')
                if not f['Connected'] or f.get('DeviceId') != 'ASCOM.StarFocuserPro.Focuser':
                    raise RuntimeError('Focus owner lost during movement')
                if not f['IsMoving'] and not f['IsSettling'] and f['Position'] == next_pos:
                    break
                if time.monotonic() > expires:
                    api('/equipment/focuser/stop-move')
                    raise RuntimeError('Focus move/readback timeout')
                time.sleep(.3)
            time.sleep(2)
            after = physical(pier)
            if after['focuser']['Position'] != next_pos:
                raise RuntimeError('Focus changed during settling')
            manifest['moves'].append(dict(**intent, after=after['focuser']))
            event('move-complete', **intent)
            current = next_pos

    save('plan.json', manifest)
    try:
        for index, position in enumerate(args.positions):
            move(position)
            for frame in range(args.frames):
                if (args.output/'STOP').exists() or time.monotonic() > deadline:
                    raise RuntimeError('Commissioning cancelled/time limit')
                state = physical(pier)
                observation_idle()
                if state['focuser']['Position'] != position:
                    raise RuntimeError('Focus changed before exposure; leave operator state untouched')
                path = (args.output/f'{index:02d}-focus-{position}-f{frame}.fit').resolve()
                command = [str(ROOT/'.dotnet/dotnet.exe'), str(ROOT/'src/UvexAdv.StarDetection.FocusHost/bin/Release/net8.0-windows/UvexAdv.StarDetection.FocusHost.dll'),
                           'capture', '--confirm-hardware', str(path), str(args.exposure_ms), '100',
                           str(args.phd_profile), args.phd_camera]
                event('capture-intent', position=position, path=str(path))
                call = subprocess.run(command, capture_output=True, text=True, timeout=55)
                if call.returncode:
                    raise RuntimeError('Native PHD2 capture failed: '+call.stderr[-1500:])
                capture = json.loads(call.stdout)
                image, header = fits.getdata(path, header=True)
                if abs(float(header['EXPOSURE'])-args.exposure_ms/1000) > .001 or header['GAIN'] != 100 or image.shape != (1080, 1920):
                    raise RuntimeError('Frame acquisition settings mismatch')
                if physical(pier)['focuser']['Position'] != position:
                    raise RuntimeError('Focus moved during exposure')
                try:
                    measured = measure(image, reference)
                    if reference is None:
                        reference = measured['reference']
                except ValueError as e:
                    measured = dict(stars=[], error=str(e), reference=reference)
                sample = dict(position=position, index=index, frame=frame, path=str(path),
                              sha256=hashlib.sha256(path.read_bytes()).hexdigest(), capture=capture,
                              state=state, measurements=measured)
                save(f'{index:02d}-focus-{position}-f{frame}.json', sample)
                manifest['samples'].append(sample)
                stars=measured['stars']
                print(json.dumps(dict(position=position, frame=frame, stars=len(stars),
                                      r50=float(np.median([s['r50'] for s in stars])) if stars else None,
                                      shift=measured.get('shift'), error=measured.get('error'))), flush=True)
                event('capture-complete', position=position, path=str(path), measured_stars=len(stars))
        manifest['complete'] = True
    except BaseException as e:
        manifest['error'] = str(e)
        raise
    finally:
        try:
            move(args.leave_position if manifest['complete'] and args.leave_position is not None else origin, restoring=True)
            manifest['final'] = physical(pier)
        except Exception as e:
            manifest['restore_error'] = str(e)
            print('RESTORE NOT CONFIRMED: '+str(e), flush=True)
        save('manifest.json', manifest)


if __name__ == '__main__':
    main()
