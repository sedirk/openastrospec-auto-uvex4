from __future__ import annotations

import os
import struct
import time

import pytest

from uvex_reduce.library_cleanup_watch import WindowsMetadataWatch, notification_changes


def event(name, action=1):
    encoded = name.encode("utf-16-le")
    return struct.pack("<III", 0, action, len(encoded)) + encoded


def test_only_exact_expected_preview_removals_are_ignored(tmp_path):
    expected = {os.path.normcase(os.path.abspath(tmp_path / "sub" / "preview.png"))}
    assert notification_changes(event("sub/preview.png", 2), tmp_path, expected) == []
    assert notification_changes(event("sub/preview.png", 4), tmp_path, expected) == []
    assert notification_changes(event("sub/preview.png", 3), tmp_path, expected) == ["sub/preview.png"]
    assert notification_changes(event("sub/preview.png", 1), tmp_path, expected) == ["sub/preview.png"]
    assert notification_changes(event("sub/unrelated.png", 2), tmp_path, expected) == ["sub/unrelated.png"]
    assert notification_changes(event("sub/new-directory.png", 5), tmp_path, expected) == ["sub/new-directory.png"]
    assert notification_changes(event("sub/new.astroproj")) == ["sub/new.astroproj"]
    assert notification_changes(event("sub/model.json", 3)) == ["sub/model.json"]
    assert notification_changes(event("sub/new-directory")) == ["sub/new-directory"]
    assert notification_changes(event("sub/new-science.fit")) == ["sub/new-science.fit"]


@pytest.mark.parametrize("payload", [b"", b"tiny", struct.pack("<III", 0, 0, 0),
                                     struct.pack("<III", 0, 1, 100),
                                     struct.pack("<III", 1, 1, 2) + b"a\0"])
def test_malformed_or_overflow_never_means_no_changes(payload):
    with pytest.raises((OSError, ValueError)):
        notification_changes(payload)


@pytest.mark.skipif(os.name != "nt", reason="Windows directory notification integration")
def test_native_nested_unindexed_metadata_change_is_detected(tmp_path):
    nested = tmp_path / "old" / "nested"
    nested.mkdir(parents=True)
    with WindowsMetadataWatch([tmp_path]) as watch:
        watch.check()
        (nested / "new.astroproj").write_text('{"newDependency":true}')
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            try:
                watch.check()
            except OSError as error:
                assert "发生变化" in str(error)
                break
            time.sleep(0.01)
        else:
            pytest.fail("Recursive dependency change was missed")
        with pytest.raises(OSError, match="发生变化"):
            watch.check()
    assert (nested / "new.astroproj").exists()


@pytest.mark.skipif(os.name != "nt", reason="Windows directory notification integration")
def test_native_own_preview_cleanup_does_not_invalidate_watch(tmp_path):
    folder = tmp_path / "nested" / "preview"
    folder.mkdir(parents=True)
    path = folder / "fixture-preview.png"
    path.write_bytes(b"synthetic disposable preview")
    with WindowsMetadataWatch([tmp_path], expected_preview_paths=[path]) as watch:
        path.unlink()
        for _ in range(5):
            time.sleep(0.01)
            watch.check()
    with pytest.raises(OSError, match="已结束"):
        watch.check()


def test_only_existing_directory_modification_is_ignored(tmp_path):
    folder = tmp_path / "preview"
    folder.mkdir()
    assert notification_changes(event("preview", 3), tmp_path) == []
    assert notification_changes(event("preview", 1), tmp_path) == ["preview"]
    assert notification_changes(event("preview", 4), tmp_path) == ["preview"]
    assert notification_changes(event("vanished", 3), tmp_path) == ["vanished"]


@pytest.mark.skipif(os.name != "nt", reason="Windows directory notification integration")
def test_native_directory_named_png_cannot_hide_new_dependency(tmp_path):
    root = tmp_path / "observations"
    root.mkdir()
    incoming = tmp_path / "incoming.png"
    incoming.mkdir()
    (incoming / "new.astroproj").write_text('{"dependency":"existing-preview.png"}')
    with WindowsMetadataWatch([root]) as watch:
        incoming.rename(root / "incoming.png")
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            try:
                watch.check()
            except OSError as error:
                assert "发生变化" in str(error)
                break
            time.sleep(0.01)
        else:
            pytest.fail("Renamed directory with preview suffix hid a new dependency")
