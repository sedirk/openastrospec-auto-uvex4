"""Recorded-frame focus comparison; no device I/O or motion authority.

Match SEP parent regions with an ensemble translation, then measure the SAME
isolated stars with a fixed SEP aperture at every focus point. A small noisy
segment in an aperture does not discard a star: no segmentation masking is used
for these isolated apertures. Real neighbours, clipped pixels and edges exclude
the aperture. Do not feed these centroids into mount/slit control.
"""
import numpy as np
import sep
from scipy.spatial import cKDTree
from sep_detector import detect, Settings


def registration(reference, current, maximum_shift=250, tolerance=5):
    a = np.array([[s['x'], s['y']] for s in reference])
    b = np.array([[s['x'], s['y']] for s in current])
    if len(a) < 3 or len(b) < 3:
        raise ValueError('At least three independent regions needed for registration')
    shifts = (b[None, :, :] - a[:, None, :]).reshape(-1, 2)
    shifts = shifts[np.linalg.norm(shifts, axis=1) <= maximum_shift]
    tree = cKDTree(b)
    best = None
    for shift in shifts:
        distances, indices = tree.query(a + shift)
        good = distances <= tolerance
        count = len(set(indices[good]))
        score = (count, -float(np.median(distances[good])) if good.any() else -1e9)
        if best is None or score > best[0]:
            best = (score, shift)
    if best is None or best[0][0] < 3:
        raise ValueError('No three-star translation consensus')
    distances, indices = tree.query(a + best[1])
    good = distances <= tolerance
    shift = np.median(b[indices[good]] - a[good], axis=0)
    distances, indices = tree.query(a + shift)
    match = [int(j) if d <= tolerance else None for d, j in zip(distances, indices)]
    used = [j for j in match if j is not None]
    if len(used) != len(set(used)):
        raise ValueError('Ambiguous many-to-one star registration')
    return shift.tolist(), match


def regions(result, matched_parent=False):
    return [s for s in result['sources'] if s['snr'] >= 15 and s['npix'] >= 9
            and s['a'] < 25 and not any(f in s['flags'] for f in
            ['saturated', 'aperture_at_edge', 'undersampled_region'])
            and (matched_parent or 'multiple_deblend_components' not in s['flags'])][:30]


def measure(image, reference=None, radius=25, saturation=65520):
    data = np.array(image, dtype=np.float32, order='C', copy=True)
    result = detect(data, Settings(saturation=saturation))
    # At the reference focus reject ambiguous parents. During a sweep an
    # isolated reference star may turn into a fragmented donut: retain its
    # complete parent, conditional on the multi-star translation and fixed
    # isolation aperture below. Never promote a child knot to a new star.
    current = regions(result, matched_parent=reference is not None)
    if reference is None:
        # An isolated reference aperture plus margin for shape changes.
        reference = [s for s in current if min(s['x'], s['y'], data.shape[1]-1-s['x'], data.shape[0]-1-s['y']) > radius+10
                     and all(np.hypot(s['x']-t['x'], s['y']-t['y']) > 2*radius+10
                             for t in current if t is not s)]
    shift, matches = registration(reference, current)
    background = sep.Background(data)
    residual = data - background.back()
    error = np.maximum(background.rms(), 1e-3)
    measured = []
    for sid, j in enumerate(matches):
        if j is None:
            continue
        s = current[j]
        x, y = s['x'], s['y']
        if min(x, y, data.shape[1]-1-x, data.shape[0]-1-y) <= radius:
            continue
        if any(np.hypot(x-t['x'], y-t['y']) < 2*radius for t in current if t is not s):
            continue
        ys, xs = np.mgrid[int(y-radius):int(y+radius)+1, int(x-radius):int(x+radius)+1]
        values = data[ys, xs][(xs-x)**2+(ys-y)**2 <= radius**2]
        if values.max() >= saturation:
            continue
        flux, err, flags = sep.sum_circle(residual, [x], [y], radius, err=error, subpix=5)
        radii, rflags = sep.flux_radius(residual, [x], [y], [radius], [0.5, 0.8], normflux=flux, subpix=5)
        r50, r80 = radii[0]
        snr = float(flux[0] / err[0])
        if flags[0] or rflags[0] or not (0 < r50 <= r80 < .85*radius) or snr < 15:
            continue
        measured.append(dict(id=sid, x=x, y=y, r50=float(r50), r80=float(r80),
                             flux=float(flux[0]), snr=snr, axis_ratio=s['axis_ratio'],
                             original_flags=s['flags'], aperture_radius=radius))
    return dict(reference=reference, shift=shift, stars=measured,
                sep_default_focus_count=result['focus_count'], source_count=len(result['sources']),
                default_sources=result['sources'], algorithm='SEP-fixed-isolated-aperture-R50-R80-v1')
