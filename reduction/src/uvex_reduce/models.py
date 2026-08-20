from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Any

import numpy as np


class FrameType(str, Enum):
    LIGHT = "light"
    FLAT = "flat"
    DARK = "dark"
    BIAS = "bias"
    ARC = "arc"
    UNKNOWN = "unknown"


@dataclass(slots=True)
class FrameRecord:
    path: Path
    relative_path: str
    frame_type: FrameType
    confidence: float
    classification_reason: str
    object_name: str | None = None
    inferred_object: str | None = None
    exposure_s: float | None = None
    gain: float | None = None
    date_obs: str | None = None
    instrument: str | None = None
    temperature_c: float | None = None
    width: int | None = None
    height: int | None = None
    bitpix: int | None = None
    is_master: bool = False
    warnings: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return {
            "path": str(self.path),
            "relativePath": self.relative_path,
            "frameType": self.frame_type.value,
            "confidence": round(self.confidence, 3),
            "classificationReason": self.classification_reason,
            "object": self.object_name,
            "inferredObject": self.inferred_object,
            "exposureSeconds": self.exposure_s,
            "gain": self.gain,
            "dateObs": self.date_obs,
            "instrument": self.instrument,
            "temperatureC": self.temperature_c,
            "width": self.width,
            "height": self.height,
            "bitpix": self.bitpix,
            "isMaster": self.is_master,
            "warnings": list(self.warnings),
        }


@dataclass(slots=True)
class AlignmentShift:
    path: Path
    dispersion_pixels: float
    spatial_pixels: float
    confidence: float


@dataclass(slots=True)
class TraceResult:
    centers: np.ndarray
    sigma_pixels: np.ndarray
    valid_bins: int
    snr: float
    method: str
    fallback_used: bool = False


@dataclass(slots=True)
class WavelengthSolution:
    wavelength_angstrom: np.ndarray
    coefficients: np.ndarray
    matched_pixels: np.ndarray
    matched_wavelengths: np.ndarray
    residuals_angstrom: np.ndarray
    rms_angstrom: float
    degree: int
    method: str
    medium: str = "unknown"
    coefficient_order: str = "descending"
    template_path: str | None = None
    template_correlation: float | None = None
    output_reversed: bool = False


@dataclass(slots=True)
class ReductionResult:
    source_files: list[Path]
    image: np.ndarray
    header: Any
    flux: np.ndarray
    uncertainty: np.ndarray
    mask: np.ndarray
    trace: TraceResult
    shifts: list[AlignmentShift]
    wavelength: WavelengthSolution | None
    extraction_backend: str
    horizontal_flip_applied: bool
    rejected_source_files: list[Path] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    spectrum: Any | None = None
