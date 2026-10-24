from __future__ import annotations

from copy import deepcopy
import hashlib
import queue
import subprocess
import sys
from types import SimpleNamespace
from pathlib import Path

from astropy.io import fits
import numpy as np
import pytest

from uvex_reduce.config import InputConfig, PipelineConfig, expand_patterns
from uvex_reduce.gui import UvexReductionApp, selected_observation_groups
from uvex_reduce.library_selection import LibrarySelection, export_library_project, pin_selection_frames
from uvex_reduce.pipeline import ReductionPipeline
from uvex_reduce.project import AstroProject
from uvex_reduce import workflow


def frame(tmp_path, name, *, target="Science", role="science"):
    path = tmp_path / name
    fits.PrimaryHDU(np.ones((32, 32), dtype=np.uint16)).writeto(path)
    return {
        "path": str(path.resolve()),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "target": target,
        "role": role,
    }


def test_export_and_roundtrip_preserve_exact_selection_and_source_bytes(tmp_path):
    selected = frame(tmp_path, "kept.fit")
    rejected = frame(tmp_path, "rejected.fit")
    before = {item["path"]: hashlib.sha256(Path(item["path"]).read_bytes()).hexdigest()
              for item in (selected, rejected)}
    path = export_library_project(
        tmp_path / "picked.astroproj", name="Picked", data_root=tmp_path,
        output_root=tmp_path / "output", frames=[selected],
    )
    project = AstroProject.load(path)
    selection = LibrarySelection.from_parameters(project.parameters)
    assert selection is not None
    assert selection.paths("science", "Science") == [selected["path"]]
    project.save(path)
    assert AstroProject.load(path).parameters == project.parameters
    for item in (selected, rejected):
        assert hashlib.sha256(Path(item["path"]).read_bytes()).hexdigest() == before[item["path"]]


@pytest.mark.parametrize("value", [None, {}, {"schemaVersion": 2, "frames": []},
                                 {"schemaVersion": True, "frames": []}])
def test_malformed_selection_never_means_legacy_directory_scan(value):
    with pytest.raises(ValueError):
        LibrarySelection.from_parameters({"librarySelection": value})
    assert LibrarySelection.from_parameters({}) is None


def test_empty_selection_is_explicit_and_cannot_fallback(tmp_path):
    selection = LibrarySelection.from_parameters({"librarySelection": {"schemaVersion": 1, "frames": []}})
    assert selection is not None
    assert selected_observation_groups(selection, {"science", "standard"}) == []
    config = PipelineConfig(InputConfig(tmp_path, ["*.fit"]))
    with pytest.raises(ValueError, match="no directory fallback"):
        selection.apply_to_config(config, target="Science", role="science")


def test_duplicate_and_invalid_roles_rejected(tmp_path):
    item = frame(tmp_path, "a.fit")
    with pytest.raises(ValueError, match="more than once"):
        LibrarySelection.create([item, item])
    with pytest.raises(ValueError, match="role"):
        LibrarySelection.create([{**item, "role": "photometry"}])


def test_missing_and_changed_files_do_not_rescan(tmp_path):
    item = frame(tmp_path, "keep.fit")
    selection = LibrarySelection.create([item])
    with open(item["path"], "ab") as stream:
        stream.write(b"changed")
    with pytest.raises(ValueError, match="SHA-256"):
        selection.validate_files()
    Path(item["path"]).unlink()
    frame(tmp_path, "replacement.fit")
    with pytest.raises(FileNotFoundError, match="missing"):
        selection.validate_files()


def test_library_groups_use_explicit_target_and_do_not_discover_unselected(tmp_path):
    keep = frame(tmp_path, "000001_acquisition.fit", target="HD 123")
    frame(tmp_path, "HD-123-rejected.fit", target="HD 123")
    selection = LibrarySelection.create([keep])
    groups = selected_observation_groups(selection, {"science"})
    assert len(groups) == 1
    assert groups[0].target == "HD 123"
    assert [str(path) for path in groups[0].files] == [keep["path"]]
    assert groups[0].role == "science"


def test_actual_pipeline_resolver_uses_only_selection_and_empty_calibrations(tmp_path):
    item = frame(tmp_path, "kept.fit")
    frame(tmp_path, "rejected.fit")
    frame(tmp_path, "LED-unselected.fit", role="flat")
    config = PipelineConfig(InputConfig(tmp_path, ["*.fit"], flat=["LED*.fit"], bias=["*.fit"]))
    LibrarySelection.create([item]).apply_to_config(config, target="Science", role="science")
    resolved, _warnings = ReductionPipeline(config)._resolve_inputs()
    assert [str(path) for path in resolved["science"]] == [item["path"]]
    assert resolved["flat"] == resolved["bias"] == resolved["dark"] == resolved["arc"] == []


def test_exact_path_metacharacters_never_expand_or_silently_drop(tmp_path):
    item = frame(tmp_path, "frame[1].fit")
    frame(tmp_path, "frame1.fit")
    assert [str(path) for path in expand_patterns(tmp_path, [item["path"]], exact=True)] == [item["path"]]
    with pytest.raises(FileNotFoundError):
        expand_patterns(tmp_path, [str(tmp_path / "absent.fit")], exact=True)


def test_quick_worker_actual_inputs_exclude_rejected_and_flat_glob(tmp_path, monkeypatch):
    import uvex_reduce.gui as gui
    standard = frame(tmp_path, "Regulus-1.fit", target="Regulus", role="standard")
    science = frame(tmp_path, "Target-1.fit")
    flat = frame(tmp_path, "LED-keep.fit", role="flat")
    rejected = frame(tmp_path, "LED-rejected.fit", role="flat")
    selection = LibrarySelection.create([standard, science, flat])
    original_pipeline = ReductionPipeline
    seen = []

    class Pipeline:
        def __init__(self, config):
            self.config = config

        def run(self):
            resolved, _ = original_pipeline(self.config)._resolve_inputs()
            seen.append(resolved)
            solution = SimpleNamespace(wavelength_angstrom=np.array([4000., 7000.]), method="test",
                                       matched_pixels=np.array([1, 2]), rms_angstrom=1., template_correlation=.9)
            return SimpleNamespace(target_name="test", artifacts={"fits": tmp_path / "output.fits"},
                                   result=SimpleNamespace(wavelength=solution, extraction_backend="test", warnings=[]))

    monkeypatch.setattr(gui, "ReductionPipeline", Pipeline)
    app = UvexReductionApp.__new__(UvexReductionApp)
    app.events = queue.Queue()
    standard_group = selected_observation_groups(selection, {"standard"})[0]
    science_group = selected_observation_groups(selection, {"science"})[0]
    app._run_worker(standard_group, science_group, tmp_path, tmp_path / "out", tmp_path, False, "mean", selection)
    event, payload = app.events.get_nowait()
    assert event == "success", payload
    assert len(seen) == 2
    assert [str(path) for path in seen[0]["science"]] == [standard["path"]]
    assert [str(path) for path in seen[1]["science"]] == [science["path"]]
    assert all([str(path) for path in inputs["flat"]] == [flat["path"]] for inputs in seen)
    assert all(rejected["path"] not in [str(path) for paths in inputs.values() for path in paths]
               for inputs in seen)


def test_full_workflow_control_flat_trial_and_science_actual_inputs_are_selected(tmp_path, monkeypatch):
    standard = frame(tmp_path, "standard.fit", target="Regulus", role="standard")
    science = frame(tmp_path, "science.fit")
    flat = frame(tmp_path, "flat.fit", role="flat")
    rejected = frame(tmp_path, "reject.fit")
    selection = LibrarySelection.create([standard, science, flat])
    base = PipelineConfig(InputConfig(tmp_path, ["*.fit"], flat=["*.fit"], bias=["*.fit"]))
    template = tmp_path / "template.dat"
    template.write_text("test", encoding="utf-8")
    seen = []
    original_pipeline = ReductionPipeline

    class ReachedScience(Exception):
        pass

    class Pipeline:
        def __init__(self, config):
            self.config = config

        def run(self):
            resolved, _ = original_pipeline(self.config)._resolve_inputs()
            seen.append(resolved)
            if len(seen) == 3:
                raise ReachedScience()
            return SimpleNamespace(result=SimpleNamespace(header={"FLATCOR": True}),
                                   artifacts={"fits": tmp_path / "standard-output.fits"})

    monkeypatch.setattr(workflow, "load_config", lambda _: deepcopy(base))
    monkeypatch.setattr(workflow, "ReductionPipeline", Pipeline)
    quality = workflow.StandardQuality(True, .9, .1, 5, 1., "test")
    monkeypatch.setattr(workflow, "_measure_standard_quality", lambda *args: (quality, object()))
    monkeypatch.setattr(workflow, "_accept_flat_trial", lambda *args: (True, "test"))
    with pytest.raises(ReachedScience):
        workflow.run_full_workflow("unused", "unused", template, tmp_path / "out",
                                   standard_name="Regulus", target_name="Science", input_root=tmp_path,
                                   library_selection=selection)
    assert len(seen) == 3
    assert [str(path) for path in seen[0]["science"]] == [standard["path"]]
    assert [str(path) for path in seen[1]["science"]] == [standard["path"]]
    assert [str(path) for path in seen[2]["science"]] == [science["path"]]
    assert all([str(path) for path in inputs["flat"]] == [flat["path"]] for inputs in seen)
    assert all(inputs["bias"] == [] for inputs in seen)
    assert all(rejected["path"] not in [str(path) for paths in inputs.values() for path in paths]
               for inputs in seen)


def test_full_workflow_missing_selected_target_fails_before_processing(tmp_path):
    standard = frame(tmp_path, "standard.fit", target="Regulus", role="standard")
    with pytest.raises(ValueError, match="no directory fallback"):
        workflow.run_full_workflow("unused", "unused", "unused", tmp_path / "out",
                                   standard_name="Regulus", target_name="Science",
                                   library_selection=LibrarySelection.create([standard]))
    assert not (tmp_path / "out").exists()


def test_export_cannot_replace_fits_input(tmp_path):
    item = frame(tmp_path, "keep.fit")
    with pytest.raises(ValueError, match=".astroproj"):
        export_library_project(item["path"], name="bad", data_root=tmp_path,
                               output_root=tmp_path / "out", frames=[item])


def test_export_pins_unhashed_basket_entries(tmp_path):
    item = frame(tmp_path, "keep.fit")
    basket_item = {key: value for key, value in item.items() if key != "sha256"}
    path = export_library_project(tmp_path / "picked.astroproj", name="picked", data_root=tmp_path,
                                  output_root=tmp_path / "out", frames=[basket_item])
    assert AstroProject.load(path).parameters["librarySelection"]["frames"] == [item]


def test_pin_never_replaces_wrong_supplied_digest(tmp_path):
    item = frame(tmp_path, "keep.fit")
    with pytest.raises(ValueError, match="SHA-256"):
        pin_selection_frames([{**item, "sha256": "0" * 64}])


def test_hash_detects_source_change_during_read(tmp_path, monkeypatch):
    import uvex_reduce.library_selection as module
    item = frame(tmp_path, "keep.fit")
    real_fstat = module.os.fstat
    calls = []

    def changed_after_read(fd):
        stat = real_fstat(fd)
        calls.append(stat)
        return stat if len(calls) == 1 else SimpleNamespace(
            st_dev=stat.st_dev, st_ino=stat.st_ino, st_size=stat.st_size,
            st_mtime_ns=stat.st_mtime_ns + 1, st_ctime_ns=stat.st_ctime_ns)

    monkeypatch.setattr(module.os, "fstat", changed_after_read)
    with pytest.raises(ValueError, match="while hashing"):
        pin_selection_frames([{key: value for key, value in item.items() if key != "sha256"}])


def test_output_cannot_contain_selected_observations(tmp_path):
    item = frame(tmp_path, "keep.fit")
    with pytest.raises(ValueError, match="output directory"):
        export_library_project(tmp_path / "picked.astroproj", name="bad", data_root=tmp_path,
                               output_root=tmp_path, frames=[item])


class Value:
    def __init__(self, value=None):
        self.value = value

    def get(self):
        return self.value

    def set(self, value):
        self.value = value


def test_studio_save_preserves_selection_and_unknown_parameters(tmp_path):
    from uvex_reduce.studio import UvexStudioApp
    item = frame(tmp_path, "keep.fit")
    parameters = {"librarySelection": LibrarySelection.create([item]).to_dict(), "futureField": "keep"}
    app = SimpleNamespace(
        project_path=None, root=None, project_name=Value("picked"), data_root=Value(str(tmp_path)),
        output_root=Value(str(tmp_path / "out")), current_stage="media", workflow_preset=Value(""),
        use_flat=Value(True), _current_equipment=lambda: None, last_output=None, approved_stages=set(),
        project_parameters=parameters, group_list=SimpleNamespace(curselection=lambda: ()),
        target_combo=SimpleNamespace(current=lambda: 0), cosmic_ray_clean=Value(True),
        combine_method=Value("mean"), status=Value(),
    )
    path = tmp_path / "saved.astroproj"
    UvexStudioApp.save_project(app, path)
    restored = AstroProject.load(path)
    assert restored.parameters["librarySelection"] == parameters["librarySelection"]
    assert restored.parameters["futureField"] == "keep"


def test_studio_load_sets_selection_before_scan_without_directory_reimport(tmp_path):
    from uvex_reduce.studio import UvexStudioApp
    item = frame(tmp_path, "keep.fit")
    path = export_library_project(tmp_path / "picked.astroproj", name="picked", data_root=tmp_path,
                                  output_root=tmp_path / "out", frames=[item])
    app = SimpleNamespace(
        project_name=Value(), data_root=Value(), output_root=Value(), workflow_preset=Value(),
        use_flat=Value(), cosmic_ray_clean=Value(), combine_method=Value(),
        stage_frames={"media": None}, status=Value(), switch_stage=lambda stage: None,
    )
    scanned = []
    app.scan = lambda: scanned.append(app.library_selection)
    UvexStudioApp.load_project(app, path)
    assert len(scanned) == 1
    assert scanned[0].paths("science") == [item["path"]]
    assert app.project_parameters["librarySelection"]["frames"] == [item]


def test_gui_project_argument_only_loads_project_not_processing(tmp_path, monkeypatch):
    import uvex_reduce.gui as gui
    import uvex_reduce.studio as studio
    loaded = []
    loops = []
    scan_modes = []
    monkeypatch.setattr(gui.tk, "Tk", lambda: SimpleNamespace(mainloop=lambda: loops.append(True)))
    def create_app(root, *, scan_on_start):
        scan_modes.append(scan_on_start)
        return SimpleNamespace(load_project=loaded.append)

    monkeypatch.setattr(studio, "UvexStudioApp", create_app)
    path = tmp_path / "选片.astroproj"
    assert gui.main(["--project", str(path)]) == 0
    assert loaded == [path]
    assert loops == [True]
    assert scan_modes == [False]


def test_gui_ordinary_start_keeps_initial_scan(monkeypatch):
    import uvex_reduce.gui as gui
    import uvex_reduce.studio as studio

    modes = []
    monkeypatch.setattr(gui.tk, "Tk", lambda: SimpleNamespace(mainloop=lambda: None))
    monkeypatch.setattr(studio, "UvexStudioApp", lambda root, *, scan_on_start: modes.append(scan_on_start))
    assert gui.main([]) == 0
    assert modes == [True]


@pytest.mark.desktop_ui
def test_explicit_project_tk_skips_environment_scan_and_shows_science_only(tmp_path):
    selected = frame(tmp_path, "science.fit", target="HD 123")
    frame(tmp_path, "unselected.fit")
    path = export_library_project(tmp_path / "picked.astroproj", name="picked", data_root=tmp_path,
                                  output_root=tmp_path / "out", frames=[selected])
    # Isolate the full Studio/Tk image-viewer lifetime from the library UI's
    # module-scoped Tk fixture (and never interact with an existing window).
    code = """
import sys
from pathlib import Path
import tkinter as tk
import uvex_reduce.gui as gui
import uvex_reduce.studio as studio
project, base = Path(sys.argv[1]), Path(sys.argv[2])
gui.DEFAULT_DATA_ROOT = str(base)
studio.KNOWN_3C273_OUTPUT = base / 'absent-results'
studio.EQUIPMENT_PRESET_STORE = base / 'absent-presets.json'
def forbidden(*args, **kwargs):
    raise AssertionError('Unexpected directory discovery or processing')
gui.discover_standard_groups = forbidden
gui.discover_science_groups = forbidden
gui.run_full_workflow = forbidden
gui.ReductionPipeline.run = forbidden
root = tk.Tk()
root.withdraw()
try:
    app = studio.UvexStudioApp(root, scan_on_start=False)
    assert app.groups == app.science_groups == []
    app.load_project(project)
    assert app.groups == []
    assert len(app.science_groups) == 1
    assert app.science_groups[0].files == [base / 'science.fit']
    assert app.science_groups[0].role == 'science'
    assert len(app.media_tree.get_children()) == 1
    assert 'standard' in app.details.cget('text')
finally:
    root.destroy()
"""
    result = subprocess.run([sys.executable, "-c", code, str(path), str(tmp_path)],
                            capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stderr
