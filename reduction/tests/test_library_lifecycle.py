from __future__ import annotations

import json
import os
import stat
from pathlib import Path
from types import SimpleNamespace

import pytest

from uvex_reduce.library_lifecycle import (
    FileProtection,
    build_lifecycle_plan,
    classify_file,
    save_lifecycle_plan,
    validate_archive_destination,
)


def known_idle(**kwargs) -> FileProtection:
    return FileProtection(
        **dict(
            {"writing": False, "reference_count": 1, "shared_calibration": False,
             "runtime_model_reference": False},
            **kwargs,
        )
    )


@pytest.mark.parametrize(
    ("name", "category"),
    [("original.FITS", "raw"), ("image.fit.gz", "raw"), ("preview.png", "preview"),
     ("frames.jsonl", "log"), ("manifest.json", "log"), ("spectrum.ecsv", "result"),
     ("session.astroproj", "result"), ("unclassified.bin", "unknown")],
)
def test_extension_classification(name, category):
    assert classify_file(name) == category


def test_plan_has_only_explicit_files_and_does_not_change_them(tmp_path):
    raw = tmp_path / "raw.fit"
    raw.write_bytes(b"immutable")
    ignored = tmp_path / "not-selected.fit"
    ignored.write_bytes(b"not counted")
    before = {file.name: file.read_bytes() for file in tmp_path.iterdir()}

    plan = build_lifecycle_plan([raw, raw, raw.parent / "." / raw.name])

    assert len(plan.items) == 1
    assert plan.selected_bytes == 9
    assert plan.bytes_released == 0
    assert plan.dry_run
    assert plan.items[0].status == "needs_review"
    assert "writer_state_unverified" in plan.items[0].reasons
    assert "reference_count_unverified" in plan.items[0].reasons
    assert {file.name: file.read_bytes() for file in tmp_path.iterdir()} == before


def test_risks_are_protected_even_when_preview_is_rebuildable(tmp_path):
    paths = [tmp_path / f"preview-{i}.png" for i in range(5)]
    for path in paths:
        path.write_bytes(b"png")
    protections = {
        paths[0]: known_idle(writing=True),
        paths[1]: known_idle(reference_count=2),
        paths[2]: known_idle(shared_calibration=True),
        paths[3]: known_idle(runtime_model_reference=True),
        paths[4]: known_idle(additional_reasons=("unsettled_motion_responsibility",)),
    }
    plan = build_lifecycle_plan(paths, protections=protections)
    assert [item.status for item in plan.items] == ["protected"] * 5
    for item, reason in zip(plan.items, ["active_writer", "multiple_references",
                                       "shared_calibration", "runtime_model_reference",
                                       "unsettled_motion_responsibility"]):
        assert reason in item.reasons


def test_known_idle_preview_is_candidate_not_permission_to_delete(tmp_path):
    preview = tmp_path / "preview.png"
    preview.write_bytes(b"pixels")
    raw = tmp_path / "science.fit"
    raw.write_bytes(b"raw")
    plan = build_lifecycle_plan([preview, raw], protections={
        preview: known_idle(), raw: known_idle(),
    })
    assert plan.items[0].status == "candidate"
    assert plan.items[1].status == "needs_review"
    assert plan.bytes_released == 0
    assert preview.exists() and raw.exists()


def test_hardlinked_file_is_counted_once_and_both_references_are_protected(tmp_path):
    first = tmp_path / "first.png"
    first.write_bytes(b"shared pixels")
    second = tmp_path / "second.png"
    os.link(first, second)
    plan = build_lifecycle_plan([first, second], protections={
        first: known_idle(), second: known_idle(),
    })
    assert len(plan.items) == 2
    assert plan.selected_bytes == len(b"shared pixels")
    assert all(item.status == "protected" for item in plan.items)
    assert all("same_physical_file_multiple_paths" in item.reasons for item in plan.items)
    assert first.read_bytes() == second.read_bytes() == b"shared pixels"


def test_manifest_category_override_and_invalid_metadata(tmp_path):
    spectrum = tmp_path / "spectrum.fits"
    spectrum.write_bytes(b"spectrum")
    plan = build_lifecycle_plan([spectrum], protections={
        spectrum: known_idle(category="result"),
    })
    assert plan.items[0].category == "result"
    with pytest.raises(ValueError, match="category"):
        build_lifecycle_plan([spectrum], protections={spectrum: FileProtection(category="safe")})
    with pytest.raises(ValueError, match="non-negative"):
        build_lifecycle_plan([spectrum], protections={spectrum: FileProtection(reference_count=-1)})
    with pytest.raises(ValueError, match="boolean"):
        build_lifecycle_plan([spectrum], protections={spectrum: FileProtection(writing="false")})


def test_directories_are_not_expanded_and_missing_files_need_review(tmp_path):
    directory = tmp_path / "night"
    directory.mkdir()
    (directory / "large.fit").write_bytes(b"unchanged")
    missing = tmp_path / "offline.fit"
    plan = build_lifecycle_plan([directory, missing])
    assert plan.selected_bytes == 0
    assert len(plan.items) == 2
    assert plan.items[0].status == "protected"
    assert "directory_requires_explicit_file_selection" in plan.items[0].reasons
    assert "missing_or_offline" in plan.items[1].reasons


def test_archive_destination_validation_does_not_create_anything(tmp_path):
    source = tmp_path / "night" / "science.fit"
    source.parent.mkdir()
    source.write_bytes(b"raw")
    inside = source.parent / "archive"
    assert not validate_archive_destination([source], source).allowed
    assert not validate_archive_destination([source], source.parent).allowed
    assert not validate_archive_destination([source], inside).allowed
    archive = tmp_path / "external-archive"
    assert validate_archive_destination([source], archive).allowed
    assert not archive.exists() and not inside.exists()
    assert not validate_archive_destination([], archive).allowed
    assert not validate_archive_destination([source], tmp_path / "night").allowed


def test_save_json_is_explicit_atomic_and_does_not_overwrite(tmp_path):
    source = tmp_path / "manifest.json"
    source.write_text('{"original": true}', encoding="utf-8")
    plan = build_lifecycle_plan([source])
    assert len(list(tmp_path.iterdir())) == 1
    with pytest.raises(ValueError, match="source"):
        save_lifecycle_plan(plan, source)
    target = tmp_path / "review.json"
    assert save_lifecycle_plan(plan, target) == target
    document = json.loads(target.read_text(encoding="utf-8"))
    assert document["dry_run"] is True
    assert document["bytes_released"] == 0
    assert document["selected_bytes"] == source.stat().st_size
    with pytest.raises(FileExistsError):
        save_lifecycle_plan(plan, target)
    assert source.read_text(encoding="utf-8") == '{"original": true}'
    assert not list(tmp_path.glob(".uvex-library-plan-*"))


def test_save_does_not_write_inside_selected_directory(tmp_path):
    source = tmp_path / "night"
    source.mkdir()
    plan = build_lifecycle_plan([source])
    with pytest.raises(ValueError, match="inside"):
        save_lifecycle_plan(plan, source / "review.json")
    assert list(source.iterdir()) == []


def test_export_collision_during_atomic_publish_preserves_existing_file(tmp_path, monkeypatch):
    plan = build_lifecycle_plan([])
    target = tmp_path / "review.json"
    real_link = os.link

    def collide(source, destination):
        Path(destination).write_text("another writer", encoding="utf-8")
        real_link(source, destination)

    monkeypatch.setattr(os, "link", collide)
    with pytest.raises(FileExistsError):
        save_lifecycle_plan(plan, target)
    assert target.read_text(encoding="utf-8") == "another writer"
    assert not list(tmp_path.glob(".uvex-library-plan-*"))


def test_link_sources_and_destinations_are_not_followed(tmp_path):
    real = tmp_path / "real"
    real.mkdir()
    raw = real / "science.fit"
    raw.write_bytes(b"raw")
    link = tmp_path / "alias"
    try:
        link.symlink_to(real, target_is_directory=True)
    except OSError:
        pytest.skip("Creating a symlink is not permitted on this host")
    selected = link / "science.fit"
    plan = build_lifecycle_plan([selected])
    assert plan.items[0].status == "protected"
    assert plan.selected_bytes == 0
    assert "symbolic_link_or_reparse_point" in plan.items[0].reasons
    assert not validate_archive_destination([raw], link / "archive").allowed
    with pytest.raises(ValueError, match="link"):
        save_lifecycle_plan(plan, link / "review.json")
    assert list(real.iterdir()) == [raw]


def test_windows_reparse_ancestor_is_protected_without_following_it(tmp_path, monkeypatch):
    root = tmp_path / "junction"
    root.mkdir()
    source = root / "science.fit"
    source.write_bytes(b"not touched")
    original_lstat = Path.lstat

    def with_reparse_attribute(path, *args, **kwargs):
        if os.path.normcase(str(path)) == os.path.normcase(str(root)):
            return SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=0x400)
        return original_lstat(path, *args, **kwargs)

    monkeypatch.setattr(Path, "lstat", with_reparse_attribute)
    plan = build_lifecycle_plan([source])
    assert plan.items[0].status == "protected"
    assert plan.selected_bytes == 0
    assert "symbolic_link_or_reparse_point" in plan.items[0].reasons
    with pytest.raises(ValueError, match="reparse"):
        save_lifecycle_plan(plan, root / "review.json")
