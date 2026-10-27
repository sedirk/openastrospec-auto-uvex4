"""Headless, opt-in probe retention; no device control and no GUI windows."""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import time

from .library_catalog import LibraryCatalog, default_catalog_path
from .library_cleanup_execute import CleanupExecutor, confirmation_phrase
from .library_probe_retention import ProbeRetentionPlanner
from .library_probe_retention_watch import ProbeMetadataWatch


def _status(folder, result):
    target = folder / "probe-retention-status.json"
    temporary = folder / f"probe-retention-status-{os.getpid()}.tmp"
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    os.replace(temporary, target)


@contextmanager
def single_worker(directory):
    """OS releases the lock after crashes; never concurrent automatic batches."""
    import msvcrt
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / "probe-retention.lock").open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def cycle(catalog, *, execute=False, include_failed=False, scan=True):
    result = {"utc": datetime.now(timezone.utc).isoformat(), "mode": "permanent" if execute else "preview",
              "require_science": not include_failed, "status": "preview", "deleted_bytes": 0}
    if scan:
        result["scan"] = catalog.scan()
        if result["scan"]["warnings"] or result["scan"]["cancelled"]:
            result["status"] = "deferred-index-incomplete"
            return result
    planner = ProbeRetentionPlanner(catalog, authorized=True, require_science=not include_failed)
    plan = planner.preview_probes()
    if plan is None:
        result.update(status="no-eligible-probes", reasons=planner._snapshot["global_reasons"])
        return result
    selected = [i for i in plan["items"] if i["selected"]]
    result.update(count=len(selected), estimated_release_bytes=plan["estimated_release_bytes"],
                  paths=[i["path"] for i in selected])
    audit = Path(catalog.db_path).parent / "cleanup-audit"
    audit.mkdir(exist_ok=True)
    preview_path = audit / (plan["plan_id"] + "-probe-preview.json")
    with preview_path.open("x", encoding="utf-8") as stream:
        json.dump(plan, stream, ensure_ascii=False)
    result["preview_path"] = str(preview_path)
    if execute:
        before = shutil.disk_usage(Path(catalog.db_path).anchor).free
        def watcher(roots, **kwargs):
            return ProbeMetadataWatch(roots, candidates=[i["path"] for i in selected],
                guard_paths=[g["path"] for g in plan["protection_documents"]], **kwargs)
        receipt = CleanupExecutor(catalog, planner, _watcher=watcher).execute(plan, confirmation_phrase(plan))
        result.update(status=receipt["status"], deleted_bytes=receipt["logical_removed_bytes"],
                      deleted_count=sum(r["status"] == "deleted" for r in receipt["results"]),
                      volume_free_before=before, volume_free_after=shutil.disk_usage(Path(catalog.db_path).anchor).free,
                      audit_path=receipt["audit_path"], errors=receipt["errors"])
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=default_catalog_path())
    parser.add_argument("--execute-permanent-rejected-probes", action="store_true",
                        help="Explicit authorization. No recycle bin; valid/science/dependent frames protected.")
    parser.add_argument("--include-failed-history", action="store_true",
                        help="One-time historical authorization, never allowed with --watch.")
    parser.add_argument("--watch", action="store_true", help="Repeat successful-terminal-run policy until disabled.")
    parser.add_argument("--interval-seconds", type=int, default=900)
    parser.add_argument("--no-scan", action="store_true", help="Use existing index; fresh dependency discovery still required.")
    args = parser.parse_args(argv)
    if args.watch and (args.include_failed_history or not args.execute_permanent_rejected_probes or args.no_scan):
        parser.error("Automatic operation requires explicit permanent authorization, successful science and fresh scans.")
    if args.interval_seconds < 60:
        parser.error("Minimum interval is 60 seconds.")
    if os.name != "nt":
        parser.error("Retention execution is Windows-only.")
    # The offline janitor yields CPU priority to the acquisition owner.
    import ctypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.SetPriorityClass.argtypes = (ctypes.c_void_p, ctypes.c_uint)
    kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL_PRIORITY_CLASS
    folder = args.catalog.parent
    enabled = folder / "probe-retention.enabled"
    with single_worker(folder):
        while not args.watch or enabled.exists():
            _status(folder, {"utc": datetime.now(timezone.utc).isoformat(), "status": "checking",
                             "watch": args.watch, "process_id": os.getpid(),
                             "require_science": not args.include_failed_history})
            try:
                with LibraryCatalog(args.catalog) as catalog:
                    result = cycle(catalog, execute=args.execute_permanent_rejected_probes,
                                   include_failed=args.include_failed_history, scan=not args.no_scan)
            except Exception as error:
                result = {"utc": datetime.now(timezone.utc).isoformat(), "status": "deferred", "error": str(error)}
            _status(folder, {**result, "watch": args.watch, "process_id": os.getpid()})
            if not args.watch:
                print(json.dumps(result, ensure_ascii=False), flush=True)
                return
            # No fixed screenshots, windows, device calls or acquisition waits.
            for _ in range(args.interval_seconds):
                if not enabled.exists():
                    return
                time.sleep(1)


if __name__ == "__main__":
    main()
