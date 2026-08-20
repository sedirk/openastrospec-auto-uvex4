from __future__ import annotations

import csv
from pathlib import Path
import re
from typing import Any

from astropy import units as u
from astropy.io import fits
from astropy.nddata import StdDevUncertainty
import numpy as np

from .models import ReductionResult


def make_spectrum(result: ReductionResult):
    try:
        from specutils import Spectrum1D as SpectrumClass
    except ImportError:
        from specutils import Spectrum as SpectrumClass

    if result.wavelength is None:
        spectral_axis = np.arange(result.flux.size, dtype=float) * u.pix
    else:
        spectral_axis = result.wavelength.wavelength_angstrom * u.AA
    return SpectrumClass(
        spectral_axis=spectral_axis,
        flux=result.flux * u.adu,
        uncertainty=StdDevUncertainty(result.uncertainty * u.adu),
        mask=result.mask,
        meta={"header": result.header.copy()},
    )


def read_spectrum(path: str | Path):
    """Read a UVEX-ADV table product back into Spectrum1D without losing its mask."""
    file_path = Path(path).expanduser().resolve()
    with fits.open(file_path, memmap=False) as hdul:
        table_hdu = hdul["SPECTRUM"]
        data = table_hdu.data
        axis_name = "WAVELENGTH" if "WAVELENGTH" in data.names else "PIXEL"
        axis_unit = u.Unit(table_hdu.columns[axis_name].unit or "pix")
        flux_unit = u.Unit(table_hdu.columns["FLUX"].unit or "adu")
        spectral_axis = np.asarray(data[axis_name], dtype=float) * axis_unit
        flux = np.asarray(data["FLUX"], dtype=float) * flux_unit
        uncertainty = StdDevUncertainty(
            np.asarray(data["UNCERTAINTY"], dtype=float) * flux_unit
        )
        mask = np.asarray(data["MASK"], dtype=bool)
        header = hdul[0].header.copy()
    try:
        from specutils import Spectrum1D as SpectrumClass
    except ImportError:
        from specutils import Spectrum as SpectrumClass
    return SpectrumClass(
        spectral_axis=spectral_axis,
        flux=flux,
        uncertainty=uncertainty,
        mask=mask,
        meta={"header": header},
    )


def write_products(result: ReductionResult, output_dir: str | Path, target_name: str) -> dict[str, Path]:
    destination = Path(output_dir).expanduser().resolve()
    destination.mkdir(parents=True, exist_ok=True)
    stem = _safe_stem(target_name)
    fits_path = destination / f"{stem}_spectrum.fits"
    csv_path = destination / f"{stem}_spectrum.csv"
    _write_fits(result, fits_path)
    _write_csv(result, csv_path)
    return {"fits": fits_path, "csv": csv_path}


def _write_fits(result: ReductionResult, path: Path) -> None:
    primary_header = _provenance_header(result.header)
    primary_header["PIPELINE"] = ("UVEX-ADV", "Reduction pipeline")
    primary_header["PIPEVER"] = ("0.4.1", "Reduction pipeline version")
    primary_header["EXTRBACK"] = (result.extraction_backend[:68], "Extraction backend")
    primary_header["DISPFLIP"] = (
        result.horizontal_flip_applied,
        "Raw UVEX image horizontally flipped",
    )
    primary_header["WAVECAL"] = (result.wavelength is not None, "Wavelength calibration applied")
    primary_header["FLUXCAL"] = (False, "Absolute flux calibration applied")
    primary_header["NCOMBINE"] = (len(result.source_files), "Combined science exposures")
    for source in result.source_files:
        primary_header.add_history(_fits_history_text(f"SCIENCE: {source}"))
    for warning in result.warnings:
        primary_header.add_history(_fits_history_text(f"WARNING: {warning}"))
    primary = fits.PrimaryHDU(header=primary_header)

    pixel = np.arange(result.flux.size, dtype=np.float64)
    if result.wavelength is None:
        axis_name = "PIXEL"
        axis = pixel
        axis_unit = "pix"
    else:
        axis_name = "WAVELENGTH"
        axis = result.wavelength.wavelength_angstrom.astype(np.float64)
        axis_unit = "Angstrom"
    columns = [fits.Column(name="PIXEL", format="D", unit="pix", array=pixel)]
    if result.wavelength is not None:
        columns.append(fits.Column(name=axis_name, format="D", unit=axis_unit, array=axis))
    columns.extend([
        fits.Column(name="FLUX", format="D", unit="adu", array=result.flux.astype(np.float64)),
        fits.Column(
            name="UNCERTAINTY",
            format="D",
            unit="adu",
            array=result.uncertainty.astype(np.float64),
        ),
        fits.Column(name="MASK", format="L", array=result.mask.astype(bool)),
    ])
    spectrum_hdu = fits.BinTableHDU.from_columns(columns, name="SPECTRUM")
    spectrum_hdu.header["BUNIT"] = "adu"
    spectrum_hdu.header["VOCLASS"] = ("SPECTRUM V1.0", "IVOA Spectrum data model")
    spectrum_hdu.header["AIRORVAC"] = (
        result.wavelength.medium.upper() if result.wavelength else "NONE",
        "Wavelength convention",
    )

    trace_columns = [
        fits.Column(name="PIXEL", format="D", unit="pix", array=pixel),
        fits.Column(name="TRACE_Y", format="D", unit="pix", array=result.trace.centers.astype(float)),
        fits.Column(name="TRACE_SIGMA", format="D", unit="pix", array=result.trace.sigma_pixels.astype(float)),
    ]
    trace_hdu = fits.BinTableHDU.from_columns(trace_columns, name="TRACE")
    trace_hdu.header["TRCMETH"] = result.trace.method[:68]
    if np.isfinite(result.trace.snr):
        trace_hdu.header["TRCSNR"] = result.trace.snr

    hdus: list[Any] = [primary, spectrum_hdu, trace_hdu]
    if result.wavelength is not None:
        solution = result.wavelength
        wave_columns = [
            fits.Column(name="PIXEL", format="D", unit="pix", array=solution.matched_pixels),
            fits.Column(name="WAVELENGTH", format="D", unit="Angstrom", array=solution.matched_wavelengths),
            fits.Column(name="RESIDUAL", format="D", unit="Angstrom", array=solution.residuals_angstrom),
        ]
        wave_hdu = fits.BinTableHDU.from_columns(wave_columns, name="WAVECAL")
        wave_hdu.header["WAVEMETH"] = solution.method[:68]
        wave_hdu.header["WAVEDEG"] = solution.degree
        if np.isfinite(solution.rms_angstrom):
            wave_hdu.header["WAVERMS"] = solution.rms_angstrom
        wave_hdu.header["AIRORVAC"] = solution.medium.upper()
        wave_hdu.header["COEWORD"] = (
            solution.coefficient_order.upper(),
            "Polynomial coefficient order",
        )
        if solution.template_path:
            wave_hdu.header["TMPLFILE"] = (Path(solution.template_path).name[:68], "Stellar template")
        if solution.template_correlation is not None and np.isfinite(
            solution.template_correlation
        ):
            wave_hdu.header["TMPLCORR"] = (
                float(solution.template_correlation),
                "Template correlation coefficient",
            )
        wave_hdu.header["AUTOREV"] = (
            solution.output_reversed,
            "Output reversed after stellar calibration",
        )
        for index, coefficient in enumerate(solution.coefficients):
            wave_hdu.header[f"COEF{index}"] = float(coefficient)
        hdus.append(wave_hdu)
    fits.HDUList(hdus).writeto(path, overwrite=True, checksum=True)


def _write_csv(result: ReductionResult, path: Path) -> None:
    axis = (
        np.arange(result.flux.size, dtype=float)
        if result.wavelength is None
        else result.wavelength.wavelength_angstrom
    )
    axis_name = "pixel" if result.wavelength is None else "wavelength_angstrom"
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow([axis_name, "flux_adu", "uncertainty_adu", "mask"])
        writer.writerows(zip(axis, result.flux, result.uncertainty, result.mask.astype(int)))


def _provenance_header(source: fits.Header) -> fits.Header:
    header = fits.Header()
    structural = {"SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "BZERO", "BSCALE"}
    for card in source.cards:
        if (
            card.keyword in structural
            or card.keyword == ""
            or _is_image_wcs_keyword(card.keyword)
        ):
            continue
        try:
            header.append(card)
        except Exception:
            continue
    return header


def _is_image_wcs_keyword(keyword: str) -> bool:
    return bool(
        re.fullmatch(
            r"(?:WCSAXES|CRPIX\d+|CRVAL\d+|CDELT\d+|CTYPE\d+|CUNIT\d+|"
            r"CD\d+_\d+|PC\d+_\d+|CROTA\d+|LONPOLE|LATPOLE)",
            keyword,
        )
    )


def _safe_stem(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip()).strip("._")
    return cleaned or "uvex"


def _fits_history_text(value: object) -> str:
    """Return an ASCII-safe FITS HISTORY value without losing path provenance."""
    text = str(value).encode("ascii", errors="backslashreplace").decode("ascii")
    return text[:70]
