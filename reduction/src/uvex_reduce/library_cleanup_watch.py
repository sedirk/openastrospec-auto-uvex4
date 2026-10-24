"""Read-only Windows change guard during a confirmed cleanup batch.

This is not an indexing service. It exists only for the lifetime of one batch,
so new/changed dependency records cannot hide in an unindexed nested directory
between the initial dependency walk and a subsequent file operation.
"""
from __future__ import annotations

import ctypes
import os
from pathlib import Path
import stat
import struct


_PENDING_ON_CLOSE = []  # Retain OS-owned buffers if an exceptional cancellation times out.


def _canonical(path):
    return os.path.normcase(os.path.abspath(path))


def notification_changes(data: bytes, root: str | Path | None = None,
                         expected_preview_paths=()) -> list[str]:
    """Decode FILE_NOTIFY_INFORMATION; malformed/overflow cannot mean no change."""
    if not data:
        raise OSError("目录变更通知溢出；清理已停止，请更新索引并重新预览。")
    offset, changed = 0, []
    while True:
        if offset + 12 > len(data):
            raise OSError("目录变更通知不完整；不能继续清理。")
        next_offset, action, length = struct.unpack_from("<III", data, offset)
        end = offset + 12 + length
        if action not in {1, 2, 3, 4, 5} or length % 2 or end > len(data):
            raise OSError("目录变更通知格式无效；不能继续清理。")
        name = data[offset + 12:end].decode("utf-16-le", errors="strict")
        # Deleting a preview also changes its parent directory's last-write
        # timestamp. Ignore ONLY modification of an existing plain directory;
        # additions/removals/renames still invalidate, and metadata-file writes
        # arrive as their own notifications under the recursive watch.
        directory_modified = False
        if action == 3 and root is not None:
            try:
                info = (Path(root) / name).lstat()
                directory_modified = (stat.S_ISDIR(info.st_mode)
                    and not getattr(info, "st_file_attributes", 0) & 0x400)
            except OSError:
                pass
        # Ignore only removal/rename-away of an exact selected file. A directory
        # named *.png can contain newly introduced dependencies, and an unrelated
        # PNG creation/write must not be mistaken for our own cleanup.
        expected_removal = (root is not None and action in {2, 4}
                            and _canonical(Path(root) / name) in expected_preview_paths)
        if not directory_modified and not expected_removal:
            changed.append(name)
        if not next_offset:
            return changed
        if next_offset < 12 + length or next_offset % 4:
            raise OSError("目录变更通知偏移无效；不能继续清理。")
        offset += next_offset


class WindowsMetadataWatch:
    """Arm before full revalidation; check immediately before each mutation."""

    def __init__(self, roots, *, expected_preview_paths=()):
        self.roots = sorted({os.path.normcase(os.path.abspath(root)) for root in roots})
        self.expected_preview_paths = frozenset(_canonical(path) for path in expected_preview_paths)
        self.entries = []
        self.error = ""
        self.closed = False

    def __enter__(self):
        if os.name != "nt":
            raise OSError("物理清理需要 Windows 目录变更保护；没有未保护的回退路径。")
        from ctypes import wintypes as w

        class Overlapped(ctypes.Structure):
            _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                        ("Offset", w.DWORD), ("OffsetHigh", w.DWORD), ("hEvent", w.HANDLE)]

        self.Overlapped = Overlapped
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        k = self.kernel
        k.CreateFileW.argtypes = (w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p,
                                  w.DWORD, w.DWORD, w.HANDLE)
        k.CreateFileW.restype = w.HANDLE
        k.CreateEventW.argtypes = (ctypes.c_void_p, w.BOOL, w.BOOL, w.LPCWSTR)
        k.CreateEventW.restype = w.HANDLE
        k.CloseHandle.argtypes = (w.HANDLE,)
        k.ResetEvent.argtypes = (w.HANDLE,)
        k.CancelIoEx.argtypes = (w.HANDLE, ctypes.POINTER(Overlapped))
        k.ReadDirectoryChangesW.argtypes = (w.HANDLE, ctypes.c_void_p, w.DWORD, w.BOOL,
                                           w.DWORD, ctypes.c_void_p, ctypes.POINTER(Overlapped),
                                           ctypes.c_void_p)
        k.GetOverlappedResult.argtypes = (w.HANDLE, ctypes.POINTER(Overlapped),
                                          ctypes.POINTER(w.DWORD), w.BOOL)
        k.WaitForSingleObject.argtypes = (w.HANDLE, w.DWORD)
        k.WaitForSingleObject.restype = w.DWORD
        try:
            for root in self.roots:
                for part in (Path(root), *Path(root).parents):
                    info = part.lstat()
                    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                        raise OSError("目录变更保护不跟随符号链接或重解析点。")
                # Read directory only; allow writes/deletion by their owners.
                handle = k.CreateFileW(root, 0x1, 0x1 | 0x2 | 0x4, None, 3,
                                       0x02000000 | 0x40000000, None)
                if handle == ctypes.c_void_p(-1).value:
                    raise ctypes.WinError(ctypes.get_last_error())
                event = k.CreateEventW(None, True, False, None)
                if not event:
                    k.CloseHandle(handle)
                    raise ctypes.WinError(ctypes.get_last_error())
                entry = {"root": root, "handle": handle, "event": event,
                         "buffer": ctypes.create_string_buffer(64 * 1024),
                         "overlapped": Overlapped(hEvent=event), "pending": False}
                self.entries.append(entry)
                self._arm(entry)
            return self
        except Exception:
            self.close()
            raise

    def _arm(self, entry):
        self.kernel.ResetEvent(entry["event"])
        if not self.kernel.ReadDirectoryChangesW(
            entry["handle"], entry["buffer"], len(entry["buffer"]), True,
            0x1 | 0x2 | 0x4 | 0x8 | 0x10, None, ctypes.byref(entry["overlapped"]), None,
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        entry["pending"] = True

    def check(self):
        from ctypes import wintypes as w
        if self.closed:
            raise OSError("目录保护已结束；不能继续使用旧清理计划。")
        if self.error:
            raise OSError(self.error)
        try:
            for entry in self.entries:
                # Bound a continuously busy producer; never starve cancellation.
                for _ in range(64):
                    count = w.DWORD()
                    ok = self.kernel.GetOverlappedResult(entry["handle"],
                        ctypes.byref(entry["overlapped"]), ctypes.byref(count), False)
                    if not ok:
                        error = ctypes.get_last_error()
                        if error == 996:  # ERROR_IO_INCOMPLETE: watch is still armed.
                            break
                        raise ctypes.WinError(error)
                    entry["pending"] = False
                    changes = notification_changes(entry["buffer"].raw[:count.value], entry["root"],
                                                   self.expected_preview_paths)
                    self._arm(entry)
                    if changes:
                        raise OSError(f"数据目录在清理期间发生变化：{changes[0]}；停止并重新预览。")
                else:
                    raise OSError("目录持续变化，无法确认稳定清理边界。")
        except (OSError, ValueError) as error:
            self.error = str(error)
            raise

    def close(self):
        if self.closed:
            return
        self.closed = True
        for entry in self.entries:
            if entry["pending"]:
                self.kernel.CancelIoEx(entry["handle"], ctypes.byref(entry["overlapped"]))
                if self.kernel.WaitForSingleObject(entry["event"], 5000) != 0:
                    # Do not free an OVERLAPPED buffer still owned by the OS.
                    _PENDING_ON_CLOSE.append((self.kernel, entry))
                    continue
            self.kernel.CloseHandle(entry["handle"])
            self.kernel.CloseHandle(entry["event"])
        self.entries = []

    def __exit__(self, *_args):
        self.close()
