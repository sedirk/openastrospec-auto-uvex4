import io
import json
import numpy as np
import pytest
from sep_detector import Settings, detect
from worker import run


def shape_frame(shape, seed=1):
    y, x = np.mgrid[:192, :192].astype(float)
    x -= 95.2
    y -= 96.4
    r = np.hypot(x, y)
    if shape == "donut":
        signal = np.exp(-0.5 * ((r - 10) / 2)**2)
    elif shape == "triangle":
        signal = sum(np.exp(-((x - dx)**2 + (y - dy)**2) / 22)
                     for dx, dy in [(-4, 3), (4, 3), (0, -4)])
    elif shape == "shuttlecock":
        signal = np.exp(-(x*x + y*y) / 16) + 0.4 * np.exp(-((x - 6)**2 / 70 + y*y / 30))
    elif shape == "chestnut":
        signal = np.exp(-r*r / 35) + 0.6 * np.exp(-((x+4)**2 / 25 + (y+4)**2 / 6))
    elif shape == "elongated":
        signal = np.exp(-0.5 * (x*x / 100 + y*y / 4))
    else:
        signal = np.exp(-r*r / 10)
    signal *= 800
    truth = [float(np.sum((x+95.2)*signal)/signal.sum()),
             float(np.sum((y+96.4)*signal)/signal.sum())]
    return 1000 + signal + np.random.default_rng(seed).normal(0, 3, signal.shape), truth


@pytest.mark.parametrize("shape", ["round", "donut", "triangle", "shuttlecock", "chestnut", "elongated"])
def test_irregular_sources_are_measured_not_roundness_rejected(shape):
    frame, truth = shape_frame(shape)
    result = detect(frame)
    star = min(result["sources"], key=lambda s: np.hypot(s["x"]-truth[0], s["y"]-truth[1]))
    assert np.hypot(star["x"]-truth[0], star["y"]-truth[1]) < 0.5
    assert star["r80"] >= star["r50"] > 0
    assert star["focus_eligible"], star["flags"]
    assert not result["motion_authorized"] and not result["target_identity_confirmed"]
    parent = min(result['parent_components'], key=lambda s: np.hypot(s['x']-truth[0], s['y']-truth[1]))
    assert np.hypot(parent['x']-truth[0], parent['y']-truth[1]) < .5
    assert not result['parent_components_truncated']


def test_hot_pixel_blank_and_nonfinite():
    frame = np.full((128, 128), 1000.0)
    frame[64, 64] = 60000
    assert detect(frame)["focus_count"] == 0
    assert detect(frame)["components"] == []
    assert detect(frame)["parent_components"] == []
    assert detect(np.full((128, 128), 1000.0))["sources"] == []
    frame[0, 0] = np.nan
    with pytest.raises(ValueError):
        detect(frame)


def test_saturation_and_edges_are_reported_not_lost():
    frame, _ = shape_frame("donut")
    result = detect(np.minimum(frame, 1600), Settings(saturation=1600))
    assert "saturated" in result["sources"][0]["flags"]
    assert not result["sources"][0]["focus_eligible"]
    edge = detect(frame[90:, 90:])
    assert "aperture_at_edge" in edge["sources"][0]["flags"]


def test_raw_immutable_and_protocol():
    frame = shape_frame("round")[0].astype("<u2")
    original = frame.copy()
    result = detect(frame)
    np.testing.assert_array_equal(frame, original)
    header = json.dumps({"schema": 1, "width": 192, "height": 192, "saturation": 65520}) + "\n"
    output = io.StringIO()
    run(io.BytesIO(header.encode() + frame.tobytes()), output)
    assert json.loads(output.getvalue())["sources"] == result["sources"]
    with pytest.raises(ValueError, match="Truncated"):
        run(io.BytesIO(header.encode()+b"0"), io.StringIO())
    with pytest.raises(ValueError, match="trailing"):
        run(io.BytesIO(header.encode()+frame.tobytes()+b"extra"), io.StringIO())


def test_nearby_double_not_authorized_as_single_focus_star():
    frame, _ = shape_frame("round")
    frame += np.roll(frame-1000, 9, axis=1)
    result = detect(frame)
    assert any("multiple_deblend_components" in s["flags"] for s in result["sources"])
    assert len(result['components']) == 2
    assert len(result['parent_components']) == 1
    assert not result['components_truncated']
    assert all(c['raw_support_pixels'] >= 3 for c in result['components'])
