"""Build the target/date UVEX reduction catalogue from retained run products.

The catalogue is deliberately a delivery layer.  Historical run directories
remain under ``output/_internal`` for provenance, while every target/date entry
gets the same FITS, CSV, PNG and JSON presentation.  Flux is never labelled as
absolute: products are either detector counts or relative-response products.
"""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass, field
from datetime import datetime, timezone
import hashlib
import html
import json
from pathlib import Path
import shutil
from typing import Any
from urllib.parse import quote

from astropy.io import fits
import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from scipy.ndimage import gaussian_filter1d  # noqa: E402

plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False


PROJECT_ROOT = Path(__file__).resolve().parents[2]
REDUCTION_ROOT = PROJECT_ROOT / "reduction"
OUTPUT_ROOT = REDUCTION_ROOT / "output"
RUNS_ROOT = OUTPUT_ROOT / "_internal" / "runs"
QUALITY_ROOT = OUTPUT_ROOT / "_internal" / "quality"


@dataclass(frozen=True)
class ProductSpec:
    target: str
    date: str
    source_csv: Path
    source_fits: Path | None = None
    component: str | None = None
    display_name: str | None = None
    diagnostics: dict[str, Path] = field(default_factory=dict)
    calibration: dict[str, Path] = field(default_factory=dict)
    metadata: dict[str, Path] = field(default_factory=dict)
    notes: tuple[str, ...] = ()

    @property
    def destination(self) -> Path:
        base = OUTPUT_ROOT / self.target / self.date
        if self.component:
            return base / "components" / self.component
        return base


def _pipeline_spec(
    target: str,
    date: str,
    directory: Path,
    prefix: str,
    *,
    component: str | None = None,
    display_name: str | None = None,
    notes: tuple[str, ...] = (),
) -> ProductSpec:
    return ProductSpec(
        target=target,
        date=date,
        component=component,
        display_name=display_name,
        source_csv=directory / f"{prefix}_spectrum.csv",
        source_fits=directory / f"{prefix}_spectrum.fits",
        diagnostics={
            "alignment.png": directory / f"{prefix}_alignment.png",
            "preprocessed_2d.png": directory / f"{prefix}_preprocessed.png",
            "trace.png": directory / f"{prefix}_trace_overlay.png",
            "wavelength_residuals.png": directory / f"{prefix}_wavelength_residuals.png",
            "pipeline_spectrum.png": directory / f"{prefix}_spectrum.png",
        },
        metadata={"processing.json": directory / f"{prefix}_run.json"},
        notes=notes,
    )


def _specs() -> list[ProductSpec]:
    three_c_root = RUNS_ROOT / "20260504-3c273" / "same-session-ngc6543"
    albireo = RUNS_ROOT / "20260504-albireo"
    may_validation = RUNS_ROOT / "may-2026-validation"
    ngc_12 = RUNS_ROOT / "20260512-sharpcap" / "ngc6543-final"
    ngc_21 = RUNS_ROOT / "full-20260221"
    order2 = QUALITY_ROOT / "order2-study-20260218"
    nova_20260826 = RUNS_ROOT / "nova-sge-2026-20260826" / "workflow"

    specs = [
        ProductSpec(
            target="3C273",
            date="2026-05-04",
            display_name="3C 273",
            source_csv=three_c_root / "final" / "3C273_same_session_normalized.csv",
            source_fits=three_c_root / "final" / "3C273_same_session_normalized.fits",
            diagnostics={
                "normalized_source.png": three_c_root
                / "final"
                / "3C273_same_session_normalized.png",
                "identity.png": three_c_root / "final" / "3C273_identity_overlay.png",
                "hbeta.png": three_c_root / "final" / "3C273_hbeta_diagnostic.png",
                "hbeta_feii_oiii_audit.png": three_c_root
                / "final"
                / "3C273_hbeta_feii_oiii_audit.png",
                "redshift.png": three_c_root / "final" / "3C273_redshift_explainer.png",
                "gaia_field.png": three_c_root / "final" / "3C273_gaia_field.png",
                "trace.png": three_c_root / "science" / "3C273-NGC6543-anchor_trace_overlay.png",
                "alignment.png": three_c_root / "science" / "3C273-NGC6543-anchor_alignment.png",
                "preprocessed_2d.png": three_c_root
                / "science"
                / "3C273-NGC6543-anchor_preprocessed.png",
                "wavelength_residuals.png": three_c_root
                / "science"
                / "3C273-NGC6543-anchor_wavelength_residuals.png",
            },
            metadata={
                "processing.json": three_c_root / "science" / "3C273-NGC6543-anchor_run.json",
                "identity.json": three_c_root / "final" / "3C273_identity.json",
                "hbeta.json": three_c_root / "final" / "3C273_hbeta_diagnostic.json",
                "normalization.json": three_c_root / "final" / "3C273_same_session_normalized.json",
            },
            notes=(
                "Counts-only continuum normalization; not an absolute flux calibration.",
                "Wavelength solution is anchored by the 2026-05-05 NGC 6543 spectrum.",
            ),
        ),
        ProductSpec(
            target="Albireo",
            date="2026-05-04",
            component="A",
            display_name="Albireo A / 辇道增七 A",
            source_csv=albireo / "clean-final-products" / "Albireo-A_normalized.csv",
            source_fits=albireo / "clean-final-products" / "Albireo-A_normalized.fits",
            diagnostics={
                "trace.png": albireo / "a-clean" / "Albireo-A-clean_trace_overlay.png",
                "alignment.png": albireo / "a-clean" / "Albireo-A-clean_alignment.png",
                "preprocessed_2d.png": albireo / "a-clean" / "Albireo-A-clean_preprocessed.png",
                "wavelength_residuals.png": albireo
                / "a-clean"
                / "Albireo-A-clean_wavelength_residuals.png",
                "pipeline_spectrum.png": albireo
                / "a-calibrated-clean"
                / "Albireo-A-clean_calibrated_1d.png",
            },
            calibration={
                "response.csv": albireo / "a-calibrated-clean" / "relative_response.csv",
                "response.fits": albireo / "a-calibrated-clean" / "relative_response.fits",
                "response.png": albireo / "a-calibrated-clean" / "relative_response.png",
            },
            metadata={
                "processing.json": albireo / "a-clean" / "Albireo-A-clean_run.json",
                "calibration.json": albireo
                / "a-calibrated-clean"
                / "Albireo-A-clean_calibration.json",
            },
            notes=("Preferred cleaned reduction.",),
        ),
        ProductSpec(
            target="Albireo",
            date="2026-05-04",
            component="B",
            display_name="Albireo B / 辇道增七 B",
            source_csv=albireo / "clean-final-products" / "Albireo-B_normalized.csv",
            source_fits=albireo / "clean-final-products" / "Albireo-B_normalized.fits",
            diagnostics={
                "trace.png": albireo / "b-clean" / "Albireo-B-clean_trace_overlay.png",
                "alignment.png": albireo / "b-clean" / "Albireo-B-clean_alignment.png",
                "preprocessed_2d.png": albireo / "b-clean" / "Albireo-B-clean_preprocessed.png",
                "wavelength_residuals.png": albireo
                / "b-clean"
                / "Albireo-B-clean_wavelength_residuals.png",
                "a_b_comparison.png": albireo
                / "clean-final-products"
                / "Albireo-AB_template_comparison.png",
                "denoise_before_after.png": albireo
                / "clean-final-products"
                / "Albireo-AB_denoise_before_after.png",
            },
            metadata={
                "processing.json": albireo / "b-clean" / "Albireo-B-clean_run.json",
                "pair_analysis.json": albireo / "clean-final-products" / "Albireo-AB_analysis.json",
                "denoise_metrics.json": albireo
                / "clean-final-products"
                / "Albireo-AB_denoise_metrics.json",
            },
            notes=("Preferred cleaned reduction; H-alpha emission core is retained.",),
        ),
        _pipeline_spec(
            "Arcturus",
            "2026-05-04",
            RUNS_ROOT / "20260504-arcturus",
            "Arcturus",
            notes=("ToupSky standard-star observation.",),
        ),
        _pipeline_spec(
            "Arcturus",
            "2026-05-06",
            RUNS_ROOT / "same-night-20260506" / "arcturus",
            "Arcturus",
            notes=("Same-night standard for the 2026-05-06 NGC 6543 run.",),
        ),
        _pipeline_spec(
            "Castor",
            "2026-02-17",
            RUNS_ROOT / "standards" / "2026-02-17-castor",
            "Castor",
        ),
        _pipeline_spec(
            "Castor",
            "2026-02-18",
            order2 / "castor",
            "Castor",
            notes=("Retained order-overlap study reduction.",),
        ),
        _pipeline_spec(
            "Pollux",
            "2026-02-18",
            RUNS_ROOT / "science" / "2026-02-18-pollux",
            "Pollux",
        ),
        _pipeline_spec(
            "Procyon",
            "2026-02-18",
            order2 / "procyon",
            "Procyon",
            notes=("Retained order-overlap study reduction.",),
        ),
        _pipeline_spec(
            "Regulus",
            "2026-02-18",
            order2 / "regulus",
            "Regulus",
            notes=("Preferred 2026-02-18 reduction; prototype variants are archived.",),
        ),
        _pipeline_spec(
            "Regulus",
            "2026-02-19",
            RUNS_ROOT / "standards" / "2026-02-19-regulus",
            "Regulus",
        ),
        _pipeline_spec(
            "Regulus",
            "2026-02-21",
            ngc_21 / "standard-regulus-wavelength",
            "Regulus",
            notes=("Wavelength-reference reduction used for NGC 2392.",),
        ),
        ProductSpec(
            target="NGC2392",
            date="2026-02-21",
            display_name="NGC 2392",
            source_csv=ngc_21 / "final-ngc2392-preferred" / "NGC2392_calibrated_1d.csv",
            source_fits=ngc_21 / "final-ngc2392-preferred" / "NGC2392_calibrated_1d.fits",
            diagnostics={
                "normalized_source.png": ngc_21
                / "final-ngc2392-preferred"
                / "NGC2392_normalised_1d.png",
                "trace.png": ngc_21 / "science-ngc2392-preferred" / "NGC2392_trace_overlay.png",
                "alignment.png": ngc_21 / "science-ngc2392-preferred" / "NGC2392_alignment.png",
                "preprocessed_2d.png": ngc_21
                / "science-ngc2392-preferred"
                / "NGC2392_preprocessed.png",
                "wavelength_residuals.png": ngc_21
                / "science-ngc2392-preferred"
                / "NGC2392_wavelength_residuals.png",
            },
            calibration={
                "response.csv": ngc_21 / "final-ngc2392-preferred" / "relative_response.csv",
                "response.fits": ngc_21 / "final-ngc2392-preferred" / "relative_response.fits",
                "response.png": ngc_21 / "final-ngc2392-preferred" / "relative_response.png",
            },
            metadata={
                "processing.json": ngc_21 / "science-ngc2392-preferred" / "NGC2392_run.json",
                "calibration.json": ngc_21 / "final-ngc2392-preferred" / "NGC2392_calibration.json",
            },
            notes=("Preferred response-corrected reduction.",),
        ),
        ProductSpec(
            target="NGC6543",
            date="2026-05-05",
            display_name="NGC 6543",
            source_csv=RUNS_ROOT
            / "20260504-3c273"
            / "ngc6543-same-session-anchor"
            / "NGC6543-same-session-anchor_spectrum.csv",
            source_fits=RUNS_ROOT
            / "20260504-3c273"
            / "ngc6543-same-session-anchor"
            / "NGC6543-same-session-anchor_spectrum.fits",
            diagnostics={
                "trace.png": RUNS_ROOT
                / "20260504-3c273"
                / "ngc6543-same-session-anchor"
                / "NGC6543-same-session-anchor_trace_overlay.png",
                "alignment.png": RUNS_ROOT
                / "20260504-3c273"
                / "ngc6543-same-session-anchor"
                / "NGC6543-same-session-anchor_alignment.png",
                "preprocessed_2d.png": RUNS_ROOT
                / "20260504-3c273"
                / "ngc6543-same-session-anchor"
                / "NGC6543-same-session-anchor_preprocessed.png",
                "wavelength_residuals.png": RUNS_ROOT
                / "20260504-3c273"
                / "ngc6543-same-session-anchor"
                / "NGC6543-same-session-anchor_wavelength_residuals.png",
            },
            metadata={
                "processing.json": RUNS_ROOT
                / "20260504-3c273"
                / "ngc6543-same-session-anchor"
                / "NGC6543-same-session-anchor_run.json",
            },
            notes=(
                "Known-line wavelength anchor; counts-only continuum normalization is derived here.",
                "This is the 2026-05-05 local-date observation used by the 3C 273 analysis.",
            ),
        ),
        ProductSpec(
            target="NGC6543",
            date="2026-05-06",
            display_name="NGC 6543",
            source_csv=may_validation / "ngc6543" / "NGC6543_calibrated_1d.csv",
            source_fits=may_validation / "ngc6543" / "NGC6543_calibrated_1d.fits",
            diagnostics={
                "normalized_source.png": may_validation / "ngc6543" / "NGC6543_normalised_1d.png",
                "trace.png": may_validation / "ngc6543" / "NGC6543_trace_overlay.png",
                "wavelength_residuals.png": may_validation
                / "ngc6543"
                / "NGC6543_wavelength_residuals.png",
            },
            calibration={
                "response.csv": may_validation / "relative_response.csv",
                "response.fits": may_validation / "relative_response.fits",
                "response.png": may_validation / "relative_response.png",
            },
            metadata={
                "validation.json": may_validation / "ngc6543" / "NGC6543_validation.json",
                "validation_summary.json": may_validation / "validation_summary.json",
            },
            notes=("Cross-night Vega response validation; not absolute flux calibrated.",),
        ),
        ProductSpec(
            target="NGC6543",
            date="2026-05-12",
            display_name="NGC 6543",
            source_csv=ngc_12 / "final" / "NGC6543_calibrated_1d.csv",
            source_fits=ngc_12 / "final" / "NGC6543_calibrated_1d.fits",
            diagnostics={
                "normalized_source.png": ngc_12 / "final" / "NGC6543_normalised_1d.png",
                "line_diagnostics.png": ngc_12 / "final" / "NGC6543_line_diagnostics.png",
                "trace.png": ngc_12 / "science-preferred" / "NGC6543_trace_overlay.png",
                "alignment.png": ngc_12 / "science-preferred" / "NGC6543_alignment.png",
                "preprocessed_2d.png": ngc_12 / "science-preferred" / "NGC6543_preprocessed.png",
                "wavelength_residuals.png": ngc_12
                / "science-preferred"
                / "NGC6543_wavelength_residuals.png",
                "line_diagnostics_corrected_experimental.png": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_line_diagnostics_corrected_experimental.png",
                "line_diagnostics_corrected_only_experimental.png": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_line_diagnostics_corrected_only_experimental.png",
                "lsf_profile_before_after_experimental.png": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_asymmetric_lsf_diagnostic.png",
                "lsf_corrected_experimental.fits": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_asymmetric_lsf_diagnostic.fits",
                "lsf_corrected_experimental.csv": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_asymmetric_lsf_diagnostic.csv",
                "LSF_CORRECTION_README.md": ngc_12 / "final" / "lsf-diagnostic" / "README.md",
            },
            calibration={
                "response.csv": ngc_12 / "final" / "relative_response.csv",
                "response.fits": ngc_12 / "final" / "relative_response.fits",
                "response.png": ngc_12 / "final" / "relative_response.png",
                "emission_lines.csv": ngc_12 / "final" / "NGC6543_emission_lines.csv",
            },
            metadata={
                "processing.json": ngc_12 / "science-preferred" / "NGC6543_run.json",
                "workflow.json": ngc_12 / "full_workflow.json",
                "calibration.json": ngc_12 / "final" / "NGC6543_calibration.json",
                "line_analysis.json": ngc_12 / "final" / "NGC6543_line_analysis.json",
                "lsf_assessment.json": ngc_12
                / "final"
                / "lsf-diagnostic"
                / "NGC6543_lsf_assessment.json",
            },
            notes=(
                "Preferred SharpCap reduction with same-night Vega relative response.",
                "The blue/green asymmetric-LSF inverse is an experimental diagnostic; the canonical spectrum is unchanged.",
            ),
        ),
        ProductSpec(
            target="HD140573",
            date="2026-05-09",
            display_name="HD 140573",
            source_csv=may_validation / "hd140573" / "HD140573_calibrated_1d.csv",
            source_fits=may_validation / "hd140573" / "HD140573_calibrated_1d.fits",
            diagnostics={
                "normalized_source.png": may_validation / "hd140573" / "HD140573_normalised_1d.png",
                "trace.png": may_validation / "hd140573" / "HD140573_trace_overlay.png",
            },
            calibration={
                "response.csv": may_validation / "relative_response.csv",
                "response.fits": may_validation / "relative_response.fits",
                "response.png": may_validation / "relative_response.png",
            },
            metadata={
                "validation.json": may_validation / "hd140573" / "HD140573_validation.json",
            },
            notes=("Cross-night Vega response validation; not absolute flux calibrated.",),
        ),
        ProductSpec(
            target="Vega",
            date="2026-05-09",
            source_csv=may_validation / "Vega_standard_1d.csv",
            source_fits=may_validation / "Vega_standard_1d.fits",
            diagnostics={
                "pipeline_spectrum.png": may_validation / "Vega_standard_1d.png",
                "trace.png": may_validation / "Vega_trace_overlay.png",
                "wavelength_residuals.png": may_validation / "Vega_wavelength_residuals.png",
            },
            calibration={
                "response.csv": may_validation / "relative_response.csv",
                "response.fits": may_validation / "relative_response.fits",
                "response.png": may_validation / "relative_response.png",
            },
            metadata={
                "processing.json": may_validation / "vega_standard_and_flat.json",
            },
        ),
        _pipeline_spec(
            "Vega",
            "2026-05-12",
            ngc_12 / "standard-no-flat",
            "Vega",
            notes=("Same-night relative-response standard for NGC 6543.",),
        ),
        ProductSpec(
            target="PNV-J19450648+1822422",
            date="2026-08-25",
            display_name="PNV J19450648+1822422 / Nova Sge 2026",
            source_csv=nova_20260826 / "final" / "Nova_Sge_2026_calibrated_1d.csv",
            source_fits=nova_20260826 / "final" / "Nova_Sge_2026_calibrated_1d.fits",
            diagnostics={
                "normalized_source.png": nova_20260826
                / "final"
                / "Nova_Sge_2026_normalised_1d.png",
                "trace.png": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_trace_overlay.png",
                "alignment.png": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_alignment.png",
                "preprocessed_2d.png": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_preprocessed.png",
                "wavelength_residuals.png": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_wavelength_residuals.png",
                "pipeline_spectrum.png": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_spectrum.png",
                "line_diagnostics_narrow_model_not_applicable.png": nova_20260826
                / "final"
                / "Nova_Sge_2026_line_diagnostics.png",
            },
            calibration={
                "response.csv": nova_20260826 / "final" / "relative_response.csv",
                "response.fits": nova_20260826 / "final" / "relative_response.fits",
                "response.png": nova_20260826 / "final" / "relative_response.png",
            },
            metadata={
                "processing.json": nova_20260826
                / "science-preferred"
                / "Nova_Sge_2026_run.json",
                "workflow.json": nova_20260826 / "full_workflow.json",
                "calibration.json": nova_20260826
                / "final"
                / "Nova_Sge_2026_calibration.json",
                "line_analysis_narrow_model_not_applicable.json": nova_20260826
                / "final"
                / "Nova_Sge_2026_line_analysis.json",
                "input_inspection.json": QUALITY_ROOT
                / "inspection"
                / "20260826-nova-sge-2026.json",
                "input_inspection.csv": QUALITY_ROOT
                / "inspection"
                / "20260826-nova-sge-2026.csv",
            },
            notes=(
                "Seven accepted 600 s spectra; 4200 s total integration.",
                "Same-session Vega transfer; relative response only, not absolute flux.",
                "The generic narrow-emission-line model is not applicable to this broad, absorption-dominated nova spectrum.",
            ),
        ),
        _pipeline_spec(
            "Vega",
            "2026-08-25",
            nova_20260826 / "standard-flat-trial",
            "Vega",
            notes=(
                "Same-session wavelength and relative-response standard for PNV J19450648+1822422.",
                "The accepted LED flat was validated against the no-flat control reduction.",
            ),
        ),
    ]
    return specs


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _column(data: np.ndarray, *names: str) -> np.ndarray | None:
    available = {name.casefold().lstrip("\ufeff"): name for name in (data.dtype.names or ())}
    for name in names:
        actual = available.get(name.casefold())
        if actual is not None:
            return np.asarray(data[actual])
    return None


def _read_mask_column(path: Path, size: int) -> np.ndarray:
    with path.open("r", newline="", encoding="utf-8-sig") as stream:
        reader = csv.DictReader(stream)
        key = next(
            (name for name in (reader.fieldnames or ()) if name.casefold() == "mask"),
            None,
        )
        if key is None:
            return np.zeros(size, dtype=bool)
        values = [
            str(row.get(key, "")).strip().casefold() in {"1", "true", "yes"} for row in reader
        ]
    if len(values) != size:
        raise RuntimeError(f"Mask length does not match spectrum length in {path}")
    return np.asarray(values, dtype=bool)


def _derive_continuum(wavelength: np.ndarray, flux: np.ndarray, mask: np.ndarray) -> np.ndarray:
    valid = ~mask & np.isfinite(wavelength) & np.isfinite(flux) & (flux > 0)
    if np.count_nonzero(valid) < 20:
        raise RuntimeError("Too few valid positive samples for continuum normalization.")
    start = float(np.nanmin(wavelength[valid]))
    stop = float(np.nanmax(wavelength[valid]))
    edges = np.arange(start, stop + 100.0, 100.0)
    centers: list[float] = []
    levels: list[float] = []
    for low, high in zip(edges[:-1], edges[1:]):
        inside = valid & (wavelength >= low) & (wavelength < high)
        if np.count_nonzero(inside) >= 8:
            centers.append(float(np.nanmedian(wavelength[inside])))
            levels.append(float(np.nanpercentile(flux[inside], 35.0)))
    if len(centers) < 4:
        level = float(np.nanmedian(flux[valid]))
        return np.full_like(flux, level, dtype=float)
    continuum = np.interp(wavelength, centers, levels)
    continuum = gaussian_filter1d(continuum, sigma=50.0, mode="nearest")
    floor = float(np.nanpercentile(continuum[np.isfinite(continuum)], 1.0))
    return np.where(continuum > max(floor * 0.05, 1e-12), continuum, np.nan)


def _load_product(spec: ProductSpec) -> tuple[dict[str, np.ndarray], dict[str, Any]]:
    if not spec.source_csv.is_file():
        raise FileNotFoundError(spec.source_csv)
    data = np.genfromtxt(spec.source_csv, delimiter=",", names=True, encoding="utf-8")
    data = np.atleast_1d(data)
    wavelength = _column(data, "wavelength_angstrom", "wavelength_angstrom_air")
    if wavelength is None:
        raise RuntimeError(f"No wavelength column in {spec.source_csv}")
    wavelength = np.asarray(wavelength, dtype=float)
    normalized = _column(data, "normalized_flux")
    continuum = _column(data, "continuum_adu_per_s", "continuum_adu", "continuum")
    legacy_continuum = _column(data, "running_median_continuum_adu_per_s")
    legacy_normalized = _column(data, "running_median_normalized_flux")
    flux = _column(
        data,
        "relative_flux_adu_per_s",
        "count_rate_adu_per_s",
        "flux_adu",
        "flux",
    )
    uncertainty = _column(
        data,
        "relative_uncertainty_adu_per_s",
        "count_rate_uncertainty_adu_per_s",
        "uncertainty_adu",
        "uncertainty",
    )
    normalized_uncertainty = _column(data, "normalized_uncertainty")
    mask = _read_mask_column(spec.source_csv, wavelength.size)

    if flux is None and normalized is not None and continuum is not None:
        flux = np.asarray(normalized, dtype=float) * np.asarray(continuum, dtype=float)
    if flux is None and normalized is not None:
        flux = np.asarray(normalized, dtype=float)
    if flux is None:
        raise RuntimeError(f"No usable flux column in {spec.source_csv}")
    flux = np.asarray(flux, dtype=float)
    if uncertainty is None:
        uncertainty = np.full_like(flux, np.nan)
    else:
        uncertainty = np.asarray(uncertainty, dtype=float)

    normalization_method = "source-product"
    if continuum is None:
        continuum = _derive_continuum(wavelength, flux, mask)
        normalization_method = "catalogue-100A-bin-35th-percentile"
    else:
        continuum = np.asarray(continuum, dtype=float)
    if normalized is None:
        normalized = np.divide(
            flux,
            continuum,
            out=np.full_like(flux, np.nan),
            where=np.isfinite(continuum) & (continuum != 0),
        )
        normalization_method = "catalogue-100A-bin-35th-percentile"
    else:
        normalized = np.asarray(normalized, dtype=float)
    if normalized_uncertainty is None:
        normalized_uncertainty = np.divide(
            uncertainty,
            continuum,
            out=np.full_like(uncertainty, np.nan),
            where=np.isfinite(continuum) & (continuum != 0),
        )
    else:
        normalized_uncertainty = np.asarray(normalized_uncertainty, dtype=float)

    order = np.argsort(wavelength)
    arrays = {
        "wavelength": wavelength[order],
        "flux": flux[order],
        "uncertainty": uncertainty[order],
        "continuum": continuum[order],
        "normalized": normalized[order],
        "normalized_uncertainty": normalized_uncertainty[order],
        "mask": mask[order],
    }
    if legacy_continuum is not None and legacy_normalized is not None:
        arrays["legacy_continuum"] = np.asarray(legacy_continuum, dtype=float)[order]
        arrays["legacy_normalized"] = np.asarray(legacy_normalized, dtype=float)[order]
    valid_wave = np.isfinite(arrays["wavelength"])
    for key in arrays:
        arrays[key] = arrays[key][valid_wave]
    if _column(data, "relative_flux_adu_per_s") is not None:
        flux_kind = "relative-response-adu-per-second"
    elif _column(data, "count_rate_adu_per_s") is not None:
        flux_kind = "detector-count-rate-adu-per-second"
    else:
        flux_kind = "detector-counts"
    return arrays, {
        "fluxKind": flux_kind,
        "absoluteFluxCalibrated": False,
        "normalizationMethod": normalization_method,
    }


def _write_csv(path: Path, arrays: dict[str, np.ndarray]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        columns: list[tuple[str, np.ndarray]] = [
            ("wavelength_angstrom", arrays["wavelength"]),
            ("flux", arrays["flux"]),
            ("uncertainty", arrays["uncertainty"]),
            ("continuum", arrays["continuum"]),
            ("normalized_flux", arrays["normalized"]),
            ("normalized_uncertainty", arrays["normalized_uncertainty"]),
        ]
        if "legacy_continuum" in arrays:
            columns.extend(
                [
                    ("running_median_continuum", arrays["legacy_continuum"]),
                    ("running_median_normalized_flux", arrays["legacy_normalized"]),
                ]
            )
        columns.append(("mask", arrays["mask"].astype(np.uint8)))
        writer.writerow([name for name, _ in columns])
        writer.writerows(zip(*(values for _, values in columns)))


def _write_fits(
    path: Path,
    spec: ProductSpec,
    arrays: dict[str, np.ndarray],
    metadata: dict[str, Any],
) -> None:
    primary = fits.PrimaryHDU()
    object_name = spec.target if not spec.component else f"{spec.target}-{spec.component}"
    primary.header["OBJECT"] = object_name
    primary.header["DATE-OBS"] = spec.date
    primary.header["PRODTYPE"] = "UVEX-ADV CATALOGUE 1D"
    primary.header["ABSFLUX"] = (False, "No absolute spectrophotometric calibration")
    primary.header["FLUXKND"] = metadata["fluxKind"]
    primary.header["CONTNORM"] = True
    primary.header["WAVEMED"] = "AIR"
    if spec.target == "3C273":
        primary.header["GRATLPMM"] = (300, "Derived from measured dispersion/span")
        primary.header["GRATBAS"] = "DISP+SPAN"
        primary.header["CONTALG"] = ("QNT-SMOOTH", "35th percentile / 450 Angstrom")
    primary.header.add_history(
        "Target/date catalogue product; source run retained under _internal."
    )
    flux_unit = "adu / s" if metadata["fluxKind"].endswith("per-second") else "adu"
    columns = [
        fits.Column(
            name="PIXEL",
            format="D",
            unit="pix",
            array=np.arange(arrays["wavelength"].size, dtype=float),
        ),
        fits.Column(
            name="WAVELENGTH",
            format="D",
            unit="Angstrom",
            array=arrays["wavelength"],
        ),
        fits.Column(name="FLUX", format="D", unit=flux_unit, array=arrays["flux"]),
        fits.Column(
            name="UNCERTAINTY",
            format="D",
            unit=flux_unit,
            array=arrays["uncertainty"],
        ),
        fits.Column(
            name="CONTINUUM",
            format="D",
            unit=flux_unit,
            array=arrays["continuum"],
        ),
        fits.Column(
            name="NORMALIZED_FLUX",
            format="D",
            array=arrays["normalized"],
        ),
        fits.Column(
            name="NORMALIZED_UNCERTAINTY",
            format="D",
            array=arrays["normalized_uncertainty"],
        ),
        fits.Column(name="MASK", format="L", array=arrays["mask"]),
    ]
    if "legacy_continuum" in arrays:
        columns[-1:-1] = [
            fits.Column(
                name="RUNMED_CONTINUUM",
                format="D",
                unit=flux_unit,
                array=arrays["legacy_continuum"],
            ),
            fits.Column(
                name="RUNMED_NORMALIZED_FLUX",
                format="D",
                array=arrays["legacy_normalized"],
            ),
        ]
    table = fits.BinTableHDU.from_columns(columns, name="SPECTRUM")
    path.parent.mkdir(parents=True, exist_ok=True)
    fits.HDUList([primary, table]).writeto(path, overwrite=True, checksum=True)


def _limits(values: np.ndarray, valid: np.ndarray, low: float, high: float) -> tuple[float, float]:
    selected = values[valid & np.isfinite(values)]
    if selected.size < 10:
        return 0.0, 1.0
    bottom, top = np.nanpercentile(selected, [low, high])
    if not np.isfinite(bottom) or not np.isfinite(top) or bottom == top:
        return float(np.nanmin(selected)), float(np.nanmax(selected) + 1.0)
    margin = 0.08 * (top - bottom)
    return float(bottom - margin), float(top + margin)


def _plot_product(path: Path, spec: ProductSpec, arrays: dict[str, np.ndarray]) -> None:
    wave = arrays["wavelength"]
    valid = ~arrays["mask"] & np.isfinite(wave)
    fig, axes = plt.subplots(2, 1, figsize=(14, 8), sharex=True, constrained_layout=True)
    axes[0].plot(wave[valid], arrays["flux"][valid], color="#2563eb", linewidth=0.65)
    axes[0].set_ylabel("Counts / relative flux")
    axes[0].set_ylim(*_limits(arrays["flux"], valid, 0.5, 99.7))
    axes[1].plot(wave[valid], arrays["normalized"][valid], color="#0f766e", linewidth=0.7)
    axes[1].axhline(1.0, color="#64748b", linewidth=0.8, linestyle="--")
    axes[1].set_ylabel("Continuum-normalized flux")
    axes[1].set_xlabel("Air wavelength (Angstrom)")
    axes[1].set_ylim(*_limits(arrays["normalized"], valid, 0.5, 99.7))
    for axis in axes:
        if np.nanmax(wave) > 6800:
            axis.axvspan(6800, np.nanmax(wave), color="#f59e0b", alpha=0.08)
        axis.grid(alpha=0.18)
    component = f" / {spec.component}" if spec.component else ""
    fig.suptitle(f"{spec.display_name or spec.target}{component} — {spec.date}")
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path, dpi=170)
    plt.close(fig)


def _copy_group(files: dict[str, Path], destination: Path) -> list[str]:
    copied = []
    for name, source in files.items():
        if not source.is_file():
            continue
        destination.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination / name)
        copied.append(name)
    return copied


def _relative(path: Path) -> str:
    return path.resolve().relative_to(OUTPUT_ROOT.resolve()).as_posix()


def _write_product(spec: ProductSpec) -> dict[str, Any]:
    arrays, science_metadata = _load_product(spec)
    if spec.target == "3C273":
        science_metadata.update(
            {
                "gratingLinesPerMm": 300,
                "gratingEvidence": (
                    "Inferred from 0.94398 A/pixel measured dispersion and full-frame "
                    "wavelength span; the raw acquisition FITS lacks a GRATING keyword."
                ),
                "normalizationMethod": "450A-window-35th-percentile-smoothed-pseudo-continuum",
                "legacyNormalizationRetained": True,
            }
        )
    destination = spec.destination
    final = destination / "final"
    diagnostics = destination / "diagnostics"
    calibration = destination / "calibration"
    metadata_dir = destination / "metadata"
    final.mkdir(parents=True, exist_ok=True)
    metadata_dir.mkdir(parents=True, exist_ok=True)

    csv_path = final / "spectrum.csv"
    fits_path = final / "spectrum.fits"
    png_path = final / "spectrum.png"
    _write_csv(csv_path, arrays)
    _write_fits(fits_path, spec, arrays, science_metadata)
    _plot_product(png_path, spec, arrays)
    copied_diagnostics = _copy_group(spec.diagnostics, diagnostics)
    copied_calibration = _copy_group(spec.calibration, calibration)
    copied_metadata = _copy_group(spec.metadata, metadata_dir)

    valid = ~arrays["mask"] & np.isfinite(arrays["wavelength"])
    record = {
        "schemaVersion": 1,
        "target": spec.target,
        "displayName": spec.display_name or spec.target,
        "observationDate": spec.date,
        "component": spec.component,
        "sourceProduct": _relative(spec.source_csv),
        "sourceSha256": _sha256(spec.source_csv),
        "sourceFits": _relative(spec.source_fits) if spec.source_fits else None,
        "sourceFitsSha256": (
            _sha256(spec.source_fits) if spec.source_fits and spec.source_fits.is_file() else None
        ),
        "rowCount": int(arrays["wavelength"].size),
        "validRowCount": int(np.count_nonzero(valid)),
        "wavelengthRangeAngstrom": [
            float(np.nanmin(arrays["wavelength"][valid])),
            float(np.nanmax(arrays["wavelength"][valid])),
        ],
        **science_metadata,
        "notes": list(spec.notes),
        "files": {
            "fits": "final/spectrum.fits",
            "csv": "final/spectrum.csv",
            "png": "final/spectrum.png",
            "diagnostics": copied_diagnostics,
            "calibration": copied_calibration,
            "metadata": copied_metadata,
        },
    }
    product_json = metadata_dir / "product.json"
    product_json.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    notes = "\n".join(f"- {item}" for item in spec.notes) or "- No additional note."
    title = spec.display_name or spec.target
    readme = (
        f"# {title} — {spec.date}\n\n"
        "统一交付目录；历史运行记录保存在 `_internal`，原始观测 FITS 未复制或改写。\n\n"
        "## 文件\n\n"
        "- `final/spectrum.fits`：标准二进制表 FITS；\n"
        "- `final/spectrum.csv`：与 FITS 相同的标准列；\n"
        "- `final/spectrum.png`：统一双面板预览；\n"
        "- `diagnostics/`：迹线、对齐、波长残差等；\n"
        "- `calibration/`：响应或谱线表（有则提供）；\n"
        "- `metadata/product.json`：来源指纹和处理层级。\n\n"
        "## 限制\n\n"
        "所有通量均为计数或相对响应尺度，不是绝对光谱流量。归一化只适合形态和谱线比较。\n\n"
        f"{notes}\n"
    )
    (destination / "README.md").write_text(readme, encoding="utf-8")
    return record


def _read_catalogue_csv(path: Path) -> dict[str, np.ndarray]:
    data = np.genfromtxt(path, delimiter=",", names=True, encoding="utf-8")
    return {
        "wavelength": np.asarray(data["wavelength_angstrom"], dtype=float),
        "normalized": np.asarray(data["normalized_flux"], dtype=float),
        "mask": np.asarray(data["mask"], dtype=bool),
    }


def _line_peak(wave: np.ndarray, flux: np.ndarray, center: float) -> tuple[float, float]:
    # Six Angstrom keeps the neighbouring H-alpha/[N II] and [S II] features
    # from stealing one another's local maximum at this instrument resolution.
    inside = np.isfinite(flux) & (wave >= center - 6.0) & (wave <= center + 6.0)
    if np.count_nonzero(inside) == 0:
        return np.nan, np.nan
    indices = np.flatnonzero(inside)
    index = int(indices[np.nanargmax(flux[inside])])
    return float(wave[index]), float(flux[index])


def _comparison_limits(values: list[np.ndarray], percentile: float = 99.5) -> tuple[float, float]:
    combined = np.concatenate([item[np.isfinite(item)] for item in values])
    low = max(0.0, float(np.nanpercentile(combined, 0.5)) - 0.1)
    high = float(np.nanpercentile(combined, percentile)) * 1.08
    return low, max(high, 1.5)


def _write_ngc6543_comparison() -> dict[str, Any]:
    first_date = "2026-05-05"
    second_date = "2026-05-12"
    first = _read_catalogue_csv(OUTPUT_ROOT / "NGC6543" / first_date / "final" / "spectrum.csv")
    second = _read_catalogue_csv(OUTPUT_ROOT / "NGC6543" / second_date / "final" / "spectrum.csv")
    low = max(
        float(np.nanmin(first["wavelength"])),
        float(np.nanmin(second["wavelength"])),
    )
    high = min(
        float(np.nanmax(first["wavelength"])),
        float(np.nanmax(second["wavelength"])),
    )
    grid = np.arange(np.ceil(low), np.floor(high) + 0.001, 1.0)

    def interpolate(product: dict[str, np.ndarray]) -> np.ndarray:
        valid = (
            ~product["mask"]
            & np.isfinite(product["wavelength"])
            & np.isfinite(product["normalized"])
        )
        return np.interp(
            grid,
            product["wavelength"][valid],
            product["normalized"][valid],
            left=np.nan,
            right=np.nan,
        )

    first_flux = interpolate(first)
    second_flux = interpolate(second)
    ratio = np.divide(
        second_flux,
        first_flux,
        out=np.full_like(first_flux, np.nan),
        where=np.isfinite(first_flux) & (first_flux != 0),
    )
    destination = OUTPUT_ROOT / "NGC6543" / "comparisons" / f"{first_date}_vs_{second_date}"
    destination.mkdir(parents=True, exist_ok=True)
    with (destination / "comparison.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom",
                "normalized_2026_05_05",
                "normalized_2026_05_12",
                "ratio_2026_05_12_over_2026_05_05",
            ]
        )
        writer.writerows(zip(grid, first_flux, second_flux, ratio))

    lines = {
        "Hbeta": 4861.35,
        "OIII4959": 4958.91,
        "OIII5007": 5006.84,
        "Halpha": 6562.79,
        "NII6583": 6583.45,
        "SII6716": 6716.44,
        "SII6731": 6730.82,
    }
    line_rows = []
    for name, reference in lines.items():
        wave_a, peak_a = _line_peak(grid, first_flux, reference)
        wave_b, peak_b = _line_peak(grid, second_flux, reference)
        line_rows.append((name, reference, wave_a, peak_a, wave_b, peak_b))
    with (destination / "line_measurements.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "line",
                "reference_air_angstrom",
                "peak_2026_05_05_angstrom",
                "peak_2026_05_05_normalized",
                "peak_2026_05_12_angstrom",
                "peak_2026_05_12_normalized",
            ]
        )
        writer.writerows(line_rows)

    fig, axes = plt.subplots(3, 1, figsize=(15, 11), constrained_layout=True)
    colors = (("#2563eb", first_date, first_flux), ("#dc2626", second_date, second_flux))
    for color, label, flux in colors:
        axes[0].plot(grid, flux, color=color, linewidth=0.7, alpha=0.88, label=label)
    axes[0].set_ylim(*_comparison_limits([first_flux, second_flux]))
    axes[0].set_title("NGC 6543 — same-grid continuum-normalized comparison")
    axes[0].legend()
    for axis, bounds, title in (
        (axes[1], (4800.0, 5050.0), "H-beta and [O III]"),
        (axes[2], (6500.0, 6760.0), "H-alpha, [N II] and [S II]"),
    ):
        inside = (grid >= bounds[0]) & (grid <= bounds[1])
        for color, label, flux in colors:
            axis.plot(grid[inside], flux[inside], color=color, linewidth=0.9, label=label)
        axis.set_xlim(*bounds)
        axis.set_ylim(*_comparison_limits([first_flux[inside], second_flux[inside]], 99.8))
        axis.set_title(title)
        axis.set_ylabel("Normalized flux")
        axis.grid(alpha=0.18)
    axes[0].set_ylabel("Normalized flux")
    axes[0].grid(alpha=0.18)
    axes[2].set_xlabel("Air wavelength (Angstrom)")
    plot_path = destination / "comparison.png"
    fig.savefig(plot_path, dpi=180)
    plt.close(fig)

    summary = {
        "schemaVersion": 1,
        "target": "NGC6543",
        "dates": [first_date, second_date],
        "commonWavelengthRangeAngstrom": [float(grid[0]), float(grid[-1])],
        "gridStepAngstrom": 1.0,
        "comparisonScale": "continuum-normalized",
        "absoluteFluxComparison": False,
        "caveats": [
            "The 2026-05-05 product is a counts-only known-line wavelength anchor.",
            "The 2026-05-12 product includes a same-night Vega relative-response correction.",
            "Use this product for line position/profile comparison, not absolute brightness.",
        ],
        "files": ["comparison.png", "comparison.csv", "line_measurements.csv"],
    }
    (destination / "comparison_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (destination / "README.md").write_text(
        "# NGC 6543：2026-05-05 与 2026-05-12\n\n"
        "两晚光谱已转成相同的 1 Å 空气波长网格，并使用各自连续谱归一化后叠加。\n"
        "`comparison.png` 是首选入口；`comparison.csv` 可供进一步分析。\n\n"
        "这不是绝对亮度比较：5 月 5 日是 counts-only 谱，5 月 12 日包含 Vega 相对响应修正。\n"
        "适合比较谱线位置、线型和相对结构，不适合直接比较两晚总通量。\n",
        encoding="utf-8",
    )
    return summary


def _rewrite_value(value: Any, replacements: dict[str, str]) -> Any:
    if isinstance(value, str):
        for old, new in replacements.items():
            value = value.replace(old, new)
        return value
    if isinstance(value, list):
        return [_rewrite_value(item, replacements) for item in value]
    if isinstance(value, dict):
        return {key: _rewrite_value(item, replacements) for key, item in value.items()}
    return value


def _rewrite_internal_json_paths() -> int:
    run_names = (
        "20260504-3c273",
        "20260504-albireo",
        "20260504-arcturus",
        "20260512-sharpcap",
        "full-20260221",
        "may-2026-validation",
        "regulus",
        "regulus-native",
        "regulus-native2",
        "regulus-tophat",
        "same-night-20260506",
        "science",
        "standards",
    )
    quality_names = (
        "classic-target-validation",
        "inspection",
        "order2-study-20260218",
        "toupsky-audit",
    )
    replacements = {str(OUTPUT_ROOT / name): str(RUNS_ROOT / name) for name in run_names}
    replacements.update(
        {str(OUTPUT_ROOT / name): str(QUALITY_ROOT / name) for name in quality_names}
    )
    changed = 0
    internal = OUTPUT_ROOT / "_internal"
    for path in sorted(internal.rglob("*.json")):
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            continue
        updated = _rewrite_value(payload, replacements)
        if updated != payload:
            path.write_text(json.dumps(updated, ensure_ascii=False, indent=2), encoding="utf-8")
            changed += 1
    return changed


def _url(path: str) -> str:
    return quote(path.replace("\\", "/"), safe="/._-")


def _write_indexes(records: list[dict[str, Any]], comparison: dict[str, Any]) -> None:
    index = {
        "schemaVersion": 1,
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "layout": "target/date/{final,diagnostics,calibration,metadata}",
        "products": records,
        "comparisons": [comparison],
    }
    (OUTPUT_ROOT / "index.json").write_text(
        json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    rows = []
    for record in records:
        component = f" / {record['component']}" if record.get("component") else ""
        destination = f"{record['target']}/{record['observationDate']}"
        if record.get("component"):
            destination += f"/components/{record['component']}"
        rows.append(
            f"| {record['displayName']}{component} | {record['observationDate']} | "
            f"[{destination}]({destination}/) | "
            f"{record['wavelengthRangeAngstrom'][0]:.0f}–"
            f"{record['wavelengthRangeAngstrom'][1]:.0f} Å |"
        )
    readme = (
        "# OpenAstroSpec 光谱结果索引 — UVEX4\n\n"
        "目录按 **目标 → 观测日期** 组织。每个普通日期目录都使用相同结构：\n\n"
        "- `final/spectrum.fits`、`spectrum.csv`、`spectrum.png`：统一交付产品；\n"
        "- `diagnostics/`：2D、迹线、对齐和波长残差；\n"
        "- `calibration/`：响应曲线或谱线表；\n"
        "- `metadata/`：处理记录、来源路径和 SHA-256；\n"
        "- `_internal/`：历史运行、试验和审计，不作为首选浏览入口。\n\n"
        "双击 `00-打开结果索引.html` 可用浏览器查看图形索引。\n\n"
        "## NGC 6543 快捷入口\n\n"
        "- [2026-05-05](NGC6543/2026-05-05/)\n"
        "- [2026-05-06](NGC6543/2026-05-06/)\n"
        "- [2026-05-12](NGC6543/2026-05-12/)\n"
        "- [05-05 对 05-12](NGC6543/comparisons/2026-05-05_vs_2026-05-12/)\n\n"
        "## 全部正式产品\n\n"
        "| 目标 | 日期 | 目录 | 波长范围 |\n"
        "|---|---|---|---:|\n"
        + "\n".join(rows)
        + "\n\n所有产品均非绝对光谱流量标定；跨夜比较默认使用连续谱归一化尺度。\n"
    )
    (OUTPUT_ROOT / "README.md").write_text(readme, encoding="utf-8")

    by_target: dict[str, list[dict[str, Any]]] = {}
    for record in records:
        by_target.setdefault(record["target"], []).append(record)
    for target, target_records in by_target.items():
        entries = []
        for record in target_records:
            component = record.get("component")
            if component:
                link = f"{record['observationDate']}/components/{component}/"
                label = f"{record['observationDate']} / {component}"
            else:
                link = f"{record['observationDate']}/"
                label = record["observationDate"]
            entries.append(f"- [{label}]({link})")
        if target == "NGC6543":
            entries.append("- [2026-05-05 对 2026-05-12](comparisons/2026-05-05_vs_2026-05-12/)")
        (OUTPUT_ROOT / target / "README.md").write_text(
            f"# {target}\n\n" + "\n".join(entries) + "\n",
            encoding="utf-8",
        )

    component_groups: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for record in records:
        if record.get("component"):
            component_groups.setdefault((record["target"], record["observationDate"]), []).append(
                record
            )
    for (target, date), component_records in component_groups.items():
        links = "\n".join(
            f"- [{record['component']}](components/{record['component']}/)"
            for record in component_records
        )
        (OUTPUT_ROOT / target / date / "README.md").write_text(
            f"# {target} — {date}\n\n双星分量：\n\n{links}\n",
            encoding="utf-8",
        )

    cards = []
    for record in records:
        destination = f"{record['target']}/{record['observationDate']}"
        if record.get("component"):
            destination += f"/components/{record['component']}"
        title = record["displayName"]
        if record.get("component"):
            title += f" / {record['component']}"
        cards.append(
            "<article class='card' "
            f"data-search='{html.escape((title + ' ' + record['observationDate']).casefold())}'>"
            f"<a href='{_url(destination + '/final/spectrum.png')}'><img "
            f"src='{_url(destination + '/final/spectrum.png')}' alt='spectrum'></a>"
            f"<h2>{html.escape(title)}</h2><p>{record['observationDate']} · "
            f"{record['wavelengthRangeAngstrom'][0]:.0f}–"
            f"{record['wavelengthRangeAngstrom'][1]:.0f} Å</p>"
            f"<nav><a href='{_url(destination + '/')}'>目录</a>"
            f"<a href='{_url(destination + '/final/spectrum.fits')}'>FITS</a>"
            f"<a href='{_url(destination + '/final/spectrum.csv')}'>CSV</a></nav></article>"
        )
    comparison_path = "NGC6543/comparisons/2026-05-05_vs_2026-05-12"
    cards.insert(
        0,
        "<article class='card highlight' data-search='ngc 6543 2026-05-05 2026-05-12 comparison'>"
        f"<a href='{_url(comparison_path + '/comparison.png')}'><img "
        f"src='{_url(comparison_path + '/comparison.png')}' alt='comparison'></a>"
        "<h2>NGC 6543 · 05-05 vs 05-12</h2><p>同一波长网格、同一归一化形式</p>"
        f"<nav><a href='{_url(comparison_path + '/')}'>对比目录</a>"
        f"<a href='{_url(comparison_path + '/comparison.csv')}'>CSV</a></nav></article>",
    )
    html_text = """<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>OpenAstroSpec 光谱结果 — UVEX4</title><style>
:root{color-scheme:dark;font-family:"Microsoft YaHei UI",system-ui,sans-serif;background:#0b1017;color:#e8eef7}
body{max-width:1500px;margin:auto;padding:28px}header{position:sticky;top:0;background:#0b1017ee;padding:8px 0 18px;z-index:2}
h1{margin:0 0 8px}p{color:#9fb0c5}input{width:min(620px,90%);padding:12px 14px;border:1px solid #334155;border-radius:10px;background:#121923;color:#fff;font-size:16px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:18px}.card{background:#121923;border:1px solid #283548;border-radius:14px;overflow:hidden;padding-bottom:14px}.highlight{border-color:#2dd4bf;box-shadow:0 0 0 1px #2dd4bf44}.card img{width:100%;height:210px;object-fit:cover;background:#fff}.card h2,.card p,.card nav{margin-left:16px;margin-right:16px}.card h2{font-size:18px}.card nav{display:flex;gap:14px}.card a{color:#5eead4;text-decoration:none}.hidden{display:none}</style></head>
<body><header><h1>OpenAstroSpec 光谱结果 — UVEX4</h1><p>目标 → 日期；正式产品与历史运行记录已经分开。</p>
<input id="q" placeholder="搜索目标或日期，例如 NGC6543、2026-05-12"></header><main class="grid">"""
    html_text += "".join(cards)
    html_text += """</main><script>const q=document.querySelector('#q');q.addEventListener('input',()=>{const s=q.value.toLowerCase().trim();document.querySelectorAll('.card').forEach(c=>c.classList.toggle('hidden',s&&!c.dataset.search.includes(s)))})</script></body></html>"""
    (OUTPUT_ROOT / "00-打开结果索引.html").write_text(html_text, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--skip-json-rewrite",
        action="store_true",
        help="Do not update absolute output paths inside archived JSON manifests.",
    )
    args = parser.parse_args()
    if not RUNS_ROOT.is_dir() or not QUALITY_ROOT.is_dir():
        raise RuntimeError(
            "The output migration has not been performed: expected output/_internal/{runs,quality}."
        )
    rewritten = 0 if args.skip_json_rewrite else _rewrite_internal_json_paths()
    records = [_write_product(spec) for spec in _specs()]
    comparison = _write_ngc6543_comparison()
    _write_indexes(records, comparison)
    print(
        f"Organized {len(records)} target/date products; updated {rewritten} archived JSON files."
    )
    print(OUTPUT_ROOT / "00-打开结果索引.html")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
