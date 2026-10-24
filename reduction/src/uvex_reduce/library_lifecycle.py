"""Read-only, explicit-file lifecycle plans for the observation library.

This module deliberately has no archive, move or deletion executor. A plan is a
review aid, not permission to remove files. The only write is an explicitly
requested JSON export, published atomically without replacing an existing file.
"""

from __future__ import annotations

import json
import os
import stat
import tempfile
from dataclasses import asdict, dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping


CATEGORIES = frozenset({"raw", "preview", "log", "result", "unknown"})


@dataclass(frozen=True)
class FileProtection:
    """Facts supplied by the index; None means unverified, never safe to delete.

    Category overrides should come from a manifest role, not a quality verdict.
    ``additional_reasons`` can retain unresolved stop/motion responsibilities or
    other protected dependencies discovered by the caller.
    """

    writing: bool | None = None
    reference_count: int | None = None
    shared_calibration: bool | None = None
    runtime_model_reference: bool | None = None
    category: str | None = None
    additional_reasons: tuple[str, ...] = ()


@dataclass(frozen=True)
class LifecycleItem:
    path: str
    category: str
    size_bytes: int
    status: str
    reasons: tuple[str, ...]


@dataclass(frozen=True)
class ArchiveDestinationValidation:
    path: str
    allowed: bool
    reasons: tuple[str, ...]


@dataclass(frozen=True)
class LifecyclePlan:
    created_utc: str
    items: tuple[LifecycleItem, ...]
    selected_bytes: int
    archive_destination: ArchiveDestinationValidation | None = None
    schema_version: int = 1
    dry_run: bool = True
    bytes_released: int = 0
    notice: str = "Review only: no files copied, moved or deleted; no space released."

    def to_dict(self) -> dict:
        return asdict(self)


def classify_file(path: str | Path) -> str:
    """Conservative extension classification, overridable with manifest facts."""
    name = Path(path).name.casefold()
    if name.endswith((".fit", ".fits", ".fts", ".fit.gz", ".fits.gz", ".fts.gz")):
        return "raw"
    if Path(name).suffix in {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}:
        return "preview"
    if Path(name).suffix in {".json", ".jsonl", ".log", ".txt"}:
        return "log"
    if Path(name).suffix in {".csv", ".ecsv", ".pdf", ".svg", ".astroproj"}:
        return "result"
    return "unknown"


def _absolute(path: str | Path) -> Path:
    # Do not resolve source symlinks: sources are indexed in place, not followed.
    return Path(os.path.abspath(os.fspath(path)))


def _key(path: str | Path) -> str:
    return os.path.normcase(str(_absolute(path)))


def _is_within(path: Path, root: Path) -> bool:
    try:
        return os.path.commonpath([_key(path), _key(root)]) == _key(root)
    except ValueError:  # Different drives on Windows.
        return False


def _is_link(info: os.stat_result) -> bool:
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0)
        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    )


def _link_or_unreadable(path: Path) -> str | None:
    # lstat every existing ancestor, so a junction cannot silently widen scope.
    for part in reversed((path, *path.parents)):
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        except OSError:
            return "path_access_unverified"
        if _is_link(info):
            return "symbolic_link_or_reparse_point"
    return None


def _protection_reasons(facts: FileProtection) -> tuple[list[str], bool]:
    if facts.reference_count is not None:
        if type(facts.reference_count) is not int or facts.reference_count < 0:
            raise ValueError("reference_count must be a non-negative integer or None")
    reasons: list[str] = []
    protected = False
    for value, risk, unknown in (
        (facts.writing, "active_writer", "writer_state_unverified"),
        (facts.shared_calibration, "shared_calibration", "calibration_references_unverified"),
        (
            facts.runtime_model_reference,
            "runtime_model_reference",
            "runtime_model_references_unverified",
        ),
    ):
        if value is not None and type(value) is not bool:
            raise ValueError("Protection flags must be boolean or None")
        if value is True:
            reasons.append(risk)
            protected = True
        elif value is None:
            reasons.append(unknown)
    if facts.reference_count is None:
        reasons.append("reference_count_unverified")
    elif facts.reference_count > 1:
        reasons.append("multiple_references")
        protected = True
    if facts.additional_reasons:
        reasons.extend(facts.additional_reasons)
        protected = True
    return reasons, protected


def validate_archive_destination(
    source_paths: Iterable[str | Path], destination: str | Path
) -> ArchiveDestinationValidation:
    """Validate only; selected files' parent directories are their source trees.

    A destination under any source tree (or an identical path) is rejected. No
    directory is created and no space/archival integrity claim is made.
    """
    target = _absolute(destination)
    reasons: list[str] = []
    issue = _link_or_unreadable(target)
    if issue:
        reasons.append(issue)
    try:
        if target.exists() and not target.is_dir():
            reasons.append("destination_is_not_directory")
    except OSError:
        reasons.append("destination_access_unverified")
    sources = tuple(_absolute(path) for path in source_paths)
    if not sources:
        reasons.append("no_explicit_sources")
    for source in sources:
        try:
            source_info = source.lstat()
            root = source if stat.S_ISDIR(source_info.st_mode) else source.parent
        except OSError:
            root = source.parent
        if _key(target) == _key(source):
            reasons.append("destination_equals_source")
        elif _is_within(target, root):
            reasons.append("destination_inside_source_tree")
    return ArchiveDestinationValidation(str(target), not reasons, tuple(dict.fromkeys(reasons)))


def build_lifecycle_plan(
    selected_paths: Iterable[str | Path],
    *,
    protections: Mapping[str | Path, FileProtection] | None = None,
    archive_destination: str | Path | None = None,
) -> LifecyclePlan:
    """Stat only the explicit selection; never recurse or hash/read file contents.

    ``candidate`` means a known unreferenced, inactive preview may be considered
    in a future cleanup flow. It is not a deletion authorization. All raw,
    result, log and unknown files require review even with no known dependency.
    """
    facts_by_key = {_key(path): facts for path, facts in (protections or {}).items()}
    unique = dict.fromkeys(_key(path) for path in selected_paths)
    items: list[LifecycleItem] = []
    physical_files: dict[tuple[int, int] | str, tuple[int, list[int]]] = {}
    for key in unique:
        path = Path(key)
        facts = facts_by_key.get(key, FileProtection())
        category = classify_file(path) if facts.category is None else facts.category
        if category not in CATEGORIES:
            raise ValueError(f"Unsupported lifecycle category: {category}")
        reasons, protected = _protection_reasons(facts)
        size = 0
        issue = _link_or_unreadable(path)
        if issue:
            reasons.append(issue)
            protected = True
        else:
            try:
                info = path.lstat()
                if stat.S_ISREG(info.st_mode):
                    size = info.st_size
                    identity = (info.st_dev, info.st_ino) if info.st_ino else key
                    physical_files.setdefault(identity, (size, []))[1].append(len(items))
                elif stat.S_ISDIR(info.st_mode):
                    reasons.append("directory_requires_explicit_file_selection")
                    protected = True
                else:
                    reasons.append("not_regular_file")
                    protected = True
            except FileNotFoundError:
                reasons.append("missing_or_offline")
            except OSError:
                reasons.append("file_access_unverified")
        if category != "preview":
            reasons.append("explicit_retention_review_required")
        status = "protected" if protected else "needs_review" if reasons else "candidate"
        items.append(LifecycleItem(str(path), category, size, status, tuple(dict.fromkeys(reasons))))
    for _, indexes in physical_files.values():
        if len(indexes) > 1:
            for index in indexes:
                items[index] = replace(
                    items[index], status="protected",
                    reasons=(*items[index].reasons, "same_physical_file_multiple_paths"),
                )
    validation = (
        validate_archive_destination((item.path for item in items), archive_destination)
        if archive_destination is not None
        else None
    )
    return LifecyclePlan(
        datetime.now(timezone.utc).isoformat(),
        tuple(items),
        sum(size for size, _ in physical_files.values()),
        validation,
    )


def save_lifecycle_plan(plan: LifecyclePlan, destination: str | Path) -> Path:
    """Export JSON only when explicitly invoked; fail if destination exists.

    Atomic no-replace publication uses a same-directory hard link. Filesystems
    without this primitive fail safely; there is no overwrite fallback. Source
    directories and symlink/reparse paths cannot be used as export destinations.
    """
    target = _absolute(destination)
    if target.suffix.casefold() != ".json":
        raise ValueError("Lifecycle plans must be saved as .json")
    if _link_or_unreadable(target):
        raise ValueError("Export path contains a link, reparse point or inaccessible component")
    for item in plan.items:
        source = Path(item.path)
        if _key(target) == _key(source):
            raise ValueError("Cannot overwrite a selected source")
        if source.is_dir() and _is_within(target, source):
            raise ValueError("Cannot save a plan inside a selected source directory")
    if not target.parent.is_dir():
        raise ValueError("Export parent directory must already exist")
    if target.exists():
        raise FileExistsError(target)
    descriptor, temporary = tempfile.mkstemp(prefix=".uvex-library-plan-", dir=target.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(plan.to_dict(), stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, target)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return target
