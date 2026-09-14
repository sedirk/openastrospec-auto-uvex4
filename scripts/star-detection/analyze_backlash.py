"""Read-only analysis of owner-mediated backlash commissioning records.

No device API, camera, profile write or motion. FITS are immutable and checked
against the acquisition record. A play/dead-band model estimates effective
OPTICAL hysteresis, not a calibrated mechanical encoder backlash. Baseline
frames are never fit as if they belonged to a directional sweep.
"""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import subprocess

import numpy as np
from scipy.optimize import least_squares


def optical_positions(positions, backlash, initial_fraction=1.0, lower=4700):
    """Positive-direction equivalent coordinate, with lost motion on reversal.

    q is confined to [x, x+B]. The preload's unknown optical starting point
    lies in [lower, lower+B]. Missing images do NOT remove physical moves.
    """
    if backlash < 0 or not 0 <= initial_fraction <= 1:
        raise ValueError('Invalid dead-band parameters')
    q = lower + backlash * initial_fraction
    values = []
    for position in positions:
        q = max(float(position), min(q, float(position) + backlash))
        values.append(q)
    return np.array(values)


def summarize(records, minimum_common=3):
    """Use a fixed intersection of stars, never a changing star population."""
    rows = [r for r in records if r['group'] in ('up1', 'down1', 'up2')]
    if not rows:
        raise ValueError('No directional sweep frames')
    ids = sorted(set.intersection(*[set(s['id'] for s in r['measurement']['stars']) for r in rows]))
    if minimum_common < 2:
        raise ValueError('At least two complete diagnostic tracks are required')
    if len(ids) < minimum_common:
        raise ValueError(f'Only {len(ids)} common stars across all frames; no ensemble backlash claim')
    groups = defaultdict(list)
    for row in rows:
        groups[row['index']].append(row)
    points = []
    for index, frames in sorted(groups.items()):
        if len(frames) != 2 or len({f['frame'] for f in frames}) != 2:
            raise ValueError(f'Point {index} does not have two independent frames')
        point = dict(index=index, group=frames[0]['group'], position=frames[0]['position'], stars={})
        for sid in ids:
            stars = [next(s for s in f['measurement']['stars'] if s['id'] == sid) for f in frames]
            point['stars'][str(sid)] = {k: float(np.median([s[k] for s in stars]))
                                        for k in ('r50', 'r80', 'flux', 'snr')}
        for key in ('r50', 'r80', 'flux'):
            values = [float(np.median([s[key] for s in f['measurement']['stars'] if s['id'] in ids]))
                      for f in frames]
            point[key] = float(np.median(values))
            if key != 'flux':
                # Two frames do not justify a tiny formal uncertainty. This
                # is a weighting floor, not an instrument uncertainty claim.
                point[key+'_error'] = max(.15, float(1.4826*np.median(np.abs(values-np.median(values)))))
            point[key+'_frames'] = values
        points.append(point)
    return ids, points


def add_sparse_tracks(records, points):
    """Keep additional same-star tracks with missing points, not substitutions.

    Require both frames at a point, >=70% of all points and >=5 points in each
    direction. The physical trajectory remains complete for the play model.
    """
    usable = [r for r in records if r['group'] in ('up1', 'down1', 'up2')]
    all_ids = sorted({s['id'] for r in usable for s in r['measurement']['stars']})
    groups = defaultdict(list)
    for row in usable:
        groups[row['index']].append(row)
    qualified = []
    for sid in all_ids:
        values = {}
        coverage = defaultdict(int)
        for point in points:
            frames = groups[point['index']]
            stars = [next((s for s in f['measurement']['stars'] if s['id'] == sid), None) for f in frames]
            if len(stars) != 2 or any(s is None for s in stars):
                continue
            value = {key: float(np.median([s[key] for s in stars])) for key in ('r50', 'r80', 'flux', 'snr')}
            for key in ('r50', 'r80'):
                measurements = [s[key] for s in stars]
                value[key+'_error'] = max(.15, float(1.4826*np.median(np.abs(measurements-np.median(measurements)))))
            values[point['index']] = value
            coverage[point['group']] += 1
        if len(values) < .7*len(points) or any(coverage[group] < 5 for group in ('up1', 'down1', 'up2')):
            continue
        qualified.append(sid)
        for point in points:
            if point['index'] in values:
                point['stars'][str(sid)] = values[point['index']]
    return qualified


def fit_play(points, key='r50', star=None):
    x = np.array([p['position'] for p in points], dtype=float)
    y = np.array([p[key] if star is None else p['stars'].get(str(star), {}).get(key, np.nan) for p in points])
    err = np.array([p[key+'_error'] if star is None else
                    p['stars'].get(str(star), {}).get(key+'_error', .15) for p in points])
    mask = np.isfinite(y)
    if sum(mask) < 10 or {p['group'] for p, valid in zip(points, mask) if valid} != {'up1', 'down1', 'up2'}:
        raise ValueError('A forward, reverse and repeat sweep are required')

    def predict(parameters):
        backlash, initial, minimum, center, slope = parameters
        q = optical_positions(x, backlash, initial)
        return np.sqrt(minimum**2 + (slope*(q-center))**2)

    def residual(parameters):
        return ((predict(parameters)-y)/err)[mask]

    solutions = []
    for backlash in (25, 100, 200, 300, 450, 575):
        for initial in (.4, .99):
            for center in (4950, 5050):
                solution = least_squares(residual, [backlash, initial, max(.5, min(y[mask])), center, .025],
                                         bounds=([0, 0, .05, 4700, .0001], [600, 1, 25, 5300, .2]),
                                         loss='soft_l1', f_scale=1, max_nfev=1500)
                if solution.success:
                    solutions.append(solution)
    if not solutions:
        raise ValueError('Dead-band fit did not converge')
    best = min(solutions, key=lambda s: s.cost)
    b, initial, minimum, center, slope = best.x
    predicted = predict(best.x)
    differences = y-predicted
    return dict(metric=key, star=star, backlash_steps=float(b), initial_fraction=float(initial),
                minimum=float(minimum), increasing_direction_focus=float(center), slope=float(slope),
                robust_cost=float(best.cost), points_used=int(sum(mask)),
                residual_rms_px=float(np.sqrt(np.mean(differences[mask]**2))),
                residual_max_abs_px=float(np.max(np.abs(differences[mask]))),
                boundary_limited=bool(b < 1 or b > 599 or center < 4701 or center > 5299),
                equivalent_positions=optical_positions(x, b, initial).tolist(),
                predictions=predicted.tolist(), residuals=[float(v) if np.isfinite(v) else None for v in differences])


def save_new(path, value):
    text = json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False)
    with path.open('x', encoding='utf-8') as handle:
        handle.write(text)


def native_fits(points, fits, output, dotnet, host, nina):
    """Independent NINA fits of each settled branch, not reversal plateaus.

    Engagement is inferred from the joint model, so these are a cross-check,
    not statistically independent proof or authority to adopt a focus.
    """
    result = {}
    model = fits['r50']
    b = model['backlash_steps']
    for name in ('up1', 'down1', 'up2'):
        rows = []
        for p, q in zip(points, model['equivalent_positions']):
            offset = b if name == 'down1' else 0
            if p['group'] == name and abs(q-p['position']-offset) < .01:
                rows.append([p['position'], p['r50'], p['r50_error']])
        result[name] = dict(points=rows)
        if len(rows) < 5:
            result[name]['error'] = 'Fewer than five engaged-branch points; no native fit'
            continue
        path = output/(name+'-native-points.json')
        save_new(path, rows)
        call = subprocess.run([str(dotnet), str(host), 'fit', str(path), str(nina)],
                              text=True, capture_output=True, timeout=30)
        result[name]['fit'] = json.loads(call.stdout) if call.returncode == 0 else dict(error=call.stderr[-1500:])
    return result


def plot(report, output):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig, axes = plt.subplots(2, 2, figsize=(13, 8), layout='constrained')
    points = report['points']
    colors = dict(up1='#0072B2', down1='#D55E00', up2='#009E73')
    names = dict(up1='Increasing #1', down1='Decreasing', up2='Increasing #2 (partial)' if report['partial_diagnostic'] else 'Increasing #2')
    for ax, key in zip(axes[0], ('r50', 'r80')):
        for name in colors:
            ps = [p for p in points if p['group'] == name]
            ax.errorbar([p['position'] for p in ps], [p[key] for p in ps],
                        yerr=[p[key+'_error'] for p in ps], fmt='o-', ms=4,
                        capsize=2, color=colors[name], label=names[name])
        ax.set(xlabel='Motor readback (steps)', ylabel=key.upper()+' (px)', title='Same-star directional measurements')
        ax.legend(fontsize=8)
        ax.grid(alpha=.2)
    for name in colors:
        indices = [i for i, p in enumerate(points) if p['group'] == name]
        ps = [points[i] for i in indices]
        qs = [report['models']['r50']['equivalent_positions'][i] for i in indices]
        ys = [report['models']['r50']['predictions'][i] for i in indices]
        axes[1, 0].plot(qs, [p['r50'] for p in ps], 'o', color=colors[name], ms=4, label=names[name])
        axes[1, 0].plot(qs, ys, '--', color=colors[name], alpha=.6)
        axes[1, 1].plot([p['index'] for p in ps], [p['flux'] for p in ps], 'o-', color=colors[name], ms=4)
    axes[1, 0].set(xlabel='Model increasing-direction equivalent (steps)', ylabel='R50 (px)',
                   title='Constant dead-band model: diagnostic, not mechanical calibration')
    axes[1, 1].set(xlabel='Measurement order', ylabel='Fixed-star median aperture flux',
                   title='Flux stability / clouds and optical changes remain confounders')
    for ax in axes[1]:
        ax.grid(alpha=.2)
    status = 'PARTIAL: repeated minimum not verified' if report['partial_diagnostic'] else 'Acquisition complete'
    fig.suptitle('Star Focuser | SEP fixed-star optical backlash measurement\n'
                 f"{status} | Common star IDs: {report['ids']} | No focus/settings adopted", fontsize=13)
    fig.savefig(output/'backlash-curves.png', dpi=160)
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--fit-host', type=Path)
    parser.add_argument('--nina-install', type=Path)
    parser.add_argument('--partial-diagnostic', action='store_true',
                        help='Analyze a stopped run as partial evidence; never mark it complete')
    args = parser.parse_args()
    result = json.loads((args.input/'result.json').read_text(encoding='utf-8'))
    if not result['complete'] and not args.partial_diagnostic:
        raise ValueError('Acquisition incomplete; no full measurement claim')
    records = result['samples']
    for r in records:
        if hashlib.sha256(Path(r['path']).read_bytes()).hexdigest() != r['sha256']:
            raise ValueError('FITS does not match recorded immutable observation')
    excluded_terminal = []
    if args.partial_diagnostic:
        # A final interrupted point has no later moves in the fitted trajectory.
        # Do not omit interior moves or turn a single frame into a pair.
        last_index = max(r['index'] for r in records)
        last_rows = [r for r in records if r['index'] == last_index]
        if len(last_rows) != 2:
            excluded_terminal = [r['path'] for r in last_rows]
            records = [r for r in records if r['index'] != last_index]
    # Two complete tracks can provide a diagnostic estimate, corroborated by
    # sparse individual tracks. This NEVER relaxes production autofocus's
    # three-common-star acceptance or the acquisition's three-star registration.
    ids, points = summarize(records, minimum_common=2)
    track_ids = add_sparse_tracks(records, points)
    args.output.mkdir(parents=True, exist_ok=False)
    models = {key: fit_play(points, key) for key in ('r50', 'r80')}
    individual = [fit_play(points, 'r50', sid) for sid in track_ids]
    estimates = [m['backlash_steps'] for m in list(models.values())+individual]
    report = dict(input=str(args.input.resolve()), ids=ids, individual_track_ids=track_ids, points=points, models=models,
                  production_three_complete_star_gate_passed=len(ids) >= 3,
                  partial_diagnostic=not result['complete'], excluded_unpaired_terminal_frames=excluded_terminal,
                  repeat_minimum_revisited=any(p['group'] == 'up2' and p['position'] >= models['r50']['increasing_direction_focus']
                                              for p in points),
                  individual_stars=individual, sensitivity_range_steps=[min(estimates), max(estimates)],
                  acquisition_complete=result['complete'], return_confirmed=result['return_confirmed'],
                  compensation_restored=result['compensation_restored'],
                  limitation='Effective optical hysteresis under one field/temperature/gravity load. '
                  'Sensitivity range is NOT a confidence interval. Seeing, tracking blur, mirror settling '
                  'and model mismatch can imitate or change backlash. No new setting or focus is adopted.')
    if args.fit_host:
        if args.dotnet is None or args.nina_install is None:
            raise ValueError('--dotnet and --nina-install required for native fit')
        report['native_fits'] = native_fits(points, models, args.output, args.dotnet, args.fit_host, args.nina_install)
    save_new(args.output/'analysis.json', report)
    plot(report, args.output)
    print(json.dumps({k: report[k] for k in ('ids', 'sensitivity_range_steps', 'acquisition_complete',
                                          'return_confirmed', 'compensation_restored')}, ensure_ascii=False))
    print(json.dumps({k: {n: m[n] for n in ('backlash_steps', 'increasing_direction_focus', 'residual_rms_px',
                                         'boundary_limited')} for k, m in models.items()}))


if __name__ == '__main__':
    main()
