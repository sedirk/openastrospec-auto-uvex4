import json
import os
from pathlib import Path
import subprocess
import sys

import numpy as np
import pytest

from uvex_reduce.live_preview import extract_preview


def fixture():
    x = np.arange(512)
    y = np.arange(160)[:, None]
    trace = 70 + 0.02 * x
    flux = 500 * (1 + 1.5 * np.exp(-0.5 * ((x - 320) / 2)**2)
                  - 0.6 * np.exp(-0.5 * ((x - 150) / 3)**2))
    image = 150 + 0.3 * x + flux * np.exp(-0.5 * ((y - trace) / 4)**2)
    image += np.random.default_rng(12).normal(0, 4, image.shape)
    return image.astype(np.float32)


@pytest.mark.parametrize("backend", ["native", "aspired"])
def test_cosmics_removed_without_erasing_narrow_emission_or_absorption(backend):
    clean = fixture()
    dirty = clean.copy()
    dirty[75, 242] += 40000
    dirty[72, 80] += 35000
    before = dirty.copy()
    reference = extract_preview(clean, 65535, 75, 12, backend=backend)
    result = extract_preview(dirty, 65535, 75, 12, backend=backend)
    np.testing.assert_array_equal(before, dirty)
    flux, raw = np.array(result["flux"]), np.array(result["rawFlux"])
    truth = np.array(reference["flux"])
    assert result["cosmicPixels"] >= 2
    assert abs(flux[242] - truth[242]) < abs(raw[242] - truth[242]) * 0.1
    np.testing.assert_allclose(flux[310:331], truth[310:331], rtol=0.01)
    np.testing.assert_allclose(flux[140:161], truth[140:161], rtol=0.01)
    assert flux[320] > 2 * np.median(flux[270:300])
    assert flux[150] < 0.5 * np.median(flux[170:190])
    assert result["previewOnly"] and not result["calibrated"]
    assert not result["spectralSmoothing"]


def test_saturation_remains_a_gap_not_repaired_signal():
    image = fixture()
    image[69:85, 310:330] = 65535
    result = extract_preview(image, 65535, 75, 12, backend="native")
    assert result["flux"][320] is None
    assert result["rawFlux"][320] is None
    assert result["maskedColumns"] >= 20
    json.dumps(result, allow_nan=False)


def test_no_trace_does_not_invent_a_spectrum():
    with pytest.raises(Exception, match="trace|Trace"):
        extract_preview(np.zeros((160, 512)), 65535, 75, 12, backend="native")


def test_excessive_cleaning_is_rejected_and_uses_measured_variance(monkeypatch):
    def overclean(image, mask, detector, *, variance):
        assert variance.shape == image.shape
        assert np.all(np.isfinite(variance))
        assert np.all(variance > 0)
        return image, int(image.size * 0.03)

    monkeypatch.setattr("uvex_reduce.live_preview._clean_cosmic_rays", overclean)
    with pytest.raises(ValueError, match="2%"):
        extract_preview(fixture(), 65535, 75, 12, backend="native")


@pytest.mark.parametrize("center,half", [(float("nan"), 12), (900, 12), (75, 0)])
def test_invalid_geometry_rejected(center, half):
    with pytest.raises(ValueError):
        extract_preview(fixture(), 65535, center, half)


def test_real_worker_protocol_uses_aspired_and_no_file_writes(tmp_path):
    image = fixture().astype("<u2")
    request = dict(schema=1, width=512, height=160, saturation=65535, traceCenter=75, traceHalfWidth=12)
    root = Path(__file__).resolve().parents[1]
    env = dict(os.environ, OMP_NUM_THREADS="2", OPENBLAS_NUM_THREADS="2", MPLCONFIGDIR=str(tmp_path))
    result = subprocess.run(
        [sys.executable, "-E", "-s", "-B", str(root / "live_preview_worker.py")],
        input=json.dumps(request).encode() + b"\n" + image.tobytes(),
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=45, env=env, cwd=tmp_path,
    )
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    payload = json.loads(result.stdout)
    # A live preview uses the flux-summing aperture path, not an uncommissioned
    # Gaussian optimal profile that can attenuate narrow-line contrast.
    assert payload["backend"] == "aspired-tophat"
    assert len(payload["flux"]) == 512
    assert payload["previewOnly"]
    assert not list(tmp_path.glob("*.fits"))
