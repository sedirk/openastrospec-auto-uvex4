"""Conservative, read-only, exact-file cleanup plans for the offline library.

The index is evidence, not permission. Only explicitly recorded, reproducible
previews with a retained raw and stopped producer are physical candidates. All
raws (including rejected/probe/photometry/calibration), products, logs, records,
dependencies and ambiguous files are protected. No function here removes files.
"""

from __future__ import annotations

from collections import defaultdict
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import stat
import tomllib
from typing import Any, Iterable
import uuid

from .library_catalog import MAX_METADATA_BYTES, PARSER_VERSION, _canonical
from .library_projects import LibraryProjects


SCHEMA_VERSION = 1
TERMINAL_STATES = frozenset({"completed", "faulted", "failed", "cancelled", "canceled"})
ACTIONS = frozenset({"mark", "recycle", "permanent"})
_ISSUED: dict[str, str] = {}
_PROVENANCE_RELATIONS = frozenset({
    "previewpath", "fitspath", "absolutepath", "evidence", "manifestpath", "frameindexpath",
})
_METADATA_SUFFIXES = frozenset({".json", ".jsonl", ".astroproj", ".toml"})


def plan_digest(plan: dict[str, Any]) -> str:
    """Canonical digest for the complete preview, excluding its own digest field."""
    body = {key: value for key, value in plan.items() if key != "digest"}
    return hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False,
                                     separators=(",", ":")).encode("utf-8")).hexdigest()


def _within(path: str, root: str) -> bool:
    try:
        return os.path.commonpath((path, root)) == root
    except ValueError:
        return False


def _reparse(info: os.stat_result) -> bool:
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def _facts(path: str, ancestors: dict[str, bool] | None = None) -> dict[str, Any]:
    """lstat only, with junction checks; never follows a path to expand scope."""
    value: dict[str, Any] = {"path": path, "size_bytes": 0, "mtime_ns": 0,
                             "dev": 0, "ino": 0, "nlink": 0, "exists": False,
                             "regular": False, "unsafe": False,
                             "allocated_bytes": 0, "filesystem_reasons": []}
    cache = ancestors if ancestors is not None else {}
    # The file itself is checked once, fresh, below. Only ancestors share the
    # per-pass reparse cache; lstat-ing every file twice dominated large indexes.
    for part in reversed(Path(path).parents):
        key = str(part)
        if key not in cache:
            try:
                cache[key] = _reparse(part.lstat())
            except FileNotFoundError:
                cache[key] = False
            except OSError:
                cache[key] = True
        if cache[key]:
            value["unsafe"] = True
            value["filesystem_reasons"].append("symbolic_link_reparse_or_inaccessible_ancestor")
            return value
    try:
        info = os.lstat(path)
    except FileNotFoundError:
        value["filesystem_reasons"].append("missing_or_offline")
        return value
    except OSError:
        value["filesystem_reasons"].append("filesystem_access_unverified")
        return value
    if _reparse(info):
        value["unsafe"] = True
        value["filesystem_reasons"].append("symbolic_link_reparse_or_inaccessible_ancestor")
        return value
    value.update(size_bytes=info.st_size, mtime_ns=info.st_mtime_ns,
                 dev=info.st_dev, ino=info.st_ino, nlink=info.st_nlink,
                 exists=True, regular=stat.S_ISREG(info.st_mode))
    if not value["regular"]:
        value["filesystem_reasons"].append("not_regular_file")
    if info.st_nlink != 1:
        value["filesystem_reasons"].append("hardlink_or_link_count_unverified")
    # Sparse/compressed files need a physical allocation query. Protect rather
    # than silently equating logical length with released disk capacity.
    attrs = getattr(info, "st_file_attributes", 0)
    if attrs & (0x200 | 0x800):
        value["filesystem_reasons"].append("sparse_or_compressed_allocation_unverified")
    if value["regular"] and not value["filesystem_reasons"]:
        blocks = getattr(info, "st_blocks", None)
        value["allocated_bytes"] = min(info.st_size, int(blocks) * 512) if blocks is not None else info.st_size
    return value


def _same_index(row: dict[str, Any], facts: dict[str, Any]) -> bool:
    return (facts.get("exists") and facts.get("regular") and not facts.get("unsafe")
            and facts["size_bytes"] == row["size_bytes"]
            and facts["mtime_ns"] == row["mtime_ns"])


def _signature(facts: dict[str, Any]) -> dict[str, Any]:
    return {key: facts[key] for key in ("path", "exists", "size_bytes", "mtime_ns", "dev", "ino", "nlink")}


def _json(path: str) -> dict[str, Any] | None:
    try:
        if os.lstat(path).st_size > MAX_METADATA_BYTES:
            return None
        value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else None
    except (OSError, UnicodeError, ValueError):
        return None


class CleanupPlanner:
    """Project-sized inventory and in-process, digest-bound cleanup previews.

    ``inventory`` scans index tables once and reads only relevant metadata, not
    image pixels. Shared entries are displayed in each project but global totals
    are unique-file totals. Preview stats exact selected project members. Issued
    plans cannot be edited, loaded from arbitrary JSON or silently expanded.
    """

    def __init__(self, catalog: Any):
        self.catalog = catalog
        self.connection = catalog.connection
        self._snapshot: dict[str, Any] | None = None
        self._validated_tokens: dict[str, dict[str, int]] = {}
        self._validated_plans: dict[str, dict[str, Any]] = {}

    def _token(self) -> dict[str, int]:
        # This is compared only against a successful full validation on THIS
        # connection, never across the UI/executor worker connections. It avoids
        # WAL checkpoint/close invalidations of an otherwise identical index.
        return {"total_changes": self.connection.total_changes,
                "data_version": self.connection.execute("PRAGMA data_version").fetchone()[0]}

    def _identity(self) -> dict[str, Any]:
        path = _canonical(self.catalog.db_path)
        info = os.stat(path)
        return {"path": path, "dev": info.st_dev, "ino": info.st_ino}

    def _load(self, *, force: bool = False) -> dict[str, Any]:
        if self._snapshot is not None and not force:
            return self._snapshot
        files = {row["id"]: dict(row) for row in self.connection.execute("SELECT * FROM files")}
        by_path = {row["path"]: row for row in files.values()}
        runs = {row["id"]: dict(row) for row in self.connection.execute("SELECT * FROM runs")}
        projects = {row["id"]: row for row in self._project_rows()}
        owners: dict[int, set[str]] = defaultdict(set)
        parent_runs: dict[str, set[str]] = defaultdict(set)
        for project in projects.values():
            if project["id"].startswith("run:"):
                parent_runs[project["run_id"]].add(project["id"])
        for row in self.connection.execute("SELECT run_id,segment_run_id FROM project_run_segments"):
            if "run:" + row[0] in projects:
                parent_runs[row[1]].add("run:" + row[0])
        for row in self.connection.execute("SELECT file_id,run_id FROM file_runs"):
            owners[row[0]].update(parent_runs.get(row[1], ()))
        links = [dict(row) for row in self.connection.execute("SELECT * FROM links")]
        links_from: dict[int, list[dict[str, Any]]] = defaultdict(list)
        inbound: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for link in links:
            links_from[link["source_file_id"]].append(link)
            inbound[link["target_path"]].append(link)
            associated = parent_runs.get(link["run_id"], ())
            owners[link["source_file_id"]].update(associated)
            if link["target_path"] in by_path:
                owners[by_path[link["target_path"]]["id"]].update(associated)
        documents = {row[0] for row in self.connection.execute("SELECT file_id FROM project_documents")}
        for project_id in projects:
            if project_id.startswith("reduction:"):
                owners[int(project_id[10:])].add(project_id)
            elif project_id.startswith("night:"):
                owners[int(project_id.split(":")[1])].add(project_id)
        # Only reduction-document relationships propagate across inputs/outputs;
        # never merge acquisition projects simply because they share a bias.
        for _ in range(max(1, len(documents))):
            changed = False
            for identifier in documents:
                related = set(owners[identifier])
                for link in links_from[identifier]:
                    target = by_path.get(link["target_path"])
                    if target and str(link["relation"]).lower() in {
                        "science", "standard", "selected:science", "selected:standard", "sciencerun",
                        "standardrun", "scienceproduct", "standardproduct", "lastoutput", "last_output",
                    }:
                        related.update(owners[target["id"]])
                if related - owners[identifier]:
                    owners[identifier].update(related)
                    changed = True
                for link in links_from[identifier]:
                    target = by_path.get(link["target_path"])
                    if target and related - owners[target["id"]]:
                        owners[target["id"]].update(related)
                        changed = True
            if not changed:
                break
        for identifier in files:
            if not owners[identifier]:
                owners[identifier].add("unattributed")
        projects["unattributed"] = {"id": "unattributed", "target": "未关联项目 / 待核对",
                                      "source": "unknown", "outcome": "unknown", "night": ""}
        snapshot = {"files": files, "by_path": by_path, "runs": runs, "projects": projects,
                    "owners": owners, "documents": documents, "links_from": links_from,
                    "inbound": inbound, "proofs": {}, "metadata_facts": {},
                    "dependency_paths": set(), "dependency_roots": set(),
                    "global_reasons": [], "run_reasons": defaultdict(list),
                    "preview_reasons": defaultdict(list)}
        self._discover_metadata(snapshot)
        self._metadata(snapshot)
        snapshot["global_reasons"] = list(dict.fromkeys(snapshot["global_reasons"]))
        self._snapshot = snapshot
        return snapshot

    def _project_rows(self):
        return LibraryProjects(self.catalog)._rows()

    def _discover_metadata(self, snapshot: dict[str, Any]) -> None:
        """Detect newly created dependency records before trusting the old index.

        One bounded metadata-name traversal per inventory/preview/full validation,
        never once per deletion. Symlinks/junctions are not followed. An unknown
        document or inaccessible directory makes physical cleanup unavailable
        until a complete index update; it is not assumed unrelated.
        """
        visited: set[str] = set()
        stack = [root["path"] for root in self.catalog.list_roots()]
        while stack:
            path = stack.pop()
            key = _canonical(path)
            if key in visited:
                continue
            visited.add(key)
            try:
                if _reparse(os.lstat(path)):
                    snapshot["global_reasons"].append("registered_tree_contains_unverified_reparse_point")
                    continue
                with os.scandir(path) as entries:
                    for entry in entries:
                        info = entry.stat(follow_symlinks=False)
                        if _reparse(info):
                            snapshot["global_reasons"].append("registered_tree_contains_unverified_reparse_point")
                        elif stat.S_ISDIR(info.st_mode):
                            stack.append(entry.path)
                        elif Path(entry.name).suffix.casefold() in _METADATA_SUFFIXES:
                            row = snapshot["by_path"].get(_canonical(entry.path))
                            if not row:
                                snapshot["global_reasons"].append("unindexed_metadata_requires_rescan")
                            elif (info.st_size, info.st_mtime_ns) != (row["size_bytes"], row["mtime_ns"]):
                                # A changed current producer is scoped later by
                                # its run; changed unknown dependencies are global.
                                if not row["run_id"] or row["kind"] == "project":
                                    snapshot["global_reasons"].append("stale_dependency_metadata_requires_rescan")
            except OSError:
                snapshot["global_reasons"].append("registered_tree_discovery_incomplete")

    def _metadata(self, snapshot: dict[str, Any]) -> None:
        files, by_path = snapshot["files"], snapshot["by_path"]
        ancestors: dict[str, bool] = {}
        # Every indexed dependency document must still match its parse. Ordinary
        # run records are scoped to their run; unknown unreadable documents are
        # global unknown dependencies, not an excuse to delete their neighbours.
        for row in files.values():
            if row["kind"] not in {"metadata", "project"} and Path(row["path"]).suffix.casefold() != ".toml":
                continue
            facts = _facts(row["path"], ancestors)
            snapshot["metadata_facts"][row["id"]] = facts
            valid = self._metadata_valid(row, facts, snapshot)
            if not valid:
                scope = snapshot["run_reasons"][row["run_id"]] if row["run_id"] else snapshot["global_reasons"]
                scope.append("stale_unparsed_or_missing_metadata")
        for identifier in snapshot["documents"] | {
            row["id"] for row in files.values() if row["kind"] == "project" or Path(row["path"]).suffix.casefold() == ".toml"
        }:
            row = files[identifier]
            if Path(row["path"]).suffix.casefold() == ".toml":
                try:
                    payload = tomllib.loads(Path(row["path"]).read_text(encoding="utf-8-sig")) if row["size_bytes"] <= MAX_METADATA_BYTES else None
                except (OSError, ValueError, UnicodeError):
                    payload = None
            else:
                payload = _json(row["path"])
            if payload is None:
                snapshot["global_reasons"].append("unreadable_reduction_dependency_document")
                continue
            for link in snapshot["links_from"][identifier]:
                snapshot["dependency_paths"].add(link["target_path"])
            self._dependency_scopes(payload, Path(row["path"]).parent, snapshot)
        # Do not interpret arbitrary PNGs as cache. The production QHY contract
        # records the exact raw/preview pair in each frame-index line (the link
        # table alone loses that one-to-one association).
        live_run_manifests: dict[str, dict[str, Any] | None] = {}
        for row in files.values():
            if row["kind"] != "metadata" or Path(row["path"]).name.casefold() != "frames.jsonl":
                continue
            facts = snapshot["metadata_facts"].get(row["id"], {})
            if not _same_index(row, facts) or row["warning"]:
                continue
            def protect_linked_previews(reason: str) -> None:
                for link in snapshot["links_from"][row["id"]]:
                    target = by_path.get(link["target_path"])
                    if target and str(link["relation"]).casefold() == "previewpath":
                        snapshot["preview_reasons"][target["id"]].append(reason)
            sibling = by_path.get(_canonical(Path(row["path"]).parent / "manifest.json"))
            job = _json(sibling["path"]) if sibling else None
            if (not sibling or not job or str(job.get("state", "")).casefold() not in TERMINAL_STATES
                    or not job.get("completedUtc") or not job.get("id")):
                protect_linked_previews("producer_camera_job_not_confirmed_terminal")
                continue
            if (sibling["warning"] or sibling["missing"]
                    or not _same_index(sibling, snapshot["metadata_facts"].get(sibling["id"], {}))):
                protect_linked_previews("producer_camera_manifest_changed_or_unreadable")
                continue
            run_id = str(job.get("observationRunId", ""))
            run = snapshot["runs"].get(run_id, {})
            if (run.get("authority", 0) < 2 or str(run.get("state", "")).casefold() not in TERMINAL_STATES
                    or run.get("source") not in {"real", "simulated"}):
                protect_linked_previews("observation_run_active_unknown_or_unverified")
                continue
            if run_id not in live_run_manifests:
                live_run_manifests[run_id] = _json(run["manifest_path"])
            main_record = live_run_manifests[run_id]
            if (not main_record or str(main_record.get("terminalState", "")).casefold() not in TERMINAL_STATES):
                protect_linked_previews("observation_run_has_no_explicit_terminal_record")
                continue
            main_file = by_path.get(_canonical(run["manifest_path"]))
            if not main_file:
                protect_linked_previews("terminal_run_manifest_unverified")
                continue
            try:
                with Path(row["path"]).open("r", encoding="utf-8-sig") as stream:
                    for line in stream:
                        if not line.strip():
                            continue
                        record = json.loads(line)
                        if not isinstance(record, dict) or not record.get("frameId"):
                            continue
                        preview, raw = record.get("previewPath"), record.get("fitsPath")
                        if not isinstance(preview, str) or not isinstance(raw, str) or not preview or not raw:
                            continue
                        base = Path(row["path"]).parent
                        preview_path = _canonical(preview if os.path.isabs(preview) else base / preview)
                        raw_path = _canonical(raw if os.path.isabs(raw) else base / raw)
                        raw_row, preview_row = by_path.get(raw_path), by_path.get(preview_path)
                        if not raw_row or raw_row["kind"] != "frame" or not preview_row:
                            continue
                        proof = {"source_ids": sorted({row["id"], sibling["id"], main_file["id"]}),
                                 "raw_id": raw_row["id"],
                                 "run_id": run_id, "producer": "qhy-frame-index"}
                        snapshot["proofs"][preview_row["id"]] = proof
                        for identifier in (preview_row["id"], raw_row["id"]):
                            snapshot["owners"][identifier].discard("unattributed")
                            snapshot["owners"][identifier].add("run:" + run_id)
            except (OSError, UnicodeError, ValueError):
                snapshot["run_reasons"][run_id].append("frame_index_unreadable_or_still_writing")
                for identifier, proof in list(snapshot["proofs"].items()):
                    if row["id"] in proof["source_ids"]:
                        del snapshot["proofs"][identifier]

    def _metadata_valid(self, row, facts, snapshot):
        return (_same_index(row, facts) and row["parser_version"] == PARSER_VERSION
                and not row["warning"] and not row["missing"])

    @staticmethod
    def _dependency_scopes(value: Any, base: Path, snapshot: dict[str, Any], key: str = "") -> None:
        if isinstance(value, dict):
            for child_key, child in value.items():
                CleanupPlanner._dependency_scopes(child, base, snapshot, str(child_key))
        elif isinstance(value, list):
            for child in value:
                CleanupPlanner._dependency_scopes(child, base, snapshot, key)
        elif isinstance(value, str) and value and "://" not in value and "\x00" not in value:
            lower = key.lower().replace("_", "")
            if lower in {"dataroot", "rawroot", "inputroot", "calibrationroot", "directory", "rootdir"}:
                path = _canonical(value if os.path.isabs(value) else base / value)
                snapshot["dependency_roots"].add(path)
            elif any(character in value for character in ("*", "?", "[")):
                # Legacy glob settings do not prove that unused files are free.
                # Protect the non-wildcard parent scope without expanding globs.
                prefix = value
                while any(character in prefix for character in ("*", "?", "[")):
                    parent = str(Path(prefix).parent)
                    if parent == prefix:
                        break
                    prefix = parent
                snapshot["dependency_roots"].add(_canonical(prefix if os.path.isabs(prefix) else base / prefix))

    def _classify(self, row: dict[str, Any], snapshot: dict[str, Any], *, exact: bool = False,
                  ancestors: dict[str, bool] | None = None) -> dict[str, Any]:
        identifier, path = row["id"], row["path"]
        reasons: list[str] = []
        category = {"frame": "raw", "product": "result", "project": "project",
                    "metadata": "record", "log": "log", "preview": "preview"}.get(row["kind"], "unknown")
        proof = snapshot["proofs"].get(identifier)
        if not self._disposable_category(category, proof):
            reasons.append({"raw": "immutable_raw_science_photometry_calibration_or_evidence",
                            "result": "retained_reduction_product", "project": "retained_project_record",
                            "record": "retained_provenance_or_runtime_record", "log": "retained_log"}.get(
                                category, "unknown_file_purpose"))
        if not proof:
            reasons.append("no_explicit_rebuildable_preview_proof")
        reasons.extend(snapshot["preview_reasons"].get(identifier, ()))
        if row["warning"]:
            reasons.append("catalog_warning_requires_review")
        if row["missing"]:
            reasons.append("missing_or_offline")
        reasons.extend(snapshot["global_reasons"])
        if proof:
            reasons.extend(snapshot["run_reasons"].get(proof["run_id"], ()))
            raw = snapshot["files"][proof["raw_id"]]
            raw_facts = _facts(raw["path"], ancestors)
            if raw["warning"] or raw["missing"] or not _same_index(raw, raw_facts) or raw_facts["unsafe"]:
                reasons.append("paired_raw_missing_changed_or_unverified")
            owner = snapshot["runs"].get(proof["run_id"], {})
            manifest = snapshot["by_path"].get(_canonical(owner.get("manifest_path", "")))
            if (not manifest or manifest["warning"] or manifest["missing"]
                    or not _same_index(manifest, snapshot["metadata_facts"].get(manifest["id"], {}))):
                reasons.append("terminal_run_manifest_unverified")
            for project_id in snapshot["owners"].get(identifier, ()):
                if project_id.startswith("run:"):
                    other = snapshot["runs"].get(project_id[4:], {})
                    if str(other.get("state", "")).casefold() not in TERMINAL_STATES:
                        reasons.append("active_or_unknown_related_run")
        if path in snapshot["dependency_paths"] or any(_within(path, root) for root in snapshot["dependency_roots"]):
            reasons.append("reduction_or_legacy_input_dependency")
        for link in snapshot["inbound"].get(path, ()):
            relation = str(link["relation"]).casefold()
            # A preview output in its own stopped producer is provenance, not an
            # input. Everything else (including runtime-model evidence) protects.
            own_output = bool(proof and link["source_file_id"] in proof["source_ids"]
                              and relation in self._output_relations(proof))
            if not own_output:
                reasons.append("referenced_dependency_or_runtime_model")
        facts = _facts(path, ancestors) if exact or not reasons else None
        if facts:
            reasons.extend(facts["filesystem_reasons"])
            if not _same_index(row, facts):
                reasons.append("source_changed_since_index")
        candidate = not reasons
        item: dict[str, Any] = {"id": identifier, "path": path, "size_bytes": row["size_bytes"],
                               "mtime_ns": row["mtime_ns"], "dev": 0, "ino": 0, "nlink": 0,
                               "category": category, "protected": not candidate,
                               "reasons": list(dict.fromkeys(reasons)), "candidate": candidate,
                               "selected": False, "estimated_release_bytes": 0,
                               "project_ids": sorted(snapshot["owners"].get(identifier, ())),
                               "proof": proof}
        if facts:
            item.update({key: facts[key] for key in ("size_bytes", "mtime_ns", "dev", "ino", "nlink")})
            item["exists"] = facts["exists"]
            item["allocated_bytes"] = facts["allocated_bytes"] if candidate else 0
        return item

    def _disposable_category(self, category: str, proof: dict | None) -> bool:
        # Explicit retention policies may provide a narrower, separately
        # authorized raw exception; ordinary library cleanup remains previews-only.
        return category == "preview"

    def _output_relations(self, proof: dict) -> frozenset[str]:
        return _PROVENANCE_RELATIONS

    def inventory(self) -> list[dict[str, Any]]:
        snapshot = self._load(force=True)
        summaries: dict[str, dict[str, Any]] = {}
        for project_id, project in snapshot["projects"].items():
            summaries[project_id] = {**project, "id": project_id, "project_id": project_id,
                                     "indexed_bytes": 0, "candidate_bytes": 0, "protected_bytes": 0,
                                     "shared_bytes": 0, "file_count": 0, "candidate_count": 0,
                                     "protected_count": 0}
        ancestors: dict[str, bool] = {}
        unique = {"indexed_bytes": 0, "candidate_bytes": 0, "protected_bytes": 0,
                  "file_count": 0, "candidate_count": 0}
        for row in snapshot["files"].values():
            item = self._classify(row, snapshot, ancestors=ancestors)
            size = row["size_bytes"] if not row["missing"] else 0
            unique["indexed_bytes"] += size
            unique["file_count"] += 1
            unique["candidate_bytes" if item["candidate"] else "protected_bytes"] += size
            unique["candidate_count"] += int(item["candidate"])
            for project_id in snapshot["owners"][row["id"]]:
                if project_id not in summaries:
                    continue
                summary = summaries[project_id]
                summary["file_count"] += 1
                summary["indexed_bytes"] += size
                summary["candidate_bytes" if item["candidate"] else "protected_bytes"] += size
                summary["candidate_count" if item["candidate"] else "protected_count"] += 1
                if len(snapshot["owners"][row["id"]]) > 1:
                    summary["shared_bytes"] += size
        self.inventory_totals = unique
        return sorted((row for row in summaries.values() if row["file_count"]),
                      key=lambda row: (-row["candidate_bytes"], -row["indexed_bytes"], row["id"]))

    def preview(self, project_ids: Iterable[str], action: str = "recycle",
                file_ids: Iterable[int] | None = None) -> dict[str, Any]:
        return self._preview_from_snapshot(self._load(force=True), project_ids, action, file_ids)

    def _preview_from_snapshot(self, snapshot, project_ids, action, file_ids=None):
        if action not in ACTIONS:
            raise ValueError("Cleanup action must be mark, recycle or permanent")
        selected_projects = sorted(set(project_ids))
        if not selected_projects:
            raise ValueError("Select at least one project")
        if any(identifier not in snapshot["projects"] for identifier in selected_projects):
            raise ValueError("Unknown project selection")
        identifiers = {identifier for identifier, owners in snapshot["owners"].items()
                       if set(selected_projects) & owners}
        explicit = set(file_ids) if file_ids is not None else None
        if explicit is not None and not explicit <= identifiers:
            raise ValueError("Explicit files must belong to the selected projects")
        items: list[dict[str, Any]] = []
        ancestors: dict[str, bool] = {}
        for identifier in sorted(identifiers):
            item = self._classify(snapshot["files"][identifier], snapshot, exact=True, ancestors=ancestors)
            item["selected"] = identifier in explicit if explicit is not None else action != "mark" and item["candidate"]
            item["actionable"] = action == "mark" or item["candidate"]
            if action == "permanent" and item["selected"] and item["candidate"]:
                item["estimated_release_bytes"] = item.get("allocated_bytes", 0)
            items.append(item)
        # Dependencies and proof documents are digest-bound and checked afresh
        # before execution. No filesystem mutation is made while creating a plan.
        guarded_ids = set(snapshot["documents"])
        guarded_ids.update(row["id"] for row in snapshot["files"].values()
                           if row["kind"] == "project" or Path(row["path"]).suffix.casefold() == ".toml")
        shared_guard_paths = {snapshot["files"][identifier]["path"] for identifier in guarded_ids}
        for item in items:
            if item["proof"]:
                guarded_ids.update(item["proof"]["source_ids"])
                guarded_ids.add(item["proof"]["raw_id"])
                run = snapshot["runs"].get(item["proof"]["run_id"], {})
                manifest = snapshot["by_path"].get(_canonical(run.get("manifest_path", "")))
                if manifest:
                    guarded_ids.add(manifest["id"])
        guards = [_signature(_facts(snapshot["files"][identifier]["path"], ancestors))
                  for identifier in sorted(guarded_ids)]
        for guard in guards:
            if (Path(guard["path"]).suffix.casefold() in _METADATA_SUFFIXES and guard["exists"]
                    and guard["size_bytes"] <= MAX_METADATA_BYTES):
                guard["sha256"] = hashlib.sha256(Path(guard["path"]).read_bytes()).hexdigest()
        plan: dict[str, Any] = {"schema_version": SCHEMA_VERSION, "plan_id": uuid.uuid4().hex,
                               "created_utc": datetime.now(timezone.utc).isoformat(),
                               "catalog_path": _canonical(self.catalog.db_path),
                               "catalog_identity": self._identity(), "catalog_token": self._token(),
                               "project_ids": selected_projects, "action": action, "items": items,
                               "protection_documents": guards,
                               "shared_guard_paths": sorted(shared_guard_paths),
                               "selected_bytes": sum(item["size_bytes"] for item in items if item["selected"]),
                               "candidate_bytes": sum(item["size_bytes"] for item in items if item["candidate"]),
                               "protected_bytes": sum(item["size_bytes"] for item in items if item["protected"]),
                               "estimated_release_bytes": sum(item["estimated_release_bytes"] for item in items),
                               "notice": "仅预览；标记及同盘系统回收站预计立即释放 0 B；原片、成果与依赖不可解除保护。"}
        plan["digest"] = plan_digest(plan)
        _ISSUED[plan["plan_id"]] = plan["digest"]
        return plan

    def _integrity(self, plan: dict[str, Any]) -> list[str]:
        try:
            if (plan.get("schema_version") != SCHEMA_VERSION or plan.get("action") not in ACTIONS
                    or _ISSUED.get(plan["plan_id"]) != plan.get("digest")
                    or plan_digest(plan) != plan.get("digest")):
                return ["plan_not_issued_here_or_modified"]
            if self._identity() != plan["catalog_identity"]:
                return ["catalog_identity_changed"]
        except (KeyError, OSError, TypeError, ValueError):
            return ["invalid_plan_or_catalog"]
        return []

    def revalidate(self, plan: dict[str, Any], completed_file_ids: Iterable[int] = ()) -> dict[str, Any]:
        """Rebuild dependency/classification facts; never silently replan a file."""
        reasons = self._integrity(plan)
        self._validated_tokens.pop(plan.get("plan_id", ""), None)
        self._validated_plans.pop(plan.get("plan_id", ""), None)
        completed = set(completed_file_ids)
        if reasons:
            return {"valid": False, "reasons": reasons, "items": []}
        snapshot = self._load(force=True)
        fresh: list[dict[str, Any]] = []
        ancestors: dict[str, bool] = {}
        for before in plan["items"]:
            if before["id"] in completed:
                if Path(before["path"]).exists():
                    reasons.append("completed_file_present_again")
                continue
            if not before["selected"]:
                continue
            row = snapshot["files"].get(before["id"])
            if not row:
                reasons.append("file_no_longer_indexed")
                continue
            current = self._classify(row, snapshot, exact=True, ancestors=ancestors)
            if plan["action"] != "mark" and current["protected"]:
                reasons.append("protection_or_dependency_changed:" + str(before["id"]))
            if plan["action"] != "mark" and any(current.get(key) != before.get(key) for key in (
                    "path", "size_bytes", "mtime_ns", "dev", "ino", "nlink")):
                reasons.append("file_changed_since_preview:" + str(before["id"]))
            current["selected"] = True
            fresh.append(current)
        guard_result = self._guards(plan) if plan["action"] != "mark" else []
        reasons.extend(guard_result)
        if not reasons:
            self._validated_tokens[plan["plan_id"]] = self._token()
            frozen = deepcopy(plan)
            self._validated_plans[plan["plan_id"]] = {
                "object_id": id(plan), "plan": frozen,
                "items": {item["id"]: item for item in frozen["items"]},
                "guards": {guard["path"]: guard for guard in frozen["protection_documents"]},
            }
        return {"valid": not reasons, "reasons": list(dict.fromkeys(reasons)), "items": fresh}

    @staticmethod
    def _guards(plan: dict[str, Any], paths: set[str] | None = None,
                *, verify_hash: bool = True) -> list[str]:
        ancestors: dict[str, bool] = {}
        for before in plan["protection_documents"]:
            if paths is not None and before["path"] not in paths:
                continue
            facts = _facts(before["path"], ancestors)
            if facts["unsafe"] or _signature(facts) != {key: value for key, value in before.items() if key != "sha256"}:
                return ["dependency_proof_or_paired_raw_changed"]
            if verify_hash and before.get("sha256"):
                try:
                    digest = hashlib.sha256(Path(before["path"]).read_bytes()).hexdigest()
                except OSError:
                    return ["dependency_proof_or_paired_raw_changed"]
                if digest != before["sha256"]:
                    return ["dependency_content_changed"]
        return []

    def revalidate_item(self, plan: dict[str, Any], item: dict[str, Any],
                        completed_file_ids: Iterable[int] = ()) -> dict[str, Any]:
        """Bounded immediate pre-operation gate after one full revalidation.

        No rescanning/reparsing 40k runs for each item. The executor still needs
        an OS locked-handle identity check to close the final filesystem race.
        """
        # Full digest verification and deep-copy happen once at full validation.
        # Each file is compared to that private snapshot, not to mutable caller
        # rows. Rehashing a 10k-item checklist 10k times would be quadratic.
        cached = self._validated_plans.get(plan.get("plan_id", ""))
        if not cached or id(plan) != cached["object_id"]:
            return {"valid": False, "reasons": ["plan_not_fully_validated_in_this_worker"], "items": []}
        frozen = cached["plan"]
        reasons: list[str] = []
        if (any(plan.get(key) != frozen.get(key) for key in ("schema_version", "plan_id", "action", "digest"))
                or _ISSUED.get(frozen["plan_id"]) != frozen["digest"]):
            reasons.append("plan_not_issued_here_or_modified")
        try:
            if self._identity() != frozen["catalog_identity"]:
                reasons.append("catalog_identity_changed")
        except OSError:
            reasons.append("invalid_plan_or_catalog")
        original = cached["items"].get(item.get("id"))
        if not original or not original["selected"] or original["id"] in set(completed_file_ids):
            reasons.append("file_not_in_pending_exact_selection")
        elif frozen["action"] != "mark" and original["protected"]:
            reasons.append("protected_file")
        elif item != original:
            reasons.append("item_modified")
        if self._token() != self._validated_tokens.get(frozen["plan_id"]):
            reasons.append("catalog_changed_since_preview")
        if frozen["action"] != "mark":
            paths = set(frozen.get("shared_guard_paths", ()))
            if original and original.get("proof"):
                proof = original["proof"]
                identifiers = {*proof["source_ids"], proof["raw_id"]}
                marks = ",".join("?" for _ in identifiers)
                paths.update(row[0] for row in self.connection.execute(
                    f"SELECT path FROM files WHERE id IN ({marks})", tuple(identifiers)))
                run = self.connection.execute("SELECT manifest_path FROM runs WHERE id=?", (proof["run_id"],)).fetchone()
                if run:
                    paths.add(_canonical(run[0]))
            relevant_guards = {"protection_documents": [cached["guards"][path] for path in paths
                                                         if path in cached["guards"]]}
            reasons.extend(self._guards(relevant_guards, verify_hash=False))
        if original:
            facts = _facts(original["path"])
            if frozen["action"] != "mark" and (facts["filesystem_reasons"] or any(
                facts[key] != original.get(key) for key in ("size_bytes", "mtime_ns", "dev", "ino", "nlink")
            )):
                reasons.append("file_changed_since_preview")
        return {"valid": not reasons, "reasons": list(dict.fromkeys(reasons)),
                "items": [original] if original and not reasons else []}
