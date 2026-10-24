from __future__ import annotations

import hashlib
from types import SimpleNamespace
from astropy.io import fits
import numpy as np
from PIL import Image
import pytest

from uvex_reduce import library_preview


def capture_plot(monkeypatch):
    captured = {}

    def plot(x, y, **kwargs):
        captured.update(x=np.asarray(x), y=np.asarray(y), **kwargs)
        return Image.new("RGB", (40, 20))

    monkeypatch.setattr(library_preview, "_plot", plot)
    return captured


@pytest.fixture
def calibrated_product(tmp_path):
    from uvex_reduce.calibration import (
        CalibratedSpectrum,
        ReducedSpectrumData,
        _write_calibrated_csv,
        _write_calibrated_fits,
    )

    wavelength = np.arange(4000, 4006, dtype=float)
    mask = np.array([0, 0, 1, 0, 0, 0], dtype=bool)
    reduced = ReducedSpectrumData(
        path=tmp_path / "source.fits", header=fits.Header(), pixel=np.arange(6),
        wavelength_angstrom=wavelength, flux_adu=np.arange(100, 106, dtype=float),
        uncertainty_adu=np.ones(6), mask=mask, exposure_s=10,
    )
    calibrated = CalibratedSpectrum(
        source=reduced, wavelength_angstrom=wavelength,
        relative_flux=np.arange(10, 16, dtype=float), relative_uncertainty=np.ones(6),
        continuum=np.ones(6) * 10, normalized_flux=np.arange(10, 16) / 10,
        normalized_uncertainty=np.ones(6) / 10, response=np.ones(6), mask=mask,
        response_standard="Fixture standard", response_template=tmp_path / "template.dat",
        zero_point=None, second_order_start_angstrom=4004,
        second_order_status="warning", second_order_empirical_onset_angstrom=None,
        second_order_diagnostic_marker_angstrom=None, second_order_assessment_path=None,
    )
    product = tmp_path / "calibrated.fits"
    csv = tmp_path / "calibrated.csv"
    _write_calibrated_fits(calibrated, product)
    _write_calibrated_csv(calibrated, csv)
    return product, csv, calibrated


def test_real_calibration_fits_contract_preserves_mask_units_and_order2_risk(
    calibrated_product, monkeypatch,
):
    source, _, calibrated = calibrated_product
    original = source.read_bytes()
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    np.testing.assert_array_equal(captured["x"], calibrated.wavelength_angstrom)
    assert captured["xlabel"] == "Angstrom"
    assert captured["ylabel"] == "RELATIVE_FLUX (adu/s)"
    assert np.isnan(captured["y"][2])
    assert captured["y"][-1] == calibrated.relative_flux[-1]
    assert "非绝对流量" in preview.caption
    assert "二级光谱风险" in preview.caption and "未裁掉" in preview.caption
    assert source.read_bytes() == original


def test_real_calibration_csv_contract_uses_wavelength_relative_flux_and_mask(
    calibrated_product, monkeypatch,
):
    _, source, calibrated = calibrated_product
    original = source.read_bytes()
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    assert preview.image is not None
    assert captured["xlabel"] == "wavelength_angstrom"
    assert captured["ylabel"] == "relative_flux_adu_per_s"
    np.testing.assert_array_equal(captured["x"], calibrated.wavelength_angstrom)
    assert np.isnan(captured["y"][2])
    assert source.read_bytes() == original


def test_normalized_only_fits_product_is_dimensionless_and_not_absolute(
    calibrated_product, tmp_path, monkeypatch,
):
    source, _, calibrated = calibrated_product
    target = tmp_path / "normalized.fits"
    with fits.open(source) as hdus:
        columns = [hdus["SPECTRUM"].columns[name] for name in
                   ("WAVELENGTH", "NORMALIZED_FLUX", "MASK", "ORDER2_RISK")]
        fits.HDUList([
            fits.PrimaryHDU(header=hdus[0].header.copy()),
            fits.BinTableHDU.from_columns(columns, name="SPECTRUM"),
        ]).writeto(target)
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(target)

    assert captured["ylabel"] == "NORMALIZED_FLUX (dimensionless)"
    assert np.isnan(captured["y"][2])
    assert captured["y"][-1] == calibrated.normalized_flux[-1]
    assert "非绝对流量" in preview.caption


def test_normalized_csv_prefers_wavelength_over_pixel(tmp_path, monkeypatch):
    source = tmp_path / "normalized.csv"
    source.write_text(
        "pixel,wavelength_angstrom,normalized_flux,mask\n"
        "0,4000,1,0\n1,4001,9,true\n2,4002,1.1,0\n", encoding="utf-8",
    )
    captured = capture_plot(monkeypatch)
    library_preview.load_library_preview(source)
    assert captured["xlabel"] == "wavelength_angstrom"
    assert captured["ylabel"] == "normalized_flux"
    np.testing.assert_array_equal(captured["x"], [4000, 4001, 4002])
    assert np.isnan(captured["y"][1])


def test_fits_2d_preview_is_bounded_neutral_and_does_not_modify_source(tmp_path):
    source = tmp_path / "science.fit"
    array = np.arange(1200 * 2000, dtype=np.float32).reshape(1200, 2000)
    fits.PrimaryHDU(array).writeto(source)
    before = hashlib.sha256(source.read_bytes()).hexdigest()

    preview = library_preview.load_library_preview(source)

    assert preview.image is not None
    assert max(preview.image.size) <= 1600
    rgb = np.asarray(preview.image)
    np.testing.assert_array_equal(rgb[..., 0], rgb[..., 1])
    np.testing.assert_array_equal(rgb[..., 1], rgb[..., 2])
    assert "2D" in preview.caption
    assert "SIMPLE" in preview.text
    assert hashlib.sha256(source.read_bytes()).hexdigest() == before
    assert list(tmp_path.iterdir()) == [source]


def test_fits_section_not_full_array_is_sent_to_display_stretch(tmp_path, monkeypatch):
    reads, stretches = [], []

    class Section:
        def __getitem__(self, selection):
            reads.append(selection)
            dimensions = tuple(len(range(*part.indices(size)))
                               for part, size in zip(selection, (3600, 6000))
                               if isinstance(part, slice))
            return np.arange(np.prod(dimensions), dtype=np.float32).reshape(dimensions)

    class HeaderOnlyHdu:
        shape = (3600, 6000)
        header = fits.Header()
        section = Section()

        @property
        def data(self):
            pytest.fail("A library preview must never materialize this full FITS array")

    class Hdus(list):
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

    def stretch(data, **kwargs):
        stretches.append((data.shape, kwargs))
        return SimpleNamespace(image=Image.new("RGB", (40, 20)), low=0, high=1)

    monkeypatch.setattr(fits, "open", lambda *args, **kwargs: Hdus([HeaderOnlyHdu()]))
    monkeypatch.setattr(library_preview, "make_neutral_fits_preview", stretch)

    preview = library_preview.load_library_preview(tmp_path / "large-camera.fit")

    assert reads
    # Columns may be read contiguously then sampled in NumPy to avoid Astropy's
    # expensive per-pixel strided I/O. Selected rows must still be bounded.
    assert all(isinstance(selection[0], int) or selection[0].step == 4 for selection in reads)
    assert stretches == [((900, 1500), {"maximum_dimension": 1600})]
    assert max(stretches[0][0]) <= 1600
    assert "采样 1/4" in preview.caption


@pytest.mark.parametrize("axis_name,unit", [("PIXEL", "pix"), ("WAVELENGTH", "Angstrom")])
def test_fits_spectrum_retains_axis_unit_and_mask(tmp_path, monkeypatch, axis_name, unit):
    source = tmp_path / "product.fits"
    axis = np.arange(6, dtype=float) + (4000 if axis_name == "WAVELENGTH" else 0)
    columns = [
        fits.Column(name=axis_name, format="D", unit=unit, array=axis),
        fits.Column(name="FLUX", format="D", unit="adu", array=np.arange(6, dtype=float)),
        fits.Column(name="UNCERTAINTY", format="D", unit="adu", array=np.ones(6)),
        fits.Column(name="MASK", format="L", array=np.array([0, 0, 1, 0, 0, 0], dtype=bool)),
    ]
    fits.HDUList([
        fits.PrimaryHDU(), fits.BinTableHDU.from_columns(columns, name="SPECTRUM"),
    ]).writeto(source)
    original = source.read_bytes()
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    assert preview.image is not None
    np.testing.assert_array_equal(captured["x"], axis)
    assert np.isnan(captured["y"][2])
    assert np.isfinite(captured["y"][[0, 1, 3, 4, 5]]).all()
    assert captured["xlabel"] == unit
    assert captured["ylabel"] == "adu"
    assert source.read_bytes() == original


def test_primary_1d_array_is_not_claimed_as_wavelength_calibrated(tmp_path, monkeypatch):
    source = tmp_path / "generic-1d.fit"
    hdu = fits.PrimaryHDU(np.arange(12, dtype=float))
    hdu.header["CRVAL1"] = 5000
    hdu.header["CDELT1"] = 2
    hdu.writeto(source)
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    np.testing.assert_array_equal(captured["x"], np.arange(12))
    assert "no WCS applied" in captured["xlabel"]
    assert "未解释波长" in preview.caption


def test_large_primary_1d_preview_is_bounded_and_keeps_original_pixel_indices(
    tmp_path, monkeypatch,
):
    source = tmp_path / "large-1d.fit"
    original = np.arange(40_001, dtype=np.float32)
    fits.PrimaryHDU(original).writeto(source)
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    assert len(captured["x"]) <= 20_000
    np.testing.assert_array_equal(captured["x"], np.arange(0, 40_001, 3))
    np.testing.assert_array_equal(captured["y"], original[::3])
    assert "采样 1/3" in preview.caption


def test_own_spectrum_csv_columns_and_mask_are_supported(tmp_path, monkeypatch):
    source = tmp_path / "star_spectrum.csv"
    source.write_text(
        "wavelength_angstrom,flux_adu,uncertainty_adu,mask\n"
        "4000,100,1,0\n4001,9999,2,1\n4002,110,1,0\n",
        encoding="utf-8-sig",
    )
    original = source.read_bytes()
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    assert preview.image is not None
    assert captured["xlabel"] == "wavelength_angstrom"
    assert captured["ylabel"] == "flux_adu"
    np.testing.assert_array_equal(captured["x"], [4000, 4001, 4002])
    assert np.isnan(captured["y"][1])
    np.testing.assert_array_equal(captured["y"][[0, 2]], [100, 110])
    assert source.read_bytes() == original


def test_light_curve_skips_bad_rows_and_inverts_magnitude_axis(tmp_path, monkeypatch):
    source = tmp_path / "photometry.tsv"
    source.write_text(
        "BJD_TDB\tdifferential_mag\n2450000\t1.2\n"
        "invalid\t1.1\n2450001\tnan\n2450002\t1.3\n",
        encoding="utf-8",
    )
    captured = capture_plot(monkeypatch)

    preview = library_preview.load_library_preview(source)

    assert preview.image is not None
    np.testing.assert_array_equal(captured["x"], [2450000, 2450002])
    np.testing.assert_allclose(captured["y"], [1.2, 1.3])
    assert captured["invert_y"]
    assert "不推断" in preview.text


def test_curve_preview_limits_work_to_twenty_thousand_rows(tmp_path, monkeypatch):
    source = tmp_path / "many-points.csv"
    source.write_text(
        "time,relative_flux\n" + "".join(f"{i},1\n" for i in range(20_001)),
        encoding="utf-8",
    )
    captured = capture_plot(monkeypatch)
    preview = library_preview.load_library_preview(source)
    assert len(captured["x"]) == 20_000
    assert "20000" in preview.caption


def test_text_and_non_curve_csv_have_bounded_read_only_preview(tmp_path):
    source = tmp_path / "evidence.log"
    source.write_text("x" * 40_000, encoding="utf-8")
    preview = library_preview.load_library_preview(source)
    assert preview.image is None
    assert preview.text.startswith("x" * 32_000)
    assert "截断" in preview.text
    assert source.stat().st_size == 40_000
    csv = tmp_path / "metadata.csv"
    csv.write_text("file,quality\nscience.fit,rejected\n", encoding="utf-8")
    result = library_preview.load_library_preview(csv)
    assert result.image is None
    assert "science.fit" in result.text


def test_image_preview_is_memory_only_and_bounded(tmp_path):
    source = tmp_path / "preview.png"
    Image.new("RGB", (2400, 1800), (30, 40, 50)).save(source)
    original = source.read_bytes()
    preview = library_preview.load_library_preview(source)
    assert preview.image.size == (1600, 1200)
    assert source.read_bytes() == original
    assert list(tmp_path.iterdir()) == [source]


def test_agg_plot_renders_without_pyplot_or_tk(tmp_path):
    # No GUI application, display connection, or persistent output is required.
    image = library_preview._plot(
        np.arange(5), [1, 2, np.nan, 4, 5], xlabel="pix", ylabel="adu", title="Fixture",
    )
    assert image.mode == "RGB"
    assert image.width > 100 and image.height > 100
    assert list(tmp_path.iterdir()) == []
