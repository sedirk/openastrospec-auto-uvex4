from __future__ import annotations

from pathlib import Path

import pytest

from uvex_reduce import library_file_state


@pytest.fixture
def explorer(monkeypatch):
    calls = []
    monkeypatch.setattr(library_file_state, "_WINDOWS", True)
    monkeypatch.setattr(
        library_file_state.subprocess, "Popen",
        lambda *args, **kwargs: calls.append(("select", args, kwargs)),
    )
    monkeypatch.setattr(
        library_file_state.os, "startfile",
        lambda *args, **kwargs: calls.append(("folder", args, kwargs)),
        raising=False,
    )
    return calls


def test_rejection_is_catalogue_only_and_source_remains_exact(tmp_path):
    source = tmp_path / "science.fit"
    original = b"immutable raw observation"
    source.write_bytes(original)
    before = source.stat()
    state = library_file_state.inspect_source_file(source, "reject")
    assert state.presence == "present_file"
    assert state.presence_label == "原位存在"
    assert state.review_label == "不采用（仅资料库标记）"
    assert "不删除、不移动、不移入系统回收站" in state.detail
    assert state.bytes_released == 0
    assert source.read_bytes() == original
    assert source.stat().st_mtime_ns == before.st_mtime_ns


@pytest.mark.parametrize("review", ["", "unreviewed", "pick", "reject", "future-state"])
def test_missing_and_operator_review_are_independent(tmp_path, review):
    state = library_file_state.inspect_source_file(tmp_path / "offline" / "science.fit", review)
    assert state.presence == "missing"
    assert state.bytes_released == 0
    assert "无法据此认定文件已删除、已回收或已归档" in state.detail


def test_permission_error_is_unverified_not_deleted(tmp_path, monkeypatch):
    def denied(_self):
        raise PermissionError("denied")

    monkeypatch.setattr(Path, "stat", denied)
    state = library_file_state.inspect_source_file(tmp_path / "science.fit", "reject")
    assert state.presence == "unverified"
    assert "不能认定文件已删除" in state.detail
    assert state.bytes_released == 0


def test_reveal_exact_file_uses_argument_list_never_file_association(tmp_path, explorer):
    source = tmp_path / "space & 中文, name.fit"
    source.write_bytes(b"raw")
    result = library_file_state.reveal_source_file(source)
    assert result.requested_path == str(source)
    assert result.opened_path == str(tmp_path)
    assert result.selected
    assert explorer == [("select", (["explorer.exe", "/select,", str(source)],), {})]
    assert source.read_bytes() == b"raw"


def test_reveal_directory_uses_only_verified_directory(tmp_path, explorer):
    result = library_file_state.reveal_source_file(tmp_path)
    assert not result.selected
    assert result.opened_path == str(tmp_path)
    assert explorer == [("folder", (str(tmp_path),), {})]


def test_missing_file_opens_nearest_existing_parent_with_explicit_notice(tmp_path, explorer):
    existing = tmp_path / "observations"
    existing.mkdir()
    source = existing / "offline-run" / "science.fit"
    result = library_file_state.reveal_source_file(source)
    assert not result.selected
    assert result.requested_path == str(source)
    assert result.opened_path == str(existing)
    assert "缺失或离线" in result.notice
    assert "这不代表原文件已删除或在回收站" in result.notice
    assert explorer == [("folder", (str(existing),), {})]
    assert not source.exists()
    assert not source.parent.exists()


def test_reveal_unreadable_path_does_not_claim_deleted(tmp_path, explorer, monkeypatch):
    source = tmp_path / "science.fit"
    original_stat = Path.stat

    def guarded_stat(self, *args, **kwargs):
        if self == source:
            raise PermissionError("denied")
        return original_stat(self, *args, **kwargs)

    monkeypatch.setattr(Path, "stat", guarded_stat)
    result = library_file_state.reveal_source_file(source)
    assert result.opened_path == str(tmp_path)
    assert "状态无法确认" in result.notice
    assert not result.selected
    assert explorer == [("folder", (str(tmp_path),), {})]


def test_launch_failure_is_propagated_not_reported_as_opened(tmp_path, explorer, monkeypatch):
    def failed(*_args, **_kwargs):
        raise OSError("Explorer unavailable")

    monkeypatch.setattr(library_file_state.os, "startfile", failed)
    with pytest.raises(OSError, match="Explorer unavailable"):
        library_file_state.reveal_source_file(tmp_path)


@pytest.mark.parametrize("source", ["", "   ", "bad\0path"])
def test_no_implicit_current_directory_or_invalid_path(source, explorer):
    with pytest.raises(ValueError, match="明确"):
        library_file_state.reveal_source_file(source)
    assert not explorer


def test_unsupported_os_does_not_launch(tmp_path, explorer, monkeypatch):
    monkeypatch.setattr(library_file_state, "_WINDOWS", False)
    with pytest.raises(OSError, match="仅支持 Windows"):
        library_file_state.reveal_source_file(tmp_path)
    assert not explorer


@pytest.mark.parametrize(("action", "status", "label"), [
    ("recycle", "recycled", "已移入系统回收站"),
    ("permanent", "deleted", "已永久删除"),
    ("mark", "marked", "仅标记不采用"),
    ("recycle", "uncertain", "结果未确认"),
])
def test_lifecycle_receipt_is_distinct_from_current_presence(tmp_path, action, status, label):
    path = tmp_path / "preview.png"
    receipt = {"action": action, "status": status}
    state = library_file_state.inspect_source_file(path, "reject", lifecycle=receipt)
    assert state.presence == "missing"
    assert label in state.lifecycle_label
    assert state.bytes_released == 0  # A historical receipt is not a new space measurement.
    path.write_bytes(b"restored or regenerated")
    state = library_file_state.inspect_source_file(path, "reject", lifecycle=receipt)
    assert state.presence == "present_file"
    assert label in state.lifecycle_label
    if status in {"recycled", "deleted"}:
        assert "不能将旧操作套用于当前文件" in state.detail


def test_unknown_receipt_does_not_imply_success(tmp_path):
    state = library_file_state.inspect_source_file(
        tmp_path / "preview.png", lifecycle={"action": "permanent", "status": "future-status"},
    )
    assert "结果未知" in state.lifecycle_label
    assert "已永久删除" not in state.detail
