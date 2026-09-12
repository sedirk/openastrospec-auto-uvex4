import numpy as np
import pytest
from focus_metrics import registration, measure, regions


def test_translation_requires_ensemble_and_is_one_to_one():
    a = [dict(x=x, y=y) for x, y in [(20, 40), (180, 30), (90, 110), (60, 210)]]
    b = [dict(x=s['x']+32, y=s['y']-17) for s in a]
    shift, matches = registration(a, b)
    assert shift == [32, -17] and matches == [0, 1, 2, 3]
    with pytest.raises(ValueError):
        registration(a[:2], b)


def test_fixed_aperture_tracks_same_irregular_stars_and_focus_trend():
    y, x = np.mgrid[:360, :360]
    results = []
    ref = None
    for i, width in enumerate([3, 5, 7]):
        image = 4100 + np.random.default_rng(i).normal(0, 5, x.shape)
        for xx, yy in [(70, 70), (260, 80), (160, 265)]:
            image += 100000/(width**2) * np.exp(-((x-xx-i*7)**2 / (2*width**2) + (y-yy+i*3)**2/(5*width**2)))
        original = image.copy()
        r = measure(image, ref, radius=40)
        ref = r['reference']
        assert len(r['stars']) == 3
        assert np.array_equal(image, original)
        results.append(np.median([s['r50'] for s in r['stars']]))
    assert results[0] < results[1] < results[2]


def test_fragmented_parent_requires_an_existing_reference_but_clipping_still_excludes():
    parent = dict(snr=40, npix=100, a=6, flags=['multiple_deblend_components'])
    result = dict(sources=[parent])
    assert regions(result) == []
    assert regions(result, matched_parent=True) == [parent]
    parent['flags'].append('saturated')
    assert regions(result, matched_parent=True) == []
