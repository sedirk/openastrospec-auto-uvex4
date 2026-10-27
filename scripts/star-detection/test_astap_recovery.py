import importlib.util
from pathlib import Path

import numpy as np
import pytest
from astropy.wcs import WCS

spec = importlib.util.spec_from_file_location("astap_recovery", Path(__file__).with_name("astap_recovery.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def test_catalogue_sirius_encoding_and_missing_header(tmp_path):
    path = tmp_path/"test.1476"
    # Public ASTAP format example: Sirius, RA 6.75247662h, Dec -16.7161401.
    path.write_bytes(b" "*109+b"\x05"+bytes([255, 255, 255, 104, 1, 195, 6, 72, 215, 57]))
    rows, _, _ = worker.read_tile(path)
    assert rows[0, 0] == pytest.approx(6.75247662*15, abs=.000001)
    assert rows[0, 1] == pytest.approx(-16.7161401, abs=.000001)
    path.write_bytes(b" "*109+b"\x06"+bytes(6))
    with pytest.raises(ValueError):
        worker.read_tile(path)


def test_noise_and_saturated_frame_do_not_invent_sources():
    assert worker.detect(np.full((128, 128), 65520., np.float32)) == []
    assert worker.detect(np.full((128, 128), 1000., np.float32)) == []
    with pytest.raises(ValueError):
        worker.detect(np.zeros((128, 127)))


def test_render_preserves_subpixel_and_does_not_change_measurements():
    rows = [dict(x=40.25, y=60.75, flux=123)]
    image = worker.point_render(rows, (128, 128))-1000
    yy, xx = np.mgrid[:128, :128]
    assert (image*xx).sum()/image.sum() == pytest.approx(40.25, abs=.001)
    assert (image*yy).sum()/image.sum() == pytest.approx(60.75, abs=.001)
    assert rows == [dict(x=40.25, y=60.75, flux=123)]


def test_unique_matching_never_reuses_detection():
    assert len(worker.matches([[10, 10], [10.1, 10.1]], [[10, 10]])) == 1
    assert worker.matches([[10, 10]], [[30, 30]]) == []


@pytest.mark.parametrize("bad", [False, True])
def test_verifier_requires_distributed_real_matches(monkeypatch, tmp_path, bad):
    wcs = WCS(naxis=2)
    wcs.wcs.crpix = [500.5, 500.5]
    wcs.wcs.crval = [52.4, 47.3]
    wcs.wcs.cd = [[-.0000235, -.0001032], [.0001017, -.0000222]]
    wcs.wcs.ctype = ["RA---TAN", "DEC--TAN"]
    points = np.array([[100, 100], [800, 150], [200, 800], [750, 750], [450, 500], [100, 600.]])
    stars = np.column_stack((wcs.all_pix2world(points, 0), np.full(6, 12.)))
    monkeypatch.setattr(worker, "catalogue", lambda *a: (stars, []))
    rows = [dict(x=x+(30 if bad else .2), y=y) for x, y in points]
    r = worker.verify(wcs, rows, (1000, 1000), tmp_path, .378, (52.4, 47.3), 2.)
    assert r["accepted"] is (not bad)
    assert not r["independentCatalogue"]


def test_missing_catalogue_is_not_empty_sky(tmp_path):
    with pytest.raises(FileNotFoundError):
        worker.catalogue(tmp_path, 0., 0., .2)


def test_ring_coverage_and_ra_wrap():
    assert sum(worker.RING_CELLS) == 1476
    assert len(worker.RING_EDGES) == len(worker.RING_CELLS)+1
    assert np.all(np.diff(worker.RING_EDGES) > 0)
