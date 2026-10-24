"""Offline spectroscopy projects derived from explicit acquisition/reduction links.

An observing project is one recorded run (including explicitly recorded recovery
segments), not every file with the same OBJECT name. Operator rejection, physical
availability and scientific quality are independent facts. Nothing here moves,
deletes, processes or opens an acquisition device.
"""

from __future__ import annotations

from datetime import date, datetime, timedelta, timezone
import json
from pathlib import Path
from typing import Any, TYPE_CHECKING

from .library_catalog import (
    MAX_METADATA_BYTES, PARSER_VERSION, SOURCE_POLICY_VERSION, _canonical, _source, _unsafe_root,
)

if TYPE_CHECKING:
    from .library_catalog import LibraryCatalog


OUTCOME_LABELS = {
    "completed": "流程完成", "failed": "目标失败", "cancelled": "已取消",
    "incomplete": "未完成 / 运行中", "unknown": "无终态记录",
}
REDUCTION_LABELS = {
    "raw": "待建后期项目", "project": "已建后期项目",
    "extracted": "已提取光谱", "wavelength": "已波长定标",
    "calibrated": "已响应定标", "workflow": "已有后期工作流",
}
SCIENCE_ROLES = {"SCIENCE", "LIGHT", "LIGHT FRAME"}


def _dict(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def _number(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def observing_night(value: str) -> str:
    """Fallback only: local noon starts an observing night, using explicit UTC+8.

    A saved deadlineSchedule.ObservingNightDate takes precedence when available.
    This deliberately does not use the machine's locale/timezone or file mtime.
    """
    try:
        instant = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if instant.tzinfo is None:
            return ""
        local = instant.astimezone(timezone(timedelta(hours=8)))
        return (local - timedelta(hours=12)).date().isoformat()
    except (ValueError, AttributeError):
        return ""


def _saved_night(value: Any) -> str:
    """A JSON null or legacy literal 'None' is not an observing-night date."""
    if not isinstance(value, str) or len(value) != 10:
        return ""
    try:
        return date.fromisoformat(value).isoformat()
    except ValueError:
        return ""


def _outcome(state: str) -> str:
    if state.casefold() == "completed":
        return "completed"
    if state.casefold() in {"faulted", "failed", "blocked", "stopped"} or state.casefold().startswith("skipped"):
        return "failed"
    if state.casefold() in {"cancelled", "canceled"}:
        return "cancelled"
    return "incomplete" if state else "unknown"


def _compact_reduction(payload: dict[str, Any], kind: str) -> dict[str, Any]:
    values = {key: payload[key] for key in (
        "name", "current_stage", "last_output", "output_root", "notes", "target",
        "wavelength", "trace", "frameSelection", "scienceFrames", "artifacts",
        "standardName", "standardProduct", "scienceProduct",
        "currentStage", "lastOutput", "outputRoot", "dataRoot",
    ) if key in payload}
    values["type"] = kind
    for snake, camel in (("current_stage", "currentStage"), ("last_output", "lastOutput"),
                         ("output_root", "outputRoot")):
        if camel in values:
            values[snake] = values[camel]
    return values


def index_document_facts(
    catalog: LibraryCatalog, file_id: int, path: Path, payload: dict[str, Any],
) -> None:
    """Called by the catalogue's existing bounded parser; no extra source reads.

    Tables contain rebuildable facts only. Neither reviews nor raw files change.
    A rescan replaces a document's previous facts, including obsolete segments.
    """
    con = catalog.connection
    for table, column in (("project_documents", "file_id"),
                          ("project_run_context", "source_file_id"),
                          ("project_run_segments", "source_file_id"),
                          ("project_night_entries", "source_file_id")):
        con.execute(f"DELETE FROM {table} WHERE {column}=?", (file_id,))
    outcomes = payload.get("outcomes")
    if isinstance(outcomes, list):
        deadline = _dict(payload.get("deadlineSchedule"))
        night = _saved_night(deadline.get("ObservingNightDate", deadline.get("observingNightDate", "")))
        for ordinal, entry in enumerate(outcomes):
            if not isinstance(entry, dict) or not entry.get("target"):
                continue
            con.execute("INSERT OR REPLACE INTO project_night_entries VALUES(?,?,?,?,?,?,?,?)",
                (file_id, ordinal, str(entry["target"]), night,
                 str(entry.get("outcome") or ""), str(entry.get("gate") or ""),
                 str(entry.get("failureScope") or ""), str(entry.get("runId") or "")))
            if not entry.get("runId"):
                continue
            run_id = str(entry["runId"])
            retained = _dict(entry.get("retainedScience"))
            segments = retained.get("segmentRunIds", [])
            segments = list(dict.fromkeys(str(v) for v in segments if isinstance(v, str) and v))
            con.execute(
                "INSERT OR REPLACE INTO project_run_context VALUES(?,?,?,?,?,?,?,?,?,?)",
                (run_id, file_id, night, str(entry.get("outcome") or ""),
                 str(entry.get("gate") or ""), str(entry.get("failureScope") or ""),
                 _number(retained.get("requestedFrames")),
                 _number(retained.get("acceptedFrames")),
                 _number(retained.get("acceptedExposureSeconds")), json.dumps(segments)),
            )
            for segment in segments:
                con.execute("INSERT OR IGNORE INTO project_run_segments VALUES(?,?,?)",
                            (run_id, segment, file_id))
        return
    kind, stage, name, target = "", "", "", payload.get("target", "")
    if path.suffix.casefold() == ".astroproj":
        kind, stage = "project", "project"
        name = str(payload.get("name") or path.stem)
    elif isinstance(payload.get("inputs"), dict) and (
        "frameSelection" in payload or "wavelength" in payload or "trace" in payload
    ):
        kind, stage = "reduction", "extracted"
        wavelength = _dict(payload.get("wavelength"))
        status = str(wavelength.get("status", "")).casefold()
        if status in {"calibrated", "passed", "valid", "solved", "success"}:
            stage = "wavelength"
    elif payload.get("scienceProduct") and payload.get("standardProduct"):
        kind, stage = "calibration", "calibrated"
    elif payload.get("scienceRun") and payload.get("standardRun"):
        kind, stage = "workflow", "workflow"
    if kind:
        if isinstance(target, dict):
            target = target.get("name", "")
        con.execute("INSERT OR REPLACE INTO project_documents VALUES(?,?,?,?,?,?)",
                    (file_id, kind, name or path.stem, str(target or ""), stage,
                     json.dumps(_compact_reduction(payload, kind), ensure_ascii=False)))


class LibraryProjects:
    """Read model over the existing catalogue; bounded manifest reads on detail only."""

    def __init__(self, catalog: LibraryCatalog):
        self.catalog = catalog
        self.connection = catalog.connection
        # Older runs stored manifest paths with Windows display-case whereas
        # files.path is canonical. Resolve that join without a source-tree scan.
        self.connection.create_function("library_canonical_path", 1,
            lambda value: _canonical(value) if value else "", deterministic=True)
        self._row_cache: dict[tuple[Any, ...], list[dict[str, Any]]] = {}
        self._cache_version: tuple[int, int] | None = None

    @staticmethod
    def _base_sql() -> str:
        # Exact scientific inputs, not shared calibration files, establish a
        # reduction-to-run association. Shared flats/bias must not merge targets.
        return f"""
        WITH RECURSIVE members AS (
            SELECT id project_run,id member_run FROM runs
            UNION SELECT run_id,segment_run_id FROM project_run_segments
        ), member_files AS (
            SELECT DISTINCT m.project_run,fr.file_id FROM members m
            JOIN file_runs fr ON fr.run_id=m.member_run
        ), document_edges AS (
            SELECT DISTINCT parent.file_id parent_file_id,child.file_id child_file_id
            FROM project_documents parent JOIN links l ON l.source_file_id=parent.file_id
            JOIN files target ON target.path=l.target_path
            JOIN project_documents child ON child.file_id=target.id
            WHERE lower(l.relation) IN ('sciencerun','standardrun','flattrialrun','lastoutput','last_output')
            UNION
            SELECT DISTINCT parent.file_id,child.file_id FROM project_documents parent
            JOIN links l ON l.source_file_id=parent.file_id
            JOIN files target ON substr(replace(target.path,char(92),'/'),1,
                length(rtrim(replace(l.target_path,char(92),'/'),'/'))+1)
                =rtrim(replace(l.target_path,char(92),'/'),'/')||'/'
            JOIN project_documents child ON child.file_id=target.id
            WHERE lower(l.relation) IN ('lastoutput','last_output')
                AND parent.file_id<>child.file_id
            UNION
            SELECT DISTINCT parent.file_id,child.file_id FROM project_documents parent
            JOIN links l ON l.source_file_id=parent.file_id
            JOIN links produced ON produced.target_path=l.target_path
            JOIN project_documents child ON child.file_id=produced.source_file_id
            WHERE lower(l.relation) IN ('scienceproduct','standardproduct')
              AND lower(produced.relation) NOT IN ('science','standard','bias','dark','flat','arc',
                  'accepted','rejected','path','selected:science','selected:standard','scienceproduct','standardproduct')
              AND parent.file_id<>child.file_id
        ), reductions(project_run,file_id,stage) AS (
            SELECT DISTINCT mf.project_run,d.file_id,d.stage
            FROM member_files mf JOIN files f ON f.id=mf.file_id
            JOIN links l ON l.target_path=f.path
            JOIN project_documents d ON d.file_id=l.source_file_id
            WHERE ((d.document_type='project' AND lower(l.relation) IN ('selected:science','selected:standard'))
               OR (d.document_type<>'project' AND lower(l.relation) IN ('science','accepted','scienceproduct','sciencerun')))
            AND (f.channel='spectroscopy' OR f.kind IN ('product','metadata'))
            UNION
            SELECT rd.project_run,d.file_id,d.stage FROM reductions rd
            JOIN document_edges e ON e.child_file_id=rd.file_id
            JOIN project_documents d ON d.file_id=e.parent_file_id
        ), stats AS (
            SELECT mf.project_run,COUNT(*) file_count,SUM(f.size_bytes) indexed_bytes,
              SUM(CASE WHEN f.kind='frame' AND f.channel='spectroscopy'
                AND upper(f.role) IN ('SCIENCE','LIGHT','LIGHT FRAME') THEN 1 ELSE 0 END) science_frames,
              SUM(CASE WHEN f.kind='frame' AND f.channel='spectroscopy'
                AND upper(f.role) IN ('SCIENCE','LIGHT','LIGHT FRAME') AND f.accepted=1
                THEN 1 ELSE 0 END) accepted_frames,
              SUM(CASE WHEN f.kind='frame' AND f.channel='spectroscopy'
                AND upper(f.role)='PROBE' THEN 1 ELSE 0 END) probe_frames,
              SUM(CASE WHEN f.kind='frame' AND f.channel='photometry' THEN 1 ELSE 0 END) photometry_frames,
              SUM(CASE WHEN f.kind='frame' AND f.channel='slit' THEN 1 ELSE 0 END) slit_frames,
              SUM(CASE WHEN f.kind='frame' AND f.channel='spectroscopy'
                AND upper(f.role) IN ('SCIENCE','LIGHT','LIGHT FRAME')
                THEN COALESCE(f.exposure_seconds,0) ELSE 0 END) exposure_seconds,
              SUM(f.missing) missing_files,
              SUM(CASE WHEN f.warning<>'' THEN 1 ELSE 0 END) warning_files,
              SUM(CASE WHEN r.decision='reject' THEN 1 ELSE 0 END) rejected_files
            FROM member_files mf JOIN files f ON f.id=mf.file_id
            LEFT JOIN reviews r ON r.file_id=f.id GROUP BY mf.project_run
        ), latest_context AS (
            SELECT pc.* FROM project_run_context pc JOIN files f ON f.id=pc.source_file_id
            WHERE pc.source_file_id=(SELECT pc2.source_file_id FROM project_run_context pc2
                JOIN files f2 ON f2.id=pc2.source_file_id WHERE pc2.run_id=pc.run_id
                ORDER BY f2.mtime_ns DESC,f2.id DESC LIMIT 1)
        ), rows AS (
            SELECT 'run:'||r.id id,r.id run_id,r.target,
                CASE WHEN manifest_source.parser_version>=4 AND manifest_source.source_policy_version>={SOURCE_POLICY_VERSION}
                     THEN manifest_source.source
                     WHEN r.source='simulated' THEN 'simulated' ELSE 'unknown' END source,
                r.state,r.reason,r.created_utc,
                r.manifest_path,COALESCE(pc.night,'') saved_night,
                COALESCE(pc.outcome,'') night_outcome,COALESCE(pc.gate,'') gate,
                COALESCE(pc.failure_scope,'') failure_scope,
                pc.requested_frames,pc.accepted_frames logged_accepted_frames,
                pc.accepted_exposure_seconds logged_accepted_exposure_seconds,
                COALESCE(s.file_count,0) file_count,COALESCE(s.indexed_bytes,0) indexed_bytes,
                COALESCE(s.science_frames,0) science_frames,COALESCE(s.accepted_frames,0) accepted_frames,
                COALESCE(s.probe_frames,0) probe_frames,COALESCE(s.photometry_frames,0) photometry_frames,
                COALESCE(s.slit_frames,0) slit_frames,COALESCE(s.exposure_seconds,0) exposure_seconds,
                COALESCE(s.missing_files,0) missing_files,COALESCE(s.warning_files,0) warning_files,
                COALESCE(s.rejected_files,0) rejected_files,
                COALESCE((SELECT rd.stage FROM reductions rd WHERE rd.project_run=r.id ORDER BY
                    CASE rd.stage WHEN 'calibrated' THEN 5 WHEN 'wavelength' THEN 4
                    WHEN 'extracted' THEN 3 WHEN 'workflow' THEN 2 ELSE 1 END DESC LIMIT 1),'raw') reduction_stage
            FROM runs r LEFT JOIN stats s ON s.project_run=r.id
            LEFT JOIN files manifest_source ON manifest_source.path=library_canonical_path(r.manifest_path)
            LEFT JOIN latest_context pc ON pc.run_id=r.id
            WHERE NOT EXISTS (SELECT 1 FROM project_run_segments sg
                WHERE sg.segment_run_id=r.id AND sg.run_id<>r.id)
            UNION ALL
            SELECT 'reduction:'||f.id,'',COALESCE(NULLIF(d.target,''),d.name),f.source,'','',
                f.acquired_utc,f.path,'','','','',NULL,NULL,NULL,
                1,f.size_bytes,0,0,0,0,0,0,f.missing,
                CASE WHEN f.warning<>'' THEN 1 ELSE 0 END,
                CASE WHEN rv.decision='reject' THEN 1 ELSE 0 END,d.stage
            FROM project_documents d JOIN files f ON f.id=d.file_id
            LEFT JOIN reviews rv ON rv.file_id=f.id
            WHERE NOT EXISTS (SELECT 1 FROM reductions rd WHERE rd.file_id=f.id)
            AND NOT EXISTS (SELECT 1 FROM document_edges e WHERE e.child_file_id=f.id)
            UNION ALL
            SELECT CASE WHEN n.run_id<>'' THEN 'run:'||n.run_id
                ELSE 'night:'||n.source_file_id||':'||n.entry_index END,n.run_id,n.target,f.source,'','',
                f.acquired_utc,f.path,n.night,n.outcome,n.gate,n.failure_scope,
                pc.requested_frames,pc.accepted_frames,pc.accepted_exposure_seconds,
                1,f.size_bytes,0,0,0,0,0,0,f.missing,
                CASE WHEN f.warning<>'' THEN 1 ELSE 0 END,0,'raw'
            FROM project_night_entries n JOIN files f ON f.id=n.source_file_id
            LEFT JOIN project_run_context pc ON pc.run_id=n.run_id AND pc.source_file_id=n.source_file_id
            WHERE n.run_id='' OR NOT EXISTS(SELECT 1 FROM runs r WHERE r.id=n.run_id)
        ) SELECT * FROM rows
        """

    def _rows(self, **filters: Any) -> list[dict[str, Any]]:
        # Filtering before Python enrichment keeps the common 40k-run catalogue
        # bounded. Night fallback is deterministic over recorded created_utc.
        version = (self.connection.total_changes,
                   self.connection.execute("PRAGMA data_version").fetchone()[0])
        if self._cache_version != version:
            self._row_cache.clear()
            self._cache_version = version
        cache_key = tuple(filters.get(key, "") for key in (
            "source", "reduction_stage", "query", "night", "outcome", "project_id"))
        if cache_key in self._row_cache:
            return [dict(row) for row in self._row_cache[cache_key]]
        clauses, args = [], []
        for key in ("source", "reduction_stage", "project_id"):
            if filters.get(key):
                clauses.append(f"{'id' if key == 'project_id' else key}=?")
                args.append(filters[key])
        if filters.get("query"):
            clauses.append("(target LIKE ? ESCAPE '\\' OR run_id LIKE ? ESCAPE '\\' OR reason LIKE ? ESCAPE '\\')")
            query = str(filters["query"]).replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
            args.extend([f"%{query}%"] * 3)
        sql = "SELECT * FROM (" + self._base_sql() + ")"
        if clauses:
            sql += " WHERE " + " AND ".join(clauses)
        sql += " ORDER BY created_utc DESC,id"
        rows = [self._row(dict(row)) for row in self.connection.execute(sql, args)]
        result = [row for row in rows if
                (not filters.get("night") or row["night"] == filters["night"]) and
                (not filters.get("outcome") or row["outcome"] == filters["outcome"])]
        if len(self._row_cache) >= 8:
            self._row_cache.clear()
        self._row_cache[cache_key] = result
        return [dict(row) for row in result]

    @staticmethod
    def _row(row: dict[str, Any]) -> dict[str, Any]:
        saved = _saved_night(row.pop("saved_night"))
        row["night"] = saved or observing_night(row["created_utc"])
        row["night_basis"] = "saved schedule" if saved else "UTC+8 noon fallback"
        row["outcome"] = _outcome(row["night_outcome"] or row["state"])
        row["outcome_label"] = OUTCOME_LABELS[row["outcome"]]
        row["reduction_label"] = REDUCTION_LABELS.get(row["reduction_stage"], "后期状态未知")
        return row

    def list_projects(self, offset: int = 0, limit: int = 200, **filters: Any) -> list[dict[str, Any]]:
        rows = self._rows(**filters)
        start = max(0, int(offset))
        return rows[start:start + max(1, min(int(limit), 1000))]

    def count_projects(self, **filters: Any) -> int:
        return len(self._rows(**filters))

    def facets(self) -> dict[str, list[str]]:
        rows = self._rows()
        return {key: sorted({str(row[key]) for row in rows if row.get(key)})
                for key in ("night", "source", "outcome", "reduction_stage", "target")}

    def _run_ids(self, project_id: str) -> list[str]:
        if not project_id.startswith("run:"):
            return []
        run_id = project_id[4:]
        return sorted({run_id, *(row[0] for row in self.connection.execute(
            "SELECT segment_run_id FROM project_run_segments WHERE run_id=?", (run_id,)))})

    def _member_ids(self, project_id: str) -> list[int]:
        runs = self._run_ids(project_id)
        if runs:
            marks = ",".join("?" for _ in runs)
            found = [r[0] for r in self.connection.execute(
                f"SELECT DISTINCT file_id FROM file_runs WHERE run_id IN ({marks})", runs)]
            if not found:
                found = [r[0] for r in self.connection.execute(
                    "SELECT source_file_id FROM project_night_entries WHERE run_id=?", (project_id[4:],))]
            return found
        if project_id.startswith("reduction:") and project_id[10:].isdigit():
            file_id = int(project_id[10:])
            docs, edges = self._document_graph()
            included = self._closure({file_id}, edges)
            paths = {reference["target_path"] for identifier in included
                     for reference in docs.get(identifier, {}).get("references", [])}
            found = set(included)
            for path in paths:
                row = self.connection.execute("SELECT id FROM files WHERE path=?", (path,)).fetchone()
                if row:
                    found.add(row[0])
            return sorted(found)
        if project_id.startswith("night:"):
            parts = project_id.split(":")
            if len(parts) == 3 and parts[1].isdigit():
                return [int(parts[1])]
        return []

    def select_science(self, project_id: str) -> list[dict[str, Any]]:
        """All eligible recorded science, without page truncation or probe substitution."""
        members = self._member_ids(project_id)
        selected = []
        for start in range(0, len(members), 500):
            chunk = members[start:start + 500]
            marks = ",".join("?" for _ in chunk)
            rows = self.connection.execute(
                "SELECT f.*,COALESCE(r.decision,'unreviewed') review,COALESCE(r.rating,0) rating,"
                "COALESCE(r.notes,'') notes FROM files f LEFT JOIN reviews r ON r.file_id=f.id "
                f"WHERE f.id IN ({marks}) AND f.kind='frame' AND f.channel='spectroscopy' "
                "AND upper(f.role) IN ('SCIENCE','LIGHT','LIGHT FRAME') AND f.accepted=1 "
                "AND f.missing=0 AND COALESCE(r.decision,'')<>'reject' "
                "AND f.warning NOT LIKE '%Identity conflict%' ORDER BY f.acquired_utc,f.path", chunk)
            selected.extend(self.catalog._ui_row(row) for row in rows)
        return selected

    @staticmethod
    def _payload(path: str, warnings: list[str]) -> dict[str, Any]:
        if not path:
            return {}
        try:
            source = Path(path)
            if _unsafe_root(source) or source.stat().st_size > MAX_METADATA_BYTES:
                raise ValueError("unsafe path or oversized manifest")
            value = json.loads(source.read_text(encoding="utf-8-sig"))
            return _dict(value)
        except (OSError, ValueError) as exc:
            warnings.append(f"记录当前无法读取：{path} ({type(exc).__name__})")
            return {}

    def get_project(self, project_id: str, member_limit: int = 1000) -> dict[str, Any] | None:
        # Reuse one consistent summary contract. This is an indexed SQLite query,
        # not a search of the source tree or a parse of every observing manifest.
        project = next(iter(self._rows(project_id=project_id)), None)
        if project is None:
            return None
        warnings: list[str] = []
        members = self._member_ids(project_id)
        cap = max(1, min(int(member_limit), 5000))
        files = [self.catalog.get_file(file_id, related_limit=1) for file_id in members[:cap]]
        project["files"] = [value for value in files if value]
        project["member_count"] = len(members)
        project["members_truncated"] = len(members) > cap
        project["segment_run_ids"] = self._run_ids(project_id)
        timeline, manifests, gates, identities, counters = [], [], [], [], []
        for run in project["segment_run_ids"]:
            row = self.connection.execute("SELECT manifest_path FROM runs WHERE id=?", (run,)).fetchone()
            if not row or not row[0]:
                continue
            manifests.append(row[0])
            payload = self._payload(row[0], warnings)
            identity = _dict(_dict(payload.get("plan")).get("target"))
            if identity:
                identities.append({"run_id": run, **identity})
            recorded_gates = payload.get("gates", {})
            recorded_gates = recorded_gates.items() if isinstance(recorded_gates, dict) else (
                ("", gate) for gate in recorded_gates if isinstance(recorded_gates, list))
            for stage, gate in recorded_gates:
                if isinstance(gate, dict):
                    gates.append({"run_id": run, "stage": stage or gate.get("stage", ""),
                                  **{key: gate.get(key) for key in (
                                      "code", "disposition", "severity", "message", "metrics")}})
            if isinstance(payload.get("counters"), dict):
                counters.append({"run_id": run, **payload["counters"]})
            for event in _dict(payload.get("snapshot")).get("recentEvents", []):
                if isinstance(event, dict):
                    timeline.append({"time": event.get("timestampUtc", ""), "run_id": run,
                                     "stage": event.get("stage", ""), "code": event.get("code", ""),
                                     "message": event.get("message", "")})
        project["timeline"] = sorted(timeline, key=lambda e: str(e["time"]))
        project["quality_gates"] = gates
        project["target_identities"] = identities
        project["run_counters"] = counters
        project["timeline_note"] = "来自保存的 recentEvents；可能仅为末段事件，不宣称完整日志。"
        reduced = self._reductions_for_members(members, project_id)
        project["reduction_projects"] = [row for row in reduced if row["document_type"] == "project"]
        project["products"] = [row for row in reduced if row["document_type"] != "project"]
        assets = self._derived_assets(reduced)
        present = {row["id"] for row in project["files"]}
        for asset in assets:
            if asset["id"] in present:
                for existing in project["files"]:
                    if existing["id"] == asset["id"]:
                        existing["library_category"] = "product"
            elif len(project["files"]) < cap:
                project["files"].append(asset)
                present.add(asset["id"])
            else:
                project["members_truncated"] = True
        for item in project["files"]:
            item.setdefault("library_category", "science" if item["kind"] == "frame" and
                item["channel"] == "spectroscopy" and item["role"].upper() in SCIENCE_ROLES
                else "probe" if item["role"].upper() == "PROBE" else item["kind"])
        project["product_file_ids"] = [row["id"] for row in assets]
        project["member_file_ids"] = sorted(set(members) | set(project["product_file_ids"]))
        project["member_count"] = len(project["member_file_ids"])
        project["source_paths"] = sorted(set(manifests + [project["manifest_path"]] +
                                              [row["path"] for row in project["files"]]))
        project["warnings"] = warnings
        if project["logged_accepted_frames"] is not None and project["logged_accepted_frames"] != project["accepted_frames"]:
            warnings.append("运行日志合格帧数与已索引科学原片数不同；保留两者，不推测缺失帧或把试拍冒充科学帧。")
        project["review_note"] = "筛除仅为资料库标记；原文件未删除、未移动，也未送入系统回收站。"
        project["outcome_note"] = "流程完成不等于光谱质量合格；合格帧仅统计原记录明确接受的科学帧。"
        return project

    def _reductions_for_members(self, members: list[int], project_id: str) -> list[dict[str, Any]]:
        found: dict[int, dict[str, Any]] = {}
        for start in range(0, len(members), 500):
            chunk = members[start:start + 500]
            marks = ",".join("?" for _ in chunk)
            for row in self.connection.execute(
                "SELECT DISTINCT d.*,f.path,f.missing FROM project_documents d JOIN files f ON f.id=d.file_id "
                "JOIN links l ON l.source_file_id=d.file_id JOIN files raw ON raw.path=l.target_path "
                f"WHERE raw.id IN ({marks}) AND ((d.document_type='project' AND lower(l.relation) IN "
                "('selected:science','selected:standard')) OR (d.document_type<>'project' "
                "AND lower(l.relation) IN ('science','accepted','scienceproduct','sciencerun')))", chunk):
                found[row["file_id"]] = dict(row)
        if project_id.startswith("reduction:"):
            row = self.connection.execute(
                "SELECT d.*,f.path,f.missing FROM project_documents d JOIN files f ON f.id=d.file_id "
                "WHERE d.file_id=?", (int(project_id[10:]),)).fetchone()
            if row:
                found[row["file_id"]] = dict(row)
        docs, edges = self._document_graph()
        parents: dict[int, set[int]] = {}
        for parent, children in edges.items():
            for child in children:
                parents.setdefault(child, set()).add(parent)
        included = self._closure(set(found), parents)
        included = self._closure(included, edges)
        return [docs[identifier] for identifier in sorted(included) if identifier in docs]

    @staticmethod
    def _closure(seeds: set[int], edges: dict[int, set[int]]) -> set[int]:
        visited = set(seeds)
        pending = list(seeds)
        while pending:
            for other in edges.get(pending.pop(), set()):
                if other not in visited:
                    visited.add(other)
                    pending.append(other)
        return visited

    def _document_graph(self) -> tuple[dict[int, dict[str, Any]], dict[int, set[int]]]:
        docs = {}
        for row in self.connection.execute(
            "SELECT d.*,f.path,f.missing FROM project_documents d JOIN files f ON f.id=d.file_id"):
            item = dict(row)
            item["id"] = item["file_id"]
            item["details"] = json.loads(item.pop("details_json"))
            item["current_stage"] = item["details"].get("current_stage", item["stage"])
            item["last_output"] = item["details"].get("last_output", "")
            item["references"] = []
            docs[item["id"]] = item
        paths = {doc["path"]: identifier for identifier, doc in docs.items()}
        produced: dict[str, set[int]] = {}
        links = [dict(row) for row in self.connection.execute(
            "SELECT l.* FROM links l JOIN project_documents d ON d.file_id=l.source_file_id")]
        inputs = {"science", "standard", "bias", "dark", "flat", "arc", "accepted", "rejected", "path",
                  "selected:science", "selected:standard", "scienceproduct", "standardproduct"}
        for link in links:
            docs[link["source_file_id"]]["references"].append(link)
            if link["relation"].casefold() not in inputs:
                produced.setdefault(link["target_path"], set()).add(link["source_file_id"])
        edges: dict[int, set[int]] = {}
        for link in links:
            key, parent = link["relation"].casefold(), link["source_file_id"]
            if key in {"sciencerun", "standardrun", "flattrialrun", "lastoutput", "last_output"}:
                if link["target_path"] in paths:
                    edges.setdefault(parent, set()).add(paths[link["target_path"]])
                if key in {"lastoutput", "last_output"}:
                    directory = Path(link["target_path"])
                    edges.setdefault(parent, set()).update(
                        identifier for path, identifier in paths.items()
                        if identifier != parent and Path(path).is_relative_to(directory))
            elif key in {"scienceproduct", "standardproduct"}:
                edges.setdefault(parent, set()).update(produced.get(link["target_path"], set()) - {parent})
        return docs, edges

    def _derived_assets(self, documents: list[dict[str, Any]]) -> list[dict[str, Any]]:
        identifiers = {row["id"] for row in documents}
        # Only output declarations (not science/calibration inputs) become
        # products. Existing indexed files only; links never expand scan roots.
        inputs = {"science", "standard", "bias", "dark", "flat", "arc", "accepted", "rejected", "path"}
        for doc in documents:
            for link in doc["references"]:
                if link["relation"].casefold() in inputs or link["relation"].casefold().startswith("selected:"):
                    continue
                row = self.connection.execute("SELECT id FROM files WHERE path=?", (link["target_path"],)).fetchone()
                if row:
                    identifiers.add(row[0])
                if link["relation"].casefold() in {"lastoutput", "last_output"}:
                    # Studio records a completed job's output DIRECTORY. Match
                    # already indexed descendants only, not a filesystem crawl.
                    prefix = link["target_path"].replace("\\", "/").rstrip("/") + "/"
                    identifiers.update(row[0] for row in self.connection.execute(
                        "SELECT id FROM files WHERE substr(replace(path,char(92),'/'),1,?)=?",
                        (len(prefix), prefix)))
        assets = []
        for identifier in sorted(identifiers):
            value = self.catalog.get_file(identifier, related_limit=1)
            if value:
                value["library_category"] = "product"
                assets.append(value)
        return assets

    def index_project(self, path: str | Path) -> dict[str, Any]:
        """Index one explicitly saved .astroproj; never scan its parent or linked inputs."""
        source = Path(_canonical(path))
        if source.suffix.casefold() != ".astroproj" or _unsafe_root(source):
            raise ValueError("Only an explicit, non-reparse .astroproj may be indexed here")
        info = source.stat()
        self.connection.execute(
            "INSERT INTO files(path,size_bytes,mtime_ns,kind,extension,parser_version) VALUES(?,?,?,?,?,?) "
            "ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes,mtime_ns=excluded.mtime_ns,"
            "missing=0,parser_version=excluded.parser_version",
            (str(source), info.st_size, info.st_mtime_ns, "project", ".astroproj", PARSER_VERSION))
        file_id = self.connection.execute("SELECT id FROM files WHERE path=?", (str(source),)).fetchone()[0]
        self.catalog.connection.execute("DELETE FROM links WHERE source_file_id=?", (file_id,))
        self.connection.execute("DELETE FROM project_documents WHERE file_id=?", (file_id,))
        self.connection.execute("DELETE FROM file_runs WHERE file_id=?", (file_id,))
        self.connection.execute("UPDATE files SET source='unknown',target='',run_id='' WHERE id=?", (file_id,))
        self.catalog._parse_json(source, file_id)
        sources = {row[0] for row in self.connection.execute(
            "SELECT DISTINCT f.source FROM files f JOIN links l ON l.target_path=f.path "
            "WHERE l.source_file_id=? AND l.relation IN ('selected:science','selected:standard')", (file_id,))}
        if len(sources) == 1:
            self.connection.execute("UPDATE files SET source=? WHERE id=?", (sources.pop(), file_id))
        self.connection.commit()
        return self.catalog.get_file(file_id) or {}

    def refresh_registered_projects(self) -> int:
        """Refresh only already indexed project documents, never their directories."""
        changed = 0
        for row in self.connection.execute("SELECT id,path,size_bytes,mtime_ns FROM files WHERE kind='project'").fetchall():
            try:
                info = Path(row["path"]).stat()
            except OSError:
                self.connection.execute("UPDATE files SET missing=1 WHERE id=?", (row["id"],))
                continue
            if (info.st_size, info.st_mtime_ns) != (row["size_bytes"], row["mtime_ns"]):
                self.index_project(row["path"])
                changed += 1
            else:
                self.connection.execute("UPDATE files SET missing=0 WHERE id=?", (row["id"],))
        self.connection.commit()
        return changed

    def refresh_run_sources(self, run_ids: list[str] | None = None, *, only_stale: bool = False) -> dict[str, Any]:
        """Re-evaluate ambiguous old provenance from known manifest paths only.

        This is a targeted metadata correction, not a scan or parser-version
        migration. By default it checks real/unknown runs. Incremental scans use
        only_stale to apply the separately versioned policy once. It never
        reads FITS pixels, registers roots or changes an observation byte.
        """
        clauses, args = ["r.manifest_path<>''"], []
        if run_ids is None:
            clauses.append("r.source IN ('real','unknown')")
        else:
            clauses.append("r.id IN (SELECT value FROM json_each(?))")
            args.append(json.dumps(run_ids))
        if only_stale:
            clauses.append("COALESCE(f.source_policy_version,0)<?")
            args.append(SOURCE_POLICY_VERSION)
        rows = self.connection.execute("SELECT r.id,r.source,r.manifest_path FROM runs r "
            "LEFT JOIN files f ON f.path=library_canonical_path(r.manifest_path) WHERE " +
                                       " AND ".join(clauses), args).fetchall()
        result: dict[str, Any] = {"evaluated": 0, "changed": 0, "warnings": []}
        for row in rows:
            payload = self._payload(row["manifest_path"], result["warnings"])
            source = _source(payload)
            result["evaluated"] += 1
            if source != row["source"]:
                result["changed"] += 1
            self.connection.execute("UPDATE runs SET source=? WHERE id=?", (source, row["id"]))
            self.connection.execute("UPDATE files SET source=? WHERE run_id=?", (source, row["id"]))
            # Sidecar reconciliation runs again on later scans. Correct its
            # cached facts too, or it would silently restore the old source.
            self.connection.execute(
                "UPDATE frame_details SET details_json=json_set(details_json,'$.source',?) "
                "WHERE source_file_id IN (SELECT id FROM files WHERE run_id=?)",
                (source, row["id"]))
            manifest = self.connection.execute("SELECT id,metadata_json FROM files WHERE path=?",
                (_canonical(row["manifest_path"]),)).fetchone()
            if manifest:
                metadata = json.loads(manifest["metadata_json"])
                if isinstance(metadata.get("context"), dict):
                    metadata["context"]["source"] = source
                if isinstance(metadata.get("frame"), dict):
                    metadata["frame"]["source"] = source
                self.connection.execute("UPDATE files SET source=?,metadata_json=?,source_policy_version=? WHERE id=?",
                    (source, json.dumps(metadata), SOURCE_POLICY_VERSION, manifest["id"]))
            if result["evaluated"] % 100 == 0:
                self.connection.commit()
        self.connection.commit()
        return result
