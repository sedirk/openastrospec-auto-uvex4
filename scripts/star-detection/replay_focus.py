"""Replay immutable commissioning frames with a fixed reference; no device I/O."""
import argparse
import json
from pathlib import Path
import subprocess
import numpy as np
from astropy.io import fits
from focus_metrics import measure


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ids', type=int, nargs='+', required=True)
    parser.add_argument('--fit-host', type=Path)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--nina-install', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    reference = measure(fits.getdata(args.reference))['reference']
    samples = []
    for path in sorted(args.input.glob('*-focus-*.fit')):
        index, _, position, frame = path.stem.split('-')
        try:
            result = measure(fits.getdata(path), reference)
        except ValueError as e:
            result = dict(stars=[], error=str(e))
        sample = dict(index=int(index), position=int(position), frame=frame,
                      path=str(path.resolve()), measurement=result)
        samples.append(sample)
    groups = []
    for index in sorted({s['index'] for s in samples}):
        rows = [s for s in samples if s['index'] == index]
        values = []
        for row in rows:
            stars = {s['id']: s for s in row['measurement']['stars']}
            if all(i in stars for i in args.ids):
                values.append([float(np.median([stars[i][key] for i in args.ids]))
                               for key in ['r50', 'r80', 'flux']])
        if values:
            array = np.array(values)
            groups.append(dict(index=index, position=rows[0]['position'], frames=len(values),
                               r50=float(np.median(array[:, 0])), r80=float(np.median(array[:, 1])),
                               flux=float(np.median(array[:, 2])),
                               error=max(.15, float(1.4826*np.median(np.abs(array[:, 0]-np.median(array[:, 0]))))),
                               values=values))
    report = dict(reference_path=str(args.reference.resolve()), ids=args.ids, groups=groups,
                  reference=reference, samples=samples,
                  limitation='Same isolated stars only; no target identity or slit-motion authority. Missing frames are not zeros.')
    points = [[g['position'], g['r50'], g['error']] for g in groups if g['frames'] >= 2]
    with (args.output/'points.json').open('x') as f:
        json.dump(points, f)
    if args.fit_host:
        if args.dotnet is None or args.nina_install is None:
            raise ValueError('--dotnet and --nina-install required with native fit host')
        call = subprocess.run([str(args.dotnet), str(args.fit_host), 'fit', str(args.output/'points.json'), str(args.nina_install)],
                              text=True, capture_output=True, timeout=30)
        report['native_fit'] = json.loads(call.stdout) if call.returncode == 0 else dict(error=call.stderr[-2000:])
    with (args.output/'replay.json').open('x', encoding='utf-8') as f:
        json.dump(report, f, indent=2, allow_nan=False)
    print(json.dumps(dict(groups=groups, native_fit={k:v for k,v in report.get('native_fit',{}).items() if k!='samples'})),flush=True)


if __name__ == '__main__':
    main()
