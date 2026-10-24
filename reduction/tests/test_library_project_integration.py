"""Sanitized historical-contract integration tests; all observations are synthetic.

Shapes follow the 2026-10-21 successful native runs and earlier manual reduction
manifests. These tests read neither the production catalogue nor historical raw
observations, and do not start an observing or reduction workflow.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from astropy.io import fits
import numpy as np

from uvex_reduce.config import InputConfig, PipelineConfig
from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_projects import LibraryProjects
from uvex_reduce.library_selection import LibrarySelection, export_library_project
from uvex_reduce.library_ui import reduction_frame
from uvex_reduce.pipeline import ReductionPipeline
from uvex_reduce.project import AstroProject


def _document(path: Path, payload: dict) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def _raw(path: Path, target: str, run_id: str = "", role: str = "SCIENCE") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    hdu = fits.PrimaryHDU(np.ones((8, 16), dtype=np.uint16))
    for key, value in {
        "OBJECT": target, "OBSRUNID": run_id, "UVEXSTG": role,
        "INSTRUME": "ATR585M", "EXPTIME": 600,
    }.items():
        hdu.header[key] = value
    hdu.writeto(path)
    return path


def _spectrum_product(path: Path, run_id: str) -> Path:
    """Real product writers preserve source identity cards in a table FITS."""
    path.parent.mkdir(parents=True, exist_ok=True)
    primary = fits.PrimaryHDU()
    for key, value in {
        "OBJECT": "HD 18552", "OBSRUNID": run_id, "UVEXSTG": "SCIENCE",
        "INSTRUME": "ATR585M", "EXPTIME": 600, "PIPELINE": "UVEX-ADV",
    }.items():
        primary.header[key] = value
    table = fits.BinTableHDU.from_columns([
        fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=[4000.0, 4001.0]),
        fits.Column(name="FLUX", format="D", unit="adu", array=[10.0, 11.0]),
        fits.Column(name="UNCERTAINTY", format="D", unit="adu", array=[1.0, 1.0]),
        fits.Column(name="MASK", format="L", array=[False, False]),
    ], name="SPECTRUM")
    fits.HDUList([primary, table]).writeto(path)
    return path


def _evidence(path: Path, run_id: str, role: str, accepted: bool, **extra) -> dict:
    return {
        "kind": "atr-science-fits" if role == "SCIENCE" else "atr-probe-fits",
        "absolutePath": str(path),
        "metadata": {
            "observationRunId": run_id, "targetName": "HD 18552",
            "captureId": path.stem, "stageRole": role,
            "qualityAccepted": str(accepted), "exposureSeconds": "600",
            **extra,
        },
    }


def _run(root: Path, run_id: str, *, state: str = "Completed", evidence=()) -> Path:
    return _document(root / run_id / "manifest.json", {
        "schemaVersion": 1, "observationRunId": run_id,
        "plan": {
            "observationRunId": run_id,
            "target": {"name": "HD 18552", "catalogId": "HD 18552"},
            "nightSetupId": "HISTORICAL-CONTRACT-ONLY", "expectedAtrCameraId": "ATR585M",
        },
        "createdUtc": "2026-10-22T02:28:13.5256491+08:00",
        "lockedMetadata": {"labels": {"adapter": "real"}},
        "terminalState": state, "terminalReason": "Synthetic recorded terminal reason",
        "snapshot": {"state": state, "recentEvents": [{
            "timestampUtc": "2026-10-21T18:55:35Z", "stage": "RunScienceBlock",
            "code": "RUN_COMPLETED" if state == "Completed" else "G3_POST_WCS_TARGET_NOT_MEASURED",
            "message": "Synthetic historical-contract event",
        }]},
        "evidence": list(evidence),
    })


def test_native_probe_credit_review_and_actual_handoff_preserve_originals(tmp_path):
    """Real-shaped duplicate probe references remain one selected physical file."""
    root = tmp_path / "observations"
    promoted = _raw(root / "success" / "final-probe.fits", "HD 18552", "success", "PROBE")
    science = _raw(root / "success" / "science.fits", "HD 18552", "success")
    rejected = _raw(root / "success" / "quality-rejected.fits", "HD 18552", "success")
    short_probe = _raw(root / "success" / "short-probe.fits", "HD 18552", "success", "PROBE")
    original = {path: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in (promoted, science, rejected, short_probe)}
    _run(root, "success", evidence=[
        _evidence(promoted, "success", "PROBE", True),
        _evidence(promoted, "success", "PROBE", True, effectiveStageRole="SCIENCE",
                  sourceCaptureRole="PROBE", scienceReusedFromProbe="True"),
        _evidence(science, "success", "SCIENCE", True),
        _evidence(rejected, "success", "SCIENCE", False),
        _evidence(short_probe, "success", "PROBE", True),
    ])
    _run(root, "failure", state="Faulted")
    _document(root / "night.json", {
        "schemaVersion": 1, "phase": "Terminal",
        "deadlineSchedule": {"ObservingNightDate": "2026-10-21"},
        "outcomes": [
            {"target": "HD 18552", "runId": "success", "outcome": "Completed",
             "retainedScience": {"requestedFrames": 2, "acceptedFrames": 2,
                                 "acceptedExposureSeconds": 1200, "segmentRunIds": ["success"]}},
            {"target": "HD 18552", "runId": "failure", "outcome": "SkippedQualityFailure",
             "gate": "G3_POST_WCS_TARGET_NOT_MEASURED",
             "retainedScience": {"requestedFrames": 2, "acceptedFrames": 0,
                                 "acceptedExposureSeconds": 0, "segmentRunIds": ["failure"]}},
            {"terminal": "Synthetic night finished"},
        ],
    })
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        assert catalog.scan()["warnings"] == []
        model = LibraryProjects(catalog)
        cards = {row["run_id"]: row for row in model.list_projects(source="real")}
        assert set(cards) == {"success", "failure"}
        assert cards["success"]["night"] == "2026-10-21"
        assert cards["failure"]["night"] == "2026-10-21"
        assert cards["success"]["accepted_frames"] == cards["success"]["logged_accepted_frames"] == 2
        assert cards["failure"]["outcome"] == "failed"
        assert model.select_science("run:failure") == []
        selected = model.select_science("run:success")
        assert {Path(row["path"]) for row in selected} == {promoted, science}
        assert len(selected) == 2

        # A human culling mark changes neither acquisition acceptance nor disk.
        marked = next(row for row in selected if Path(row["path"]) == science)
        catalog.set_review(marked["id"], "reject", notes="Synthetic operator cull")
        assert model.get_project("run:success")["accepted_frames"] == 2
        selected = model.select_science("run:success")
        assert [Path(row["path"]) for row in selected] == [promoted]
        saved = export_library_project(
            tmp_path / "projects" / "HD18552.astroproj", name="HD 18552",
            data_root=root, output_root=tmp_path / "reduction-results",
            frames=[reduction_frame(row, "science") for row in selected],
        )
        project = AstroProject.load(saved)
        selection = LibrarySelection.from_parameters(project.parameters)
        assert selection is not None
        selection.validate_files()
        config = PipelineConfig(InputConfig(root, ["**/*.fits"], bias=["**/*.fits"]))
        selection.apply_to_config(config, target="HD 18552", role="science")
        resolved, _warnings = ReductionPipeline(config)._resolve_inputs()
        assert resolved["science"] == [promoted]
        assert all(resolved[role] == [] for role in ("bias", "dark", "flat", "arc"))
        model.index_project(saved)
        assert model.get_project("run:success")["reduction_projects"]
    assert {path: hashlib.sha256(path.read_bytes()).hexdigest() for path in original} == original


def test_exact_science_chain_reaches_response_products_without_shared_bias_merge(tmp_path):
    root = tmp_path / "data"
    raw = _raw(root / "science-run" / "raw.fits", "HD 18552", "science-run")
    other = _raw(root / "other-run" / "raw.fits", "HD 18552", "other-run")
    _run(root, "science-run", evidence=[_evidence(raw, "science-run", "SCIENCE", True)])
    _run(root, "other-run", evidence=[_evidence(other, "other-run", "SCIENCE", True)])
    extracted = _spectrum_product(root / "reduced" / "HD18552_spectrum.fits", "science-run")
    calibrated = _spectrum_product(root / "final" / "HD18552_calibrated_1d.fits", "science-run")
    reduction = _document(root / "reduced" / "HD18552_run.json", {
        "schemaVersion": 1, "target": "HD 18552",
        "inputs": {"science": [str(raw)], "bias": [str(other)]},
        "frameSelection": {"accepted": [str(raw)], "rejected": []},
        "wavelength": {"status": "calibrated", "rmsAngstrom": 0.5},
        "artifacts": {"fits": str(extracted)},
    })
    calibration = _document(root / "final" / "HD18552_calibration.json", {
        "schemaVersion": 1, "target": "HD 18552",
        "scienceProduct": str(extracted), "standardProduct": str(tmp_path / "shared-standard.fits"),
        "standardName": "Vega", "absoluteFluxCalibrated": False,
        "artifacts": {"calibrated_fits": str(calibrated)},
    })
    workflow = _document(root / "full_workflow.json", {
        "schemaVersion": 1, "target": "HD 18552", "standard": "Vega",
        "scienceRun": str(reduction), "standardRun": str(tmp_path / "Vega_run.json"),
        "artifacts": {"calibrated_fits": str(calibrated), "manifest": str(calibration)},
    })
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        assert catalog.scan()["warnings"] == []
        model = LibraryProjects(catalog)
        target = model.get_project("run:science-run")
        other_target = model.get_project("run:other-run")
        assert target["reduction_stage"] in {"calibrated", "workflow"}
        assert other_target["reduction_stage"] == "raw"
        assert target["science_frames"] == target["accepted_frames"] == 1
        assert target["exposure_seconds"] == 600
        paths = {Path(row["path"]) for row in target["files"]}
        assert {reduction, workflow, calibration, extracted, calibrated} <= paths
        assert {Path(row["path"]) for row in model.select_science("run:science-run")} == {raw}
        assert model.get_project("run:other-run")["products"] == []


def test_manual_historical_reduction_is_a_project_without_native_run_or_astroproj(tmp_path):
    root = tmp_path / "historical-manual-data"
    # Actual historical Regulus files had a wrong OBJECT header; the reduction
    # manifest records that conflict and must not disappear or become NGC2392.
    raw = _raw(root / "raw" / "Regulus-10.fit", "NGC2392", "", "LIGHT")
    product = root / "reduced" / "Regulus_spectrum.csv"
    product.parent.mkdir(parents=True)
    product.write_text("wavelength_angstrom,flux_adu,uncertainty_adu,mask\n4000,1,0.1,0\n")
    manifest = _document(root / "reduced" / "Regulus_run.json", {
        "schemaVersion": 1, "pipelineVersion": "0.2.0", "target": "Regulus",
        "createdUtc": "2026-08-14T16:37:16.108968+00:00",
        "inputs": {"science": [str(raw)], "bias": [], "dark": [], "flat": [], "arc": []},
        "wavelength": {"status": "calibrated", "medium": "air", "rmsAngstrom": 0.1347},
        "warnings": ["Header OBJECT='NGC2392' conflicts with filename target 'Regulus'."],
        "artifacts": {"csv": str(product)},
    })
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        assert catalog.scan()["warnings"] == []
        model = LibraryProjects(catalog)
        rows = model.list_projects(query="Regulus")
        assert rows, "Historical processed spectroscopy must be visible without an acquisition run."
        assert rows[0]["target"] == "Regulus"
        assert rows[0]["source"] == "unknown"
        detail = model.get_project(rows[0]["id"])
        assert {raw, manifest, product} <= {Path(row["path"]) for row in detail["files"]}
        # Library browsing must not convert a wrong header into a silently
        # authoritative identity or implicitly accept old quality-unknown raws.
        assert model.select_science(rows[0]["id"]) == []


def test_night_only_failed_and_preflight_targets_remain_visible_without_raws(tmp_path):
    root = tmp_path / "night-records"
    remembered_manifest = tmp_path / "not-registered" / "missing-run" / "manifest.json"
    night = _document(root / "night.json", {
        "schemaVersion": 1, "phase": "Terminal",
        "deadlineSchedule": {"ObservingNightDate": "2026-10-21"},
        "outcomes": [
            {"target": "HD 18552", "runId": "missing-run", "ManifestPath": str(remembered_manifest),
             "outcome": "Stopped", "gate": "ATR_COOLING_RECOVERY_CAMERA_BUSY",
             "retainedScience": {"requestedFrames": 2, "acceptedFrames": 0,
                                 "acceptedExposureSeconds": 0, "segmentRunIds": ["missing-run"]}},
            {"target": "HD 21455", "outcome": "SkippedPreflightHorizon",
             "gate": "TARGET_BELOW_HORIZON"},
            {"terminal": "Synthetic close confirmed"},
        ],
    })
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        assert catalog.scan()["warnings"] == []
        model = LibraryProjects(catalog)
        rows = {row["target"]: row for row in model.list_projects()}
        assert set(rows) == {"HD 18552", "HD 21455"}
        assert rows["HD 18552"]["outcome"] not in {"incomplete", "completed"}
        for row in rows.values():
            assert row["science_frames"] == row["accepted_frames"] == 0
            detail = model.get_project(row["id"])
            assert night in {Path(path) for path in detail["source_paths"]}
            assert model.select_science(row["id"]) == []
        assert not remembered_manifest.exists()


def test_saved_studio_project_uses_real_camelcase_and_output_directory_contract(tmp_path):
    root = tmp_path / "indexed-results"
    raw = _raw(root / "manual-raw" / "one.fits", "HD 18552", "", "LIGHT")
    output_dir = root / "existing-output"
    product = output_dir / "HD18552_spectrum.csv"
    product.parent.mkdir(parents=True)
    product.write_text("pixel,flux_adu,uncertainty_adu,mask\n0,1,0.1,0\n")
    manifest = _document(output_dir / "HD18552_run.json", {
        "schemaVersion": 1, "target": "HD 18552",
        "inputs": {"science": [str(raw)]}, "wavelength": {"status": "needs_reference"},
        "artifacts": {"csv": str(product)},
    })
    saved = AstroProject(
        name="Saved HD 18552", data_root=str(root), output_root=str(output_dir),
        current_stage="science", last_output=str(output_dir), notes="Existing operator project",
    ).save(tmp_path / "operator-projects" / "saved.astroproj")
    with LibraryCatalog(tmp_path / "catalog.sqlite3") as catalog:
        catalog.add_root(root)
        assert catalog.scan()["warnings"] == []
        model = LibraryProjects(catalog)
        roots_before = catalog.list_roots()
        model.index_project(saved)
        assert catalog.list_roots() == roots_before
        rows = model.list_projects(query="Saved HD 18552")
        assert len(rows) == 1
        detail = model.get_project(rows[0]["id"])
        project = next(item for item in detail["reduction_projects"] if Path(item["path"]) == saved)
        assert project["current_stage"] == "science"
        assert Path(project["last_output"]) == output_dir
        assert {saved, manifest, product} <= {Path(item["path"]) for item in detail["files"]}
        # Loading the existing project for inspection must not rewrite it.
        assert AstroProject.load(saved).last_output == str(output_dir)
