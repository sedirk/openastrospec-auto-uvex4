from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime, timezone
import json
from pathlib import Path
from typing import Iterable

import numpy as np

from .calibration import (
    DEFAULT_NEBULAR_LINES_ANGSTROM,
    CalibratedSpectrum,
    RelativeResponse,
    ZeroPointCorrection,
    apply_response_and_normalize,
    derive_relative_response,
    load_reduced_spectrum,
    refine_emission_zero_point,
    write_calibration_products,
)
from .config import load_config
from .order2 import SecondOrderAssessment, load_second_order_assessment
from .pipeline import PipelineRun, ReductionPipeline
from .line_analysis import measure_nebular_lines, write_nebular_line_products


@dataclass(slots=True)
class StandardQuality:
    usable: bool
    template_correlation: float | None
    response_fractional_scatter: float | None
    matched_line_count: int
    wavelength_rms_angstrom: float | None
    reason: str


@dataclass(slots=True)
class FullWorkflowRun:
    standard_run: PipelineRun
    flat_trial_run: PipelineRun | None
    science_run: PipelineRun
    response: RelativeResponse
    calibrated: CalibratedSpectrum
    zero_point: ZeroPointCorrection | None
    flat_accepted: bool
    flat_decision: str
    artifacts: dict[str, Path]


def run_full_workflow(
    standard_config_path: str | Path,
    science_config_path: str | Path,
    template_path: str | Path,
    output_dir: str | Path,
    *,
    standard_name: str,
    target_name: str,
    input_root: str | Path | None = None,
    evaluate_flat: bool = True,
    refine_emission: bool = False,
    reference_lines_angstrom: Iterable[float] = DEFAULT_NEBULAR_LINES_ANGSTROM,
    maximum_zero_point_offset_angstrom: float = 80.0,
    continuum_bin_angstrom: float = 100.0,
    continuum_percentile: float = 35.0,
    second_order_warning_start_angstrom: float = 6800.0,
    second_order_assessment_path: str | Path | None = None,
    cosmic_ray_clean: bool | None = None,
    combine_method: str | None = None,
) -> FullWorkflowRun:
    """Run standard, flat trial, science stack, response and normalization.

    Candidate flats are never accepted merely because files were supplied.  A
    no-flat standard is always reduced as the control, then the flat trial must
    retain stellar-template agreement and must not materially increase response
    roughness.  Both trials remain on disk for inspection.
    """

    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    template = Path(template_path).expanduser().resolve()
    if not template.is_file():
        raise FileNotFoundError(
            f"Stellar template does not exist: {template}. Supply an explicit local template path."
        )

    base_standard = load_config(standard_config_path)
    if input_root is None and "<local-data-root>" in str(base_standard.inputs.root):
        raise ValueError(
            "The published standard config contains <local-data-root>; supply input_root "
            "(CLI: --input-root) or use a machine-local config."
        )
    if input_root is not None:
        base_standard.inputs.root = Path(input_root).expanduser().resolve()
    if base_standard.wavelength.mode == "stellar_template":
        base_standard.wavelength.template_directory = template.parent
        base_standard.wavelength.template_path = template
    if cosmic_ray_clean is not None:
        base_standard.preprocess.cosmic_ray_clean = cosmic_ray_clean
    if combine_method is not None:
        base_standard.preprocess.combine_method = combine_method
    no_flat_config = deepcopy(base_standard)
    no_flat_config.preprocess.use_flat = False
    no_flat_config.inputs.output_dir = destination / "standard-no-flat"
    no_flat_run = ReductionPipeline(no_flat_config).run()
    no_flat_quality, no_flat_response = _measure_standard_quality(
        no_flat_run,
        template,
        standard_name,
        base_standard.wavelength.minimum_template_correlation,
    )
    if not no_flat_quality.usable or no_flat_response is None:
        raise RuntimeError(
            "The no-flat standard-star control did not pass wavelength/response "
            f"quality gates: {no_flat_quality.reason}"
        )

    flat_trial_run = None
    flat_quality = None
    flat_response = None
    flat_accepted = False
    flat_decision = "No candidate flat was evaluated."
    if evaluate_flat and base_standard.inputs.flat:
        flat_config = deepcopy(base_standard)
        flat_config.preprocess.use_flat = True
        flat_config.inputs.output_dir = destination / "standard-flat-trial"
        flat_trial_run = ReductionPipeline(flat_config).run()
        if not bool(flat_trial_run.result.header.get("FLATCOR", False)):
            flat_quality = StandardQuality(
                usable=False,
                template_correlation=None,
                response_fractional_scatter=None,
                matched_line_count=0,
                wavelength_rms_angstrom=None,
                reason=(
                    "No camera-compatible candidate flat survived preprocessing; "
                    "FLATCOR is false."
                ),
            )
            flat_response = None
        else:
            flat_quality, flat_response = _measure_standard_quality(
                flat_trial_run,
                template,
                standard_name,
                base_standard.wavelength.minimum_template_correlation,
            )
        flat_accepted, flat_decision = _accept_flat_trial(
            no_flat_quality,
            flat_quality,
        )

    selected_standard = flat_trial_run if flat_accepted else no_flat_run
    selected_response = flat_response if flat_accepted else no_flat_response
    if selected_response is None:
        raise RuntimeError("Selected standard-star run has no usable response curve.")

    science_config = load_config(science_config_path)
    if input_root is None and "<local-data-root>" in str(science_config.inputs.root):
        raise ValueError(
            "The published science config contains <local-data-root>; supply input_root "
            "(CLI: --input-root) or use a machine-local config."
        )
    if input_root is not None:
        science_config.inputs.root = Path(input_root).expanduser().resolve()
    if cosmic_ray_clean is not None:
        science_config.preprocess.cosmic_ray_clean = cosmic_ray_clean
    if combine_method is not None:
        science_config.preprocess.combine_method = combine_method
    science_config.preprocess.use_flat = flat_accepted
    science_config.wavelength.mode = "solution_file"
    science_config.wavelength.solution_path = selected_standard.artifacts["fits"]
    science_config.inputs.output_dir = destination / "science-preferred"
    science_run = ReductionPipeline(science_config).run()
    if science_run.result.wavelength is None:
        raise RuntimeError("The selected standard wavelength solution was not transferred.")

    science = load_reduced_spectrum(science_run.artifacts["fits"])
    zero_point = None
    zero_point_warning = None
    if refine_emission:
        try:
            zero_point = refine_emission_zero_point(
                science,
                reference_lines_angstrom,
                maximum_offset_angstrom=maximum_zero_point_offset_angstrom,
            )
        except Exception as error:
            zero_point_warning = f"Emission-line wavelength refinement failed: {error}"

    assessment = _optional_second_order_assessment(second_order_assessment_path)
    warning_start = (
        second_order_warning_start_angstrom
        if assessment is None
        else assessment.warning_start_angstrom
    )
    calibrated = apply_response_and_normalize(
        science,
        selected_response,
        zero_point,
        continuum_bin_angstrom=continuum_bin_angstrom,
        continuum_percentile=continuum_percentile,
        second_order_start_angstrom=warning_start,
        second_order_status=("not_tested" if assessment is None else assessment.status),
        second_order_empirical_onset_angstrom=(
            None if assessment is None else assessment.empirical_onset_angstrom
        ),
        second_order_diagnostic_marker_angstrom=(
            7292.0
            if assessment is None
            else assessment.balmer_second_order_marker_angstrom
        ),
        second_order_assessment_path=second_order_assessment_path,
    )
    final_artifacts = write_calibration_products(
        selected_response,
        calibrated,
        destination / "final",
        target_name,
    )
    line_analysis = None
    if refine_emission:
        line_analysis = measure_nebular_lines(calibrated)
        final_artifacts.update(
            write_nebular_line_products(
                calibrated,
                line_analysis,
                destination / "final",
                target_name,
            )
        )
    workflow_manifest = destination / "full_workflow.json"
    payload = {
        "schemaVersion": 1,
        "workflow": "standard-flat-trial-science-stack-relative-response-normalization",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "standard": standard_name,
        "target": target_name,
        "template": str(template),
        "flat": {
            "evaluated": flat_trial_run is not None,
            "accepted": flat_accepted,
            "decision": flat_decision,
            "control": _quality_payload(no_flat_quality),
            "trial": None if flat_quality is None else _quality_payload(flat_quality),
        },
        "standardRun": str(selected_standard.artifacts["manifest"]),
        "flatTrialRun": (
            None if flat_trial_run is None else str(flat_trial_run.artifacts["manifest"])
        ),
        "scienceRun": str(science_run.artifacts["manifest"]),
        "scienceFrames": {
            "accepted": [str(path) for path in science_run.result.source_files],
            "rejected": [str(path) for path in science_run.result.rejected_source_files],
            "totalExposureSeconds": science_run.result.header.get("TOTEXP"),
            "referenceExposureSeconds": science_run.result.header.get("EXPTIME"),
        },
        "wavelengthRefinement": (
            {"status": "skipped" if not refine_emission else "failed", "reason": zero_point_warning}
            if zero_point is None
            else {
                "status": "applied",
                "method": zero_point.method,
                "scale": zero_point.scale,
                "pivotAngstrom": zero_point.pivot_angstrom,
                "offsetAtPivotAngstrom": zero_point.applied_offset_angstrom,
                "rmsAngstrom": zero_point.rms_angstrom,
                "matchedLineCount": int(zero_point.reference_wavelengths.size),
            }
        ),
        "absoluteFluxCalibrated": False,
        "lineAnalysis": None if line_analysis is None else line_analysis.summary(),
        "artifacts": {key: str(path) for key, path in final_artifacts.items()},
    }
    workflow_manifest.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    artifacts = dict(final_artifacts)
    artifacts["workflow_manifest"] = workflow_manifest
    return FullWorkflowRun(
        standard_run=selected_standard,
        flat_trial_run=flat_trial_run,
        science_run=science_run,
        response=selected_response,
        calibrated=calibrated,
        zero_point=zero_point,
        flat_accepted=flat_accepted,
        flat_decision=flat_decision,
        artifacts=artifacts,
    )


def _measure_standard_quality(
    run: PipelineRun,
    template_path: Path,
    standard_name: str,
    minimum_template_correlation: float = 0.35,
) -> tuple[StandardQuality, RelativeResponse | None]:
    solution = run.result.wavelength
    if solution is None:
        return (
            StandardQuality(False, None, None, 0, None, "No wavelength solution."),
            None,
        )
    try:
        response = derive_relative_response(
            load_reduced_spectrum(run.artifacts["fits"]),
            template_path,
            standard_name,
        )
    except Exception as error:
        return (
            StandardQuality(
                False,
                _finite(solution.template_correlation),
                None,
                int(solution.matched_pixels.size),
                _finite(solution.rms_angstrom),
                f"Response derivation failed: {error}",
            ),
            None,
        )
    correlation = _finite(solution.template_correlation)
    usable = (
        correlation is not None
        and correlation >= minimum_template_correlation
    )
    return (
        StandardQuality(
            usable=usable,
            template_correlation=correlation,
            response_fractional_scatter=response.fractional_scatter,
            matched_line_count=int(solution.matched_pixels.size),
            wavelength_rms_angstrom=_finite(solution.rms_angstrom),
            reason=(
                "Passed."
                if usable
                else "Template correlation is unavailable or below the configured "
                f"{minimum_template_correlation:.3f} threshold."
            ),
        ),
        response if usable else None,
    )


def _accept_flat_trial(
    control: StandardQuality,
    trial: StandardQuality,
) -> tuple[bool, str]:
    if not trial.usable:
        return False, f"Candidate flat rejected: {trial.reason}"
    if trial.template_correlation is None or control.template_correlation is None:
        return False, "Candidate flat rejected: template correlation is unavailable."
    if trial.template_correlation < control.template_correlation - 0.03:
        return (
            False,
            "Candidate flat rejected: stellar-template correlation decreased from "
            f"{control.template_correlation:.3f} to {trial.template_correlation:.3f}.",
        )
    if (
        trial.response_fractional_scatter is None
        or control.response_fractional_scatter is None
    ):
        return False, "Candidate flat rejected: response roughness is unavailable."
    scatter_limit = max(
        control.response_fractional_scatter + 0.01,
        1.5 * control.response_fractional_scatter,
    )
    if trial.response_fractional_scatter > scatter_limit:
        return (
            False,
            "Candidate flat rejected: response fractional scatter increased from "
            f"{control.response_fractional_scatter:.4f} to "
            f"{trial.response_fractional_scatter:.4f} (limit {scatter_limit:.4f}).",
        )
    return (
        True,
        "Candidate flat accepted: wavelength/template agreement was retained and "
        "response roughness stayed within the control-derived limit.",
    )


def _optional_second_order_assessment(
    path: str | Path | None,
) -> SecondOrderAssessment | None:
    return None if path is None else load_second_order_assessment(path)


def _quality_payload(quality: StandardQuality) -> dict[str, object]:
    return {
        "usable": quality.usable,
        "templateCorrelation": quality.template_correlation,
        "responseFractionalScatter": quality.response_fractional_scatter,
        "matchedLineCount": quality.matched_line_count,
        "wavelengthRmsAngstrom": quality.wavelength_rms_angstrom,
        "reason": quality.reason,
    }


def _finite(value: float | None) -> float | None:
    return float(value) if value is not None and np.isfinite(value) else None
