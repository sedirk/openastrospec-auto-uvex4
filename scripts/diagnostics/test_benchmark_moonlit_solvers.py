"""Offline benchmark controls; run in the existing SEP runtime."""
import json
import math
from pathlib import Path
import tempfile
import unittest

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy import ndimage

from benchmark_moonlit_solvers import (angular_distance, bin_mean, filter_rows,
    parse_pl3, prepare_variants, sha, validate_output, write_json)
from validate_moonlit_solvers import check_candidate, point_render, unique_matches


class MoonBenchmarkTests(unittest.TestCase):
    def test_bin_mean_preserves_flux_surface_brightness_and_support(self):
        a = np.arange(48, dtype=np.float32).reshape(6, 8)
        b = bin_mean(a, 2)
        self.assertEqual(b.shape, (3, 4))
        self.assertAlmostEqual(float(b.mean()), float(a.mean()))
        self.assertAlmostEqual(float(b[0, 0]), 4.5)
        with self.assertRaises(ValueError):
            bin_mean(a, 4)

    def test_pixel_center_mapping_and_nonfinite_shape_rejection(self):
        rows = [dict(x=10., y=12., flux=50., fwhm=3.),
                dict(x=20., y=22., flux=100., fwhm=float("nan"))]
        kept = filter_rows(rows, (60, 80), 2)
        self.assertEqual(len(kept), 1)
        self.assertEqual((kept[0]["x"], kept[0]["y"]), (20.5, 24.5))
        self.assertEqual(rows[0]["x"], 10.)

    def test_separable_filter_preserves_noise_normalization(self):
        a = np.random.default_rng(42).normal(size=(30, 50))
        sigma, radius = 2, 8
        y, x = np.mgrid[-radius:radius+1, -radius:radius+1]
        k = np.exp(-(x*x+y*y)/(2*sigma*sigma))
        k /= np.sqrt(np.sum(k*k))
        expected = ndimage.convolve(a, k, mode="reflect")
        actual = ndimage.gaussian_filter(a, sigma, radius=radius)*k.sum()
        np.testing.assert_allclose(actual, expected, atol=1e-12)

    def test_solver_true_alone_is_not_accepted(self):
        sample = dict(ra=45., dec=38., scale=.384)
        def result(ra=45., scale=.384, stars=7):
            return f"True\n{math.radians(ra)},{math.radians(38)}\n{206264.806247/scale},100\nmethod\n0\n0\n0,0.2,20000,20,{stars}\n"
        self.assertTrue(parse_pl3(result(), sample, 1)["plausible"])
        self.assertFalse(parse_pl3(result(149.), sample, 1)["plausible"])
        self.assertFalse(parse_pl3(result(scale=1.2), sample, 1)["plausible"])
        self.assertFalse(parse_pl3(result(stars=3), sample, 1)["plausible"])
        self.assertTrue(parse_pl3("True\nmissing", sample, 1)["parseError"])

    def test_angular_wrap(self):
        self.assertAlmostEqual(angular_distance(359.9, 0, .1, 0), .2)

    def test_many_catalogue_rows_cannot_claim_one_detection_repeatedly(self):
        pairs = unique_matches([[10, 10], [10.2, 10.1], [70, 80]], [[10, 10], [70, 80]])
        self.assertEqual(len(pairs), 2)
        self.assertEqual(len({p["detectionIndex"] for p in pairs}), 2)

    def test_point_render_preserves_measured_centroid_not_a_brightest_pixel(self):
        image = point_render([dict(x=40.3, y=51.7)], (100, 100))-1000
        y, x = ndimage.center_of_mass(image)
        self.assertAlmostEqual(x, 40.3, places=3)
        self.assertAlmostEqual(y, 51.7, places=3)

    def test_output_rejects_archive_source_or_repo_root(self):
        root = Path(__file__).resolve().parents[2]
        self.assertEqual(validate_output(root/"tmp/moon-test"), root/"tmp/moon-test")
        for path in (root, root/"tmp", root/"docs/report", root/"tmp/../src/x"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                validate_output(path)

    def test_derived_variants_never_change_original_pixels_or_headers(self):
        with tempfile.TemporaryDirectory() as t:
            root = Path(t)
            raw = root/"raw.fit"
            header = fits.Header()
            header["OBJECT"] = "synthetic regression only"
            fits.writeto(raw, np.full((128, 128), 25000, dtype=np.uint16), header)
            digest = sha(raw)
            sample = dict(path=str(raw), sha256=digest, ra=45., dec=38.)
            _, _, _, variants = prepare_variants(sample, root/"derived")
            self.assertEqual(sha(raw), digest)
            self.assertEqual(sha(variants["original"]), digest)
            self.assertTrue(fits.getheader(variants["background"])["HINTONLY"])
            self.assertEqual(fits.getdata(variants["bin2_raw"]).shape, (64, 64))

    def test_five_raw_catalogue_positions_with_spread_pass_but_four_do_not(self):
        with tempfile.TemporaryDirectory() as t:
            path = Path(t)/"solution.wcs"
            w = WCS(naxis=2)
            w.wcs.crpix = [500, 400]
            w.wcs.crval = [45, 38]
            w.wcs.cdelt = [-.384/3600, .384/3600]
            w.wcs.ctype = ["RA---TAN", "DEC--TAN"]
            fits.writeto(path, np.zeros((1, 1)), w.to_header())
            points = np.array([[100, 100], [300, 600], [800, 200], [650, 650], [400, 320]])
            stars = [dict(ra=float(p[0]), dec=float(p[1])) for p in w.all_pix2world(points, 0)]
            rows = [dict(x=float(p[0]), y=float(p[1])) for p in points]
            record = dict(wcsPath=str(path), factor=1, plausible=True)
            good = check_candidate(record, dict(shape=[800, 1000]), dict(test=rows), stars)
            bad = check_candidate(record, dict(shape=[800, 1000]), dict(test=rows[:4]), stars)
            self.assertTrue(good["catalogueCrossChecked"])
            self.assertFalse(bad["catalogueCrossChecked"])
            self.assertFalse(good["independentFit"])

    def test_json_rejects_nan_instead_of_silently_hiding_bad_measurement(self):
        with tempfile.TemporaryDirectory() as t:
            path = Path(t)/"result.json"
            with self.assertRaises(ValueError):
                write_json(path, {"bad": float("nan")})
            write_json(path, {"count": 2})
            self.assertEqual(json.loads(path.read_text()), {"count": 2})


if __name__ == "__main__":
    unittest.main()
