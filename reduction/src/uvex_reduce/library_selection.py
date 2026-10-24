"""Exact, read-only frame handoff from the library to the existing reduction engine.

Selection is an allowlist, not a hint for another directory scan. It lives inside
schema-1 project parameters so old operator projects remain compatible.
"""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import re
from typing import Any, Iterable, Mapping

from .config import PipelineConfig
from .project import AstroProject


SELECTION_SCHEMA_VERSION = 1
SELECTION_ROLES = frozenset({"science", "standard", "bias", "dark", "flat", "arc"})
CALIBRATION_ROLES = ("bias", "dark", "flat", "arc")


@dataclass(frozen=True, slots=True)
class SelectedFrame:
    path: str
    sha256: str
    target: str
    role: str

    @classmethod
    def from_dict(cls, value: Mapping[str, Any]) -> SelectedFrame:
        if not isinstance(value, Mapping) or set(value) != {"path", "sha256", "target", "role"}:
            raise ValueError("Each selected frame needs exactly path, sha256, target and role.")
        if any(not isinstance(value[key], str) for key in value):
            raise ValueError("Selected frame fields must be strings.")
        path = Path(value["path"]).expanduser()
        if not path.is_absolute() or path.suffix.casefold() not in {".fit", ".fits", ".fts"}:
            raise ValueError("Selected frame paths must be absolute FITS paths.")
        if not re.fullmatch(r"[0-9a-fA-F]{64}", value["sha256"]):
            raise ValueError("Selected frames require a complete SHA-256 digest.")
        target = value["target"].strip()
        role = value["role"]
        if role not in SELECTION_ROLES or (role in {"science", "standard"} and not target):
            raise ValueError("Invalid selected role or missing science/standard target.")
        return cls(str(path.resolve()), value["sha256"].lower(), target, role)

    def to_dict(self) -> dict[str, str]:
        return {"path": self.path, "sha256": self.sha256, "target": self.target, "role": self.role}


@dataclass(frozen=True, slots=True)
class LibrarySelection:
    frames: tuple[SelectedFrame, ...]

    @classmethod
    def create(cls, frames: Iterable[Mapping[str, Any] | SelectedFrame]) -> LibrarySelection:
        parsed = tuple(
            SelectedFrame.from_dict(frame.to_dict() if isinstance(frame, SelectedFrame) else frame)
            for frame in frames
        )
        identities: dict[str, SelectedFrame] = {}
        for frame in parsed:
            key = str(Path(frame.path)).casefold()
            if key in identities:
                raise ValueError(f"Selected file appears more than once: {frame.path}")
            identities[key] = frame
        return cls(parsed)

    @classmethod
    def from_parameters(cls, parameters: Mapping[str, Any]) -> LibrarySelection | None:
        if "librarySelection" not in parameters:
            return None
        value = parameters["librarySelection"]
        if (
            not isinstance(value, dict)
            or set(value) != {"schemaVersion", "frames"}
            or type(value["schemaVersion"]) is not int
            or value["schemaVersion"] != SELECTION_SCHEMA_VERSION
            or not isinstance(value["frames"], list)
        ):
            raise ValueError("Unsupported or malformed librarySelection; directory fallback is forbidden.")
        return cls.create(value["frames"])

    def to_dict(self) -> dict[str, Any]:
        return {"schemaVersion": SELECTION_SCHEMA_VERSION, "frames": [f.to_dict() for f in self.frames]}

    def validate_files(self) -> None:
        """Recheck immutable bytes before processing; never substitute a nearby file."""
        for frame in self.frames:
            path = Path(frame.path)
            if not path.is_file():
                raise FileNotFoundError(f"Selected library frame is missing: {path}")
            if _stable_sha256(path) != frame.sha256:
                raise ValueError(f"Selected library frame changed (SHA-256 mismatch): {path}")

    def paths(self, role: str, target: str | None = None) -> list[str]:
        return [
            frame.path for frame in self.frames
            if frame.role == role and (target is None or frame.target.casefold() == target.casefold())
        ]

    def apply_to_config(self, config: PipelineConfig, *, target: str, role: str) -> None:
        science = self.paths(role, target)
        if role not in {"science", "standard"} or not science:
            raise ValueError(f"No explicitly selected {role} frames for {target}; no directory fallback.")
        config.inputs.science = science
        config.inputs.target_name = target
        config.inputs.exact_files = True
        # Even an empty list is authoritative. Never retain base-config globs.
        for calibration_role in CALIBRATION_ROLES:
            setattr(config.inputs, calibration_role, self.paths(calibration_role))


def export_library_project(
    path: str | Path,
    *,
    name: str,
    data_root: str | Path,
    output_root: str | Path,
    frames: Iterable[Mapping[str, Any] | SelectedFrame],
) -> Path:
    """Save a schema-1 project without copying, modifying or processing observations."""
    destination = Path(path).expanduser()
    if destination.suffix and destination.suffix.casefold() != ".astroproj":
        raise ValueError("Library handoff must be saved as .astroproj, not an observation file.")
    destination = destination.with_suffix(".astroproj")
    if destination.is_symlink():
        raise ValueError("Library project destination must not be a symbolic link.")
    destination = destination.resolve()
    selection = pin_selection_frames(frames)
    if not any(frame.role in {"science", "standard"} for frame in selection.frames):
        raise ValueError("Select at least one spectroscopy science or standard frame for Studio.")
    resolved_output = Path(output_root).expanduser().resolve()
    if any(Path(frame.path).is_relative_to(resolved_output) for frame in selection.frames):
        raise ValueError("Reduction output directory must not contain selected observation inputs.")
    project = AstroProject(
        name=name,
        data_root=str(Path(data_root).expanduser().resolve()),
        output_root=str(resolved_output),
        parameters={"librarySelection": selection.to_dict()},
        notes="Exact library selection. Missing or changed files never trigger a directory rescan.",
    )
    if any(
        destination == Path(frame.path)
        or (destination.exists() and destination.samefile(frame.path))
        for frame in selection.frames
    ):
        raise ValueError("A project cannot overwrite an observation input.")
    return project.save(destination)


def pin_selection_frames(
    frames: Iterable[Mapping[str, Any] | SelectedFrame],
) -> LibrarySelection:
    """Pin UI basket entries to hashes; supplied hashes are verified, never replaced.

    The UI may supply path/target/role only and call this on its worker thread.
    Serialized projects always contain all four fields. No raw file is written.
    """
    pinned = []
    for frame in frames:
        value = frame.to_dict() if isinstance(frame, SelectedFrame) else dict(frame)
        calculated = False
        if set(value) == {"path", "target", "role"}:
            # Validate structure/path/role before reading any large source file.
            candidate = SelectedFrame.from_dict({**value, "sha256": "0" * 64})
            value["sha256"] = _stable_sha256(Path(candidate.path))
            calculated = True
        parsed = SelectedFrame.from_dict(value)
        if not calculated and _stable_sha256(Path(parsed.path)) != parsed.sha256:
            raise ValueError(f"Selected library frame changed (SHA-256 mismatch): {parsed.path}")
        pinned.append(parsed)
    return LibrarySelection.create(pinned)


def _stable_sha256(path: Path) -> str:
    def signature(stat: os.stat_result) -> tuple[int, int, int, int, int]:
        return stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns

    digest = hashlib.sha256()
    with path.open("rb") as stream:
        before = signature(os.fstat(stream.fileno()))
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
        after = signature(os.fstat(stream.fileno()))
    if before != after or after != signature(path.stat()):
        raise ValueError(f"Selected frame changed while hashing; retry when writing has stopped: {path}")
    return digest.hexdigest()
