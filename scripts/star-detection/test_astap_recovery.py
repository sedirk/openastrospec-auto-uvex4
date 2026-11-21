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


@pytest.mark.parametrize("degrees", [0., 127.99, 128.01, 180., 255.99, 256.01, 356.960378, 358.169241, 359.999])
def test_d80_unsigned_ra_covers_whole_sky(tmp_path, degrees):
    packed = round(degrees/360*16777215)
    path = tmp_path/"unsigned.1476"
    path.write_bytes(b" "*109+b"\x05"+bytes([255, 255, 255, 128, 16])+packed.to_bytes(3, "little")+b"\x00\x00")
    rows, _, _ = worker.read_tile(path)
    assert rows.shape == (1, 3)
    assert rows[0, 0] == pytest.approx(degrees, abs=360/16777215)


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


@pytest.mark.parametrize("spread,offset,width,expected", [
    (True, 0., 12., True), (False, 0., 12., False), (True, 10., 12., False), (True, 0., 6., False)])
def test_broad_proposal_requires_independent_distributed_native_support(monkeypatch, spread, offset, width, expected):
    data = np.zeros((512, 512), dtype=np.float32)
    rows = [dict(x=40+180*(i % 3), y=40+(150*(i//3) if spread else i), flux=100-i, rawFwhm=width) for i in range(9)]
    proposals = [dict(r, x=r["x"]+offset) for r in rows]
    monkeypatch.setattr(worker, "detect", lambda data, threshold: rows[:2])
    monkeypatch.setattr(worker, "broad_solver_proposals", lambda data: proposals)
    selected, receipt = worker.select_solver_sources(data, rows)
    assert (selected is proposals) is expected
    assert receipt["solverCalls"] == 1
    assert rows[0]["x"] == 40


def test_broad_proposals_reject_isolated_pixel_defects():
    data = np.full((256, 256), 4096., dtype=np.float32)
    data[20:240:20, 20:240:20] = 65520
    assert not worker.broad_solver_proposals(data)


def test_established_noise_tail_with_spare_strong_star_stays_on_original_route(monkeypatch):
    rows = [dict(x=i, y=i, rawFwhm=15., flux=100-i) for i in range(36)]
    monkeypatch.setattr(worker, "detect", lambda data, threshold: rows[:6])
    monkeypatch.setattr(worker, "broad_solver_proposals", lambda _: pytest.fail("do not displace noise-tail route"))
    _, receipt = worker.select_solver_sources(np.zeros((128, 128)), rows)
    assert receipt["mode"] == "noise-tail-5sigma"


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


@pytest.mark.parametrize("measured_count,strong_count,mode", [
    (49, 7, "noise-tail-5sigma"), (39, 5, "noise-tail-5sigma"),
    (25, 5, "noise-tail-5sigma"), (24, 5, "original-3.5sigma"),
    (21, 5, "original-3.5sigma"), (16, 3, "original-3.5sigma"),
    (100, 4, "original-3.5sigma"), (20, 0, "original-3.5sigma"),
])
def test_noise_tail_selection_keeps_sparse_faint_support_and_does_not_call_solver(
        monkeypatch, measured_count, strong_count, mode):
    measured = [dict(x=i, y=i, flux=100-i) for i in range(measured_count)]
    original = [dict(r) for r in measured]
    strong = measured[:strong_count]
    calls = []

    def detect(data, threshold):
        calls.append(threshold)
        return strong

    monkeypatch.setattr(worker, "detect", detect)
    selected, receipt = worker.select_solver_sources(None, measured)
    assert receipt["mode"] == mode
    assert receipt["solverCalls"] == 1  # budget, not a claim that solving happened
    assert selected is (strong if mode == "noise-tail-5sigma" else measured)
    assert measured == original
    assert calls == [5.]


def test_noise_only_comparison_does_not_become_field_or_focus_evidence():
    data = np.full((128, 128), 1000., np.float32)
    rows = worker.detect(data)
    selected, receipt = worker.select_solver_sources(data, rows)
    assert rows == selected == []
    assert receipt["mode"] == "original-3.5sigma"


def test_selected_sources_get_one_solver_call_not_an_unbounded_fallback(monkeypatch, tmp_path):
    from astropy.io import fits
    from types import SimpleNamespace
    raw_dir = tmp_path/"raw"
    raw_dir.mkdir()
    raw = raw_dir/"frame.fit"
    fits.writeto(raw, np.full((128, 128), 1000., np.float32))
    output = tmp_path/"analysis"
    output.mkdir()
    exe = tmp_path/"fake.exe"
    exe.write_bytes(b"not executed")
    rows = [dict(x=20+i, y=30+i, flux=100-i) for i in range(25)]
    monkeypatch.setattr(worker, "detect", lambda data, threshold=3.5: rows if threshold == 3.5 else rows[:5])
    calls = []

    def run(command, **kwargs):
        calls.append((command, kwargs))
        return SimpleNamespace(returncode=1, stdout="no solution", stderr="")

    monkeypatch.setattr(worker.subprocess, "run", run)
    digest = worker.sha(raw)
    result = worker.recover(dict(sourcePath=str(raw), sourceSha256=digest,
        astapPath=str(exe), astapSha256=worker.sha(exe), databasePath=str(tmp_path),
        ra=10., dec=20., scale=.384, radiusDegrees=2.), output)
    assert len(calls) == 1
    assert calls[0][1]["timeout"] == 22
    assert result["reason"] == "solver_no_solution"
    assert not result["accepted"] and not result["motionAuthorized"] and not result["targetIdentityConfirmed"]
    assert result["solverSelection"]["mode"] == "noise-tail-5sigma"
    assert result["detections"] == rows and len(result["solverDetections"]) == 5
    assert worker.sha(raw) == digest
