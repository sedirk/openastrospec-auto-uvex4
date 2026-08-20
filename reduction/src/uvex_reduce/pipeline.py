from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
import re

import numpy as np

from .config import PipelineConfig, expand_patterns, load_config
from .diagnostics import write_diagnostics, write_preextraction_diagnostic
from .extraction import extract_spectrum
from .inspector import infer_object_from_filename, inspect_file
from .models import FrameType, ReductionResult
from .preprocess import preprocess_arc, preprocess_science
from .products import make_spectrum, write_products
from .wavelength import calibrate_wavelength


@dataclass(slots=True)
class PipelineRun:
    result: ReductionResult
    artifacts: dict[str, Path]
    target_name: str


class ReductionPipeline:
    """Orchestrate one fault-tolerant UVEX long-slit reduction run."""

    def __init__(self, config: PipelineConfig):
        self.config = config

    @classmethod
    def from_config(cls, path: str | Path) -> "ReductionPipeline":
        return cls(load_config(path))

    def run(self) -> PipelineRun:
        published_placeholders = [
            self.config.inputs.root,
            self.config.wavelength.template_directory,
            self.config.wavelength.template_path,
            self.config.wavelength.solution_path,
        ]
        if any(
            value is not None and "<local-" in str(value)
            for value in published_placeholders
        ):
            raise ValueError(
                "This published example config still contains a <local-...> path. "
                "Copy it to an ignored *.local.toml file and replace every local path, "
                "or use the GUI/CLI path overrides."
            )
        inputs, warnings = self._resolve_inputs()
        science_stack = preprocess_science(
            inputs["science"],
            inputs["bias"],
            inputs["dark"],
            inputs["flat"],
            self.config.detector,
            self.config.preprocess,
            self.config.orientation,
        )
        target_name = self._target_name(inputs["science"], science_stack.header)
        preview_path = write_preextraction_diagnostic(
            science_stack.image,
            science_stack.mask,
            self.config.inputs.output_dir,
            target_name,
        )
        try:
            arc_image, arc_header, arc_warnings = preprocess_arc(
                inputs["arc"],
                inputs["bias"],
                self.config.detector,
                self.config.preprocess,
                self.config.orientation,
            )
            extraction = extract_spectrum(
                science_stack.image,
                science_stack.variance,
                science_stack.mask,
                science_stack.header,
                self.config.detector,
                self.config.extraction,
                arc_image,
                arc_header,
            )
        except Exception as error:
            raise RuntimeError(
                f"{error} Pre-extraction diagnostic was saved to {preview_path}"
            ) from error
        wavelength, wavelength_warnings = calibrate_wavelength(
            extraction,
            self.config.wavelength,
            target_name,
            self.config.inputs.root,
        )

        effective_horizontal_flip = self.config.orientation.horizontal_flip
        if wavelength is not None and wavelength.output_reversed:
            _reverse_products_for_increasing_wavelength(science_stack, extraction)
            effective_horizontal_flip = not effective_horizontal_flip
            science_stack.header["DISPFLIP"] = (
                effective_horizontal_flip,
                "Net horizontal orientation change after stellar calibration",
            )
            science_stack.header.add_history(
                "UVEX-ADV: stellar template automatically corrected dispersion orientation"
            )
            preview_path = write_preextraction_diagnostic(
                science_stack.image,
                science_stack.mask,
                self.config.inputs.output_dir,
                target_name,
            )

        if self.config.orientation.red_left_blue_right and not self.config.orientation.horizontal_flip:
            warnings.append(
                "Configuration says raw data are red-left/blue-right, but horizontal_flip is disabled."
            )
        if not self.config.orientation.red_left_blue_right and self.config.orientation.horizontal_flip:
            warnings.append(
                "horizontal_flip is enabled although red_left_blue_right is false; verify the orientation."
            )

        original_object = str(science_stack.header.get("OBJECT", "")).strip()
        if original_object and original_object.casefold() != target_name.casefold():
            science_stack.header["ORIGOBJ"] = (original_object, "Original possibly stale OBJECT")
        science_stack.header["OBJECT"] = (target_name, "Reduction target")
        combined_warnings = _deduplicate(
            warnings
            + science_stack.warnings
            + arc_warnings
            + extraction.warnings
            + wavelength_warnings
        )
        result = ReductionResult(
            source_files=science_stack.source_files,
            rejected_source_files=science_stack.rejected_files,
            image=science_stack.image,
            header=science_stack.header,
            flux=extraction.flux,
            uncertainty=extraction.uncertainty,
            mask=extraction.mask,
            trace=extraction.trace,
            shifts=science_stack.shifts,
            wavelength=wavelength,
            extraction_backend=extraction.backend,
            horizontal_flip_applied=effective_horizontal_flip,
            warnings=combined_warnings,
        )
        result.spectrum = make_spectrum(result)

        artifacts = {"preprocessed": preview_path}
        artifacts.update(write_products(result, self.config.inputs.output_dir, target_name))
        artifacts.update(write_diagnostics(result, self.config.inputs.output_dir, target_name))
        manifest_path = self._write_run_manifest(result, inputs, artifacts, target_name)
        artifacts["manifest"] = manifest_path
        return PipelineRun(result=result, artifacts=artifacts, target_name=target_name)

    def _resolve_inputs(self) -> tuple[dict[str, list[Path]], list[str]]:
        patterns = {
            "science": self.config.inputs.science,
            "flat": self.config.inputs.flat,
            "dark": self.config.inputs.dark,
            "bias": self.config.inputs.bias,
            "arc": self.config.inputs.arc,
        }
        resolved = {
            frame_type: expand_patterns(self.config.inputs.root, frame_patterns)
            for frame_type, frame_patterns in patterns.items()
        }
        if not resolved["science"]:
            raise FileNotFoundError(
                f"No science FITS matched under {self.config.inputs.root}: "
                + ", ".join(patterns["science"])
            )

        warnings: list[str] = []
        for frame_type in ("flat", "dark", "bias", "arc"):
            if patterns[frame_type] and not resolved[frame_type]:
                warnings.append(
                    f"No {frame_type} FITS matched configured pattern(s): "
                    + ", ".join(patterns[frame_type])
                )

        owners: dict[Path, str] = {}
        for frame_type, paths in resolved.items():
            for path in paths:
                previous = owners.setdefault(path, frame_type)
                if previous != frame_type:
                    raise ValueError(
                        f"Input file is assigned to both {previous} and {frame_type}: {path}"
                    )

        expected = {
            "science": FrameType.LIGHT,
            "flat": FrameType.FLAT,
            "dark": FrameType.DARK,
            "bias": FrameType.BIAS,
            "arc": FrameType.ARC,
        }
        for frame_type, paths in resolved.items():
            for path in paths:
                record = inspect_file(path, self.config.inputs.root)
                if record.frame_type not in {expected[frame_type], FrameType.UNKNOWN}:
                    warnings.append(
                        f"Configured {frame_type} {path.name} was classified as "
                        f"{record.frame_type.value} ({record.classification_reason})."
                    )
                warnings.extend(f"{path.name}: {warning}" for warning in record.warnings)
        return resolved, warnings

    def _target_name(self, science_paths: list[Path], header) -> str:
        if self.config.inputs.target_name:
            return self.config.inputs.target_name.strip()
        inferred = [infer_object_from_filename(path) for path in science_paths]
        inferred = [item for item in inferred if item]
        if inferred and len({item.casefold() for item in inferred}) == 1:
            return inferred[0]
        object_name = str(header.get("OBJECT", "")).strip()
        if object_name and object_name.lower() not in {"unknown", "none", "object"}:
            return object_name
        return re.sub(r"[-_ ]?\d+$", "", science_paths[0].stem) or science_paths[0].stem

    def _write_run_manifest(
        self,
        result: ReductionResult,
        inputs: dict[str, list[Path]],
        artifacts: dict[str, Path],
        target_name: str,
    ) -> Path:
        stem = re.sub(r"[^A-Za-z0-9_.-]+", "_", target_name).strip("._") or "uvex"
        path = self.config.inputs.output_dir / f"{stem}_run.json"
        wavelength = None
        if result.wavelength is not None:
            wavelength = {
                "status": "calibrated",
                "method": result.wavelength.method,
                "medium": result.wavelength.medium,
                "degree": result.wavelength.degree,
                "rmsAngstrom": _finite_or_none(result.wavelength.rms_angstrom),
                "matchedLineCount": int(result.wavelength.matched_pixels.size),
                "templatePath": result.wavelength.template_path,
                "templateCorrelation": _finite_or_none(
                    result.wavelength.template_correlation
                ),
                "automaticOutputReversal": result.wavelength.output_reversed,
            }
        else:
            wavelength = {"status": "needs_reference", "axis": "pixel"}
        payload = {
            "schemaVersion": 1,
            "pipelineVersion": "0.4.1",
            "createdUtc": datetime.now(timezone.utc).isoformat(),
            "target": target_name,
            "inputs": {key: [str(item) for item in value] for key, value in inputs.items()},
            "orientation": {
                "rawRedLeftBlueRight": self.config.orientation.red_left_blue_right,
                "horizontalFlipApplied": result.horizontal_flip_applied,
                "outputDirection": (
                    "blue-to-red"
                    if result.wavelength is not None
                    else "blue-to-red-assumed"
                    if result.horizontal_flip_applied
                    else "detector-order"
                ),
            },
            "alignment": [
                {
                    "file": str(shift.path),
                    "dispersionPixels": shift.dispersion_pixels,
                    "spatialPixels": shift.spatial_pixels,
                    "confidence": shift.confidence,
                }
                for shift in result.shifts
            ],
            "frameSelection": {
                "configuredCount": len(inputs["science"]),
                "acceptedCount": len(result.source_files),
                "accepted": [str(path) for path in result.source_files],
                "rejected": [str(path) for path in result.rejected_source_files],
            },
            "combination": {
                "method": self.config.preprocess.combine_method.lower(),
                "sigmaClip": self.config.preprocess.sigma_clip,
                "temporalSigmaClippedSamples": int(result.header.get("TCRSAMP", 0)),
                "temporalSigmaClippedFraction": float(result.header.get("TCRFRAC", 0.0)),
                "perFrameCosmicRayClean": self.config.preprocess.cosmic_ray_clean,
                "cosmicRayReplacements": _cosmic_ray_replacements_from_warnings(
                    result.warnings
                ),
            },
            "trace": {
                "method": result.trace.method,
                "validBins": result.trace.valid_bins,
                "snr": _finite_or_none(result.trace.snr),
                "fallbackUsed": result.trace.fallback_used,
                "medianY": float(np.nanmedian(result.trace.centers)),
                "medianFwhmPixels": float(2.355 * np.nanmedian(result.trace.sigma_pixels)),
            },
            "extractionBackend": result.extraction_backend,
            "wavelength": wavelength,
            # Keep the SDK repair history structured as well as human-readable.
            # The warning strings remain useful in FITS HISTORY, while this list
            # lets the GUI and audit tools prove exactly which files moved and in
            # which direction without parsing prose downstream.
            "sdkWrapRepairs": _sdk_wrap_repairs_from_warnings(result.warnings),
            "warnings": result.warnings,
            "artifacts": {key: str(value) for key, value in artifacts.items()},
        }
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        return path


def _deduplicate(messages: list[str]) -> list[str]:
    return list(dict.fromkeys(message for message in messages if message))


def _finite_or_none(value: float) -> float | None:
    return float(value) if value is not None and np.isfinite(value) else None


_DOCUMENTED_SDK_REPAIR = re.compile(
    r"^(?P<file>[^:]+): applied documented ATR585M SDK wrap repair "
    r"\(cyclic x shift (?P<shift>[+-]?\d+) px, direction=(?P<direction>left|right)\);"
)
_AUTOMATIC_SDK_REPAIR = re.compile(
    r"^(?P<file>[^:]+): automatically detected ATR585M SDK x-wrap .* "
    r"and applied cyclic x shift (?P<shift>[+-]?\d+) px in memory;"
)
_COSMIC_RAY_REPLACEMENT = re.compile(
    r"^(?P<file>[^:]+): replaced (?P<count>\d+) cosmic-ray candidate pixels\.$"
)


def _cosmic_ray_replacements_from_warnings(
    warnings: list[str],
) -> list[dict[str, object]]:
    replacements: list[dict[str, object]] = []
    for warning in warnings:
        match = _COSMIC_RAY_REPLACEMENT.match(warning)
        if match is not None:
            replacements.append(
                {
                    "file": match.group("file"),
                    "replacedPixelCount": int(match.group("count")),
                }
            )
    return replacements


def _sdk_wrap_repairs_from_warnings(warnings: list[str]) -> list[dict[str, object]]:
    repairs: list[dict[str, object]] = []
    for warning in warnings:
        match = _DOCUMENTED_SDK_REPAIR.match(warning)
        detection = "configured"
        if match is None:
            match = _AUTOMATIC_SDK_REPAIR.match(warning)
            detection = "automatic"
        if match is None:
            continue
        applied_shift = int(match.group("shift"))
        direction = (
            match.groupdict().get("direction")
            or ("left" if applied_shift < 0 else "right")
        )
        repairs.append(
            {
                "file": match.group("file"),
                "appliedShiftPixels": applied_shift,
                "direction": direction,
                "detection": detection,
                "sourceFitsModified": False,
            }
        )
    return repairs


def _reverse_products_for_increasing_wavelength(science_stack, extraction) -> None:
    science_stack.image = science_stack.image[:, ::-1].copy()
    science_stack.variance = science_stack.variance[:, ::-1].copy()
    science_stack.mask = science_stack.mask[:, ::-1].copy()
    for shift in science_stack.shifts:
        shift.dispersion_pixels = -shift.dispersion_pixels
    extraction.flux = extraction.flux[::-1].copy()
    extraction.uncertainty = extraction.uncertainty[::-1].copy()
    extraction.mask = extraction.mask[::-1].copy()
    extraction.trace.centers = extraction.trace.centers[::-1].copy()
    extraction.trace.sigma_pixels = extraction.trace.sigma_pixels[::-1].copy()
