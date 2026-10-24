"""Offline geometry unit tests; use the existing star-detection environment."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from astropy.io import fits

from replay_sparse_g3 import apply, extract, inside, native_success, register, run, similarity


def extraction(points):
    return dict(stars=[dict(x=float(p[0]), y=float(p[1])) for p in points])


class ReplayGeometryTests(unittest.TestCase):
    def setUp(self):
        self.points = np.random.default_rng(521).uniform([100, 100], [1800, 950], (12, 2))

    def test_similarity_recovers_known_point_without_fitting_it(self):
        matrix = np.array([[1.0001, -.0002, 7.7], [.0002, 1.0001, 10.1], [0, 0, 1.]])
        fitted = similarity(self.points[:3], apply(matrix, self.points[:3]))
        np.testing.assert_allclose(apply(fitted, self.points[3:]), apply(matrix, self.points[3:]), atol=1e-8)

    def test_field_has_real_independent_validation(self):
        destination = self.points+[6.7, 9.9]
        result = register(extraction(self.points), extraction(destination), 3, 5)
        self.assertTrue(result["accepted"])
        self.assertEqual(3, result["fittedStars"])
        self.assertEqual(9, result["heldOutStars"])
        self.assertLess(max(result["heldOutResiduals"]), 1e-8)

    def test_three_fit_points_cannot_validate_themselves(self):
        result = register(extraction(self.points[:3]), extraction(self.points[:3]+[8, 10]), 2, 5)
        self.assertFalse(result["accepted"])

    def test_wrong_field_is_not_forced_into_mosaic(self):
        unrelated = np.random.default_rng(302).uniform([50, 50], [1850, 1000], (12, 2))
        result = register(extraction(self.points), extraction(unrelated), 3, 5)
        self.assertFalse(result["accepted"])

    def test_incompatible_rotation_is_rejected(self):
        theta = np.deg2rad(37)
        matrix = np.array([[np.cos(theta), -np.sin(theta), 900], [np.sin(theta), np.cos(theta), 0], [0, 0, 1]])
        result = register(extraction(self.points), extraction(apply(matrix, self.points)), 3, 5)
        self.assertFalse(result["accepted"])

    def test_empty_source_list_is_not_matched(self):
        result = register(extraction(np.empty((0, 2))), extraction(self.points), 3, 5)
        self.assertFalse(result["accepted"])

    def test_equally_supported_repeated_pattern_is_ambiguous(self):
        pattern = np.array([[100., 100.], [250., 175.], [175., 300.], [470., 230.], [400., 430.], [230., 520.]])
        duplicated = np.vstack([pattern+[100, 30], pattern+[900, 50]])
        result = register(extraction(pattern), extraction(duplicated), 3, 5)
        self.assertFalse(result["accepted"])
        self.assertTrue(result.get("ambiguous"))
        self.assertGreater(result["equallySupportedDistinctTransforms"], 1)

    def test_fits_extraction_does_not_modify_raw(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)/"raw.fit"
            fits.writeto(path, np.random.default_rng(12).normal(1000, 10, (128, 128)).astype(np.float32))
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            extract(path, 4, None)
            self.assertEqual(before, hashlib.sha256(path.read_bytes()).hexdigest())

    def test_cli_refuses_output_inside_observations(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            obs = base/"observations"
            obs.mkdir()
            source = obs/"raw.fit"
            fits.writeto(source, np.zeros((32, 32), dtype=np.uint16))
            output = obs/"derived"
            result = subprocess.run([sys.executable, str(Path(__file__).with_name("replay_sparse_g3.py")),
                                     "extract", "--observations", str(obs), "--output", str(output),
                                     "--source", str(source)], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(output.exists())

    def test_path_comparison_is_structural_not_prefix(self):
        base = Path.cwd()/"observations"
        self.assertTrue(inside(base/"run"/"evidence", base))
        self.assertFalse(inside(base.parent/"observations-other", base))

    def test_native_true_requires_zero_exit_and_no_timeout(self):
        self.assertTrue(native_success(dict(exitCode=0, timedOut=False, resultText="True\n")))
        self.assertFalse(native_success(dict(exitCode=1, timedOut=False, resultText="True\n")))
        self.assertFalse(native_success(dict(exitCode=0, timedOut=True, resultText="True\n")))
        self.assertFalse(native_success(dict(timedOut=True, resultText="True\n")))
        self.assertFalse(native_success(dict(exitCode=0, timedOut=False, resultText="False\n")))

    def test_original_error_has_failure_receipt_and_post_hash(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            raw = base/"raw.fit"
            fits.writeto(raw, np.zeros((32, 32), dtype=np.uint16))
            out = base/"derived"
            out.mkdir()
            original = ValueError("Original diagnostic failure")
            with patch("replay_sparse_g3.experiment", side_effect=original):
                with self.assertRaises(ValueError) as raised:
                    run(SimpleNamespace(mode="extract"), [raw], out)
            self.assertIs(original, raised.exception)
            receipt = json.loads((out/"report.json").read_text())
            self.assertEqual("Failed", receipt["status"])
            self.assertEqual("ValueError", receipt["failure"]["type"])
            self.assertTrue(receipt["sourcesUnchanged"])
            self.assertEqual(receipt["sourceVerification"][0]["before"], receipt["sourceVerification"][0]["after"])

    def test_missing_input_still_checks_all_requested_inputs(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            raw = base/"raw.fit"
            fits.writeto(raw, np.zeros((32, 32), dtype=np.uint16))
            out = base/"derived"
            out.mkdir()
            with self.assertRaises(FileNotFoundError):
                run(SimpleNamespace(mode="extract"), [base/"missing.fit", raw], out)
            receipt = json.loads((out/"report.json").read_text())
            self.assertEqual(2, len(receipt["sourceVerification"]))
            self.assertIn("verificationError", receipt["sourceVerification"][0])
            self.assertIn("after", receipt["sourceVerification"][1])
            self.assertFalse(receipt["sourcesUnchanged"])
            self.assertEqual("FileNotFoundError", receipt["failure"]["type"])

    def test_receipt_error_does_not_mask_original_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            raw = base/"raw.fit"
            fits.writeto(raw, np.zeros((32, 32), dtype=np.uint16))
            out = base/"derived"
            out.mkdir()
            original = ValueError("Original failure")
            with patch("replay_sparse_g3.experiment", side_effect=original), patch.object(Path, "write_text", side_effect=OSError("receipt unavailable")), patch("sys.stderr"):
                with self.assertRaises(ValueError) as raised:
                    run(SimpleNamespace(mode="extract"), [raw], out)
            self.assertIs(original, raised.exception)

    def test_changed_input_cannot_end_as_success(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            raw = base/"raw.fit"
            fits.writeto(raw, np.zeros((32, 32), dtype=np.uint16))
            out = base/"derived"
            out.mkdir()
            def external_test_mutation(*_):
                raw.write_bytes(raw.read_bytes()+b"test-only external change")
            with patch("replay_sparse_g3.experiment", side_effect=external_test_mutation):
                with self.assertRaises(RuntimeError):
                    run(SimpleNamespace(mode="extract"), [raw], out)
            receipt = json.loads((out/"report.json").read_text())
            self.assertEqual("Failed", receipt["status"])
            self.assertFalse(receipt["sourcesUnchanged"])


if __name__ == "__main__":
    unittest.main()
