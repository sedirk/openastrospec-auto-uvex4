"""Opt-in retention of failed ATR probes, using the library's exact-file executor.

No acquisition or device API. The ordinary cleanup planner still protects all
raws. This policy needs an explicit caller authorization and terminal manifests;
automatic operation additionally requires an existing accepted science frame.
"""
from __future__ import annotations

from collections import defaultdict
import hashlib
import json
import os
from pathlib import Path

from .library_catalog import PARSER_VERSION, _canonical
from .library_cleanup_plan import CleanupPlanner, TERMINAL_STATES, _facts, _json, _same_index
from .library_probe_retention_watch import CandidatePaths, METADATA_SUFFIXES, unrelated_metadata

# A non-selected tier can be a perfectly good probe! In particular do NOT treat
# qualityAccepted=False / EXPOSURE_TIER_SELECTED alone as permission to delete.
REJECT_CODES = frozenset({
    "TARGET_SKY_CONTRAST_LOW", "NO_POSITIVE_SPECTRAL_SIGNAL", "GUIDING_UNSTABLE",
    "NO_SAFE_SATURATION_BACKOFF", "NO_SAFE_EXPOSURE_TIER", "SATURATION_BACKOFF",
})


def _flag(value, expected: bool) -> bool:
    return value is expected or isinstance(value, str) and value.casefold() == str(expected).casefold()


def _role(metadata: dict) -> str:
    return str(metadata.get("effectiveStageRole", metadata.get("stageRole", ""))).upper()


def rejected_probe(evidence: dict) -> bool:
    meta = evidence.get("metadata") or {}
    return (evidence.get("kind") == "atr-probe-fits" and _role(meta) == "PROBE"
            and _flag(meta.get("qualityAccepted"), False)
            and meta.get("qualityCode") in REJECT_CODES
            and _flag(meta.get("fitsProvenanceVerified"), True)
            and bool(meta.get("captureId")) and len(str(evidence.get("sha256", ""))) == 64)


class ProbeRetentionPlanner(CleanupPlanner):
    def __init__(self, catalog, *, authorized: bool = False, require_science: bool = True):
        super().__init__(catalog)
        self.authorized = authorized
        self.require_science = require_science

    def _project_rows(self):
        # Retention needs membership, not UI aggregate science counts for tens
        # of thousands of simulation runs. The base loader still traverses ALL
        # file/run/continuation and reduction links for dependency protection.
        for row in self.connection.execute("SELECT id,target,source,state FROM runs"):
            yield {"id": "run:" + row[0], "run_id": row[0], "target": row[1], "source": row[2], "state": row[3]}
        for row in self.connection.execute("SELECT file_id FROM project_documents"):
            yield {"id": "reduction:" + str(row[0]), "run_id": ""}

    def _discover_metadata(self, snapshot):
        # New unrelated acquisition evidence may be produced while a historical
        # probe is cleaned. Read it rather than assuming every new JSON is an
        # input or disabling cleanup for the entire observing night.
        from .library_cleanup_plan import _reparse
        candidates = CandidatePaths(row["path"] for row in snapshot["files"].values()
                      if row["kind"] == "frame" and row["role"] == "PROBE" and not row["missing"]
                      and row["accepted"] == 0
                      and str(snapshot["runs"].get(row["run_id"], {}).get("state", "")).casefold() in TERMINAL_STATES)
        snapshot["live_unrelated_metadata"] = set()
        stack = [Path(root["path"]) for root in self.catalog.list_roots()]
        visited = set()
        while stack:
            path = stack.pop()
            if _canonical(path) in visited:
                continue
            visited.add(_canonical(path))
            try:
                if _reparse(path.lstat()):
                    raise OSError("reparse")
                with os.scandir(path) as entries:
                    for entry in entries:
                        info = entry.stat(follow_symlinks=False)
                        if _reparse(info):
                            raise OSError("reparse")
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(Path(entry.path))
                        elif Path(entry.name).suffix.lower() in METADATA_SUFFIXES:
                            row = snapshot["by_path"].get(_canonical(entry.path))
                            if not row or (info.st_size, info.st_mtime_ns) != (row["size_bytes"], row["mtime_ns"]):
                                if unrelated_metadata(Path(entry.path), candidates):
                                    snapshot["live_unrelated_metadata"].add(_canonical(entry.path))
                                else:
                                    snapshot["global_reasons"].append("new_or_changed_dependency_requires_rescan:" + entry.path)
            except OSError:
                snapshot["global_reasons"].append("registered_tree_discovery_incomplete")

    def _metadata_valid(self, row, facts, snapshot):
        # Old references remain in the snapshot. A newly read unrelated record
        # cannot invalidate other runs just because its cached parse is older.
        return (super()._metadata_valid(row, facts, snapshot)
                or row["path"] in snapshot.get("live_unrelated_metadata", ())
                or self._removed_qhy_record(row))

    def _metadata(self, snapshot):
        super()._metadata(snapshot)
        # This policy must never select PNGs or anything other than its probes.
        snapshot["proofs"].clear()
        if not self.authorized:
            return
        files, by_path = snapshot["files"], snapshot["by_path"]
        # Removed QHY job metadata is not an ATR input. Keep its indexed links
        # (so any old reference still protects) but do not let the operator's
        # deletion of a different camera's data poison the entire ATR run.
        candidate_runs = {row["run_id"] for row in files.values()
                          if row["kind"] == "frame" and row["role"] == "PROBE"
                          and row["accepted"] == 0 and not row["missing"]}
        for run_id in candidate_runs:
            run = snapshot["runs"].get(run_id, {})
            if run.get("authority", 0) < 2 or run.get("source") != "real":
                continue
            manifest_row = by_path.get(_canonical(run.get("manifest_path", "")))
            if not manifest_row or not _same_index(manifest_row, _facts(manifest_row["path"])):
                continue
            record = _json(manifest_row["path"])
            if (not record or record.get("observationRunId") != run_id
                    or str(record.get("terminalState", "")).casefold() not in TERMINAL_STATES):
                continue
            grouped = defaultdict(list)
            for evidence in record.get("evidence", []):
                if isinstance(evidence, dict) and isinstance(evidence.get("absolutePath"), str):
                    grouped[_canonical(evidence["absolutePath"])].append(evidence)
            science = []
            for path, records in grouped.items():
                if any(_role(e.get("metadata") or {}) == "SCIENCE"
                       and _flag((e.get("metadata") or {}).get("qualityAccepted"), True) for e in records):
                    row = by_path.get(path)
                    if row and row["kind"] == "frame" and not row["warning"] and _same_index(row, _facts(path)):
                        science.append(row)
            if self.require_science and not science:
                continue
            for path, records in grouped.items():
                row = by_path.get(path)
                if (not row or row["kind"] != "frame" or row["role"] != "PROBE"
                        or row["accepted"] != 0 or row["missing"] or row["run_id"] != run_id):
                    continue
                # Any effective science/reused/accepted/unknown second record
                # wins over the original rejection. Never delete by file name.
                if not records or not all(rejected_probe(e) for e in records):
                    continue
                evidence = records[0]
                meta = evidence["metadata"]
                if (meta.get("observationRunId") != run_id or row["capture_id"] != meta["captureId"]
                        or len({str(e["sha256"]).lower() for e in records}) != 1):
                    continue
                # Other indexed descriptions can include later reuse, including
                # a continuation segment. Conflicting facts always protect.
                details = self.connection.execute(
                    "SELECT details_json FROM frame_details WHERE target_path=?", (path,)).fetchall()
                if any((str((d := json.loads(value[0])).get("role", "")).upper() not in {"", "PROBE"}
                        or _flag(d.get("accepted"), True)) for value in details):
                    continue
                review = self.connection.execute("SELECT decision,rating FROM reviews WHERE file_id=?", (row["id"],)).fetchone()
                if review and (review[0] == "pick" or review[1] > 0):
                    continue
                snapshot["proofs"][row["id"]] = {
                    "producer": "atr-rejected-probe", "source_ids": [manifest_row["id"]],
                    "raw_id": science[0]["id"] if science else manifest_row["id"],
                    "run_id": run_id, "capture_id": meta["captureId"], "quality_code": meta["qualityCode"],
                    "sha256": evidence["sha256"].lower(), "require_science": self.require_science,
                }

    @staticmethod
    def _removed_qhy_record(row):
        path = Path(row["path"])
        return (row["missing"] and not row["warning"] and row["parser_version"] == PARSER_VERSION and not path.exists()
                and path.name in {"manifest.json", "frames.jsonl"}
                and path.parent.name.lower().startswith("qhy-")
                and path.parent.parent.name.lower().startswith("uvex-")
                and path.parent.parent.parent.name.lower() == "runs"
                and "qhy" in [part.lower() for part in path.parts])

    def _disposable_category(self, category, proof):
        return (self.authorized and category == "raw" and proof
                and proof.get("producer") == "atr-rejected-probe")

    def _output_relations(self, proof):
        return frozenset({"absolutepath", "evidence", "evidencepath"})

    def _classify(self, row, snapshot, *, exact=False, ancestors=None):
        item = super()._classify(row, snapshot, exact=exact, ancestors=ancestors)
        if exact and item["candidate"]:
            # Read original header and content, not a filename or stale DB role.
            # Revalidation repeats this before the OS locked-handle executor.
            try:
                from astropy.io import fits
                h = fits.getheader(item["path"], 0)
                proof = item["proof"]
                with Path(item["path"]).open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                if (h.get("OBSRUNID") != proof["run_id"] or h.get("UVEXCID") != proof["capture_id"]
                        or h.get("UVEXSTG") != "PROBE" or digest != proof["sha256"]):
                    raise ValueError("Probe identity/content differs from its recorded evidence")
            except (OSError, ValueError, KeyError):
                item.update(candidate=False, protected=True, allocated_bytes=0)
                item["reasons"].append("probe_header_or_recorded_sha256_mismatch")
        return item

    def preview_probes(self):
        snapshot = self._load(force=True)
        projects, ids = set(), set()
        for identifier, proof in snapshot["proofs"].items():
            row = snapshot["files"][identifier]
            if self._classify(row, snapshot, exact=True)["candidate"]:
                projects.add("run:" + proof["run_id"])
                ids.add(identifier)
        if not ids:
            return None
        # Build from this just-read inventory, then perform an independent full
        # revalidation under the watcher at execution. No duplicate full walk
        # merely to construct the same preview, and no cached execution authority.
        return self._preview_from_snapshot(snapshot, projects, "permanent", ids)
