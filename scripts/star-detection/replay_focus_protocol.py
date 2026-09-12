"""Compare recorded focus replay with the real C# -> SEP protocol, read-only FITS."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from astropy.io import fits
import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    baseline = json.loads(args.baseline.read_text(encoding='utf-8'))
    args.output.mkdir(parents=True, exist_ok=False)
    reference = [{k: s[k] for k in ['x', 'y']} for s in baseline['reference']]
    samples = []
    largest_difference = 0.0
    for row in baseline['samples']:
        path = Path(row['path'])
        before = hashlib.sha256(path.read_bytes()).hexdigest()
        data = np.asarray(fits.getdata(path), dtype='<u2')
        header = dict(width=data.shape[1], height=data.shape[0], reference=reference)
        call = subprocess.run([str(args.dotnet), str(args.client), 'focus', sys.executable,
                               str(Path(__file__).with_name('worker.py').resolve())],
                              input=(json.dumps(header)+'\n').encode()+data.tobytes(),
                              capture_output=True, check=True, timeout=65)
        measured = json.loads(call.stdout)
        actual = {s['Id']: s for s in measured['Stars']}
        expected = {s['id']: s for s in row['measurement']['stars']}
        if set(actual) != set(expected):
            raise AssertionError(f'{path.name}: changed matched stars {set(actual)} != {set(expected)}')
        for i in actual:
            for field in ['x', 'y', 'r50', 'r80', 'flux', 'snr']:
                difference = abs(actual[i][field[0].upper()+field[1:]]-expected[i][field])
                largest_difference = max(largest_difference, difference)
                if difference > 1e-6:
                    raise AssertionError(f'{path.name}: star {i} {field} changed by {difference}')
        if hashlib.sha256(path.read_bytes()).hexdigest() != before:
            raise AssertionError('Raw FITS changed')
        samples.append(dict(path=str(path), sha256=before, measurement=measured))
        print(f'{path.name}: {len(actual)} identical star measurements', flush=True)
    report = dict(frames=len(samples), largest_difference=largest_difference,
                  raw_files_unchanged=True, baseline=str(args.baseline.resolve()), samples=samples,
                  scope='Offline C# -> Python image protocol only; no hardware or frontend sky acceptance.')
    with (args.output/'protocol-replay.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2, allow_nan=False)
    print(f'{len(samples)} frames passed; maximum numeric difference {largest_difference}', flush=True)


if __name__ == '__main__':
    main()
