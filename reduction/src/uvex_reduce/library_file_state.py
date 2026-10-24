"""Explicit source-file facts and read-only Windows Explorer navigation.

Operator review is catalogue metadata, not a filesystem lifecycle event.  In
particular, an absent source cannot tell us whether a disk is offline, a file
was moved, or something outside this application deleted it.  This module has
no deletion, recycling, moving, or archive executor.
"""

from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import stat
import subprocess


_WINDOWS = os.name == "nt"
REJECTION_NOTICE = (
    "不采用仅改变资料库标记，不删除、不移动、不移入系统回收站；释放空间 0 B。"
)


@dataclass(frozen=True)
class SourceFileState:
    path: str
    presence: str
    review: str
    presence_label: str
    review_label: str
    detail: str
    bytes_released: int = 0
    lifecycle_label: str = "无资料库物理清理回执"


@dataclass(frozen=True)
class ExplorerResult:
    """The navigation requested, not proof that Explorer displayed the item."""

    requested_path: str
    opened_path: str
    selected: bool
    notice: str


def _absolute(path: str | Path) -> Path:
    text = os.fspath(path)
    if not isinstance(text, str) or not text.strip() or "\0" in text:
        raise ValueError("需要明确的源文件或目录路径。")
    # Preserve the indexed spelling and do not resolve or rewrite source links.
    return Path(os.path.abspath(text))


def inspect_source_file(
    path: str | Path, review: str = "", *, lifecycle: dict | None = None,
) -> SourceFileState:
    """Read current path presence independently of the operator's review mark."""
    source = _absolute(path)
    try:
        info = source.stat()
    except (FileNotFoundError, NotADirectoryError):
        presence = "missing"
        label = "当前路径不可见（缺失或离线）"
        explanation = "无法据此认定文件已删除、已回收或已归档。"
    except OSError:
        presence = "unverified"
        label = "当前路径状态无法确认"
        explanation = "路径读取失败；不能认定文件已删除或位于回收站。"
    else:
        if stat.S_ISREG(info.st_mode):
            presence, label = "present_file", "原位存在"
        elif stat.S_ISDIR(info.st_mode):
            presence, label = "present_directory", "目录存在"
        else:
            presence, label = "present_other", "路径存在（非普通文件）"
        explanation = "这是当前路径读回结果，不代表文件完整性或光谱质量已验证。"

    review_label = {
        "reject": "不采用（仅资料库标记）",
        "pick": "采用（仅资料库标记）",
        "unreviewed": "尚未筛选",
        "": "尚未筛选",
    }.get(review, "筛选标记未知")
    notice = REJECTION_NOTICE if review == "reject" else "筛选标记不改变原文件；释放空间 0 B。"
    lifecycle_label = "无资料库物理清理回执"
    if lifecycle:
        action, result = lifecycle.get("action"), lifecycle.get("status")
        if action == "recycle" and result == "recycled":
            lifecycle_label = "上次操作：已移入系统回收站（当时确认）"
            explanation += "该回执不证明文件现在仍在回收站；恢复或清空可能发生在资料库之外。"
        elif action == "permanent" and result == "deleted":
            lifecycle_label = "上次操作：已永久删除（未进入回收站）"
        elif action == "mark" and result == "marked":
            lifecycle_label = "上次操作：仅标记不采用，未改文件，释放 0 B"
        elif result in {"uncertain", "failed", "cancelled", "intent"}:
            lifecycle_label = "上次清理未完成或结果未确认；查看操作回执"
        else:
            lifecycle_label = "已有清理记录，但结果未知；查看操作回执"
        if presence == "present_file" and result in {"deleted", "recycled"}:
            explanation += "当前路径又存在文件，可能已恢复或重新生成，不能将旧操作套用于当前文件。"
    return SourceFileState(
        str(source), presence, review, label, review_label,
        f"{label} · {review_label} · {lifecycle_label}。{explanation}{notice}",
        lifecycle_label=lifecycle_label,
    )


def reveal_source_file(path: str | Path) -> ExplorerResult:
    """Request Explorer selection, or show an explicitly labelled parent fallback.

    A missing or unreadable source is never passed to its file association.
    Existing files use Explorer's exact-file selection; existing directories
    use ``os.startfile`` only after a directory check.  Neither operation uses
    a shell command string.  Failure to launch is propagated for the UI to show.
    """
    source = _absolute(path)
    if not _WINDOWS:
        raise OSError("Windows 文件管理器定位仅支持 Windows；可复制源路径手动查看。")
    state = inspect_source_file(source)
    if state.presence == "present_file":
        subprocess.Popen(["explorer.exe", "/select,", str(source)])
        return ExplorerResult(
            str(source), str(source.parent), True,
            "已请求 Windows 文件管理器选中原文件；未删除、移动或改写文件。",
        )
    if state.presence == "present_directory":
        os.startfile(str(source))
        return ExplorerResult(
            str(source), str(source), False,
            "已请求 Windows 文件管理器打开原目录；未改变目录内容。",
        )

    for parent in source.parents:
        try:
            if not parent.is_dir():
                continue
        except OSError:
            continue
        os.startfile(str(parent))
        return ExplorerResult(
            str(source), str(parent), False,
            f"{state.presence_label}；已请求打开最近存在的上级目录：{parent}。"
            "这不代表原文件已删除或在回收站；未改变任何文件。",
        )
    raise FileNotFoundError(f"源路径及可访问的上级目录均不可用：{source}")
