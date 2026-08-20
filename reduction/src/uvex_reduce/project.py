"""Persistent UVEX operator projects and reusable equipment presets."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
import json
from pathlib import Path
from typing import Any


PROJECT_SCHEMA_VERSION = 1


@dataclass(frozen=True, slots=True)
class EquipmentPreset:
    name: str
    telescope: str
    reducer: str
    spectrograph: str
    camera: str
    grating_lines_per_mm: int
    slit_micrometre: int
    estimated_dispersion_angstrom_per_pixel: float
    raw_dispersion_direction: str
    second_order_warning_angstrom: float = 6800.0

    @classmethod
    def from_dict(cls, values: dict[str, Any]) -> EquipmentPreset:
        allowed = set(cls.__dataclass_fields__)
        unknown = set(values) - allowed
        if unknown:
            raise ValueError(f"Unknown equipment preset field(s): {sorted(unknown)}")
        return cls(**values)


DEFAULT_EQUIPMENT_PRESETS = tuple(
    EquipmentPreset(
        name=f"C11+CCDT67 · UVEX4i 300 l/mm · {slit} µm",
        telescope="Celestron C11",
        reducer=(
            "Astro-Physics CCDT67 (~0.769x measured on 2026-05-16; SCT focus/backfocus dependent)"
        ),
        spectrograph="UVEX4i motorized",
        camera="ToupTek ATR585M, 1x1",
        grating_lines_per_mm=300,
        slit_micrometre=slit,
        estimated_dispersion_angstrom_per_pixel=0.94,
        raw_dispersion_direction="left-blue-right-red before later camera remount",
    )
    for slit in (15, 25, 35)
)


@dataclass(slots=True)
class AstroProject:
    name: str
    data_root: str
    output_root: str
    current_stage: str = "media"
    workflow_preset: str = ""
    evaluate_flat: bool = True
    equipment: EquipmentPreset | None = None
    last_output: str | None = None
    approved_stages: list[str] = field(default_factory=list)
    parameters: dict[str, Any] = field(default_factory=dict)
    manual_calibration_points: list[dict[str, float]] = field(default_factory=list)
    notes: str = ""
    created_utc: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    modified_utc: str = field(default_factory=lambda: datetime.now(timezone.utc).isoformat())
    schema_version: int = PROJECT_SCHEMA_VERSION

    def to_dict(self) -> dict[str, Any]:
        payload = asdict(self)
        payload["schemaVersion"] = payload.pop("schema_version")
        payload["createdUtc"] = payload.pop("created_utc")
        payload["modifiedUtc"] = payload.pop("modified_utc")
        payload["dataRoot"] = payload.pop("data_root")
        payload["outputRoot"] = payload.pop("output_root")
        payload["currentStage"] = payload.pop("current_stage")
        payload["workflowPreset"] = payload.pop("workflow_preset")
        payload["evaluateFlat"] = payload.pop("evaluate_flat")
        payload["lastOutput"] = payload.pop("last_output")
        payload["approvedStages"] = payload.pop("approved_stages")
        payload["manualCalibrationPoints"] = payload.pop("manual_calibration_points")
        return payload

    def save(self, path: str | Path) -> Path:
        destination = Path(path).expanduser().resolve()
        if destination.suffix.casefold() != ".astroproj":
            destination = destination.with_suffix(".astroproj")
        destination.parent.mkdir(parents=True, exist_ok=True)
        self.modified_utc = datetime.now(timezone.utc).isoformat()
        destination.write_text(
            json.dumps(self.to_dict(), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return destination

    @classmethod
    def load(cls, path: str | Path) -> AstroProject:
        source = Path(path).expanduser().resolve()
        payload = json.loads(source.read_text(encoding="utf-8"))
        version = int(payload.pop("schemaVersion", 0))
        if version != PROJECT_SCHEMA_VERSION:
            raise ValueError(
                f"Unsupported .astroproj schema {version}; expected {PROJECT_SCHEMA_VERSION}."
            )
        equipment_values = payload.get("equipment")
        payload["equipment"] = (
            None if equipment_values is None else EquipmentPreset.from_dict(equipment_values)
        )
        aliases = {
            "createdUtc": "created_utc",
            "modifiedUtc": "modified_utc",
            "dataRoot": "data_root",
            "outputRoot": "output_root",
            "currentStage": "current_stage",
            "workflowPreset": "workflow_preset",
            "evaluateFlat": "evaluate_flat",
            "lastOutput": "last_output",
            "approvedStages": "approved_stages",
            "manualCalibrationPoints": "manual_calibration_points",
        }
        for serialized, field_name in aliases.items():
            if serialized in payload:
                payload[field_name] = payload.pop(serialized)
        payload["schema_version"] = version
        allowed = set(cls.__dataclass_fields__)
        unknown = set(payload) - allowed
        if unknown:
            raise ValueError(f"Unknown .astroproj field(s): {sorted(unknown)}")
        return cls(**payload)


def load_equipment_presets(path: str | Path) -> list[EquipmentPreset]:
    source = Path(path).expanduser()
    presets = {preset.name: preset for preset in DEFAULT_EQUIPMENT_PRESETS}
    if source.is_file():
        payload = json.loads(source.read_text(encoding="utf-8"))
        for values in payload.get("presets", []):
            preset = EquipmentPreset.from_dict(values)
            presets[preset.name] = preset
    return list(presets.values())


def save_equipment_presets(path: str | Path, presets: list[EquipmentPreset]) -> Path:
    destination = Path(path).expanduser().resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(
            {
                "schemaVersion": 1,
                "presets": [asdict(preset) for preset in presets],
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    return destination
