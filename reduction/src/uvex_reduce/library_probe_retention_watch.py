"""Dependency-aware change checks; unrelated active runs do not stop cleanup.

Reuse the existing Windows overflow/reparse/locked-handle guards. Only complete,
parseable metadata with no selected-file or ancestor/glob reference is unrelated.
Unknown/partial metadata defers cleanup, never the observation.
"""
import json
import os
from pathlib import Path
import stat
import tomllib
import threading
from urllib.parse import unquote, urlsplit

from .library_catalog import MAX_METADATA_BYTES, _canonical
from .library_cleanup_plan import _reparse
from .library_cleanup_watch import WindowsMetadataWatch

METADATA_SUFFIXES = {".json", ".jsonl", ".astroproj", ".toml"}


class CandidatePaths(frozenset):
    def __init__(self, paths):
        # Every exact candidate and ancestor can be tested in O(1), rather than
        # N commonpath calls for every path in a continuously growing manifest.
        self.ancestors = {str(parent) for path in self for parent in (Path(path), *Path(path).parents)}


def mentions_inputs(value, base, candidates, key=""):
    if not isinstance(candidates, CandidatePaths):
        candidates = CandidatePaths(candidates)
    if isinstance(value, dict):
        return any(mentions_inputs(child, base, candidates, str(name)) for name, child in value.items())
    if isinstance(value, list):
        return any(mentions_inputs(child, base, candidates, key) for child in value)
    if not isinstance(value, str) or not value or "\x00" in value:
        return False
    if value.lower().startswith("file:"):
        uri = urlsplit(value)
        value = unquote(uri.path)
        if uri.netloc and uri.netloc.lower() != "localhost":
            value = "//" + uri.netloc + value
        elif os.name == "nt" and len(value) > 2 and value[0] == "/" and value[2] == ":":
            value = value[1:]
    elif "://" in value:
        return False
    if value.startswith(("\\\\?\\", "\\\\.\\")):
        tail = value[4:]
        if len(tail) > 2 and tail[1:3] in {":\\", ":/"}:
            value = tail
        elif tail.lower().startswith("unc\\"):
            value = "\\\\" + tail[4:]
        else:
            # Windows USB/COM/device identifiers are not disk input wildcards.
            return False
    # Every path-like string, not only known field names. A directory or legacy
    # wildcard matching an ancestor is protected as well as an exact input.
    root_key = key.lower().replace("_", "") in {"dataroot", "rawroot", "inputroot", "calibrationroot", "directory", "rootdir"}
    if not root_key and not os.path.isabs(value) and not any(c in value.lower() for c in ("/", "\\", ".fits", ".fit", "*", "?", "[")):
        return False
    prefix = value
    while any(c in prefix for c in ("*", "?", "[")):
        parent = str(Path(prefix).parent)
        if parent == prefix:
            return True
        prefix = parent
    path = _canonical(prefix if os.path.isabs(prefix) else base / prefix)
    return path in candidates.ancestors


def unrelated_metadata(path, candidates):
    if not isinstance(candidates, CandidatePaths):
        candidates = CandidatePaths(candidates)
    try:
        info = path.lstat()
        if _reparse(info) or not stat.S_ISREG(info.st_mode) or info.st_size > MAX_METADATA_BYTES:
            return False
        content = path.read_text(encoding="utf-8-sig")
        if path.suffix.lower() == ".toml":
            values = [tomllib.loads(content)]
        elif path.suffix.lower() == ".jsonl":
            values = [json.loads(line) for line in content.splitlines() if line.strip()]
        else:
            values = [json.loads(content)]
        after = path.stat()
        if (info.st_ino, info.st_size, info.st_mtime_ns) != (after.st_ino, after.st_size, after.st_mtime_ns):
            return False
        return not any(mentions_inputs(value, path.parent, candidates) for value in values)
    except (OSError, UnicodeError, ValueError):
        return False


class ProbeMetadataWatch(WindowsMetadataWatch):
    def __init__(self, roots, *, candidates, guard_paths=(), **kwargs):
        super().__init__(roots, **kwargs)
        self.candidates = CandidatePaths(_canonical(p) for p in candidates)
        self.guard_paths = frozenset(_canonical(p) for p in guard_paths)
        self._drain_lock = threading.RLock()
        self._stop = threading.Event()
        self._pump = None
        self._background = False
        self._pending_metadata = set()

    def __enter__(self):
        super().__enter__()
        # Full dependency revalidation may take minutes on a historical library.
        # Drain continuously, not only just before deletion; the same overflow
        # and dependency checks remain fatal to this batch, not to acquisition.
        self._pump = threading.Thread(target=self._consume, name="probe-dependency-watch", daemon=True)
        self._pump.start()
        return self

    def _consume(self):
        while not self._stop.wait(0.1):
            try:
                with self._drain_lock:
                    # A burst can fill 64 completions without losing events.
                    # Yield this background pass; the executor's immediate check
                    # still requires the original bounded, fully drained gate.
                    self._background = True
                    try:
                        super().check(_background_drain=True)
                    finally:
                        self._background = False
            except (OSError, ValueError):
                return  # Base watch retains the error for the executor.

    def check(self):
        with self._drain_lock:
            super().check()
            # A producer can notify immediately after opening/truncating JSON.
            # During the long read-only inventory, retain that uncertainty and
            # keep draining events. It must be resolved before ANY deletion.
            for path in tuple(self._pending_metadata):
                if path.exists() and not unrelated_metadata(path, self.candidates):
                    raise OSError(f"试拍依赖或不完整元数据尚未排除：{path}；延后清理。")
                self._pending_metadata.remove(path)
            # Recheck changes that arrived while those documents were read.
            super().check()

    def close(self):
        self._stop.set()
        if self._pump:
            self._pump.join(timeout=10)
            if self._pump.is_alive():
                raise OSError("目录核查尚未停止；保留监视资源，不继续删除。")
        with self._drain_lock:
            super().close()

    def changed(self, root, changes):
        stack = [Path(root) / name for name in changes]
        checked = 0
        while stack:
            path = stack.pop()
            checked += 1
            key = _canonical(path)
            if checked > 4096 or key in self.candidates or key in self.guard_paths:
                raise OSError("清理依据或选定试拍已变化；延后清理，不干扰观测。")
            try:
                info = path.lstat()
            except FileNotFoundError:
                # Removing an unrelated file cannot introduce a dependency;
                # rename destinations arrive as separate watched notifications.
                continue
            if _reparse(info):
                raise OSError("新重解析路径需要重新核查；延后清理。")
            if stat.S_ISDIR(info.st_mode):
                stack.extend(path.iterdir())
            elif path.suffix.lower() in METADATA_SUFFIXES and not unrelated_metadata(path, self.candidates):
                if self._background:
                    self._pending_metadata.add(path)
                else:
                    raise OSError(f"出现新的试拍依赖或未完整写入的元数据：{path}；延后清理。")
