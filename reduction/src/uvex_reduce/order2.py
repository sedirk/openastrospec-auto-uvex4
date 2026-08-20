from __future__ import annotations

import csv
from dataclasses import asdict, dataclass
import json
from pathlib import Path
import re
from typing import Mapping, Sequence

import matplotlib

matplotlib.use("Agg")
from matplotlib import pyplot as plt
import numpy as np
from scipy.ndimage import gaussian_filter1d

from .calibration import RelativeResponse
from .stellar import load_stellar_template


BALMER_DISCONTINUITY_ANGSTROM = 3646.0
SECOND_ORDER_BALMER_MARKER_ANGSTROM = 2.0 * BALMER_DISCONTINUITY_ANGSTROM


@dataclass(slots=True)
class SecondOrderAssessment:
    """Result of a differential hot/cool standard-star second-order test.

    A grating can place second-order light from wavelength ``lambda / 2`` at
    the same detector coordinate as first-order light labelled ``lambda``.
    Dividing each observed standard by its own template removes the stellar
    continuum to first order.  A genuine leak should then make the inferred
    response of the bluer (hotter) standards rise relative to a cooler
    standard at the red end.  The test deliberately refuses a change with the
    opposite sign.
    """

    status: str
    warning_start_angstrom: float
    empirical_onset_angstrom: float | None
    formal_change_candidate_angstrom: float | None
    formal_change_sign: str | None
    physically_consistent: bool
    tested_min_angstrom: float
    tested_max_angstrom: float
    baseline_min_angstrom: float
    baseline_max_angstrom: float
    baseline_scatter_fraction: float
    detection_threshold_fraction: float
    balmer_second_order_marker_angstrom: float
    hot_standard_names: tuple[str, ...]
    cool_standard_name: str
    notes: tuple[str, ...]


@dataclass(slots=True)
class SecondOrderStudy:
    assessment: SecondOrderAssessment
    wavelength_angstrom: np.ndarray
    standard_responses: dict[str, np.ndarray]
    hot_mean_response: np.ndarray
    cool_response: np.ndarray
    hot_to_cool_ratio: np.ndarray
    fitted_baseline_ratio: np.ndarray
    residual_fraction: np.ndarray
    expected_blue_leverage: np.ndarray


def assess_second_order(
    responses: Mapping[str, RelativeResponse],
    hot_standard_names: Sequence[str],
    cool_standard_name: str,
    *,
    warning_start_angstrom: float = 6800.0,
    baseline_min_angstrom: float = 5200.0,
    baseline_max_angstrom: float = 6400.0,
    smoothing_angstrom: float = 35.0,
    persistence_angstrom: float = 100.0,
    minimum_fractional_excess: float = 0.03,
) -> SecondOrderStudy:
    """Compare standard-star response curves without inventing a cutoff.

    This is a screening diagnostic, not a substitute for a paired exposure
    through a characterized order-sorting filter.  Unknown atmospheric
    extinction and chromatic slit loss are removed only approximately by a
    low-order baseline fitted before the suspected contamination region.
    """

    if not hot_standard_names:
        raise ValueError("At least one hot standard is required.")
    missing = [
        name for name in (*hot_standard_names, cool_standard_name) if name not in responses
    ]
    if missing:
        raise KeyError(f"Missing response curve(s): {', '.join(missing)}")
    if warning_start_angstrom <= baseline_max_angstrom:
        raise ValueError("The warning threshold must be redward of the baseline interval.")

    selected_names = tuple(dict.fromkeys((*hot_standard_names, cool_standard_name)))
    selected = [responses[name] for name in selected_names]
    common_min = max(float(item.wavelength_angstrom[0]) for item in selected)
    common_max = min(float(item.wavelength_angstrom[-1]) for item in selected)
    steps = [float(np.median(np.diff(item.wavelength_angstrom))) for item in selected]
    step = max(steps)
    if not np.isfinite(step) or step <= 0:
        raise ValueError("Standard spectra do not have a usable wavelength sampling.")
    wavelength = np.arange(common_min, common_max + 0.25 * step, step)
    if wavelength.size < 300:
        raise RuntimeError("The standard spectra have too little common wavelength coverage.")

    curves: dict[str, np.ndarray] = {}
    leverage: dict[str, np.ndarray] = {}
    for name in selected_names:
        response = responses[name]
        valid = (
            ~response.mask
            & np.isfinite(response.response)
            & (response.response > 0)
        )
        if np.count_nonzero(valid) < 100:
            raise RuntimeError(f"{name} has too few valid response samples.")
        curve = np.interp(
            wavelength,
            response.wavelength_angstrom[valid],
            response.response[valid],
            left=np.nan,
            right=np.nan,
        )
        reference = (
            np.isfinite(curve)
            & (wavelength >= 5400.0)
            & (wavelength <= 5600.0)
        )
        normalizer = float(np.nanmedian(curve[reference]))
        if not np.isfinite(normalizer) or normalizer <= 0:
            raise RuntimeError(f"{name} has no response normalization band.")
        curves[name] = curve / normalizer

        template = load_stellar_template(response.template_path)
        red = np.interp(
            wavelength,
            template.wavelength_angstrom,
            template.flux,
            left=np.nan,
            right=np.nan,
        )
        blue = np.interp(
            wavelength / 2.0,
            template.wavelength_angstrom,
            template.flux,
            left=np.nan,
            right=np.nan,
        )
        leverage[name] = np.divide(
            blue,
            red,
            out=np.full_like(red, np.nan),
            where=np.isfinite(blue) & np.isfinite(red) & (red > 0),
        )

    hot_log = np.nanmean(
        np.vstack([np.log(curves[name]) for name in hot_standard_names]),
        axis=0,
    )
    hot_mean = np.exp(hot_log)
    cool = curves[cool_standard_name]
    log_ratio = hot_log - np.log(cool)
    valid = np.isfinite(log_ratio)
    baseline = (
        valid
        & (wavelength >= baseline_min_angstrom)
        & (wavelength <= baseline_max_angstrom)
    )
    if np.count_nonzero(baseline) < 100:
        raise RuntimeError("Too little common coverage remains in the baseline interval.")

    center = 0.5 * (baseline_min_angstrom + baseline_max_angstrom)
    scale = max(baseline_max_angstrom - baseline_min_angstrom, 1.0)
    x = (wavelength - center) / scale
    coefficients = _robust_polyfit(x[baseline], log_ratio[baseline], degree=2)
    fitted_log_ratio = np.polyval(coefficients, x)
    residual_log = log_ratio - fitted_log_ratio
    sigma_pixels = max(1.0, smoothing_angstrom / step)
    residual_smooth = gaussian_filter1d(
        _interpolate_finite(residual_log),
        sigma=sigma_pixels,
        mode="nearest",
    )
    baseline_residual = residual_log[baseline]
    scatter_log = _robust_sigma(baseline_residual)
    threshold_log = max(3.0 * scatter_log, float(np.log1p(minimum_fractional_excess)))

    search = valid & (wavelength >= baseline_max_angstrom)
    formal_index = _first_persistent_crossing(
        wavelength,
        np.abs(residual_smooth),
        search,
        threshold_log,
        persistence_angstrom,
    )
    formal_candidate = None if formal_index is None else float(wavelength[formal_index])
    formal_sign = None
    if formal_index is not None:
        formal_sign = "positive" if residual_smooth[formal_index] > 0 else "negative"

    hot_leverage = np.nanmean(
        np.vstack([leverage[name] for name in hot_standard_names]),
        axis=0,
    )
    expected_blue_leverage = hot_leverage - leverage[cool_standard_name]
    red_test = (
        np.isfinite(expected_blue_leverage)
        & (wavelength >= warning_start_angstrom)
    )
    leverage_is_positive = (
        np.count_nonzero(red_test) >= 20
        and float(np.nanmedian(expected_blue_leverage[red_test])) > 0
    )
    positive_index = _first_persistent_crossing(
        wavelength,
        residual_smooth,
        search,
        threshold_log,
        persistence_angstrom,
    )
    physically_consistent = positive_index is not None and leverage_is_positive
    empirical_onset = (
        float(wavelength[positive_index]) if physically_consistent else None
    )

    if common_max < warning_start_angstrom + persistence_angstrom:
        status = "insufficient_coverage"
    elif physically_consistent:
        status = "detected"
    else:
        status = "undetermined"

    notes = [
        "The warning threshold is conservative and is not an empirical cutoff.",
        "The standard-star comparison is differential and cannot fully remove airmass, atmospheric extinction, seeing, or chromatic slit loss.",
        "A confirmed onset requires a paired observation with and without a characterized order-sorting filter in an unchanged optical setup.",
        "All red data are retained; the second-order flag is separate from the bad-pixel mask.",
    ]
    if formal_sign == "negative":
        notes.append(
            "The first statistically persistent response change had the opposite sign from the expected hot-star second-order excess and was rejected."
        )
    if common_max < 7600.0:
        notes.append(
            "The common spectrum ends before 7600 Angstrom, so it provides little leverage on second order from wavelengths near 3800 Angstrom."
        )

    assessment = SecondOrderAssessment(
        status=status,
        warning_start_angstrom=float(warning_start_angstrom),
        empirical_onset_angstrom=empirical_onset,
        formal_change_candidate_angstrom=formal_candidate,
        formal_change_sign=formal_sign,
        physically_consistent=physically_consistent,
        tested_min_angstrom=float(wavelength[0]),
        tested_max_angstrom=float(wavelength[-1]),
        baseline_min_angstrom=float(baseline_min_angstrom),
        baseline_max_angstrom=float(baseline_max_angstrom),
        baseline_scatter_fraction=float(np.expm1(scatter_log)),
        detection_threshold_fraction=float(np.expm1(threshold_log)),
        balmer_second_order_marker_angstrom=SECOND_ORDER_BALMER_MARKER_ANGSTROM,
        hot_standard_names=tuple(hot_standard_names),
        cool_standard_name=cool_standard_name,
        notes=tuple(notes),
    )
    return SecondOrderStudy(
        assessment=assessment,
        wavelength_angstrom=wavelength,
        standard_responses=curves,
        hot_mean_response=hot_mean,
        cool_response=cool,
        hot_to_cool_ratio=np.exp(log_ratio),
        fitted_baseline_ratio=np.exp(fitted_log_ratio),
        residual_fraction=np.expm1(residual_smooth),
        expected_blue_leverage=expected_blue_leverage,
    )


def write_second_order_products(
    study: SecondOrderStudy,
    output_dir: str | Path,
) -> dict[str, Path]:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    paths = {
        "json": destination / "second_order_assessment.json",
        "csv": destination / "second_order_diagnostic.csv",
        "png": destination / "second_order_diagnostic.png",
    }
    payload = {
        "schemaVersion": 1,
        "assessmentType": "differential-hot-cool-standard-star-second-order-test",
        **asdict(study.assessment),
        "artifacts": {name: str(path) for name, path in paths.items()},
    }
    paths["json"].write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    names = list(study.standard_responses)
    with paths["csv"].open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            [
                "wavelength_angstrom",
                *[f"response_{_safe_stem(name)}" for name in names],
                "hot_mean_response",
                "cool_response",
                "hot_to_cool_ratio",
                "baseline_ratio",
                "residual_fraction",
                "expected_hot_minus_cool_blue_leverage",
            ]
        )
        writer.writerows(
            zip(
                study.wavelength_angstrom,
                *[study.standard_responses[name] for name in names],
                study.hot_mean_response,
                study.cool_response,
                study.hot_to_cool_ratio,
                study.fitted_baseline_ratio,
                study.residual_fraction,
                study.expected_blue_leverage,
            )
        )

    _plot_study(study, paths["png"])
    return paths


def load_second_order_assessment(path: str | Path) -> SecondOrderAssessment:
    source = Path(path).expanduser().resolve()
    payload = json.loads(source.read_text(encoding="utf-8"))
    if payload.get("assessmentType") != "differential-hot-cool-standard-star-second-order-test":
        raise ValueError(f"{source} is not a UVEX second-order assessment.")
    return SecondOrderAssessment(
        status=str(payload["status"]),
        warning_start_angstrom=float(payload["warning_start_angstrom"]),
        empirical_onset_angstrom=_optional_float(payload.get("empirical_onset_angstrom")),
        formal_change_candidate_angstrom=_optional_float(
            payload.get("formal_change_candidate_angstrom")
        ),
        formal_change_sign=payload.get("formal_change_sign"),
        physically_consistent=bool(payload["physically_consistent"]),
        tested_min_angstrom=float(payload["tested_min_angstrom"]),
        tested_max_angstrom=float(payload["tested_max_angstrom"]),
        baseline_min_angstrom=float(payload["baseline_min_angstrom"]),
        baseline_max_angstrom=float(payload["baseline_max_angstrom"]),
        baseline_scatter_fraction=float(payload["baseline_scatter_fraction"]),
        detection_threshold_fraction=float(payload["detection_threshold_fraction"]),
        balmer_second_order_marker_angstrom=float(
            payload["balmer_second_order_marker_angstrom"]
        ),
        hot_standard_names=tuple(payload["hot_standard_names"]),
        cool_standard_name=str(payload["cool_standard_name"]),
        notes=tuple(payload.get("notes", ())),
    )


def _plot_study(study: SecondOrderStudy, path: Path) -> None:
    assessment = study.assessment
    figure, axes = plt.subplots(3, 1, figsize=(14, 11), sharex=True, constrained_layout=True)
    for name, curve in study.standard_responses.items():
        axes[0].plot(study.wavelength_angstrom, curve, linewidth=1.0, label=name)
    axes[0].set_ylabel("Normalized inferred response")
    axes[0].set_title("Differential second-order screening with standard stars")
    axes[0].legend()

    axes[1].plot(
        study.wavelength_angstrom,
        study.hot_to_cool_ratio,
        linewidth=1.0,
        label="Hot mean / cool response",
    )
    axes[1].plot(
        study.wavelength_angstrom,
        study.fitted_baseline_ratio,
        linewidth=1.2,
        linestyle="--",
        label="Pre-risk chromatic baseline",
    )
    axes[1].axhline(1.0, color="0.5", linewidth=0.7)
    axes[1].set_ylabel("Response ratio")
    axes[1].legend()

    axes[2].plot(
        study.wavelength_angstrom,
        100.0 * study.residual_fraction,
        linewidth=1.0,
        label="Residual after baseline",
    )
    threshold = 100.0 * assessment.detection_threshold_fraction
    axes[2].axhline(threshold, color="tab:red", linestyle=":", label="Positive detection gate")
    axes[2].axhline(-threshold, color="tab:red", linestyle=":")
    axes[2].axhline(0.0, color="0.5", linewidth=0.7)
    axes[2].set(xlabel="Wavelength (Angstrom, air)", ylabel="Residual (%)")
    axes[2].legend()

    for axis in axes:
        axis.axvspan(
            assessment.warning_start_angstrom,
            assessment.tested_max_angstrom,
            color="#ff9800",
            alpha=0.10,
        )
        axis.axvline(
            assessment.balmer_second_order_marker_angstrom,
            color="tab:purple",
            linestyle="--",
            linewidth=0.9,
        )
        axis.grid(alpha=0.2)
    figure.suptitle(
        f"Status: {assessment.status}; empirical onset: "
        + (
            "not determined"
            if assessment.empirical_onset_angstrom is None
            else f"{assessment.empirical_onset_angstrom:.1f} A"
        )
    )
    figure.savefig(path, dpi=160)
    plt.close(figure)


def _robust_polyfit(x: np.ndarray, y: np.ndarray, degree: int) -> np.ndarray:
    keep = np.isfinite(x) & np.isfinite(y)
    for _ in range(5):
        coefficients = np.polyfit(x[keep], y[keep], degree)
        residual = y - np.polyval(coefficients, x)
        sigma = _robust_sigma(residual[keep])
        if not np.isfinite(sigma) or sigma <= 0:
            break
        updated = keep & (np.abs(residual) <= 4.0 * sigma)
        if np.array_equal(updated, keep) or np.count_nonzero(updated) < degree + 3:
            break
        keep = updated
    return coefficients


def _first_persistent_crossing(
    wavelength: np.ndarray,
    signal: np.ndarray,
    search: np.ndarray,
    threshold: float,
    persistence_angstrom: float,
) -> int | None:
    indices = np.flatnonzero(search & np.isfinite(signal))
    if indices.size < 2:
        return None
    step = float(np.median(np.diff(wavelength)))
    window = max(3, int(round(persistence_angstrom / step)))
    above = np.zeros(signal.size, dtype=float)
    above[indices] = signal[indices] > threshold
    fraction = np.convolve(above, np.ones(window), mode="same") / window
    median = gaussian_filter1d(_interpolate_finite(signal), sigma=max(1.0, window / 6.0))
    candidates = indices[(fraction[indices] >= 0.8) & (median[indices] > threshold)]
    if candidates.size == 0:
        return None
    return int(candidates[0])


def _interpolate_finite(values: np.ndarray) -> np.ndarray:
    indices = np.arange(values.size)
    good = np.flatnonzero(np.isfinite(values))
    if good.size < 2:
        raise RuntimeError("At least two finite values are needed for interpolation.")
    return np.interp(indices, good, values[good])


def _robust_sigma(values: np.ndarray) -> float:
    finite = np.asarray(values, dtype=float)
    finite = finite[np.isfinite(finite)]
    if finite.size == 0:
        return float("nan")
    center = float(np.median(finite))
    return 1.4826 * float(np.median(np.abs(finite - center)))


def _optional_float(value) -> float | None:
    return None if value is None else float(value)


def _safe_stem(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip()).strip("._")
    return cleaned or "standard"
