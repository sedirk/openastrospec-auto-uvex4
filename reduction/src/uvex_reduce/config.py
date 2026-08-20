from __future__ import annotations

from dataclasses import dataclass, field
import glob
from pathlib import Path
from typing import Any
import tomllib


@dataclass(slots=True)
class InputConfig:
    root: Path
    science: list[str]
    target_name: str | None = None
    arc: list[str] = field(default_factory=list)
    flat: list[str] = field(default_factory=list)
    dark: list[str] = field(default_factory=list)
    bias: list[str] = field(default_factory=list)
    output_dir: Path = Path("output")


@dataclass(slots=True)
class DetectorConfig:
    gain_e_per_adu: float = 1.0
    read_noise_e: float = 6.5
    saturation_adu: float = 65_520.0


@dataclass(slots=True)
class OrientationConfig:
    dispersion_axis: int = 1
    red_left_blue_right: bool = True
    horizontal_flip: bool = True


@dataclass(slots=True)
class PreprocessConfig:
    align_frames: bool = True
    # Long-slit targets often provide a high-S/N spatial trace but too little
    # spectral structure for a trustworthy dispersion correlation.  Keep the
    # two axes independently selectable so a faint continuum cannot acquire a
    # large, spurious x shift from detector/background structure.
    align_spatial: bool = True
    align_dispersion: bool = True
    maximum_shift_pixels: float = 40.0
    sigma_clip: float = 5.0
    # Median is the conservative default.  After per-frame cosmic-ray cleaning,
    # a sigma-clipped mean retains more of the S/N available in short sequences
    # (especially quantised 8-bit captures).
    combine_method: str = "median"
    use_bias: bool = True
    use_dark: bool = True
    use_flat: bool = True
    cosmic_ray_clean: bool = False
    maximum_dark_temperature_delta_c: float = 3.0
    allow_dark_exposure_scaling: bool = False
    normalize_science_exposure: bool = True
    reject_mixed_camera_gain: bool = True
    reject_unalignable_frames: bool = True
    minimum_alignment_confidence: float = 0.10
    alignment_probe_shift_pixels: float = 90.0
    # Some ToupTek/ATR585M SDK frames contain a 64-column cyclic displacement.
    # Only explicitly listed files use ``sdk_wrap_fix_direction``; silently
    # rolling every frame, or assuming that every incident has the same sign,
    # would corrupt unaffected data.
    sdk_wrap_fix_files: list[str] = field(default_factory=list)
    sdk_wrap_shift_pixels: int = 64
    sdk_wrap_fix_direction: str = "left"
    # Never mutate science data from a heuristic alone.  Known camera-buffer
    # incidents must be commissioned as an explicit per-file list.
    auto_detect_sdk_wrap: bool = False
    sdk_wrap_seam_sigma: float = 4.0
    minimum_flat_frames: int = 2
    maximum_flat_saturation_fraction: float = 0.01
    minimum_flat_valid_fraction: float = 0.50


@dataclass(slots=True)
class ExtractionConfig:
    backend: str = "aspired"
    allow_native_fallback: bool = True
    trace_bins: int = 24
    trace_half_width: int = 40
    trace_degree: int = 2
    minimum_trace_snr: float = 3.0
    minimum_valid_trace_bins: int = 5
    manual_trace_y: float | None = None
    allow_low_confidence_trace: bool = False
    aperture_half_width: int = 16
    sky_separation: int = 8
    sky_width: int = 14
    optimal: bool = True
    aspired_faint_percent: float = 20.0
    minimum_valid_fraction: float = 0.5
    maximum_optimal_to_boxcar_peak_ratio: float = 8.0
    maximum_optimal_to_boxcar_noise_ratio: float = 4.0


@dataclass(slots=True)
class WavelengthConfig:
    mode: str = "none"
    polynomial_degree: int = 2
    known_pixels: list[float] = field(default_factory=list)
    known_angstroms: list[float] = field(default_factory=list)
    atlas_elements: list[str] = field(default_factory=list)
    minimum_angstrom: float = 3_500.0
    maximum_angstrom: float = 7_500.0
    peak_prominence: float = 3.0
    peak_distance_pixels: float = 6.0
    medium: str = "air"
    minimum_matched_lines: int = 4
    maximum_rms_angstrom: float = 5.0
    minimum_pixel_span_fraction: float = 0.2
    template_directory: Path | None = None
    template_path: Path | None = None
    solution_path: Path | None = None
    template_star: str | None = None
    minimum_template_correlation: float = 0.35
    stellar_feature_prominence: float = 0.02
    stellar_feature_tolerance_pixels: float = 40.0
    minimum_wavelength_span_angstrom: float = 2_500.0
    minimum_abs_dispersion_angstrom_per_pixel: float = 0.3
    maximum_abs_dispersion_angstrom_per_pixel: float = 2.0
    auto_reverse_output: bool = True


@dataclass(slots=True)
class PipelineConfig:
    inputs: InputConfig
    detector: DetectorConfig = field(default_factory=DetectorConfig)
    orientation: OrientationConfig = field(default_factory=OrientationConfig)
    preprocess: PreprocessConfig = field(default_factory=PreprocessConfig)
    extraction: ExtractionConfig = field(default_factory=ExtractionConfig)
    wavelength: WavelengthConfig = field(default_factory=WavelengthConfig)


def load_config(path: str | Path) -> PipelineConfig:
    config_path = Path(path).expanduser().resolve()
    with config_path.open("rb") as stream:
        raw = tomllib.load(stream)

    allowed_sections = {
        "inputs",
        "detector",
        "orientation",
        "preprocess",
        "extraction",
        "wavelength",
    }
    unknown_sections = sorted(set(raw) - allowed_sections)
    if unknown_sections:
        raise ValueError(f"Unknown top-level configuration section(s): {', '.join(unknown_sections)}")

    base = config_path.parent
    input_raw = raw.get("inputs", {})
    if not isinstance(input_raw, dict):
        raise TypeError("[inputs] must be a TOML table.")
    input_keys = {field.name for field in InputConfig.__dataclass_fields__.values()}
    unknown_inputs = sorted(set(input_raw) - input_keys)
    if unknown_inputs:
        raise ValueError(f"Unknown InputConfig option(s): {', '.join(unknown_inputs)}")
    root = _resolve_path(base, input_raw.get("root", "."))
    output_dir = _resolve_path(base, input_raw.get("output_dir", "output"))
    inputs = InputConfig(
        root=root,
        science=_string_list(input_raw.get("science", [])),
        target_name=input_raw.get("target_name"),
        arc=_string_list(input_raw.get("arc", [])),
        flat=_string_list(input_raw.get("flat", [])),
        dark=_string_list(input_raw.get("dark", [])),
        bias=_string_list(input_raw.get("bias", [])),
        output_dir=output_dir,
    )
    if not inputs.science:
        raise ValueError("[inputs].science must contain at least one FITS path or glob.")

    wavelength = _construct(WavelengthConfig, raw.get("wavelength", {}))
    if wavelength.template_directory is not None:
        wavelength.template_directory = _resolve_path(base, wavelength.template_directory)
    if wavelength.template_path is not None:
        wavelength.template_path = _resolve_path(base, wavelength.template_path)
    if wavelength.solution_path is not None:
        wavelength.solution_path = _resolve_path(base, wavelength.solution_path)

    config = PipelineConfig(
        inputs=inputs,
        detector=_construct(DetectorConfig, raw.get("detector", {})),
        orientation=_construct(OrientationConfig, raw.get("orientation", {})),
        preprocess=_construct(PreprocessConfig, raw.get("preprocess", {})),
        extraction=_construct(ExtractionConfig, raw.get("extraction", {})),
        wavelength=wavelength,
    )
    _validate(config)
    return config


def expand_patterns(root: Path, patterns: list[str]) -> list[Path]:
    paths: set[Path] = set()
    for pattern in patterns:
        candidate = Path(pattern).expanduser()
        if candidate.is_absolute():
            matches = (
                (Path(match) for match in glob.glob(str(candidate), recursive=True))
                if any(char in pattern for char in "*?[")
                else [candidate]
            )
        else:
            matches = root.glob(pattern)
        for match in matches:
            if match.is_file() and match.suffix.lower() in {".fit", ".fits", ".fts"}:
                paths.add(match.resolve())
    return sorted(paths, key=lambda item: str(item).lower())


def _resolve_path(base: Path, value: str | Path) -> Path:
    path = Path(value).expanduser()
    return (path if path.is_absolute() else base / path).resolve()


def _string_list(value: Any) -> list[str]:
    if isinstance(value, str):
        return [value]
    if isinstance(value, list) and all(isinstance(item, str) for item in value):
        return value
    raise TypeError("Input patterns must be a string or a list of strings.")


def _construct(cls: type[Any], values: dict[str, Any]) -> Any:
    if not isinstance(values, dict):
        raise TypeError(f"Configuration section for {cls.__name__} must be a table.")
    known = {field.name for field in cls.__dataclass_fields__.values()}
    unknown = sorted(set(values) - known)
    if unknown:
        raise ValueError(f"Unknown {cls.__name__} option(s): {', '.join(unknown)}")
    return cls(**values)


def _validate(config: PipelineConfig) -> None:
    if config.orientation.dispersion_axis != 1:
        raise ValueError("Phase 1 supports horizontal (x-axis) dispersion only: dispersion_axis=1.")
    if config.detector.gain_e_per_adu <= 0:
        raise ValueError("gain_e_per_adu must be positive.")
    if config.detector.read_noise_e < 0:
        raise ValueError("read_noise_e cannot be negative.")
    if config.detector.saturation_adu <= 0:
        raise ValueError("saturation_adu must be positive.")
    if config.preprocess.maximum_shift_pixels < 0:
        raise ValueError("maximum_shift_pixels cannot be negative.")
    if config.preprocess.sigma_clip <= 0:
        raise ValueError("preprocess.sigma_clip must be positive.")
    if config.preprocess.combine_method.lower() not in {"median", "mean"}:
        raise ValueError("preprocess.combine_method must be 'median' or 'mean'.")
    if config.preprocess.sdk_wrap_shift_pixels <= 0:
        raise ValueError("preprocess.sdk_wrap_shift_pixels must be positive.")
    if config.preprocess.sdk_wrap_fix_direction.lower() not in {"left", "right"}:
        raise ValueError(
            "preprocess.sdk_wrap_fix_direction must be 'left' or 'right'."
        )
    if not 0 <= config.preprocess.minimum_alignment_confidence <= 1:
        raise ValueError("preprocess.minimum_alignment_confidence must be in [0, 1].")
    if (
        config.preprocess.alignment_probe_shift_pixels
        < config.preprocess.maximum_shift_pixels
    ):
        raise ValueError(
            "preprocess.alignment_probe_shift_pixels cannot be smaller than "
            "maximum_shift_pixels."
        )
    if config.preprocess.sdk_wrap_seam_sigma <= 0:
        raise ValueError("preprocess.sdk_wrap_seam_sigma must be positive.")
    if config.preprocess.minimum_flat_frames < 1:
        raise ValueError("preprocess.minimum_flat_frames must be at least 1.")
    if not 0 <= config.preprocess.maximum_flat_saturation_fraction < 1:
        raise ValueError(
            "preprocess.maximum_flat_saturation_fraction must be in [0, 1)."
        )
    if not 0 < config.preprocess.minimum_flat_valid_fraction <= 1:
        raise ValueError("preprocess.minimum_flat_valid_fraction must be in (0, 1].")
    if config.extraction.trace_bins < 4:
        raise ValueError("trace_bins must be at least 4.")
    if config.extraction.trace_degree < 0:
        raise ValueError("trace_degree cannot be negative.")
    if config.extraction.trace_half_width < 2:
        raise ValueError("trace_half_width must be at least 2 pixels.")
    if config.extraction.aperture_half_width < 1:
        raise ValueError("aperture_half_width must be positive.")
    if config.extraction.sky_separation < 0 or config.extraction.sky_width < 1:
        raise ValueError("sky_separation must be nonnegative and sky_width must be positive.")
    if not 0 <= config.extraction.aspired_faint_percent < 90:
        raise ValueError("aspired_faint_percent must be in [0, 90).")
    if config.extraction.backend.lower() not in {"aspired", "native", "native-boxcar"}:
        raise ValueError("extraction.backend must be 'aspired' or 'native'.")
    if not 0 < config.extraction.minimum_valid_fraction <= 1:
        raise ValueError("minimum_valid_fraction must be in (0, 1].")
    if config.extraction.maximum_optimal_to_boxcar_peak_ratio <= 1:
        raise ValueError("maximum_optimal_to_boxcar_peak_ratio must exceed 1.")
    if config.extraction.maximum_optimal_to_boxcar_noise_ratio <= 1:
        raise ValueError("maximum_optimal_to_boxcar_noise_ratio must exceed 1.")
    if config.wavelength.polynomial_degree < 1:
        raise ValueError("wavelength.polynomial_degree must be at least 1.")
    if config.wavelength.minimum_angstrom >= config.wavelength.maximum_angstrom:
        raise ValueError("minimum_angstrom must be lower than maximum_angstrom.")
    if config.wavelength.medium.lower() not in {"air", "vacuum", "unknown"}:
        raise ValueError("wavelength.medium must be 'air', 'vacuum', or 'unknown'.")
    if config.wavelength.minimum_matched_lines < config.wavelength.polynomial_degree + 1:
        raise ValueError("minimum_matched_lines must exceed the polynomial degree.")
    if config.wavelength.maximum_rms_angstrom <= 0:
        raise ValueError("maximum_rms_angstrom must be positive.")
    if not 0 < config.wavelength.minimum_pixel_span_fraction <= 1:
        raise ValueError("minimum_pixel_span_fraction must be in (0, 1].")
    if not -1 <= config.wavelength.minimum_template_correlation <= 1:
        raise ValueError("minimum_template_correlation must be in [-1, 1].")
    if config.wavelength.stellar_feature_prominence <= 0:
        raise ValueError("stellar_feature_prominence must be positive.")
    if config.wavelength.stellar_feature_tolerance_pixels <= 0:
        raise ValueError("stellar_feature_tolerance_pixels must be positive.")
    if config.wavelength.minimum_wavelength_span_angstrom <= 0:
        raise ValueError("minimum_wavelength_span_angstrom must be positive.")
    if config.wavelength.minimum_abs_dispersion_angstrom_per_pixel <= 0:
        raise ValueError("minimum_abs_dispersion_angstrom_per_pixel must be positive.")
    if (
        config.wavelength.maximum_abs_dispersion_angstrom_per_pixel
        <= config.wavelength.minimum_abs_dispersion_angstrom_per_pixel
    ):
        raise ValueError(
            "maximum_abs_dispersion_angstrom_per_pixel must exceed the minimum."
        )
