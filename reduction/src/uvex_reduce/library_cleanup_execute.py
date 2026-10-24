"""Confirmed, exact-file cleanup with durable receipts, never recursive deletion.

Only an in-process plan issued by ``CleanupPlanner`` is executable. A preview is
not consent: the caller must supply the exact action/count/digest phrase. Native
mutation is Windows-only; the catalog and every raw file are untouched on import.
"""

from __future__ import annotations

from copy import deepcopy
from contextlib import nullcontext
from datetime import datetime, timezone
import ctypes
import json
import os
from pathlib import Path
import sqlite3
import stat
from typing import Any, Callable
import uuid


class CleanupExecutionError(ValueError):
    """No mutation was authorized, or the exact preview is no longer applicable."""


def _utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def _canonical(path: str | Path) -> str:
    return os.path.normcase(os.path.abspath(path))


def default_audit_directory(catalog: Any) -> Path:
    # The default catalog already lives in the application-data library folder.
    return Path(_canonical(Path(catalog.db_path).parent / "cleanup-audit"))


def _selected(plan: dict) -> list[dict]:
    return [item for item in plan.get("items", ()) if item.get("selected")]


def confirmation_phrase(plan: dict) -> str:
    """The exact phrase displayed alongside the immutable preview checklist."""
    labels = {"mark": "仅标记", "recycle": "移入系统回收站", "permanent": "永久删除"}
    action = plan.get("action")
    if action not in labels or not plan.get("digest"):
        raise CleanupExecutionError("无效的清理预览。")
    return f"确认{labels[action]} {len(_selected(plan))} 文件 {plan['digest']}"


def _safe_file(item: dict) -> None:
    """Fail closed for changed identities, parents, links, folders and writers."""
    path = Path(item["path"])
    if not path.is_absolute():
        raise CleanupExecutionError("清理只接受完整绝对路径。")
    for part in (path, *path.parents):
        info = part.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise CleanupExecutionError(f"禁止符号链接或重解析路径：{part}")
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise CleanupExecutionError(f"只允许无硬链接的普通文件：{path}")
    for key, value in (("size_bytes", info.st_size), ("mtime_ns", info.st_mtime_ns),
                       ("dev", info.st_dev), ("ino", info.st_ino), ("nlink", info.st_nlink)):
        if item.get(key) != value:
            raise CleanupExecutionError(f"文件已变化，请重新预览：{path}（{key}）")


def _safe_audit_directory(catalog: Any, directory: Path) -> None:
    target = Path(_canonical(directory))
    roots = [_canonical(row["path"]) for row in catalog.list_roots()]
    for root in roots:
        try:
            if os.path.commonpath((root, str(target))) == root:
                raise CleanupExecutionError("清理审计目录必须位于所有数据目录之外。")
        except ValueError:
            if os.path.splitdrive(root)[0] == os.path.splitdrive(str(target))[0]:
                raise
    for part in (target, *target.parents):
        if part.exists():
            info = part.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise CleanupExecutionError("清理审计目录不能经过重解析点。")
    target.mkdir(parents=True, exist_ok=True)


class _Journal:
    def __init__(self, directory: Path, plan: dict):
        # A plan may be attempted only once. Even an interrupted intent cannot be
        # retried against a possibly recreated path under its old confirmation.
        plan_id = str(uuid.UUID(str(plan["plan_id"])))
        self.path = directory / f"{plan_id}.jsonl"
        descriptor = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        self.stream = os.fdopen(descriptor, "w", encoding="utf-8")
        try:
            self.append({"event": "plan", "timestamp_utc": _utc(), "plan": plan})
        except OSError:
            try:
                self.stream.close()
            except OSError:
                pass
            raise

    def append(self, value: dict) -> None:
        self.stream.write(json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n")
        self.stream.flush()
        os.fsync(self.stream.fileno())

    def close(self) -> None:
        self.stream.close()


def _record_receipt(directory: Path, result: dict) -> None:
    index = directory / "receipts.sqlite3"
    for path in (index, Path(str(index) + "-wal"), Path(str(index) + "-shm")):
        try:
            info = path.lstat()
        except FileNotFoundError:
            continue
        if (not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode)
                or getattr(info, "st_file_attributes", 0) & 0x400 or info.st_nlink != 1):
            raise CleanupExecutionError("清理回执索引不能使用链接、重解析点或非普通文件。")
    with sqlite3.connect(index) as connection:
        connection.execute("PRAGMA synchronous=FULL")
        connection.execute("CREATE TABLE IF NOT EXISTS receipts("
                           "sequence INTEGER PRIMARY KEY,path TEXT NOT NULL,"
                           "payload TEXT NOT NULL)")
        connection.execute("CREATE INDEX IF NOT EXISTS receipts_path ON receipts(path,sequence)")
        connection.execute("INSERT INTO receipts(path,payload) VALUES(?,?)",
                           (_canonical(result["path"]), json.dumps(result, ensure_ascii=False)))


def latest_file_receipt(catalog: Any, path: str | Path, *,
                        audit_dir: str | Path | None = None) -> dict | None:
    """Last recorded library action, separate from today's presence/review state."""
    directory = Path(audit_dir) if audit_dir else default_audit_directory(catalog)
    index = directory / "receipts.sqlite3"
    if not index.is_file():
        return None
    try:
        with sqlite3.connect(f"{index.as_uri()}?mode=ro", uri=True) as connection:
            row = connection.execute("SELECT payload FROM receipts WHERE path=? "
                                     "ORDER BY sequence DESC LIMIT 1", (_canonical(path),)).fetchone()
        result = json.loads(row[0]) if row else None
        if result and result.get("status") == "intent":
            # The physical operation may have completed before a full disk or
            # crash prevented indexing its result. Resolve only this plan's
            # durable journal; an unresolved intent remains explicitly unknown.
            journal = directory / f"{str(uuid.UUID(result['plan_id']))}.jsonl"
            try:
                with journal.open(encoding="utf-8") as stream:
                    for line in stream:
                        event = json.loads(line)
                        if event.get("event") == "result" and _canonical(event["path"]) == _canonical(path):
                            result = {key: value for key, value in event.items() if key != "event"}
                        if event.get("event") == "finished":
                            for candidate in event["receipt"].get("results", ()):
                                if _canonical(candidate["path"]) == _canonical(path):
                                    result = candidate
            except (OSError, ValueError, KeyError):
                pass
        return result
    except (OSError, sqlite3.Error, ValueError):
        # An unavailable journal must never be interpreted as proof of deletion.
        return None


class CleanupExecutor:
    """Execute only an exact live preview after explicit confirmation.

    ``_backend`` exists for isolated synthetic tests, not for runtime UI policy.
    The planner always retains the protection decision, including with a test
    adapter. No receipt/JSON import API grants execution authority.
    """

    def __init__(self, catalog: Any, planner: Any = None, *,
                 audit_dir: str | Path | None = None, _backend: Any = None,
                 _watcher: Any = None):
        if planner is None:
            from .library_cleanup_plan import CleanupPlanner
            planner = CleanupPlanner(catalog)
        self.catalog = catalog
        self.planner = planner
        self.audit_dir = Path(_canonical(audit_dir)) if audit_dir else default_audit_directory(catalog)
        self.backend = _backend if _backend is not None else WindowsCleanupBackend()
        if _watcher is None:
            from .library_cleanup_watch import WindowsMetadataWatch
            _watcher = WindowsMetadataWatch
        self.watcher = _watcher

    def execute(self, plan: dict, confirmation: str, *,
                cancelled: Callable[[], bool] = lambda: False,
                progress: Callable[[int, int, dict], None] | None = None) -> dict:
        # The UI/caller cannot alter a checklist dictionary while its worker is
        # midway through execution. The registry still validates this snapshot.
        plan = deepcopy(plan)
        if confirmation != confirmation_phrase(plan):
            raise CleanupExecutionError("确认语句与本次动作、文件数量或计划摘要不符；未执行。")
        # Begin observing new/unindexed dependency documents before the full
        # initial discovery, not after it. Any overflow or unavailable watcher
        # fails closed. Review-only actions never need filesystem watching.
        context = nullcontext(None) if plan["action"] == "mark" else self.watcher(
            [row["path"] for row in self.catalog.list_roots()],
            expected_preview_paths=[item["path"] for item in _selected(plan)])
        with context as watch:
            return self._execute(plan, cancelled, progress, watch)

    def _execute(self, plan: dict, cancelled: Callable[[], bool], progress: Any,
                 watch: Any) -> dict:
        selected = _selected(plan)
        if not selected:
            raise CleanupExecutionError("没有选中文件；未执行。")
        validation = self.planner.revalidate(plan)
        if not validation["valid"]:
            raise CleanupExecutionError("预览已失效，未执行：" + "；".join(validation["reasons"]))
        if watch:
            watch.check()
        if plan["action"] != "mark" and any(
                item.get("protected") or not item.get("candidate") for item in selected):
            raise CleanupExecutionError("所选项目包含受保护或不可清理文件；未执行。")
        _safe_audit_directory(self.catalog, self.audit_dir)
        try:
            journal = _Journal(self.audit_dir, plan)
        except FileExistsError as error:
            raise CleanupExecutionError("此计划已有执行或中断记录；必须重新预览，不能重试旧计划。") from error
        receipt = {"plan_id": plan["plan_id"], "action": plan["action"], "status": "completed",
                   "results": [], "audit_path": str(journal.path), "logical_removed_bytes": 0,
                   "estimated_release_bytes": 0, "errors": []}
        completed: list[int] = []
        try:
            if plan["action"] == "mark":
                return self._mark(plan, selected, journal, receipt, cancelled, progress)
            for item in selected:
                if cancelled():
                    receipt["status"] = "cancelled"
                    break
                result = {"id": item["id"], "path": item["path"], "size_bytes": item["size_bytes"],
                          "action": plan["action"], "plan_id": plan["plan_id"],
                          "timestamp_utc": _utc(), "status": "failed", "message": ""}
                backend_started = False

                def before_mutation() -> None:
                    if cancelled():
                        raise CleanupExecutionError("操作员已取消；未开始此文件操作。")
                    current = self.planner.revalidate_item(
                        plan, item, completed_file_ids=tuple(completed))
                    if not current["valid"]:
                        raise CleanupExecutionError("预览或依赖已变化：" + "；".join(current["reasons"]))
                    if watch:
                        watch.check()
                    _safe_file(item)

                try:
                    before_mutation()
                    journal.append({"event": "intent", **result})
                    _record_receipt(self.audit_dir, {**result, "status": "intent",
                                                    "message": "已记录操作意图；结果尚未确认。"})
                    method = getattr(self.backend, plan["action"])
                    backend_started = True
                    outcome = method(item, before_mutation)
                    result.update(outcome)
                    result["timestamp_utc"] = _utc()
                    # Record the physical outcome before touching the rebuildable
                    # catalog. A failed DB update cannot turn a removed file into
                    # a claim that nothing happened.
                    journal.append({"event": "result", **result})
                    _record_receipt(self.audit_dir, result)
                    receipt["results"].append(result)
                    if result["status"] in {"deleted", "recycled"}:
                        receipt["logical_removed_bytes"] += item["size_bytes"]
                        if result["status"] == "deleted":
                            receipt["estimated_release_bytes"] += item.get("estimated_release_bytes", 0)
                    if result["status"] not in {"deleted", "recycled", "marked"}:
                        receipt["status"] = "partial" if completed else "failed"
                        receipt["errors"].append(result["message"])
                        break
                    completed.append(item["id"])
                    if progress:
                        progress(len(completed), len(selected), dict(result))
                except Exception as error:
                    # If result persistence/catalog maintenance fails after a
                    # mutation, retain its known outcome and say so explicitly.
                    if result not in receipt["results"]:
                        result["message"] = str(error)
                        if backend_started and result["status"] == "failed" and not Path(item["path"]).exists():
                            result["status"] = "uncertain"
                            result["message"] += "；源路径已不存在但操作结果未确认，停止后续清理并核查。"
                        elif result["status"] == "failed" and cancelled():
                            result["status"] = "cancelled"
                        try:
                            journal.append({"event": "result", **result})
                            _record_receipt(self.audit_dir, result)
                        except (OSError, sqlite3.Error, CleanupExecutionError) as audit_error:
                            receipt["errors"].append(f"审计写入失败：{audit_error}")
                        receipt["results"].append(result)
                    receipt["errors"].append(str(error))
                    receipt["status"] = ("cancelled" if result["status"] == "cancelled" else
                                         "partial" if completed or result["status"] in {
                                             "deleted", "recycled", "marked", "uncertain"} else "failed")
                    break
            # Own catalog writes are deferred so the immutable dependency token
            # is unchanged during physical revalidation of remaining files.
            removed = [row for row in receipt["results"] if row["status"] in {"deleted", "recycled"}]
            try:
                self.catalog.connection.executemany(
                    "UPDATE files SET missing=1 WHERE id=? AND path=?",
                    [(row["id"], row["path"]) for row in removed])
                self.catalog.connection.commit()
            except sqlite3.Error as error:
                self.catalog.connection.rollback()
                receipt["status"] = "partial"
                receipt["errors"].append(f"物理结果已记录，但索引缺失状态更新失败：{error}")
            # Count known physical outcomes even if the later index/audit failed.
            receipt["logical_removed_bytes"] = sum(row["size_bytes"] for row in removed)
            deleted_ids = {row["id"] for row in removed if row["status"] == "deleted"}
            receipt["estimated_release_bytes"] = sum(
                item.get("estimated_release_bytes", 0) for item in selected
                if item["id"] in deleted_ids)
            self._finish(journal, receipt)
        finally:
            try:
                journal.close()
            except OSError as error:
                receipt["status"] = "partial" if receipt["results"] else "failed"
                receipt["errors"].append(f"审计文件关闭失败：{error}")
        return receipt

    @staticmethod
    def _finish(journal: _Journal, receipt: dict) -> None:
        try:
            journal.append({"event": "finished", "timestamp_utc": _utc(), "receipt": receipt})
        except OSError as error:
            receipt["status"] = "partial" if receipt["results"] else "failed"
            receipt["errors"].append(f"最终审计写入失败；以已有逐文件意图/结果为准：{error}")

    def _mark(self, plan: dict, selected: list[dict], journal: _Journal, receipt: dict,
              cancelled: Callable[[], bool], progress: Any) -> dict:
        """One review-only transaction; never stat/follow a marked source file."""
        try:
            with self.catalog.connection:
                for item in selected:
                    if cancelled():
                        raise CleanupExecutionError("标记已取消，事务未提交。")
                    journal.append({"event": "intent", "action": "mark", "plan_id": plan["plan_id"],
                                    "id": item["id"], "path": item["path"], "timestamp_utc": _utc()})
                    _record_receipt(self.audit_dir, {
                        "action": "mark", "plan_id": plan["plan_id"], "id": item["id"],
                        "path": item["path"], "timestamp_utc": _utc(), "status": "intent",
                        "size_bytes": item["size_bytes"], "message": "标记事务尚未确认提交。"})
                    self.catalog.connection.execute(
                        "INSERT INTO reviews(file_id,decision,rating,notes,updated_utc) "
                        "VALUES(?,'reject',0,'',?) ON CONFLICT(file_id) DO UPDATE SET "
                        "decision='reject',updated_utc=excluded.updated_utc", (item["id"], _utc()))
            receipt["results"] = [
                {"id": item["id"], "path": item["path"], "size_bytes": item["size_bytes"],
                 "action": "mark", "plan_id": plan["plan_id"], "timestamp_utc": _utc(),
                 "status": "marked", "message": "仅资料库标记；原文件未改动，释放 0 B。"}
                for item in selected]
            receipt["mark_committed_count"] = len(selected)
            for number, result in enumerate(receipt["results"], 1):
                journal.append({"event": "result", **result})
                _record_receipt(self.audit_dir, result)
                if progress:
                    progress(number, len(selected), dict(result))
        except Exception as error:
            receipt["status"] = ("partial" if receipt["results"] else
                                 "cancelled" if cancelled() else "failed")
            receipt["errors"].append(str(error))
        self._finish(journal, receipt)
        return receipt


def _windows_libraries() -> tuple[Any, Any, Any]:
    if os.name != "nt":
        raise CleanupExecutionError("物理清理只支持 Windows；不会降级为普通 unlink。")
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel.SetFileInformationByHandle.argtypes = (wintypes.HANDLE, ctypes.c_int,
                                                 ctypes.c_void_p, wintypes.DWORD)
    kernel.SetFileInformationByHandle.restype = wintypes.BOOL
    kernel.GetDriveTypeW.argtypes = (wintypes.LPCWSTR,)
    kernel.GetDriveTypeW.restype = wintypes.UINT
    return kernel, ctypes.OleDLL("ole32"), ctypes.OleDLL("shell32")


class WindowsCleanupBackend:
    """No permanent fallback, no folder recursion, no recycle-bin emptying."""

    def permanent(self, item: dict, before_mutation: Callable[[], None]) -> dict:
        from ctypes import wintypes
        kernel, _, _ = _windows_libraries()
        # DELETE + FILE_READ_ATTRIBUTES; share-read only excludes writers,
        # renamers/deleters and existing incompatible open handles.
        handle = kernel.CreateFileW(item["path"], 0x10000 | 0x80, 0x1, None, 3, 0x00200000, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            _verify_handle(kernel, handle, item)
            before_mutation()
            disposition = wintypes.BOOL(True)
            if not kernel.SetFileInformationByHandle(handle, 4, ctypes.byref(disposition),
                                                      ctypes.sizeof(disposition)):
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            kernel.CloseHandle(handle)
        if Path(item["path"]).exists():
            return {"status": "uncertain", "message": "已提交句柄删除，但路径仍存在；停止后续清理并复核。"}
        return {"status": "deleted", "message": "已永久删除此精确文件；不可从系统回收站恢复。"}

    def recycle(self, item: dict, before_mutation: Callable[[], None]) -> dict:
        return _recycle_windows(item, before_mutation)


def _verify_handle(kernel: Any, handle: Any, item: dict) -> None:
    from ctypes import wintypes

    class Info(ctypes.Structure):
        _fields_ = [("attributes", wintypes.DWORD), ("creation", wintypes.FILETIME),
                    ("access", wintypes.FILETIME), ("write", wintypes.FILETIME),
                    ("volume", wintypes.DWORD), ("size_high", wintypes.DWORD),
                    ("size_low", wintypes.DWORD), ("links", wintypes.DWORD),
                    ("index_high", wintypes.DWORD), ("index_low", wintypes.DWORD)]

    kernel.GetFileInformationByHandle.argtypes = (wintypes.HANDLE, ctypes.POINTER(Info))
    kernel.GetFileInformationByHandle.restype = wintypes.BOOL
    info = Info()
    if not kernel.GetFileInformationByHandle(handle, ctypes.byref(info)):
        raise ctypes.WinError(ctypes.get_last_error())
    identity = (info.index_high << 32) | info.index_low
    size = (info.size_high << 32) | info.size_low
    mtime = (((info.write.dwHighDateTime << 32) | info.write.dwLowDateTime)
             - 116444736000000000) * 100
    if (info.attributes & (0x400 | 0x10) or info.links != 1 or identity != item["ino"]
            or info.volume != item["dev"] or size != item["size_bytes"]
            or mtime != item["mtime_ns"]):
        raise CleanupExecutionError("已打开的文件句柄与预览身份不符；未删除。")


def _recycle_windows(item: dict, before_mutation: Callable[[], None]) -> dict:
    """Native IFileOperation, strict pre-delete and per-item post-delete receipt.

    FOFX_RECYCLEONDELETE is not FOF_ALLOWUNDO's best effort. A missing recycle
    flag, unavailable bin, changed identity or a NULL new-bin item is never
    silently accepted as a successful move to the system recycle bin.
    """
    from ctypes import wintypes
    kernel, ole, shell = _windows_libraries()
    root = Path(item["path"]).anchor
    if not root or kernel.GetDriveTypeW(root) != 3:
        raise CleanupExecutionError("系统回收站仅允许本地固定磁盘；网络、可移动或未知磁盘不执行。")

    class BinInfo(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("bytes", ctypes.c_longlong),
                    ("items", ctypes.c_longlong)]

    bin_info = BinInfo(ctypes.sizeof(BinInfo), 0, 0)
    shell.SHQueryRecycleBinW.argtypes = (wintypes.LPCWSTR, ctypes.POINTER(BinInfo))
    if shell.SHQueryRecycleBinW(root, ctypes.byref(bin_info)) != 0:
        raise CleanupExecutionError("系统回收站不可用；不会改为永久删除。")

    class GUID(ctypes.Structure):
        _fields_ = [("data", ctypes.c_ubyte * 16)]

        @classmethod
        def parse(cls, value: str) -> Any:
            return cls((ctypes.c_ubyte * 16).from_buffer_copy(uuid.UUID(value).bytes_le))

    iid_operation = GUID.parse("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")
    iid_item = GUID.parse("43826d1e-e718-42ee-bc55-a1e261c37bfe")
    cls_operation = GUID.parse("3ad05575-8857-4850-9277-11b85bdb8e09")
    iid_sink = GUID.parse("04b0f1a7-9490-44bc-96e1-4296a31252e2")
    iid_unknown = GUID.parse("00000000-0000-0000-c000-000000000046")
    hresult = ctypes.c_long
    pointer = ctypes.c_void_p

    def call(obj: Any, index: int, args: tuple[Any, ...], *values: Any) -> Any:
        table = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(pointer))).contents
        function = ctypes.WINFUNCTYPE(hresult, pointer, *args)(table[index])
        result = function(obj, *values)
        if result < 0:
            raise CleanupExecutionError(f"Windows 回收站操作失败（0x{result & 0xffffffff:08X}）；未降级删除。")
        return result

    state: dict[str, Any] = {"pre": False, "post": False, "recycled": False,
                             "error": "", "handle": None}
    callbacks: list[Any] = []
    abort = ctypes.c_long(0x80004004).value

    class Sink(ctypes.Structure):
        _fields_ = [("table", ctypes.POINTER(pointer))]

    sink = Sink()

    def query(this: Any, iid: Any, output: Any) -> int:
        if ctypes.string_at(iid, 16) not in (bytes(iid_sink.data), bytes(iid_unknown.data)):
            output[0] = None
            return ctypes.c_long(0x80004002).value
        output[0] = ctypes.addressof(sink)
        return 0

    def pre_delete(this: Any, flags: int, shell_item: Any) -> int:
        try:
            if not flags & 0x80:  # TSF_DELETE_RECYCLE_IF_POSSIBLE
                raise CleanupExecutionError("Shell 未确认回收语义；取消，不允许永久删除。")
            # Shell must be allowed to rename/delete into its own recycle store,
            # but a writer must not change the file between preflight and move.
            handle = kernel.CreateFileW(item["path"], 0x80, 0x1 | 0x4, None, 3, 0x00200000, None)
            if handle == ctypes.c_void_p(-1).value:
                raise ctypes.WinError(ctypes.get_last_error())
            state["handle"] = handle
            _verify_handle(kernel, handle, item)
            before_mutation()
            state["pre"] = True
            return 0
        except Exception as error:
            state["error"] = str(error)
            return abort

    def post_delete(this: Any, flags: int, original: Any, result: int, new_item: Any) -> int:
        state.update(post=True, recycled=False)
        if result < 0:
            state["error"] = f"Shell 文件处理失败（0x{result & 0xffffffff:08X}）。"
        if new_item:
            value = pointer()
            try:
                call(new_item, 5, (ctypes.c_int, ctypes.POINTER(pointer)), 0x80058000,
                     ctypes.byref(value))  # SIGDN_FILESYSPATH
                state["recycle_path"] = ctypes.wstring_at(value)
                # The Shell must move the same object that was previewed, not a
                # replacement at the source name. Unlike handle-disposition
                # deletion, Shell recycling requires share-delete access; this
                # post-move identity corroboration detects a substitution race
                # and reports uncertainty instead of claiming a valid recycle.
                recycled_handle = kernel.CreateFileW(state["recycle_path"], 0x80, 0x1 | 0x4,
                                                       None, 3, 0x00200000, None)
                if recycled_handle == ctypes.c_void_p(-1).value:
                    raise ctypes.WinError(ctypes.get_last_error())
                try:
                    _verify_handle(kernel, recycled_handle, item)
                finally:
                    kernel.CloseHandle(recycled_handle)
                state["recycled"] = result >= 0
            except Exception as error:
                state["error"] = f"系统返回了回收站项，但同一文件身份复核未通过：{error}"
            finally:
                if value:
                    ole.CoTaskMemFree(value)
        return 0

    signatures = [
        (hresult, (pointer, ctypes.POINTER(GUID), ctypes.POINTER(pointer)), query),
        (ctypes.c_ulong, (pointer,), lambda this: 1),
        (ctypes.c_ulong, (pointer,), lambda this: 1),
        (hresult, (pointer,), lambda this: 0),
        (hresult, (pointer, hresult), lambda this, result: 0),
    ]
    # Renames, moves, copies and new-item operations are not authorized here.
    for count in (4, 6, 5, 7, 5, 7):
        signatures.append((hresult, (pointer,) * count, lambda *args: abort))
    signatures.extend([
        (hresult, (pointer, wintypes.DWORD, pointer), pre_delete),
        (hresult, (pointer, wintypes.DWORD, pointer, hresult, pointer), post_delete),
        (hresult, (pointer,) * 4, lambda *args: abort),
        (hresult, (pointer,) * 7, lambda *args: abort),
        (hresult, (pointer, wintypes.UINT, wintypes.UINT), lambda *args: 0),
        (hresult, (pointer,), lambda *args: 0),
        (hresult, (pointer,), lambda *args: 0),
        (hresult, (pointer,), lambda *args: 0),
    ])
    for result_type, args, function in signatures:
        callbacks.append(ctypes.WINFUNCTYPE(result_type, *args)(function))
    table = (pointer * len(callbacks))(*(ctypes.cast(fn, pointer).value for fn in callbacks))
    sink.table = ctypes.cast(table, ctypes.POINTER(pointer))
    operation = pointer()
    source = pointer()
    cookie = wintypes.DWORD()
    advised = False
    ole.CoInitializeEx.argtypes = (pointer, wintypes.DWORD)
    ole.CoCreateInstance.argtypes = (ctypes.POINTER(GUID), pointer, wintypes.DWORD,
                                    ctypes.POINTER(GUID), ctypes.POINTER(pointer))
    shell.SHCreateItemFromParsingName.argtypes = (wintypes.LPCWSTR, pointer,
                                                 ctypes.POINTER(GUID), ctypes.POINTER(pointer))
    # OleDLL translates failed HRESULTs to OSError. No MTA fallback is allowed.
    ole.CoInitializeEx(None, 2)
    try:
        ole.CoCreateInstance(ctypes.byref(cls_operation), None, 1, ctypes.byref(iid_operation),
                             ctypes.byref(operation))
        shell.SHCreateItemFromParsingName(item["path"], None, ctypes.byref(iid_item), ctypes.byref(source))
        flags = 0x00080000 | 0x00100000 | 0x0400 | 0x1000 | 0x2000 | 0x0004 | 0x4000
        call(operation, 5, (wintypes.DWORD,), flags)
        call(operation, 3, (pointer, ctypes.POINTER(wintypes.DWORD)), ctypes.byref(sink), ctypes.byref(cookie))
        advised = True
        call(operation, 18, (pointer, pointer), source, None)
        before_mutation()
        call(operation, 21, ())
        aborted = wintypes.BOOL()
        call(operation, 22, (ctypes.POINTER(wintypes.BOOL),), ctypes.byref(aborted))
        if state["pre"] and state["post"] and state["recycled"] and not aborted.value:
            return {"status": "recycled", "message": "已移入 Windows 系统回收站；未释放磁盘空间。",
                    "recycle_path": state.get("recycle_path", "")}
        if not Path(item["path"]).exists():
            return {"status": "uncertain", "message": "源路径已不存在，但系统回收结果未通过同一文件核对；"
                    "停止后续清理，需核查。" + state["error"]}
        raise CleanupExecutionError(state["error"] or "回收操作未完成；文件保留，未降级删除。")
    finally:
        if state["handle"]:
            kernel.CloseHandle(state["handle"])
        if operation:
            if advised:
                call(operation, 4, (wintypes.DWORD,), cookie.value)
            call(operation, 2, ())
        if source:
            call(source, 2, ())
        ole.CoUninitialize()
