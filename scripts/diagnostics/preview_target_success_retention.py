"""Read-only policy replay against one completed observation run.

The script builds a temporary catalog from only the roots referenced by that
run's manifest, prints the exact permanent-deletion preview, and exits. It never
calls the cleanup executor and never opens N.I.N.A., PHD2 or a device.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import sys
import tempfile

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_probe_retention import TargetSuccessRetentionPlanner


def roots_for_manifest(manifest_path: Path, record: dict) -> list[Path]:
    roots = {manifest_path.parent}
    for evidence in record.get("evidence", []):
        if not isinstance(evidence, dict):
            continue
        path = Path(str(evidence.get("absolutePath", "")))
        if not path.exists() or path.suffix.casefold() not in {".fit", ".fits"}:
            continue
        parts = [part.casefold() for part in path.parts]
        if "qhy" in parts and "runs" in parts and "raw" in parts:
            roots.add(path.parent.parent)
        elif "n.i.n.a" in parts and len(path.parents) >= 3:
            roots.add(path.parents[1])
    return sorted(roots)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observations-root", type=Path,
                        default=Path(os.environ["LOCALAPPDATA"]) / "UVEX-ADV" / "observations")
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--not-before-utc", required=True)
    args = parser.parse_args(argv)

    manifest_path = args.observations_root / args.run_id / "manifest.json"
    record = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if str(record.get("terminalState", "")).casefold() != "completed":
        raise SystemExit(f"Run is not Completed: {record.get('terminalState')!r}")
    roots = roots_for_manifest(manifest_path, record)

    with tempfile.TemporaryDirectory(prefix="uvex-target-retention-replay-") as directory:
        catalog_path = Path(directory) / "catalog.sqlite3"
        with LibraryCatalog(catalog_path) as catalog:
            for root in roots:
                catalog.add_root(root)
            scan = catalog.scan()
            if scan["warnings"] or scan["cancelled"]:
                raise SystemExit(f"Replay index incomplete: {scan}")
            planner = TargetSuccessRetentionPlanner(
                catalog, authorized=True, not_before_utc=args.not_before_utc)
            plan = planner.preview_candidates()
            if plan is None:
                print(json.dumps({"run_id": args.run_id, "eligible": False,
                                  "reasons": planner._snapshot["global_reasons"]},
                                 ensure_ascii=False, indent=2))
                return 0
            selected = [item for item in plan["items"] if item["selected"]]
            protected = [item for item in plan["items"] if not item["selected"]]
            kinds = Counter(item["proof"]["producer"] for item in selected)
            print(json.dumps({
                "run_id": args.run_id,
                "eligible": True,
                "roots": [str(root) for root in roots],
                "scan": scan,
                "selected_count": len(selected),
                "selected_bytes": plan["selected_bytes"],
                "selected_producers": dict(kinds),
                "protected_count": len(protected),
                "selected_paths": [item["path"] for item in selected],
                "motion_or_deletion_executed": False,
            }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
