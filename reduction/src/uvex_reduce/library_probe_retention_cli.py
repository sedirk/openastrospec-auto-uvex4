"""Headless target-success retention; no device control and no GUI windows."""
import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import time

from .library_catalog import LibraryCatalog, default_catalog_path
from .library_cleanup_execute import CleanupExecutor, WindowsCleanupBackend, confirmation_phrase
from .library_probe_retention import (
    OWNER_SPACE_RECLAIM_POLICY,
    REJECTED_PROBE_POLICY,
    TARGET_SUCCESS_POLICY,
    TERMINAL_TARGET_POLICY,
    OwnerSpaceReclaimRetentionPlanner,
    ProbeRetentionPlanner,
    TargetSuccessRetentionPlanner,
    TerminalTargetRetentionPlanner,
)
from .library_probe_retention_watch import ProbeMetadataWatch


def _state_stem(policy):
    if policy == OWNER_SPACE_RECLAIM_POLICY:
        return "owner-space-reclaim"
    if policy == TERMINAL_TARGET_POLICY:
        return "terminal-target-retention"
    return "target-success-retention" if policy == TARGET_SUCCESS_POLICY else "probe-retention"


def _status(folder, policy, result):
    stem = _state_stem(policy)
    target = folder / f"{stem}-status.json"
    temporary = folder / f"{stem}-status-{os.getpid()}.tmp"
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    os.replace(temporary, target)


def _parse_utc(value):
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed.replace(tzinfo=timezone.utc) if parsed.tzinfo is None else parsed.astimezone(timezone.utc)


def _enabled_time(path):
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, ValueError):
        return None
    return _parse_utc(payload.get("enabledUtc")) if isinstance(payload, dict) else None


@contextmanager
def single_worker(directory, policy=REJECTED_PROBE_POLICY):
    """OS releases the lock after crashes; never concurrent automatic batches."""
    import msvcrt
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / f"{_state_stem(policy)}.lock").open("a+b") as stream:
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


def cycle(catalog, *, execute=False, include_failed=False, scan=True,
          policy=TARGET_SUCCESS_POLICY, not_before_utc=None):
    result = {"utc": datetime.now(timezone.utc).isoformat(),
              "policy": policy, "mode": "permanent" if execute else "preview",
              "require_science": policy != TERMINAL_TARGET_POLICY and not include_failed,
              "status": "preview", "deleted_bytes": 0}
    if scan:
        result["scan"] = catalog.scan()
        if result["scan"]["warnings"] or result["scan"]["cancelled"]:
            result["status"] = "deferred-index-incomplete"
            return result
    planner = (OwnerSpaceReclaimRetentionPlanner(catalog, authorized=True)
               if policy == OWNER_SPACE_RECLAIM_POLICY else
               TerminalTargetRetentionPlanner(catalog, authorized=True,
                                              not_before_utc=not_before_utc)
               if policy == TERMINAL_TARGET_POLICY else
               TargetSuccessRetentionPlanner(catalog, authorized=True,
                                             require_science=not include_failed,
                                             not_before_utc=not_before_utc)
               if policy == TARGET_SUCCESS_POLICY else
               ProbeRetentionPlanner(catalog, authorized=True,
                                     require_science=not include_failed,
                                     policy=REJECTED_PROBE_POLICY))
    denied = []

    def writable(row):
        if WindowsCleanupBackend.delete_access(row["path"]):
            return True
        denied.append(row["path"])
        return False

    plan = (planner.preview_candidates(allow_item=writable)
            if execute and policy == TERMINAL_TARGET_POLICY else planner.preview_candidates())
    result.update(access_denied_count=len(denied), first_denied_path=denied[0] if denied else "")
    if plan is None:
        result.update(status="deferred-access-denied" if denied else "no-eligible-candidates",
                      reasons=planner._snapshot["global_reasons"])
        return result
    selected = [i for i in plan["items"] if i["selected"]]
    result.update(count=len(selected), estimated_release_bytes=plan["estimated_release_bytes"],
                  paths=[i["path"] for i in selected])
    audit = Path(catalog.db_path).parent / "cleanup-audit"
    audit.mkdir(exist_ok=True)
    preview_name = ("-owner-space-reclaim-preview.json"
                    if policy == OWNER_SPACE_RECLAIM_POLICY else
                    "-terminal-target-preview.json"
                    if policy == TERMINAL_TARGET_POLICY else
                    "-target-success-preview.json"
                    if policy == TARGET_SUCCESS_POLICY else "-probe-preview.json")
    preview_path = audit / (plan["plan_id"] + preview_name)
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
    parser.add_argument("--policy", choices=[
        TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY,
        REJECTED_PROBE_POLICY, OWNER_SPACE_RECLAIM_POLICY,
    ],
                        default=REJECTED_PROBE_POLICY)
    parser.add_argument("--execute-permanent-target-success", action="store_true",
                        help="Explicit authorization for successful-target parse/probe cleanup.")
    parser.add_argument("--execute-permanent-terminal-target", action="store_true",
                        help="Explicit authorization for terminal target acquisition/probe cleanup, "
                             "including failed and cancelled runs.")
    parser.add_argument("--execute-permanent-rejected-probes", action="store_true",
                        help="Legacy explicit authorization for rejected ATR probes only.")
    parser.add_argument("--execute-permanent-owner-space-reclaim", action="store_true",
                        help="One-time owner authorization for all terminal QHY photometry media "
                             "plus historical successful-target probes/solve frames.")
    parser.add_argument("--not-before-utc", type=str,
                        help="Required for manual target-success runs; no historical run before this UTC time is eligible.")
    parser.add_argument("--include-failed-history", action="store_true",
                        help="One-time historical authorization, never allowed with --watch.")
    parser.add_argument("--watch", action="store_true", help="Repeat successful-terminal-run policy until disabled.")
    parser.add_argument("--interval-seconds", type=int, default=900)
    parser.add_argument("--no-scan", action="store_true", help="Use existing index; fresh dependency discovery still required.")
    args = parser.parse_args(argv)
    execute = (args.execute_permanent_owner_space_reclaim
               if args.policy == OWNER_SPACE_RECLAIM_POLICY else
               args.execute_permanent_terminal_target
               if args.policy == TERMINAL_TARGET_POLICY else
               args.execute_permanent_target_success
               if args.policy == TARGET_SUCCESS_POLICY
               else args.execute_permanent_rejected_probes)
    if (args.execute_permanent_owner_space_reclaim
            and args.policy != OWNER_SPACE_RECLAIM_POLICY):
        parser.error("The owner-space-reclaim flag requires --policy owner-space-reclaim.")
    if (args.execute_permanent_terminal_target
            and args.policy != TERMINAL_TARGET_POLICY):
        parser.error("The terminal-target flag requires --policy terminal-target.")
    if (args.policy == TERMINAL_TARGET_POLICY
            and (args.execute_permanent_target_success
                 or args.execute_permanent_rejected_probes
                 or args.execute_permanent_owner_space_reclaim)):
        parser.error("Terminal-target cleanup requires its own explicit authorization.")
    if (args.policy == OWNER_SPACE_RECLAIM_POLICY
            and args.execute_permanent_target_success):
        parser.error("The target-success flag is not owner-space-reclaim authorization.")
    if (args.policy == OWNER_SPACE_RECLAIM_POLICY
            and args.execute_permanent_rejected_probes):
        parser.error("The rejected-probe flag is not owner-space-reclaim authorization.")
    if args.policy == TARGET_SUCCESS_POLICY and args.execute_permanent_rejected_probes:
        parser.error("The rejected-probe flag is not target-success authorization.")
    if args.policy == REJECTED_PROBE_POLICY and args.execute_permanent_target_success:
        parser.error("The target-success flag requires --policy target-success.")
    if args.include_failed_history and args.policy != REJECTED_PROBE_POLICY:
        parser.error("Failed-history authorization is only available to the legacy rejected-probe policy.")
    if args.policy == OWNER_SPACE_RECLAIM_POLICY and args.watch:
        parser.error("Owner-space-reclaim is a one-time authorized batch and cannot watch.")
    manual_not_before = _parse_utc(args.not_before_utc) if args.not_before_utc else None
    if args.not_before_utc and manual_not_before is None:
        parser.error("--not-before-utc must be an ISO-8601 timestamp.")
    if args.policy == TARGET_SUCCESS_POLICY and not args.watch and manual_not_before is None:
        parser.error("A manual target-success run requires --not-before-utc.")
    if args.policy == TERMINAL_TARGET_POLICY and not args.watch and manual_not_before is None:
        parser.error("A manual terminal-target run requires --not-before-utc.")
    if args.watch and (args.include_failed_history or not execute or args.no_scan):
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
    enabled = folder / (f"{_state_stem(args.policy)}.enabled")
    with single_worker(folder, args.policy):
        while not args.watch or enabled.exists():
            _status(folder, args.policy, {
                "utc": datetime.now(timezone.utc).isoformat(), "status": "checking",
                "policy": args.policy, "watch": args.watch, "process_id": os.getpid(),
                "require_science": args.policy != TERMINAL_TARGET_POLICY and not args.include_failed_history,
            })
            try:
                not_before = manual_not_before
                if args.policy in {TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY} and args.watch:
                    not_before = _enabled_time(enabled)
                    if not_before is None:
                        raise ValueError("Retention enable flag has no valid enabledUtc watermark.")
                with LibraryCatalog(args.catalog) as catalog:
                    result = cycle(catalog, execute=execute,
                                   include_failed=args.include_failed_history,
                                   scan=not args.no_scan, policy=args.policy,
                                   not_before_utc=not_before)
            except Exception as error:
                result = {"utc": datetime.now(timezone.utc).isoformat(),
                          "policy": args.policy, "status": "deferred", "error": str(error)}
            _status(folder, args.policy, {
                **result, "watch": args.watch, "process_id": os.getpid(),
            })
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
