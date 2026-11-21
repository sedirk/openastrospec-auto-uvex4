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
TERMINAL_TARGET_POLICY = "terminal-target"
REJECTED_PROBE_POLICY = "rejected-probes"
OWNER_SPACE_RECLAIM_POLICY = "owner-space-reclaim"


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
        if policy not in {REJECTED_PROBE_POLICY, TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY}:
            raise ValueError(f"Unknown retention policy: {policy}")
        super().__init__(catalog)
        self.authorized = authorized
        self.require_science = require_science if policy != TERMINAL_TARGET_POLICY else False
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
        self._retention_metadata(snapshot)

    def _retention_metadata(self, snapshot):
        # These policies must never select PNGs or records.
        snapshot["proofs"].clear()
        if not self.authorized:
            return
        files, by_path = snapshot["files"], snapshot["by_path"]
        credited = {str(r["capture_id"]).casefold() for r in files.values()
                    if r.get("capture_id") and r.get("role") == "SCIENCE"}
        candidate_runs = {row["run_id"] for row in files.values()
                          if self._candidate_run_seed(row)}
        for run_id in candidate_runs:
            run = snapshot["runs"].get(run_id, {})
            if run.get("authority", 0) < 2 or run.get("source") != "real":
                continue
            run_started = _run_id_utc(run_id)
            if (self.policy in {TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY}
                    and self.not_before_utc is not None
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
            if self.policy == TERMINAL_TARGET_POLICY and str(run.get("state", "")).casefold() != terminal:
                continue
            if (self.policy in {TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY}
                    and self.not_before_utc is not None):
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
            science_captures = {str((e.get("metadata") or {}).get("captureId") or "").casefold()
                                for records in grouped.values() for e in records
                                if e.get("kind") == "atr-science-fits"
                                or _role(e.get("metadata") or {}) == "SCIENCE"} - {""}
            for path, records in grouped.items():
                row = by_path.get(path)
                if (not row or row["kind"] != "frame" or row["missing"]
                        or row["run_id"] != run_id or path in science_paths):
                    continue
                if self.policy in {TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY}:
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
                if self.policy == TERMINAL_TARGET_POLICY and producer == "target-success-uncredited-probe":
                    capture = str(meta.get("captureId", "")).casefold()
                    if (capture in science_captures or str(row["capture_id"]).casefold() != capture
                            or capture in credited):
                        continue
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
                    if (source_row and source_row.get("run_id") == run_id
                            and link.get("run_id") in {"", None, run_id}):
                        source_ids.add(source_row["id"])
                snapshot["proofs"][row["id"]] = {
                    "producer": producer, "source_ids": sorted(source_ids),
                    "raw_id": science[0]["id"] if science else manifest_row["id"],
                    "run_id": run_id, "capture_id": meta.get("captureId", ""),
                    "quality_code": meta.get("qualityCode", ""),
                    "sha256": next(iter(hashes)), "require_science": self.require_science,
                }
                if self.policy == TERMINAL_TARGET_POLICY:
                    snapshot["proofs"][row["id"]]["manifest_id"] = manifest_row["id"]
                    if evidence.get("kind") == "qhy-acquisition-fits":
                        snapshot["proofs"][row["id"]]["qhy_identity"] = (
                            (record.get("plan", {}).get("target") or {}).get("name"),
                            meta.get("role"), meta.get("jobId"), meta.get("frameId"))

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
                and path.parent.parent.parent.name.lower() == "runs"
                and "qhy" in [part.lower() for part in path.parts])

    def _disposable_category(self, category, proof):
        if not self.authorized or not proof:
            return False
        producer = proof.get("producer")
        return category == "raw" and producer in {
            "atr-rejected-probe", "target-success-parse-frame",
            "target-success-uncredited-probe",
        } or category == "preview" and producer == "terminal-target-qhy-preview"

    def _output_relations(self, proof):
        if self.policy in {TARGET_SUCCESS_POLICY, TERMINAL_TARGET_POLICY}:
            return _PROVENANCE_RELATIONS
        return frozenset({"absolutepath", "evidence", "evidencepath"})

    def _classify(self, row, snapshot, *, exact=False, ancestors=None):
        item = super()._classify(row, snapshot, exact=exact, ancestors=ancestors)
        if exact and item["candidate"]:
            # The QHY frame index already establishes the exact raw/preview
            # pair and its terminal job/run. Avoid hashing 180+ GiB during the
            # one-time reclaim pass; executor identity checks still run.
            if item["proof"]["producer"] in {
                    "owner-photometry-fits", "owner-photometry-preview",
                    "owner-g3-preshot", "terminal-target-qhy-preview",
            }:
                return item
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
                elif (proof["producer"] not in {
                          "owner-photometry-fits", "owner-photometry-preview",
                      }
                      and (h.get("OBSRUNID") != proof["run_id"]
                           or h.get("UVEXCID") != proof["capture_id"]
                           or h.get("UVEXSTG") != "PROBE"
                           or digest != proof["sha256"])):
                    raise ValueError("Probe identity/content differs from its recorded evidence")
            except (OSError, ValueError, KeyError):
                item.update(candidate=False, protected=True, allocated_bytes=0)
                item["reasons"].append("probe_header_or_recorded_sha256_mismatch")
        return item

    def preview_candidates(self, *, allow_item=None):
        snapshot = self._load(force=True)
        projects, ids = set(), set()
        for identifier, proof in snapshot["proofs"].items():
            row = snapshot["files"][identifier]
            if self._classify(row, snapshot, exact=True)["candidate"] and (allow_item is None or allow_item(row)):
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


class TerminalTargetRetentionPlanner(ProbeRetentionPlanner):
    """Clean disposable per-target frames after any explicit terminal outcome."""

    def __init__(self, catalog, *, authorized=False, not_before_utc=None):
        super().__init__(catalog, authorized=authorized, require_science=False,
                         policy=TERMINAL_TARGET_POLICY, not_before_utc=not_before_utc)

    def _metadata_valid(self, row, facts, snapshot):
        if super()._metadata_valid(row, facts, snapshot):
            return True
        # The native owner renames this tiny control record and persists an
        # explicit receipt. Do not suppress generic missing/invalid metadata.
        path = Path(row["path"])
        guide = path.name in {"g3-acquisition-motion.json", "phd2-lock-shift-pending.json"}
        if (not row["missing"] or row["warning"] or row["parser_version"] != PARSER_VERSION
                or not (guide or path.name == "native-meridian-flip.json") or path.parent.name != "control"
                or facts["filesystem_reasons"] != ["missing_or_offline"]):
            return False
        marker = "terminal" if guide else "pre-dispatch"
        guards = []
        for receipt in path.parent.glob(path.name + "." + marker + "-*.archive.audit.json"):
            archive = Path(str(receipt)[:-len(".audit.json")])
            if not re.fullmatch(re.escape(path.name + "." + marker) + r"-[0-9a-f]{32}\.archive", archive.name):
                continue
            a, b = (snapshot["by_path"].get(_canonical(p)) for p in (archive, receipt))
            if (not a or not b or any(r["warning"] or r["missing"] or
                    not _same_index(r, _facts(r["path"])) for r in (a,b)) or
                    (not guide and (a["size_bytes"],a["mtime_ns"]) != (row["size_bytes"],row["mtime_ns"]))
                    or (guide and a["mtime_ns"] < row["mtime_ns"])
                    or a["size_bytes"] > 16384):
                continue
            audit, entry = _json(str(receipt)), _json(str(archive))
            if (not audit or not entry or audit.get("motionSent") is not False
                    or _canonical(audit.get("originalPath") or ".") != str(path)
                    or _canonical(audit.get("archivePath") or ".") != _canonical(archive)
                    or hashlib.sha256(archive.read_bytes()).hexdigest() != str(audit.get("originalSha256")).lower()):
                continue
            if guide:
                state = entry.get("state") or {}
                if (audit.get("action") != "TerminalTargetGuideStateRetired" or not row["run_id"]
                        or audit.get("runId") != row["run_id"] or state.get("observationRunId") != row["run_id"]
                        or path.parent.parent.name.casefold() != row["run_id"].casefold()):
                    continue
                # Retain every indexed reference, and reject unfamiliar archived
                # file/directory inputs rather than assuming they're unrelated.
                links = {x["target_path"] for x in snapshot["links_from"].get(row["id"], ())}
                if not self._archive_inputs_known(entry, links):
                    continue
                snapshot["dependency_paths"].update(links)
            elif (audit.get("action") != "EndedPreDispatchMeridianIntentRetired"
                    or snapshot["links_from"].get(row["id"]) or audit.get("Phase") != "OwnersIdle"
                    or entry.get("Phase") != "OwnersIdle" or entry.get("RunId") != audit.get("oldRunId")
                    or entry.get("Segments") != audit.get("segments")
                    or set(entry) != {"RunId","Phase","BeforeSide","AfterSide","UpdatedUtc","Segments","Reason"}
                    or not all(isinstance(s,str) and re.fullmatch(r"[A-Za-z0-9_-]+",s)
                               for s in [entry["RunId"],entry["Reason"],*entry["Segments"]])
                    or entry["BeforeSide"] not in {"pierEast","pierWest"} or entry["AfterSide"] != ""
                    or not _utc(entry["UpdatedUtc"])):
                continue
            guards.append((a["id"],b["id"]))
        if len(guards) != 1:
            return False
        snapshot.setdefault("extra_guard_ids", set()).update((row["id"], *guards[0]))
        return True

    @staticmethod
    def _archive_inputs_known(value, links, key=""):
        if isinstance(value, dict):
            return all(TerminalTargetRetentionPlanner._archive_inputs_known(v, links, str(k))
                       for k,v in value.items())
        if isinstance(value, list):
            return all(TerminalTargetRetentionPlanner._archive_inputs_known(v, links, key) for v in value)
        if not isinstance(value, str) or not value:
            return True
        # Literal producer prose, not a relative file named "copy/copies".
        if key == "lastReason" and re.fullmatch(
                r"Monotonic G3 lineage aggregate adopted from \d+ trustworthy durable copy/copies "
                r"without counter or clock rollback\.", value):
            return True
        if any(c in value for c in "*?[]") or key.casefold().endswith(("root","directory")):
            return False
        if any(c in value for c in ("/","\\")) or key.casefold().endswith("path") or ".fit" in value.casefold():
            return os.path.isabs(value) and _canonical(value) in links
        return True

    def _metadata(self, snapshot):
        CleanupPlanner._metadata(self, snapshot)
        indexed_pairs = dict(snapshot["proofs"])
        self._retention_metadata(snapshot)
        previews = {}
        for raw_id, proof in tuple(snapshot["proofs"].items()):
            if proof.get("producer") != "target-success-parse-frame":
                continue
            raw = snapshot["files"].get(raw_id)
            if (not raw or raw.get("kind") != "frame"
                    or str(raw.get("channel", "")).casefold() != "photometry"
                    or not str(raw.get("role", "")).upper().startswith("ACQUISITION")):
                continue
            if raw["warning"] and self._verified_witness_alias(raw, proof, snapshot):
                # Private snapshot only. Keep the original FITS and catalog
                # warning intact; repeat verification on each fresh snapshot.
                raw["warning"] = ""
            if raw["warning"]:
                continue
            preview_path = Path(str(raw["path"]).replace("\\raw\\", "\\preview\\")).with_suffix(".png")
            preview = snapshot["by_path"].get(_canonical(preview_path))
            if not preview or preview.get("kind") != "preview" or preview.get("missing"):
                continue
            pair = indexed_pairs.get(preview["id"]) or {}
            if (pair.get("producer") != "qhy-frame-index" or pair.get("run_id") != proof["run_id"]
                    or pair.get("raw_id") != raw_id):
                continue
            review = self.connection.execute("SELECT decision,rating FROM reviews WHERE file_id=?",
                                             (preview["id"],)).fetchone()
            if review and (review[0] == "pick" or review[1] > 0):
                continue
            previews[preview["id"]] = {
                **proof,
                "producer": "terminal-target-qhy-preview",
                "raw_id": raw["id"],
                "source_ids": sorted({*proof.get("source_ids", ()), *pair["source_ids"], raw["id"]}),
            }
        snapshot["proofs"].update(previews)

    def _is_output_link(self, link, relation, proof, snapshot):
        # Only the run manifest that explicitly proved this very output.
        # Identically named fields in e.g. a fitted model still protect it.
        if relation == "evidencepath":
            return link["source_file_id"] == proof.get("manifest_id") and link.get("run_id") == proof["run_id"]
        return super()._is_output_link(link, relation, proof, snapshot)

    @staticmethod
    def _verified_witness_alias(row, proof, snapshot):
        from astropy.io import fits
        identity = proof.get("qhy_identity")
        if not identity:
            return False
        name, role, job_id, frame_id = identity
        if (not all(isinstance(x, str) and x for x in identity)
                or role != row["role"] or role != "ACQUISITION-WIDE-STREAM"
                or frame_id != row["capture_id"]):
            return False
        recorded = name + " / continuous acquisition witness"
        if (row["target"] != recorded or row["warning"] !=
                f"Identity conflict (target): FITS={recorded!r}; linked metadata={name!r}; FITS identity retained"):
            return False
        path = Path(row["path"])
        job_dir = path.parent.parent
        job_meta = snapshot["by_path"].get(_canonical(job_dir / "manifest.json"))
        index = snapshot["by_path"].get(_canonical(job_dir / "frames.jsonl"))
        if (path.parent.name.lower() != "raw" or job_dir.name.lower() != "qhy-" + job_id.replace("-", "").lower()
                or job_dir.parent.name.casefold() != proof["run_id"].casefold()
                or not all(r and not r["warning"] and not r["missing"]
                           and _same_index(r, snapshot["metadata_facts"].get(r["id"], {}))
                           for r in (job_meta, index)) or index["id"] not in proof["source_ids"]):
            return False
        try:
            cache = snapshot.setdefault("qhy_job_identities", {})
            if job_dir not in cache:
                job = _json(job_meta["path"]) or {}
                records = {}
                with open(index["path"], encoding="utf-8-sig") as stream:
                    for line in stream:
                        if line.strip():
                            entry = json.loads(line)
                            key = entry["frameId"]
                            records[key] = None if key in records else entry
                cache[job_dir] = (job, records)
            job, records = cache[job_dir]
            frame = records.get(frame_id) or {}
            if (job.get("id") != job_id or job.get("observationRunId") != proof["run_id"]
                    or str(job.get("state")).casefold() not in TERMINAL_STATES or not job.get("completedUtc")
                    or frame.get("role") != role or not frame.get("fitsPath")
                    or _canonical(frame["fitsPath"]) != row["path"]
                    or str(frame.get("sha256", "")).lower() != proof["sha256"]):
                return False
            header = fits.getheader(path, 0)
            if not all(header.get(k) == v for k, v in (
                    ("OBJECT", recorded), ("FRAMROLE", role), ("OBS-RUN", proof["run_id"]),
                    ("JOBID", job_id), ("FRAMEID", frame_id))):
                return False
            with path.open("rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != proof["sha256"]:
                    return False
            # The QHY job manifest stores a rolling tail (32 frames); the
            # complete frame index, not that tail, proves exact membership.
            proof["source_ids"] = sorted({*proof["source_ids"],job_meta["id"],index["id"]})
            return True
        except (OSError, ValueError, KeyError, TypeError):
            return False


class OwnerSpaceReclaimRetentionPlanner(TargetSuccessRetentionPlanner):
    """One-time owner authorization for disposable acquisition media.

    In addition to historical successful-target probes/solve frames, this
    removes every QHY photometry-camera FITS and its paired PNG after the QHY
    job and observation run are confirmed terminal. It never includes active or
    unknown jobs and the ordinary dependency scanner still protects any image
    referenced by reduction/project state.
    """

    def __init__(self, catalog, *, authorized=False):
        super().__init__(catalog, authorized=authorized, not_before_utc=None)

    def _metadata(self, snapshot):
        # Discover the automatically verified QHY job/frame-index proofs first.
        CleanupPlanner._metadata(self, snapshot)
        photometry = {}
        for identifier, proof in snapshot["proofs"].items():
            if proof.get("producer") != "qhy-frame-index":
                continue
            raw = snapshot["files"].get(proof.get("raw_id"))
            if not raw or not self._photometry_frame(raw):
                continue
            source_ids = {*proof.get("source_ids", ()), raw["id"]}
            # Keep same-run evidence JSON as audit provenance while allowing its
            # exact QHY image to be reclaimed. Records from projects/other runs
            # remain ordinary dependency edges and continue to protect the file.
            for link in snapshot["inbound"].get(raw["path"], ()):
                source = snapshot["files"].get(link.get("source_file_id"))
                if (source and source["kind"] == "metadata"
                        and source.get("run_id") == proof["run_id"]):
                    source_ids.add(source["id"])
            source_ids = sorted(source_ids)
            photometry[raw["id"]] = {
                **proof,
                "producer": "owner-photometry-fits",
                "raw_id": raw["id"],
                "source_ids": source_ids,
            }
            photometry[identifier] = {
                **proof,
                "producer": "owner-photometry-preview",
                "source_ids": source_ids,
            }
        for row in snapshot["files"].values():
            if (row["id"] in photometry or not self._photometry_frame(row)
                    or row["missing"] or not _fits_path(row["path"])):
                continue
            proof = self._terminal_unindexed_photometry_proof(snapshot, row)
            if not proof:
                continue
            photometry[row["id"]] = proof
            sibling = Path(row["path"]).with_suffix(".png")
            sibling = Path(str(sibling).replace("\\raw\\", "\\preview\\"))
            preview_row = snapshot["by_path"].get(_canonical(sibling))
            if preview_row and preview_row["kind"] == "preview":
                photometry[preview_row["id"]] = {
                    **proof,
                    "producer": "owner-photometry-preview",
                }
        # Apply the established successful-target trial/solve policy, then add
        # the separately authorized photometry pairs to the same exact plan.
        self._retention_metadata(snapshot)
        snapshot["proofs"].update(photometry)
        self._add_successful_g3_preshots(snapshot)

    def _terminal_unindexed_photometry_proof(self, snapshot, row):
        """Fallback for a final frame omitted from a crashed frame index."""
        path = Path(row["path"])
        job = next((parent for parent in path.parents
                    if parent.name.casefold().startswith("qhy-")), None)
        run = snapshot["runs"].get(row["run_id"], {})
        run_manifest = snapshot["by_path"].get(_canonical(run.get("manifest_path", "")))
        if (not job or not run_manifest or run.get("authority", 0) < 2
                or str(run.get("state", "")).casefold() not in TERMINAL_STATES):
            return None
        manifest = snapshot["by_path"].get(_canonical(job / "manifest.json"))
        if (not manifest or manifest["warning"] or manifest["missing"]
                or not _same_index(manifest, _facts(manifest["path"]))):
            return None
        record = _json(manifest["path"])
        if (not record or record.get("observationRunId") != row["run_id"]
                or str(record.get("state", "")).casefold() not in TERMINAL_STATES
                or not record.get("completedUtc")):
            return None
        source_ids = {manifest["id"], run_manifest["id"], row["id"]}
        for link in snapshot["inbound"].get(row["path"], ()):
            source = snapshot["files"].get(link.get("source_file_id"))
            if source and source["kind"] == "metadata" and source.get("run_id") == row["run_id"]:
                source_ids.add(source["id"])
        return {
            "producer": "owner-photometry-fits",
            "source_ids": sorted(source_ids),
            "raw_id": row["id"],
            "run_id": row["run_id"],
        }

    def _add_successful_g3_preshots(self, snapshot):
        """Cover LED/slit-placement frames the evidence list did not enumerate."""
        files, by_path = snapshot["files"], snapshot["by_path"]
        for run_id, run in snapshot["runs"].items():
            if (run.get("authority", 0) < 2 or run.get("source") != "real"
                    or str(run.get("state", "")).casefold() != "completed"):
                continue
            manifest = by_path.get(_canonical(run.get("manifest_path", "")))
            if (not manifest or manifest["warning"] or manifest["missing"]
                    or not _same_index(manifest, _facts(manifest["path"]))):
                continue
            record = _json(manifest["path"])
            if (not record or record.get("observationRunId") != run_id
                    or str(record.get("terminalState", "")).casefold() != "completed"):
                continue
            grouped = defaultdict(list)
            for evidence in record.get("evidence", []):
                if isinstance(evidence, dict) and isinstance(evidence.get("absolutePath"), str):
                    grouped[_canonical(evidence["absolutePath"])].append(evidence)
            science = [
                by_path[path] for path, evidence in grouped.items()
                if path in by_path and any(
                    _role(item.get("metadata") or {}) == "SCIENCE"
                    and _flag((item.get("metadata") or {}).get("qualityAccepted"), True)
                    for item in evidence)
            ]
            if not science:
                continue
            science_paths = {row["path"] for row in science}
            for row in files.values():
                if (row["kind"] != "frame" or row["run_id"] != run_id
                        or row["missing"] or row["path"] in science_paths
                        or not self._g3_preshot_frame(row)):
                    continue
                if row["id"] in snapshot["proofs"]:
                    continue
                source_ids = {manifest["id"]}
                for link in snapshot["inbound"].get(row["path"], ()):
                    source_row = files.get(link.get("source_file_id"))
                    if source_row and source_row.get("run_id") == run_id:
                        source_ids.add(source_row["id"])
                snapshot["proofs"][row["id"]] = {
                    "producer": "owner-g3-preshot",
                    "source_ids": sorted(source_ids),
                    "raw_id": science[0]["id"],
                    "run_id": run_id,
                }

    @staticmethod
    def _photometry_frame(row):
        return (row.get("kind") == "frame"
                and (str(row.get("channel", "")).casefold() == "photometry"
                     or str(row.get("role", "")).upper().startswith("PHOTOMETRY")))

    @staticmethod
    def _g3_preshot_frame(row):
        role = str(row.get("role", "")).upper()
        return (row.get("kind") == "frame"
                and str(row.get("channel", "")).casefold() == "slit"
                and Path(row.get("path", "")).suffix.casefold() in {".fit", ".fits"}
                and (role in {"", "ACQUISITION"} or role.startswith("LED-")))

    def _classify(self, row, snapshot, *, exact=False, ancestors=None):
        item = super()._classify(row, snapshot, exact=exact, ancestors=ancestors)
        proof = item.get("proof") or {}
        if (exact and proof.get("producer") in {
                "owner-photometry-fits", "owner-photometry-preview",
        }):
            # These catalog warnings describe target-name presentation, not file
            # identity. The QHY frame index still proves the exact pair; changed
            # bytes, missing paths and all dependency reasons remain protected.
            removable = {
                "catalog_warning_requires_review",
                "paired_raw_missing_changed_or_unverified",
            }
            item["reasons"] = [reason for reason in item["reasons"] if reason not in removable]
            if (not item["reasons"] and item.get("exists")
                    and "source_changed_since_index" not in item["reasons"]):
                facts = _facts(item["path"], ancestors)
                if not facts["filesystem_reasons"] and _same_index(row, facts):
                    item.update({
                        "candidate": True, "protected": False,
                        "allocated_bytes": facts["allocated_bytes"],
                        "size_bytes": facts["size_bytes"],
                        "mtime_ns": facts["mtime_ns"],
                        "dev": facts["dev"], "ino": facts["ino"],
                        "nlink": facts["nlink"],
                    })
        return item

    def _disposable_category(self, category, proof):
        if super()._disposable_category(category, proof):
            return True
        return bool(self.authorized and proof
                    and proof.get("producer") in {
                        "owner-photometry-fits", "owner-photometry-preview",
                        "owner-g3-preshot",
                    }
                    and category in {"raw", "preview"})

    def _output_relations(self, proof):
        relations = super()._output_relations(proof)
        if proof.get("producer") in {
                "owner-photometry-fits", "owner-photometry-preview",
        }:
            return relations | {"evidencepath", "framepath", "qhyframepath"}
        return relations
