"""Offline, in-place observation catalogue; never changes or opens acquisition devices.

The database is a rebuildable index plus independent operator decisions. Scans read
metadata and FITS headers only. No deletion, relocation or archive operation is exposed.
"""

from __future__ import annotations

import base64
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sqlite3
import stat
import time
from typing import Any, Callable, Iterable
from uuid import uuid4


FITS_SUFFIXES = {".fit", ".fits", ".fts"}
PRODUCT_SUFFIXES = {".csv", ".ecsv", ".npy", ".npz", ".pdf", ".svg"}
MAX_METADATA_BYTES = 32 * 1024 * 1024
PARSER_VERSION = 4
SOURCE_POLICY_VERSION = 2
SOURCES = {"real", "simulated", "unknown"}
DECISIONS = {"unreviewed", "pick", "reject"}


def default_catalog_path() -> Path:
    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local" / "share"))
    return base / "UVEX-ADV" / "library" / "catalog.sqlite3"


def _canonical(path: str | Path) -> str:
    # abspath rather than resolve: never silently follow a junction/symlink.
    return os.path.normcase(os.path.abspath(os.path.expanduser(str(path))))


def _utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def _reparse(path: str | Path) -> bool:
    try:
        info = os.lstat(path)
        return stat.S_ISLNK(info.st_mode) or bool(
            getattr(info, "st_file_attributes", 0) & 0x400
        )
    except OSError:
        return False


def _unsafe_root(path: str | Path) -> bool:
    absolute = Path(_canonical(path))
    return any(_reparse(part) for part in (absolute, *absolute.parents))


def _source(values: Any) -> str:
    """Use recorded execution provenance, not target names or generic model hints.

    Expected IDs such as ATR/G3/QHY or a plan's Real mode are not evidence that a
    fixture executed against physical equipment. Ambiguous legacy records remain
    unknown; they are retained and visible through the all/unknown source views.
    """
    if not isinstance(values, dict):
        return "unknown"
    plan = values.get("plan", values)
    if not isinstance(plan, dict):
        plan = {}
    identifiers = [str(plan.get(key, "")) for key in (
        "nightSetupId", "expectedAtrCameraId", "expectedG3ProfileName", "expectedQhyCameraId"
    )]
    identifiers.append(str(values.get("expectedCameraStableId") or ""))
    labels = values.get("lockedMetadata")
    labels = labels.get("labels") if isinstance(labels, dict) else {}
    labels = labels if isinstance(labels, dict) else {}
    adapter = str(labels.get("adapter") or "").casefold()
    flags = [values.get(key) for key in ("simulator", "isSimulation", "isSimulated", "isSimulator")]
    gates = values.get("gates")
    gates = gates.values() if isinstance(gates, dict) else gates if isinstance(gates, list) else []
    fixture = any(isinstance(gate, dict) and str(gate.get("code") or "").upper()
                  in {"TEST", "FIXTURE_STAGE"} for gate in gates)
    if (any(flag is True for flag in flags) or adapter in {"simulated", "simulator", "simulation"}
            or str(values.get("source") or "").casefold() in {"simulated", "simulation"} or fixture or any(
        x.upper().startswith(("SIM-", "OFFLINE-")) for x in identifiers
    )):
        return "simulated"
    if (any(flag is False for flag in flags) or adapter == "real"
            or str(values.get("source") or "").casefold() == "real"
            or any("usb#" in value.casefold() or "vid_" in value.casefold()
                   or value.upper().startswith("QHYMINICAM") and "-" in value
                   for value in identifiers[1:])):
        return "real"
    return "unknown"


def _kind(path: Path) -> str:
    suffix = path.suffix.lower()
    if suffix in FITS_SUFFIXES:
        return "frame"
    if suffix in {".png", ".jpg", ".jpeg", ".webp"}:
        return "preview"
    if suffix in {".log", ".txt"}:
        return "log"
    if suffix == ".astroproj":
        return "project"
    if suffix in PRODUCT_SUFFIXES:
        return "product"
    if suffix in {".json", ".jsonl"}:
        return "metadata"
    return "other"


def _channel(role: str, path: str = "") -> str:
    upper = f"{role} {path}".upper()
    if "PHOTOMETRY" in upper or "QHY" in upper:
        return "photometry"
    if "G3" in upper or "PHD2" in upper:
        return "slit"
    if "ATR" in upper or "SPECTR" in upper:
        return "spectroscopy"
    return "unknown"


def _accepted(value: Any) -> int | None:
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)) and value in (0, 1):
        return int(value)
    if isinstance(value, str) and value.strip().lower() in {"true", "false", "0", "1"}:
        return int(value.strip().lower() in {"true", "1"})
    return None


class LibraryCatalog:
    """One SQLite connection per worker/thread; UI readers may open another instance."""

    def __init__(self, db_path: str | Path | None = None):
        self.db_path = Path(db_path) if db_path is not None else default_catalog_path()
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(self.db_path, timeout=30)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("PRAGMA journal_mode=WAL")
        self.connection.execute("PRAGMA foreign_keys=ON")
        self.connection.executescript("""
            CREATE TABLE IF NOT EXISTS roots(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, label TEXT NOT NULL,
                last_scan_utc TEXT, warning TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, size_bytes INTEGER NOT NULL,
                mtime_ns INTEGER NOT NULL, kind TEXT NOT NULL, extension TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'unknown', channel TEXT NOT NULL DEFAULT 'unknown',
                role TEXT NOT NULL DEFAULT '', target TEXT NOT NULL DEFAULT '',
                run_id TEXT NOT NULL DEFAULT '', capture_id TEXT NOT NULL DEFAULT '',
                exposure_seconds REAL, acquired_utc TEXT NOT NULL DEFAULT '',
                accepted INTEGER, missing INTEGER NOT NULL DEFAULT 0,
                warning TEXT NOT NULL DEFAULT '', metadata_json TEXT NOT NULL DEFAULT '{}',
                source_policy_version INTEGER NOT NULL DEFAULT 0,
                parser_version INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS root_files(
                root_id INTEGER NOT NULL REFERENCES roots(id),
                file_id INTEGER NOT NULL REFERENCES files(id), generation TEXT NOT NULL,
                PRIMARY KEY(root_id,file_id));
            CREATE TABLE IF NOT EXISTS reviews(
                file_id INTEGER PRIMARY KEY REFERENCES files(id),
                decision TEXT NOT NULL DEFAULT 'unreviewed', rating INTEGER NOT NULL DEFAULT 0,
                notes TEXT NOT NULL DEFAULT '', updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(
                id TEXT PRIMARY KEY, target TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT 'unknown', state TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL DEFAULT '', created_utc TEXT NOT NULL DEFAULT '',
                manifest_path TEXT NOT NULL DEFAULT '', authority INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS file_runs(
                file_id INTEGER NOT NULL REFERENCES files(id), run_id TEXT NOT NULL,
                PRIMARY KEY(file_id,run_id));
            CREATE TABLE IF NOT EXISTS links(
                source_file_id INTEGER NOT NULL REFERENCES files(id), target_path TEXT NOT NULL,
                relation TEXT NOT NULL, run_id TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(source_file_id,target_path,relation));
            CREATE INDEX IF NOT EXISTS files_target ON files(target);
            CREATE INDEX IF NOT EXISTS files_run ON files(run_id);
            CREATE INDEX IF NOT EXISTS files_date ON files(mtime_ns DESC,id);
            CREATE INDEX IF NOT EXISTS files_kind_date ON files(kind,mtime_ns DESC,id);
            CREATE INDEX IF NOT EXISTS file_runs_run ON file_runs(run_id,file_id);
            CREATE INDEX IF NOT EXISTS links_target ON links(target_path);
            CREATE INDEX IF NOT EXISTS links_run ON links(run_id,target_path);
            CREATE TABLE IF NOT EXISTS frame_details(
                source_file_id INTEGER,target_path TEXT,details_json TEXT,
                PRIMARY KEY(source_file_id,target_path));
            CREATE INDEX IF NOT EXISTS frame_details_target ON frame_details(target_path);
            CREATE TABLE IF NOT EXISTS project_documents(
                file_id INTEGER PRIMARY KEY REFERENCES files(id),
                document_type TEXT NOT NULL, name TEXT NOT NULL DEFAULT '',
                target TEXT NOT NULL DEFAULT '', stage TEXT NOT NULL DEFAULT '',
                details_json TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE IF NOT EXISTS project_run_context(
                run_id TEXT NOT NULL, source_file_id INTEGER NOT NULL REFERENCES files(id),
                night TEXT NOT NULL DEFAULT '', outcome TEXT NOT NULL DEFAULT '',
                gate TEXT NOT NULL DEFAULT '', failure_scope TEXT NOT NULL DEFAULT '',
                requested_frames INTEGER, accepted_frames INTEGER,
                accepted_exposure_seconds REAL, segments_json TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY(run_id,source_file_id));
            CREATE INDEX IF NOT EXISTS project_context_run ON project_run_context(run_id);
            CREATE INDEX IF NOT EXISTS project_context_source ON project_run_context(source_file_id);
            CREATE TABLE IF NOT EXISTS project_run_segments(
                run_id TEXT NOT NULL, segment_run_id TEXT NOT NULL,
                source_file_id INTEGER NOT NULL REFERENCES files(id),
                PRIMARY KEY(run_id,segment_run_id,source_file_id));
            CREATE INDEX IF NOT EXISTS project_segments_segment ON project_run_segments(segment_run_id);
            CREATE INDEX IF NOT EXISTS project_segments_source ON project_run_segments(source_file_id);
            CREATE TABLE IF NOT EXISTS project_night_entries(
                source_file_id INTEGER NOT NULL REFERENCES files(id),entry_index INTEGER NOT NULL,
                target TEXT NOT NULL,night TEXT NOT NULL DEFAULT '',outcome TEXT NOT NULL DEFAULT '',
                gate TEXT NOT NULL DEFAULT '',failure_scope TEXT NOT NULL DEFAULT '',
                run_id TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(source_file_id,entry_index));
        """)
        columns = {row[1] for row in self.connection.execute("PRAGMA table_info(files)")}
        if "parser_version" not in columns:
            self.connection.execute("ALTER TABLE files ADD COLUMN parser_version INTEGER NOT NULL DEFAULT 0")
        if "source_policy_version" not in columns:
            self.connection.execute("ALTER TABLE files ADD COLUMN source_policy_version INTEGER NOT NULL DEFAULT 0")
        if "authority" not in {row[1] for row in self.connection.execute("PRAGMA table_info(runs)")}:
            self.connection.execute("ALTER TABLE runs ADD COLUMN authority INTEGER NOT NULL DEFAULT 0")
        if "run_id" not in {row[1] for row in self.connection.execute("PRAGMA table_info(project_night_entries)")}:
            self.connection.execute("ALTER TABLE project_night_entries ADD COLUMN run_id TEXT NOT NULL DEFAULT ''")
        self.connection.commit()

    def close(self) -> None:
        self.connection.close()

    def __enter__(self) -> LibraryCatalog:
        return self

    def __exit__(self, *_args: Any) -> None:
        self.close()

    def add_root(self, path: str | Path, label: str = "") -> dict[str, Any]:
        value = _canonical(path)
        if _unsafe_root(value):
            raise ValueError("A library root must not be a symlink or reparse point")
        self.connection.execute(
            "INSERT INTO roots(path,label) VALUES(?,?) "
            "ON CONFLICT(path) DO UPDATE SET label=excluded.label", (value, label or Path(value).name)
        )
        self.connection.commit()
        return dict(self.connection.execute("SELECT * FROM roots WHERE path=?", (value,)).fetchone())

    def list_roots(self) -> list[dict[str, Any]]:
        return [dict(row) for row in self.connection.execute("SELECT * FROM roots ORDER BY path")]

    def _warning(self, file_id: int, warning: str) -> None:
        self.connection.execute("UPDATE files SET warning=? WHERE id=?", (warning, file_id))

    def _append_warning(self, file_id: int, warning: str) -> None:
        current = self.connection.execute("SELECT warning FROM files WHERE id=?", (file_id,)).fetchone()[0]
        if warning not in current:
            self._warning(file_id, "; ".join(value for value in (current, warning) if value))

    def _apply_frame_details(self, file_id: int, details: dict[str, Any]) -> None:
        existing = self.connection.execute(
            "SELECT target,run_id,capture_id,metadata_json FROM files WHERE id=?", (file_id,)
        ).fetchone()
        header = json.loads(existing["metadata_json"])
        protected = {key for key in ("target", "run_id", "capture_id") if header.get(key)}
        for key in protected:
            if details.get(key) and str(details[key]) != str(header[key]):
                self._append_warning(file_id, f"Identity conflict ({key}): FITS={header[key]!r}; "
                                     f"linked metadata={details[key]!r}; FITS identity retained")
        self._details(file_id, {key: value for key, value in details.items() if key not in protected})
        if isinstance(details.get("quality_metadata"), dict):
            header["quality"] = details["quality_metadata"]
            self.connection.execute("UPDATE files SET metadata_json=? WHERE id=?",
                                    (json.dumps(header), file_id))

    def _clear_linked_details(self, source_id: int) -> None:
        """Refresh changed sidecars without retaining an obsolete acceptance/role."""
        rows = self.connection.execute(
            "SELECT f.id,f.metadata_json FROM files f JOIN frame_details fd "
            "ON f.path=fd.target_path WHERE fd.source_file_id=?", (source_id,)
        ).fetchall()
        for row in rows:
            own = json.loads(row["metadata_json"])
            own.pop("quality", None)
            self.connection.execute("UPDATE files SET accepted=NULL,role='',capture_id='',"
                                    "exposure_seconds=NULL,metadata_json=? WHERE id=?",
                                    (json.dumps(own), row["id"]))
            self._details(row["id"], own)

    def _touch(self, path: str, root_id: int, generation: str) -> tuple[int, bool]:
        path = _canonical(path)
        info = os.stat(path, follow_symlinks=False)
        old = self.connection.execute(
            "SELECT id,size_bytes,mtime_ns,parser_version FROM files WHERE path=?", (path,)
        ).fetchone()
        changed = old is None or (old["size_bytes"], old["mtime_ns"]) != (
            info.st_size, info.st_mtime_ns
        ) or old["parser_version"] != PARSER_VERSION
        self.connection.execute(
            "INSERT INTO files(path,size_bytes,mtime_ns,kind,extension,parser_version) VALUES(?,?,?,?,?,?) "
            "ON CONFLICT(path) DO UPDATE SET size_bytes=excluded.size_bytes,"
            "mtime_ns=excluded.mtime_ns,missing=0,parser_version=excluded.parser_version",
            (path, info.st_size, info.st_mtime_ns, _kind(Path(path)), Path(path).suffix.lower(),
             PARSER_VERSION)
        )
        file_id = self.connection.execute("SELECT id FROM files WHERE path=?", (path,)).fetchone()[0]
        self.connection.execute(
            "INSERT INTO root_files VALUES(?,?,?) ON CONFLICT(root_id,file_id) "
            "DO UPDATE SET generation=excluded.generation", (root_id, file_id, generation)
        )
        return file_id, changed

    def _details(self, file_id: int, values: dict[str, Any], *, overwrite: bool = True) -> None:
        allowed = {"source", "channel", "role", "target", "run_id", "capture_id",
                   "exposure_seconds", "acquired_utc", "accepted"}
        for key, value in values.items():
            if key not in allowed or value is None or value == "" or value == "unknown":
                continue
            if key in {"exposure_seconds"}:
                try:
                    value = float(value)
                except (TypeError, ValueError):
                    continue
            elif key != "accepted":
                value = str(value)
            if key == "accepted":
                value = _accepted(value)
            if overwrite:
                self.connection.execute(f"UPDATE files SET {key}=? WHERE id=?", (value, file_id))
            else:
                self.connection.execute(
                    f"UPDATE files SET {key}=? WHERE id=? AND "
                    f"({key} IS NULL OR {key}='' OR {key}='unknown')", (value, file_id)
                )
        run_id = values.get("run_id")
        if run_id:
            self.connection.execute("INSERT OR IGNORE INTO file_runs VALUES(?,?)", (file_id, run_id))
            self.connection.execute(
                "INSERT OR IGNORE INTO runs(id,target,source) VALUES(?,?,?)",
                (run_id, str(values.get("target") or ""), str(values.get("source") or "unknown"))
            )

    def _link(self, source_id: int, value: str, relation: str, base: Path, run_id: str) -> str:
        if not value or "://" in value or "\x00" in value:
            return ""
        path = _canonical(value if os.path.isabs(value) else base / value)
        self.connection.execute("INSERT OR IGNORE INTO links VALUES(?,?,?,?)",
                                (source_id, path, relation, run_id))
        return path

    def _references(self, file_id: int, payload: Any, base: Path, run_id: str) -> None:
        path_suffixes = FITS_SUFFIXES | PRODUCT_SUFFIXES | {
            ".json", ".jsonl", ".astroproj", ".png", ".jpg", ".log", ".txt", ".toml"
        }

        def visit(value: Any, key: str = "", depth: int = 0,
                  path_container: bool = False, selection: bool = False) -> None:
            if depth > 30:
                return
            if isinstance(value, dict):
                for child_key, child in value.items():
                    child_key = str(child_key)
                    lower = child_key.lower()
                    nested_paths = path_container or lower in {"inputs", "artifacts", "outputs"}
                    nested_paths |= selection and lower in {"accepted", "rejected"}
                    visit(child, child_key, depth + 1, nested_paths,
                          selection or lower == "frameselection")
            elif isinstance(value, list):
                for child in value:
                    visit(child, key, depth + 1, path_container, selection)
            elif isinstance(value, str):
                lower = key.lower()
                if (lower.endswith(("path", "paths", "file", "files")) or lower in {
                        "sciencerun", "standardrun", "flattrialrun", "scienceproduct",
                        "standardproduct", "lastoutput", "last_output"} or
                        path_container and (os.path.isabs(value)
                                            or Path(value).suffix.lower() in path_suffixes)):
                    self._link(file_id, value, key, base, run_id)
        visit(payload)

    def _parse_json(self, path: Path, file_id: int) -> dict[str, Any]:
        if path.stat().st_size > MAX_METADATA_BYTES:
            self._warning(file_id, "Metadata exceeds bounded parser limit; file retained unparsed")
            return {}
        try:
            payload = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(payload, dict):
                return {}
        except (OSError, ValueError) as exc:
            self._warning(file_id, f"Metadata unreadable: {type(exc).__name__}: {exc}")
            return {}
        plan = payload.get("plan", {})
        if not isinstance(plan, dict):
            plan = {}
        target_value = plan.get("target", payload.get("target", payload.get("requestedTarget", {})))
        target = target_value.get("name", "") if isinstance(target_value, dict) else target_value
        run_id = str(payload.get("observationRunId", payload.get(
            "runId", plan.get("observationRunId", ""))))
        source = _source(payload)
        context = {"run_id": run_id, "target": str(target or ""), "source": source}
        # Offline project interpretation is derived from these same authoritative
        # manifests, not filename/target-name guesses or a second directory crawl.
        from .library_projects import index_document_facts

        index_document_facts(self, file_id, path, payload)
        if run_id and (plan or "snapshot" in payload or "requestedTarget" in payload):
            snapshot = payload.get("snapshot", {})
            if not isinstance(snapshot, dict):
                snapshot = {}
            state = payload.get("terminalState") or snapshot.get("state", payload.get("state", ""))
            authority = 2 if plan or "snapshot" in payload else 1
            updates = ",".join(
                f"{key}=CASE WHEN excluded.{key}<>'' AND "
                f"(excluded.authority>=runs.authority OR runs.{key}='') "
                f"THEN excluded.{key} ELSE runs.{key} END"
                for key in ("target", "state", "reason", "created_utc", "manifest_path")
            )
            self.connection.execute(
                "INSERT INTO runs VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET "
                + updates + ",source=CASE WHEN excluded.source<>'unknown' AND "
                "(excluded.authority>=runs.authority OR runs.source='unknown') "
                "THEN excluded.source ELSE runs.source END,"
                "authority=MAX(runs.authority,excluded.authority)",
                (run_id, context["target"], source, state or "",
                 str(payload.get("terminalReason") or ""),
                 str(payload.get("createdUtc") or ""), str(path), authority)
            )
            self.connection.execute("UPDATE files SET source_policy_version=? WHERE id=?",
                                    (SOURCE_POLICY_VERSION, file_id))
        self._details(file_id, context)
        self._references(file_id, payload, path.parent, run_id)
        for evidence in payload.get("evidence", []) if isinstance(payload.get("evidence"), list) else []:
            if not isinstance(evidence, dict):
                continue
            metadata = evidence.get("metadata") or {}
            if not isinstance(metadata, dict):
                metadata = {}
            absolute = evidence.get("absolutePath", evidence.get("fitsPath", ""))
            if not isinstance(absolute, str) or Path(absolute).suffix.lower() not in FITS_SUFFIXES:
                continue
            target_path = self._link(file_id, absolute, "evidence", path.parent, run_id)
            if not target_path:
                continue
            details = {**context, "run_id": metadata.get("observationRunId", run_id),
                       "target": metadata.get("targetName", context["target"]),
                       "capture_id": metadata.get("captureId", ""),
                       "role": metadata.get("effectiveStageRole", metadata.get(
                           "stageRole", metadata.get("role", ""))),
                       "channel": _channel(str(evidence.get("kind", ""))),
                       "exposure_seconds": metadata.get("exposureSeconds"),
                       "accepted": metadata.get("qualityAccepted"),
                       "quality_metadata": {key: metadata[key] for key in (
                           "qualityAccepted", "qualityCode", "qualityMessage", "qualityDisposition")
                           if key in metadata}}
            self.connection.execute("INSERT OR REPLACE INTO frame_details VALUES(?,?,?)",
                                    (file_id, target_path, json.dumps(details)))
        selection = payload.get("parameters", {}).get("librarySelection", {}) if isinstance(
            payload.get("parameters"), dict) else {}
        if isinstance(selection, dict) and isinstance(selection.get("frames"), list):
            for frame in selection["frames"]:
                if isinstance(frame, dict) and isinstance(frame.get("path"), str):
                    self._link(file_id, frame["path"], "selected:" + str(frame.get("role", "")),
                               path.parent, "")
        # Evidence may describe an ATR frame independently of the run manifest.
        frame_values = {
            **context, "role": payload.get("role", ""),
            "capture_id": payload.get("captureId", ""),
            "accepted": payload.get("qualityAccepted"),
            "exposure_seconds": payload.get("exposureSeconds"),
        }
        if frame_values["role"] or frame_values["capture_id"]:
            frame_values["channel"] = _channel(str(frame_values["role"]), path.name)
        self.connection.execute("UPDATE files SET metadata_json=? WHERE id=?",
                                (json.dumps({"context": context, "frame": frame_values}), file_id))
        # Evidence and reduction manifests link raw/product paths. Propagate only
        # explicitly reported frame facts, without opening referenced paths.
        if frame_values.get("role") or frame_values.get("capture_id"):
            for row in self.connection.execute(
                "SELECT target_path FROM links WHERE source_file_id=?", (file_id,)
            ).fetchall():
                if Path(row[0]).suffix.lower() in FITS_SUFFIXES:
                    self.connection.execute("INSERT OR REPLACE INTO frame_details VALUES(?,?,?)",
                                            (file_id, row[0], json.dumps(frame_values)))
        self._warning(file_id, "")
        return context

    def _fits(self, path: Path, file_id: int) -> None:
        try:
            from astropy.io import fits

            header = fits.getheader(path, 0)  # header only; never .data or getdata
            # Reduction products deliberately retain OBSRUNID/UVEXSTG provenance.
            # They are not another acquired science exposure, even when their
            # primary header still says SCIENCE and repeats the source EXPTIME.
            if str(header.get("PIPELINE", "")).strip().upper() == "UVEX-ADV":
                self.connection.execute("UPDATE files SET kind='product' WHERE id=?", (file_id,))
            target = str(header.get("OBJECT", ""))
            if header.get("OBJENC") == "UTF8-B64":
                count = int(header.get("OBJCNT", 0))
                if not 0 < count <= 9999:
                    raise ValueError("Invalid UTF-8 target chunk count")
                target = base64.b64decode("".join(
                    str(header[f"OBJ{index:04d}"]) for index in range(1, count + 1)
                ), validate=True).decode("utf-8")
            role = str(header.get("UVEXSTG", header.get("IMAGETYP", "")))
            camera = str(header.get("INSTRUME", header.get("CAMERA", "")))
            values = {
                "target": target, "run_id": str(header.get("OBSRUNID", "")),
                "capture_id": str(header.get("UVEXCID", "")), "role": role,
                "channel": _channel(role, camera), "exposure_seconds": header.get("EXPTIME"),
                "acquired_utc": str(header.get("DATE-OBS", "")),
            }
            existing = self.connection.execute(
                "SELECT target,run_id FROM files WHERE id=?", (file_id,)
            ).fetchone()
            conflicts = []
            for key in ("target", "run_id"):
                if values.get(key) and existing[key] and str(values[key]) != str(existing[key]):
                    conflicts.append(f"Identity conflict ({key}): FITS={values[key]!r}; "
                                     f"folder metadata={existing[key]!r}; FITS identity retained")
            if values["run_id"]:
                self.connection.execute("DELETE FROM file_runs WHERE file_id=?", (file_id,))
            self._details(file_id, values)
            self.connection.execute("UPDATE files SET metadata_json=? WHERE id=?",
                                    (json.dumps(dict(values), default=str), file_id))
            self._warning(file_id, "; ".join(conflicts))
        except Exception as exc:  # invalid/truncated FITS is a library warning, not a lost file
            self._warning(file_id, f"FITS header unreadable: {type(exc).__name__}: {exc}")

    def _frames(self, path: Path, file_id: int, context: dict[str, Any],
                cancelled: Callable[[], bool] | None = None) -> None:
        warnings: list[str] = []
        try:
            with path.open("r", encoding="utf-8-sig") as stream:
                for number, line in enumerate(stream, 1):
                    if cancelled and cancelled():
                        warnings.append("Frame index scan cancelled; existing entries retained")
                        self.connection.execute("UPDATE files SET mtime_ns=-1 WHERE id=?", (file_id,))
                        break
                    if not line.strip():
                        continue
                    try:
                        frame = json.loads(line)
                        if not isinstance(frame, dict):
                            raise ValueError("frame index row is not an object")
                    except ValueError:
                        warnings.append(f"Unparsed frame-index line {number} (possibly still writing)")
                        continue
                    run_id = str(frame.get("observationRunId", context.get("run_id", "")))
                    fits_path = self._link(file_id, str(frame.get("fitsPath", "")),
                                           "fitsPath", path.parent, run_id)
                    self._link(file_id, str(frame.get("previewPath", "")),
                               "previewPath", path.parent, run_id)
                    role = str(frame.get("role", ""))
                    details = {**context, "run_id": run_id, "channel": "photometry", "role": role,
                               "capture_id": frame.get("frameId", ""),
                               "exposure_seconds": (frame.get("settings") or {}).get(
                                   "exposureSeconds"),
                               "acquired_utc": frame.get("exposureStartedUtc", "")}
                    # Persist frame metadata before the traversal reaches the raw directory.
                    if fits_path:
                        row = self.connection.execute("SELECT id FROM files WHERE path=?",
                                                      (fits_path,)).fetchone()
                        if row:
                            self._apply_frame_details(row[0], details)
                        self.connection.execute(
                            "INSERT OR REPLACE INTO frame_details VALUES(?,?,?)",
                            (file_id, fits_path, json.dumps(details))
                        )
        except (OSError, UnicodeError) as exc:
            warnings.append(f"Frame index unreadable: {exc}")
        self._warning(file_id, "; ".join(warnings[:10]))

    def scan(
        self, root_ids: Iterable[int] | None = None,
        progress: Callable[[dict[str, Any]], None] | None = None,
        cancelled: Callable[[], bool] | None = None,
    ) -> dict[str, Any]:
        """Explicit incremental scan. Partial/cancelled scans never mark files missing."""
        self.connection.execute("CREATE TABLE IF NOT EXISTS frame_details("
                                "source_file_id INTEGER,target_path TEXT,details_json TEXT,"
                                "PRIMARY KEY(source_file_id,target_path))")
        roots = self.list_roots()
        if root_ids is not None:
            wanted = set(root_ids)
            roots = [root for root in roots if root["id"] in wanted]
        result = {"files_seen": 0, "files_parsed": 0, "roots_scanned": 0,
                  "cancelled": False, "warnings": []}
        # Clock resolution (or rollback) must not merge consecutive scan batches.
        generation = uuid4().hex
        last_progress = 0.0
        for root in roots:
            root_path = Path(root["path"])
            if _unsafe_root(root_path) or not root_path.is_dir():
                warning = "Root missing/offline or a reparse point; no files removed"
                self.connection.execute("UPDATE roots SET warning=? WHERE id=?",
                                        (warning, root["id"]))
                result["warnings"].append(f"{root_path}: {warning}")
                continue
            stack: list[tuple[Path, dict[str, Any]]] = [(root_path, {})]
            complete = True
            while stack:
                if cancelled and cancelled():
                    result["cancelled"] = True
                    complete = False
                    break
                directory, inherited = stack.pop()
                try:
                    with os.scandir(directory) as iterator:
                        entries = sorted(iterator, key=lambda e: (
                            0 if e.name == "manifest.json" else
                            1 if e.name == "frames.jsonl" else 2, e.name))
                except OSError as exc:
                    complete = False
                    result["warnings"].append(f"{directory}: {exc}")
                    continue
                context = dict(inherited)
                child_dirs: list[Path] = []
                for entry in entries:
                    if cancelled and cancelled():
                        result["cancelled"] = True
                        complete = False
                        break
                    path = Path(entry.path)
                    if _reparse(path):
                        result["warnings"].append(f"Skipped reparse point: {path}")
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        child_dirs.append(path)
                        continue
                    if not entry.is_file(follow_symlinks=False):
                        continue
                    if _canonical(path) in {_canonical(self.db_path),
                                             _canonical(str(self.db_path) + "-wal"),
                                             _canonical(str(self.db_path) + "-shm")}:
                        continue
                    try:
                        file_id, changed = self._touch(str(path), root["id"], generation)
                        if changed:
                            # Discard only derived facts when source metadata changes.
                            # Operator decisions live in reviews and are never reset.
                            self.connection.execute(
                                "UPDATE files SET source='unknown',channel='unknown',role='',"
                                "target='',run_id='',capture_id='',exposure_seconds=NULL,"
                                "acquired_utc='',accepted=NULL,warning='',metadata_json='{}' WHERE id=?",
                                (file_id,)
                            )
                            self.connection.execute("DELETE FROM file_runs WHERE file_id=?", (file_id,))
                            for table, column in (("project_documents", "file_id"),
                                                  ("project_run_context", "source_file_id"),
                                                  ("project_run_segments", "source_file_id"),
                                                  ("project_night_entries", "source_file_id")):
                                self.connection.execute(f"DELETE FROM {table} WHERE {column}=?", (file_id,))
                        self._details(file_id, context, overwrite=False)
                        if path.name == "manifest.json" and not changed:
                            stored = self.connection.execute(
                                "SELECT metadata_json FROM files WHERE id=?", (file_id,)
                            ).fetchone()[0]
                            context.update(json.loads(stored).get("context", {}))
                        if changed:
                            result["files_parsed"] += 1
                            self._clear_linked_details(file_id)
                            self.connection.execute("DELETE FROM links WHERE source_file_id=?",
                                                    (file_id,))
                            self.connection.execute("DELETE FROM frame_details WHERE source_file_id=?",
                                                    (file_id,))
                            if path.suffix.lower() in {".json", ".astroproj"}:
                                found = self._parse_json(path, file_id)
                                if path.name == "manifest.json":
                                    context.update({k: v for k, v in found.items() if v})
                            elif path.name == "frames.jsonl":
                                self._frames(path, file_id, context, cancelled)
                            elif path.suffix.lower() in FITS_SUFFIXES:
                                self._fits(path, file_id)
                        frame_rows = self.connection.execute(
                            "SELECT details_json FROM frame_details WHERE target_path=?",
                            (_canonical(path),)
                        )
                        for frame_row in frame_rows:
                            self._apply_frame_details(file_id, json.loads(frame_row[0]))
                        result["files_seen"] += 1
                        now = time.monotonic()
                        if progress and (now - last_progress > 0.1):
                            progress({**result, "path": str(path), "root": str(root_path)})
                            last_progress = now
                        if result["files_seen"] % 200 == 0:
                            self.connection.commit()
                    except (OSError, ValueError, TypeError, AttributeError) as exc:
                        result["warnings"].append(f"{path}: {exc}")
                stack.extend((child, dict(context)) for child in reversed(child_dirs))
                if result["cancelled"]:
                    break
            if complete:
                unseen = self.connection.execute(
                    "SELECT f.id,f.path FROM files f JOIN root_files rf ON f.id=rf.file_id "
                    "WHERE rf.root_id=? AND rf.generation<>?", (root["id"], generation)
                ).fetchall()
                for row in unseen:
                    self.connection.execute("UPDATE files SET missing=? WHERE id=?",
                                            (int(not os.path.isfile(row["path"])), row["id"]))
                self.connection.execute("UPDATE roots SET last_scan_utc=?,warning='' WHERE id=?",
                                        (_utc(), root["id"]))
                result["roots_scanned"] += 1
            self.connection.commit()
            if result["cancelled"]:
                break
        self.connection.commit()
        # Evidence can occur after its target in directory order. Reconcile only
        # already indexed paths; references never expand the explicitly selected roots.
        for row in self.connection.execute(
            "SELECT f.id,fd.details_json FROM frame_details fd JOIN files f "
            "ON f.path=fd.target_path"
        ).fetchall():
            self._apply_frame_details(row[0], json.loads(row[1]))
        self.connection.commit()
        if not result["cancelled"]:
            # Source policy is separate from FITS/parser migrations. Revisit only
            # known stale manifest facts, not all 177k observations or pixels.
            from .library_projects import LibraryProjects

            result["source_policy"] = LibraryProjects(self).refresh_run_sources(only_stale=True)
        if progress:
            progress({**result, "path": "", "complete": not result["cancelled"]})
        return result

    def _where(self, **filters: Any) -> tuple[str, list[Any]]:
        clauses: list[str] = []
        args: list[Any] = []
        if filters.get("file_ids") is not None:
            identifiers = list(dict.fromkeys(int(value) for value in filters["file_ids"]))
            clauses.append("f.id IN (SELECT value FROM json_each(?))")
            args.append(json.dumps(identifiers))
        for key in ("source", "channel", "role", "target", "kind", "run_id"):
            value = filters.get(key)
            if value:
                if key == "run_id":
                    clauses.append("f.id IN (SELECT file_id FROM file_runs WHERE run_id=?)")
                else:
                    clauses.append(f"f.{key}=?")
                args.append(value)
        if filters.get("review"):
            clauses.append("COALESCE(r.decision,'unreviewed')=?")
            args.append(filters["review"])
        if filters.get("query"):
            clauses.append("(f.path LIKE ? ESCAPE '\\' OR f.target LIKE ? ESCAPE '\\')")
            value = str(filters["query"]).replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
            args.extend([f"%{value}%"] * 2)
        if filters.get("date_prefix") or filters.get("night"):
            clauses.append("f.acquired_utc LIKE ?")
            args.append(str(filters.get("date_prefix") or filters["night"]) + "%")
        return (" WHERE " + " AND ".join(clauses) if clauses else ""), args

    def list_files(self, offset: int = 0, limit: int = 100,
                   sort: str = "date_desc", **filters: Any) -> list[dict[str, Any]]:
        where, args = self._where(**filters)
        ordering = {"date_desc": "f.mtime_ns DESC,f.id", "date_asc": "f.mtime_ns,f.id",
                    "size_desc": "f.size_bytes DESC,f.id", "path": "f.path",
                    "target": "f.target,f.path"}.get(sort, "f.mtime_ns DESC,f.id")
        rows = self.connection.execute(
            "SELECT f.*,COALESCE(r.decision,'unreviewed') review,COALESCE(r.rating,0) rating,"
            "COALESCE(r.notes,'') notes FROM files f LEFT JOIN reviews r ON f.id=r.file_id"
            + where + f" ORDER BY {ordering} LIMIT ? OFFSET ?",
            args + [max(1, min(int(limit), 1000)), max(0, int(offset))]
        )
        return [self._ui_row(row) for row in rows]

    @staticmethod
    def _ui_row(row: sqlite3.Row) -> dict[str, Any]:
        value = dict(row)
        value["date_obs"] = value.get("acquired_utc", "")
        value["warnings"] = [value["warning"]] if value.get("warning") else []
        value["status"] = "missing" if value.get("missing") else "available"
        return value

    def count_files(self, **filters: Any) -> int:
        where, args = self._where(**filters)
        return self.connection.execute(
            "SELECT COUNT(*) FROM files f LEFT JOIN reviews r ON f.id=r.file_id" + where, args
        ).fetchone()[0]

    def get_file(self, file_id: int, related_limit: int = 500) -> dict[str, Any] | None:
        row = self.connection.execute(
            "SELECT f.*,COALESCE(r.decision,'unreviewed') review,COALESCE(r.rating,0) rating,"
            "COALESCE(r.notes,'') notes FROM files f LEFT JOIN reviews r ON f.id=r.file_id "
            "WHERE f.id=?", (file_id,)
        ).fetchone()
        if not row:
            return None
        item = self._ui_row(row)
        cap = max(1, min(int(related_limit), 2000))
        item["metadata"] = json.loads(item.pop("metadata_json"))
        item["runs"] = [dict(r) for r in self.connection.execute(
            "SELECT runs.* FROM runs JOIN file_runs fr ON runs.id=fr.run_id "
            "WHERE fr.file_id=? LIMIT 100",
            (file_id,))]
        item["references"] = [dict(r) for r in self.connection.execute(
            "SELECT l.source_file_id,l.target_path,l.relation,l.run_id,"
            "CASE WHEN f.id IS NULL THEN 'not-indexed' WHEN f.missing=1 THEN 'missing' "
            "ELSE 'available' END status FROM links l LEFT JOIN files f ON f.path=l.target_path "
            "WHERE l.source_file_id=? OR l.target_path=? LIMIT ?",
            (file_id, item["path"], cap + 1))]
        item["references_truncated"] = len(item["references"]) > cap
        item["references"] = item["references"][:cap]
        run_ids = [r["id"] for r in item["runs"]]
        related = {item["path"]}
        if run_ids:
            placeholders = ",".join("?" for _ in run_ids)
            related.update(r[0] for r in self.connection.execute(
                f"SELECT DISTINCT f.path FROM files f JOIN file_runs fr ON f.id=fr.file_id "
                f"WHERE fr.run_id IN ({placeholders}) LIMIT ?", run_ids + [cap + 1]))
            related.update(r[0] for r in self.connection.execute(
                f"SELECT DISTINCT target_path FROM links WHERE run_id IN ({placeholders}) LIMIT ?",
                run_ids + [cap + 1]))
        related.update(r["target_path"] for r in item["references"])
        item["related_paths_truncated"] = len(related) > cap
        item["related_paths"] = sorted(related)[:cap]
        return item

    def set_review(self, file_id: int, decision: str = "unreviewed", rating: int = 0,
                   notes: str = "") -> None:
        if decision not in DECISIONS or not 0 <= rating <= 5:
            raise ValueError("Review must be unreviewed/pick/reject with rating 0..5")
        self.connection.execute(
            "INSERT INTO reviews VALUES(?,?,?,?,?) ON CONFLICT(file_id) DO UPDATE SET "
            "decision=excluded.decision,rating=excluded.rating,notes=excluded.notes,"
            "updated_utc=excluded.updated_utc", (file_id, decision, rating, notes, _utc())
        )
        self.connection.commit()

    def list_runs(self, offset: int = 0, limit: int = 100, source: str = "",
                  target: str = "") -> list[dict[str, Any]]:
        clauses = ["1=1"]
        args: list[Any] = []
        for key, value in (("source", source), ("target", target)):
            if value:
                clauses.append(f"{key}=?")
                args.append(value)
        return [dict(row) for row in self.connection.execute(
            "SELECT * FROM runs WHERE " + " AND ".join(clauses)
            + " ORDER BY created_utc DESC,id LIMIT ? OFFSET ?",
            args + [max(1, min(limit, 1000)), max(0, offset)])]

    def list_targets(self) -> list[str]:
        return [row[0] for row in self.connection.execute(
            "SELECT DISTINCT target FROM files WHERE target<>'' ORDER BY target")]

    def stats(self) -> dict[str, Any]:
        total = dict(self.connection.execute(
            "SELECT COUNT(*) files,COALESCE(SUM(size_bytes),0) indexed_bytes,"
            "COALESCE(SUM(CASE WHEN missing=0 THEN size_bytes ELSE 0 END),0) available_bytes,"
            "COALESCE(SUM(missing),0) missing_files,"
            "COALESCE(SUM(CASE WHEN warning<>'' THEN 1 ELSE 0 END),0) warning_files "
            "FROM files").fetchone())
        total["roots"] = len(self.list_roots())
        total["runs"] = self.connection.execute("SELECT COUNT(*) FROM runs").fetchone()[0]
        for column in ("kind", "source", "channel", "role"):
            total[column] = [dict(row) for row in self.connection.execute(
                f"SELECT {column} name,COUNT(*) files,SUM(size_bytes) bytes "
                f"FROM files GROUP BY {column} ORDER BY bytes DESC")]
        total["review"] = dict(self.connection.execute(
            "SELECT COALESCE(r.decision,'unreviewed'),COUNT(*) FROM files f "
            "LEFT JOIN reviews r ON f.id=r.file_id "
            "GROUP BY COALESCE(r.decision,'unreviewed')"))
        total.update(file_count=total["files"], total_bytes=total["indexed_bytes"],
                     run_count=total["runs"], missing_count=total["missing_files"] or 0,
                     channels=total["channel"])
        return total
