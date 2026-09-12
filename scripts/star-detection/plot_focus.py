"""Scientific same-star focus curves; read-only recorded-frame measurements."""
import argparse
import json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--report', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    if args.output.exists():
        raise FileExistsError(args.output)
    r = json.loads(args.report.read_text(encoding='utf-8'))
    fig, axes = plt.subplots(1, 2, figsize=(12, 4.4), layout='constrained')
    for ax, key in zip(axes, ['r50', 'r80']):
        for sid in r['ids']:
            points = []
            for sample in r['samples']:
                star = next((s for s in sample['measurement']['stars'] if s['id'] == sid), None)
                if star:
                    points.append((sample['position'], star[key]))
            if points:
                x, y = zip(*points)
                ax.scatter(x, y, s=18, alpha=.5, label=f'Same star {sid}')
        groups = sorted(r['groups'], key=lambda g:g['position'])
        ax.plot([g['position'] for g in groups], [g[key] for g in groups], 'ko--', ms=4, label='Fixed-ensemble median')
        if key == 'r50' and 'samples' in r.get('native_fit', {}):
            model = np.array(r['native_fit']['samples'])
            ax.plot(model[:,0], model[:,1], color='tab:red', label=f"NINA fit: {r['native_fit']['position']:.0f}, R²={r['native_fit']['rSquared']:.3f}")
        ax.set(xlabel='Star Focuser position (steps)', ylabel=f'{key.upper()} (pixels)', title=f'{key.upper()}: lower = more concentrated')
        ax.grid(alpha=.2)
        ax.legend(fontsize=8)
    fig.suptitle('SEP focus scan | fixed 25 px apertures | no circular-PSF requirement')
    fig.savefig(args.output, dpi=160)
    plt.close(fig)


if __name__ == '__main__':
    main()
