"""Opt-in target-success retention using the library's exact-file executor.

No acquisition or device API. The ordinary cleanup planner still protects all
raws. These policies need explicit caller authorization and terminal manifests.
The legacy rejected-probe policy remains available; the target-success policy
also includes QHY/G3 solve frames and every unaccepted ATR trial except a trial
that was credited as science.
"""
from __future__ import annotations

from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re

from .library_catalog import PARSER_VERSION, _canonical
from .library_cleanup_plan import (
    CleanupPlanner,
    TERMINAL_STATES,
    _PROVENANCE_RELATIONS,
    _facts,
    _json,
    _same_index,
)
from .library_probe_retention_watch import CandidatePaths, METADATA_SUFFIXES, unrelated_metadata

# A non-selected tier can be a perfectly good probe! In particular do NOT treat
# qualityAccepted=False / EXPOSURE_TIER_SELECTED alone as permission to delete.
REJECT_CODES = frozenset({
    "TARGET_SKY_CONTRAST_LOW", "NO_POSITIVE_SPECTRAL_SIGNAL", "GUIDING_UNSTABLE",
    "NO_SAFE_SATURATION_BACKOFF", "NO_SAFE_EXPOSURE_TIER", "SATURATION_BACKOFF",
})

TARGET_SUCCESS_POLICY = "target-success"
REJECTED_PROBE_POLICY = "rejected-probes"


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


def uncredited_probe(evidence: dict) -> bool:
    """A fully identified ATR trial not itself carrying science credit.

    The caller separately protects every path with an ATR science record. Unknown
    quality acceptance, provenance, capture identity or hash protects the file.
    """
    meta = evidence.get("metadata") or {}
    quality = meta.get("qualityAccepted")
    explicit_quality = (isinstance(quality, bool)
                        or isinstance(quality, str) and quality.casefold() in {"true", "false"})
    return (evidence.get("kind") == "atr-probe-fits" and _role(meta) == "PROBE"
            and explicit_quality
            and _flag(meta.get("fitsProvenanceVerified"), True)
            and bool(meta.get("captureId")) and len(str(evidence.get("sha256", ""))) == 64)


def _fits_path(path: str) -> bool:
    return Path(path).suffix.casefold() in {".fit", ".fits"}


def _utc(value):
    if isinstance(value, datetime):
        parsed = value
    elif isinstance(value, str) and value:
        try:
            parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    else:
        return None
    return parsed.replace(tzinfo=timezone.utc) if parsed.tzinfo is None else parsed.astimezone(timezone.utc)


def _run_id_utc(run_id):
    match = re.search(r"(?<!\d)(\d{8}T\d{6})Z", str(run_id), re.IGNORECASE)
    if not match:
        return None
    try:
        return datetime.strptime(match.group(1), "%Y%m%dT%H%M%S").replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def target_success_parse_frame(evidence: dict) -> bool:
    """Only detached acquisition/solve FITS are candidates.

    ATR science and QHY photometry kinds never match this function. Unknown
    kinds stay protected rather than being deleted by a broad prefix guess.
    """
    kind = str(evidence.get("kind", ""))
    path = str(evidence.get("absolutePath", ""))
    if not _fits_path(path):
        return False
    return kind == "qhy-acquisition-fits" or kind.startswith("g3-")


class ProbeRetentionPlanner(CleanupPlanner):
    def __init__(self, catalog, *, authorized: bool = False, require_science: bool = True,
                 policy: str = REJECTED_PROBE_POLICY, not_before_utc=None):
        if policy not in {REJECTED_PROBE_POLICY, TARGET_SUCCESS_POLICY}:
            raise ValueError(f"Unknown retention policy: {policy}")
        super().__init__(catalog)
        self.authorized = authorized
        self.require_science = require_science
        self.policy = policy
        self.not_before_utc = _utc(not_before_utc) if not_before_utc is not None else None
        if not_before_utc is not None and self.not_before_utc is None:
            raise ValueError("Target-success activation time is not a valid UTC timestamp")

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
        candidates = CandidatePaths(
            row["path"] for row in snapshot["files"].values()
            if self._candidate_run_seed(row)
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
        # These policies must never select PNGs or records.
        snapshot["proofs"].clear()
        if not self.authorized:
            return
        files, by_path = snapshot["files"], snapshot["by_path"]
        candidate_runs = {row["run_id"] for row in files.values()
                          if self._candidate_run_seed(row)}
        for run_id in candidate_runs:
            run = snapshot["runs"].get(run_id, {})
            if run.get("authority", 0) < 2 or run.get("source") != "real":
                continue
            run_started = _run_id_utc(run_id)
            if (self.policy == TARGET_SUCCESS_POLICY and self.not_before_utc is not None
                    and run_started is not None and run_started < self.not_before_utc):
                continue
            manifest_row = by_path.get(_canonical(run.get("manifest_path", "")))
            if not manifest_row or not _same_index(manifest_row, _facts(manifest_row["path"])):
                continue
            record = _json(manifest_row["path"])
            terminal = str(record.get("terminalState", "")).casefold() if record else ""
            terminal_ok = terminal == "completed" if self.policy == TARGET_SUCCESS_POLICY else terminal in TERMINAL_STATES
            if (not record or record.get("observationRunId") != run_id or not terminal_ok):
                continue
            if self.policy == TARGET_SUCCESS_POLICY and self.not_before_utc is not None:
                start = _utc((record.get("plan") or {}).get("plannedStartUtc")) or run_started
                if start is None or start < self.not_before_utc:
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
            science_paths = {row["path"] for row in science}
            for path, records in grouped.items():
                row = by_path.get(path)
                if (not row or row["kind"] != "frame" or row["missing"]
                        or row["run_id"] != run_id or path in science_paths):
                    continue
                if self.policy == TARGET_SUCCESS_POLICY:
                    producer = self._target_success_producer(records)
                    if producer is None:
                        continue
                    if producer == "target-success-uncredited-probe" and any(
                            (e.get("metadata") or {}).get("observationRunId") != run_id
                            for e in records):
                        continue
                else:
                    if row["role"] != "PROBE" or row["accepted"] != 0:
                        continue
                    producer = "atr-rejected-probe"
                    # Any effective science/reused/accepted/unknown second
                    # record wins over the original rejection.
                    if not records or not all(rejected_probe(e) for e in records):
                        continue
                if not records:
                    continue
                evidence = records[0]
                meta = evidence.get("metadata") or {}
                hashes = {str(e.get("sha256", "")).lower() for e in records}
                if (len(hashes) != 1 or len(next(iter(hashes))) != 64
                        or (self.policy == REJECTED_PROBE_POLICY and
                            (meta.get("observationRunId") != run_id
                             or row["capture_id"] != meta["captureId"]))):
                    continue
                # Other indexed descriptions can include later reuse, including
                # a continuation segment. Conflicting facts always protect.
                details = self.connection.execute(
                    "SELECT details_json FROM frame_details WHERE target_path=?", (path,)).fetchall()
                if any(self._details_conflict(json.loads(value[0])) for value in details):
                    continue
                review = self.connection.execute("SELECT decision,rating FROM reviews WHERE file_id=?", (row["id"],)).fetchone()
                if review and (review[0] == "pick" or review[1] > 0):
                    continue
                source_ids = {manifest_row["id"]}
                for link in snapshot["inbound"].get(path, ()):
                    source_row = files.get(link.get("source_file_id"))
                    if source_row and (
                            link.get("run_id") == run_id or source_row.get("run_id") == run_id):
                        source_ids.add(source_row["id"])
                snapshot["proofs"][row["id"]] = {
                    "producer": producer, "source_ids": sorted(source_ids),
                    "raw_id": science[0]["id"] if science else manifest_row["id"],
                    "run_id": run_id, "capture_id": meta.get("captureId", ""),
                    "quality_code": meta.get("qualityCode", ""),
                    "sha256": next(iter(hashes)), "require_science": self.require_science,
                }

    def _candidate_run_seed(self, row):
        if row["kind"] != "frame" or row["missing"] or not _fits_path(row["path"]):
            return False
        if self.policy == REJECTED_PROBE_POLICY:
            return row["role"] == "PROBE" and row["accepted"] == 0
        role = str(row.get("role") or "").upper()
        return (role == "PROBE" or row.get("accepted") in {None, 0}
                and (role.startswith("ACQUISITION") or
                     str(row.get("channel") or "").casefold() == "slit" and
                     "g3" in Path(row["path"]).name.casefold()))

    @staticmethod
    def _target_success_producer(records):
        if not records:
            return None
        if all(target_success_parse_frame(e) for e in records):
            return "target-success-parse-frame"
        if all(uncredited_probe(e) and e.get("metadata", {}).get("observationRunId") for e in records):
            return "target-success-uncredited-probe"
        return None

    def _details_conflict(self, details):
        role = str(details.get("role", "")).upper()
        if role == "SCIENCE" or (
                self.policy == REJECTED_PROBE_POLICY and _flag(details.get("accepted"), True)):
            return True
        return role not in {"", "PROBE", "ACQUISITION", "ACQUISITION-WIDE-STREAM"}

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
                and proof.get("producer") in {
                    "atr-rejected-probe", "target-success-parse-frame",
                    "target-success-uncredited-probe",
                })

    def _output_relations(self, proof):
        if self.policy == TARGET_SUCCESS_POLICY:
            return _PROVENANCE_RELATIONS
        return frozenset({"absolutepath", "evidence", "evidencepath"})

    def _classify(self, row, snapshot, *, exact=False, ancestors=None):
        item = super()._classify(row, snapshot, exact=exact, ancestors=ancestors)
        if exact and item["candidate"]:
            try:
                from astropy.io import fits
                h = fits.getheader(item["path"], 0)
                proof = item["proof"]
                with Path(item["path"]).open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                if proof["producer"] == "target-success-parse-frame":
                    header_run = h.get("OBSRUNID")
                    stage = str(h.get("UVEXSTG", "")).upper()
                    if ((header_run and header_run != proof["run_id"])
                            or stage in {"PROBE", "SCIENCE"} or digest != proof["sha256"]):
                        raise ValueError("Parse frame identity/content differs from its recorded evidence")
                elif (h.get("OBSRUNID") != proof["run_id"] or h.get("UVEXCID") != proof["capture_id"]
                      or h.get("UVEXSTG") != "PROBE" or digest != proof["sha256"]):
                    raise ValueError("Probe identity/content differs from its recorded evidence")
            except (OSError, ValueError, KeyError):
                item.update(candidate=False, protected=True, allocated_bytes=0)
                item["reasons"].append("probe_header_or_recorded_sha256_mismatch")
        return item

    def preview_candidates(self):
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

    def preview_probes(self):
        """Backward-compatible name used by the legacy rejected-probe policy."""
        return self.preview_candidates()


class TargetSuccessRetentionPlanner(ProbeRetentionPlanner):
    def __init__(self, catalog, *, authorized=False, require_science=True, not_before_utc=None):
        super().__init__(catalog, authorized=authorized, require_science=require_science,
                         policy=TARGET_SUCCESS_POLICY, not_before_utc=not_before_utc)
