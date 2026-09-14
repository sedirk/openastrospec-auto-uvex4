"""Synthetic analysis checks only; no hardware or saved observing data changed."""
import numpy as np
import pytest

from analyze_backlash import add_sparse_tracks, fit_play, optical_positions, summarize


def test_direction_reversal_holds_optical_position_until_dead_band_taken_up():
    x = [5000, 5300, 5275, 5200, 5100, 4900, 4700, 4750, 4800, 4900, 5000]
    assert optical_positions(x, 200).tolist() == [5000, 5300, 5300, 5300, 5300,
                                                5100, 4900, 4900, 4900, 4900, 5000]


def test_zero_backlash_is_identity():
    x = [4800, 5000, 5300, 5100, 4700, 4750, 4950]
    assert optical_positions(x, 0).tolist() == x


def frames(index, position, group, ids=(1, 2, 3), value=5):
    return [dict(index=index, position=position, group=group, frame=f,
                 measurement=dict(stars=[dict(id=i, r50=value, r80=value+2, flux=1000, snr=30)
                                         for i in ids])) for f in range(2)]


def test_baseline_never_enters_sweep_fit_or_star_intersection():
    records = frames(0, 5000, 'baseline', ids=(8,), value=1000)
    records += frames(1, 4950, 'up1')+frames(2, 5000, 'up1')
    ids, points = summarize(records)
    assert ids == [1, 2, 3]
    assert [p['index'] for p in points] == [1, 2]
    assert all(p['r50'] == 5 for p in points)


def test_changing_population_cannot_fake_fixed_ensemble():
    records = frames(1, 4950, 'up1', ids=(1, 2, 3))+frames(2, 5000, 'up1', ids=(2, 3, 4))
    with pytest.raises(ValueError, match='common stars'):
        summarize(records)


def test_two_track_diagnostic_is_explicit_not_production_three_star_acceptance():
    records = frames(1, 4950, 'up1', ids=(1, 2, 3))+frames(2, 5000, 'up1', ids=(2, 3, 4))
    ids, points = summarize(records, minimum_common=2)
    assert ids == [2, 3]
    assert all(set(point['stars']) == {'2', '3'} for point in points)
    with pytest.raises(ValueError, match='two complete'):
        summarize(records, minimum_common=1)


def test_sparse_star_requires_two_frames_without_removing_missing_physical_position():
    records = []
    for i in range(24):
        group = ('up1', 'down1', 'up2')[i//8]
        records += frames(i+1, 4800+i*10, group, ids=(1, 2, 3, 4) if i not in (3, 11, 19) else (1, 2, 3))
    _, points = summarize(records)
    assert add_sparse_tracks(records, points) == [1, 2, 3, 4]
    assert len(points) == 24
    assert '4' not in points[3]['stars']
    assert '4' in points[4]['stars']


def test_missing_or_duplicate_frame_not_counted_as_independent_measurement():
    records = frames(1, 4950, 'up1')
    with pytest.raises(ValueError, match='two independent frames'):
        summarize(records[:1])
    with pytest.raises(ValueError, match='two independent frames'):
        summarize([records[0], records[0]])


def test_joint_model_recovers_synthetic_backlash_not_just_shifted_focus():
    up = [4800, 4900, 4950, 5000, 5050, 5100, 5200, 5300]
    down = [5275, 5250, 5225, 5200, 5150, 5100, 5050, 5000, 4950, 4900, 4800, 4700]
    repeat = [4725, 4750, 4775, 4800, 4850, 4900, 4950, 5000, 5050, 5100, 5200, 5300]
    x = up+down+repeat
    q = optical_positions(x, 183, .8)
    y = np.sqrt(4.2**2 + (.029*(q-5024))**2)
    groups = ['up1']*len(up)+['down1']*len(down)+['up2']*len(repeat)
    points = [dict(position=p, group=g, r50=float(r), r50_error=.15) for p, g, r in zip(x, groups, y)]
    result = fit_play(points)
    assert result['backlash_steps'] == pytest.approx(183, abs=.1)
    assert result['increasing_direction_focus'] == pytest.approx(5024, abs=.1)
    assert result['residual_rms_px'] < .001
    assert not result['boundary_limited']


def test_one_direction_cannot_claim_backlash():
    with pytest.raises(ValueError, match='reverse and repeat'):
        fit_play([dict(position=4800+i*20, group='up1', r50=4, r50_error=.15) for i in range(12)])
