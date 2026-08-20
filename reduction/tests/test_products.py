from pathlib import Path

from astropy.io import fits
import numpy as np

from uvex_reduce.models import ReductionResult, TraceResult, WavelengthSolution
from uvex_reduce.products import make_spectrum, read_spectrum, write_products
from uvex_reduce.wavelength import _load_solution_file


def _result() -> ReductionResult:
    size = 50
    header = fits.Header()
    header["OBJECT"] = "Synthetic"
    header["CTYPE1"] = "RA---TAN"
    trace = TraceResult(
        centers=np.full(size, 20.0),
        sigma_pixels=np.full(size, 3.0),
        valid_bins=10,
        snr=12.0,
        method="test",
    )
    return ReductionResult(
        source_files=[Path("science-1.fit")],
        image=np.zeros((40, size)),
        header=header,
        flux=np.linspace(10.0, 20.0, size),
        uncertainty=np.full(size, 2.0),
        mask=np.arange(size) % 7 == 0,
        trace=trace,
        shifts=[],
        wavelength=None,
        extraction_backend="native-boxcar",
        horizontal_flip_applied=True,
    )


def test_spectrum1d_and_fits_roundtrip_preserve_mask(tmp_path: Path) -> None:
    result = _result()
    spectrum = make_spectrum(result)
    paths = write_products(result, tmp_path, "Synthetic")
    restored = read_spectrum(paths["fits"])

    np.testing.assert_array_equal(spectrum.mask, result.mask)
    np.testing.assert_array_equal(restored.mask, result.mask)
    assert restored.spectral_axis.unit.to_string() == "pix"
    with fits.open(paths["fits"]) as hdul:
        hdul.verify("exception")
        assert hdul["SPECTRUM"].columns.names == ["PIXEL", "FLUX", "UNCERTAINTY", "MASK"]
        assert "CTYPE1" not in hdul[0].header
        assert "CTYPE1" not in hdul["SPECTRUM"].header


def test_written_wavelength_solution_can_be_transferred(tmp_path: Path) -> None:
    result = _result()
    pixels = np.asarray([5.0, 20.0, 35.0, 45.0])
    wavelengths = 4000.0 + 2.0 * pixels
    result.wavelength = WavelengthSolution(
        wavelength_angstrom=4000.0 + 2.0 * np.arange(result.flux.size),
        coefficients=np.asarray([2.0, 4000.0]),
        matched_pixels=pixels,
        matched_wavelengths=wavelengths,
        residuals_angstrom=np.zeros(pixels.size),
        rms_angstrom=0.2,
        degree=1,
        method="stellar-template-balmer",
        medium="air",
        template_path="p_b8v.dat",
        template_correlation=0.8,
        output_reversed=True,
    )
    path = write_products(result, tmp_path, "Reference")["fits"]

    restored = _load_solution_file(path, result.flux.size)

    np.testing.assert_allclose(restored.wavelength_angstrom, result.wavelength.wavelength_angstrom)
    assert restored.output_reversed
    assert restored.template_correlation == 0.8
    assert restored.method.startswith("transferred-")


def test_fits_history_accepts_non_ascii_source_paths(tmp_path: Path) -> None:
    result = _result()
    result.source_files = [Path("20260506凌晨") / "Arcturus" / "science.fit"]

    path = write_products(result, tmp_path, "Arcturus")["fits"]

    with fits.open(path) as hdul:
        history = "\n".join(str(item) for item in hdul[0].header["HISTORY"])
    assert "20260506\\u51cc\\u6668" in history
    assert "science.fit" in history
