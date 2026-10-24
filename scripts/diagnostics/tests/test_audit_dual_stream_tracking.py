"""Offline stream diagnostic guards; run with the existing star-detection Python."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np


SCRIPT = Path(__file__).resolve().parents[1] / "audit_dual_stream_tracking.py"
SPEC = importlib.util.spec_from_file_location("audit_dual_stream_tracking", SCRIPT)
STREAM = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STREAM)
REPO = SCRIPT.parents[2]


class StreamTrackingGuardTests(unittest.TestCase):
    def test_output_must_be_new_resolved_repository_tmp_descendant(self):
        with tempfile.TemporaryDirectory(dir=REPO / "tmp", prefix="stream-guard-test-") as temporary:
            allowed = Path(temporary) / "new-report"
            self.assertEqual(STREAM.validate_output_and_range(allowed, 2, 0), allowed.resolve())
            with self.assertRaisesRegex(ValueError, "new directory"):
                STREAM.validate_output_and_range(Path(temporary), 2, 0)
        for outside in [REPO / "tmp", REPO / "config" / "tmp" / "not-allowed",
                        REPO / "tmp" / ".." / "outside-report"]:
            with self.subTest(outside=outside), self.assertRaisesRegex(ValueError, "new directory"):
                STREAM.validate_output_and_range(outside, 2, 0)

    def test_invalid_frame_count_or_skip_rejected_before_any_input_read(self):
        for frames, skip in [(0, 0), (1, 0), (2, -1)]:
            with self.subTest(frames=frames, skip=skip), self.assertRaisesRegex(ValueError, "frames must"):
                STREAM.validate_output_and_range(REPO / "tmp" / "not-created", frames, skip)
            with self.assertRaisesRegex(ValueError, "frames must"):
                STREAM.select_records([], frames, skip)

    def test_missing_consecutive_metadata_frames_are_not_silently_shortened(self):
        records = [{"frameId": index} for index in range(4)]
        self.assertEqual(STREAM.select_records(records, 2, 1), records[1:3])
        with self.assertRaisesRegex(ValueError, "only 1 exist"):
            STREAM.select_records(records, 2, 3)

    def test_missing_raw_source_fails_without_creating_or_replacing_it(self):
        with tempfile.TemporaryDirectory(dir=REPO / "tmp", prefix="stream-guard-test-") as temporary:
            missing = Path(temporary) / "not-acquired.fits"
            with self.assertRaises(FileNotFoundError):
                STREAM.extract({"fitsPath": str(missing), "sha256": "A" * 64})
            self.assertFalse(missing.exists())

    def test_heldout_stars_never_enter_any_fit_and_bad_holdout_rejects(self):
        xy = np.array([(100 + x * 100, 100 + y * 100) for y in range(5) for x in range(6)], dtype=float)
        current = xy + (3, 4)
        current[np.arange(len(xy)) % 3 == 0] += (4, 0)
        reference = {"xy": xy, "shape": (2180, 3856)}
        heldout_centred = xy[np.arange(len(xy)) % 3 == 0] - (1928, 1090)
        original_fit = STREAM.fit
        with patch.object(STREAM, "fit", wraps=original_fit) as spy:
            result = STREAM.register(reference, {"xy": current, "shape": (2180, 3856)})
        self.assertFalse(result["accepted"])
        self.assertEqual(result["reason"], "independent_validation_failed")
        self.assertAlmostEqual(result["centreTranslationXPixels"], 3, places=7)
        self.assertAlmostEqual(result["centreTranslationYPixels"], 4, places=7)
        self.assertGreater(spy.call_count, 1)
        for call in spy.call_args_list:
            source = call.args[0]
            self.assertFalse(np.any(np.all(source[:, None, :] == heldout_centred[None, :, :], axis=2)))

    def test_same_field_positive_and_disjoint_random_field_negative(self):
        rng = np.random.default_rng(20261024)
        xy = rng.uniform((50, 50), (3800, 2120), (100, 2))
        reference = {"xy": xy, "shape": (2180, 3856)}
        positive = STREAM.register(reference, {"xy": xy + (2, 3), "shape": (2180, 3856)})
        wrong_field = rng.uniform((50, 50), (3800, 2120), (100, 2))
        negative = STREAM.register(reference, {"xy": wrong_field, "shape": (2180, 3856)})
        self.assertTrue(positive["accepted"])
        self.assertFalse(negative["accepted"])


if __name__ == "__main__":
    unittest.main()
