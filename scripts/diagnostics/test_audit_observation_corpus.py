"""Read-only census contracts; synthetic manifests do not assert sky success."""
from pathlib import Path
import tempfile
import unittest

from audit_observation_corpus import census, quantiles, summarize, timestamp


class CorpusTests(unittest.TestCase):
    def document(self, events=()):
        return {"lockedMetadata": {"labels": {"adapter": "real"}},
                "snapshot": {"recentEvents": list(events)}, "plan": {"target": {"name": "Synthetic"}}}

    def event(self, code, second, stage="AcquireG3SlitField"):
        return {"code": code, "stage": stage, "timestampUtc": f"2026-01-01T00:00:{second:02d}+00:00"}

    def test_explicit_real_adapter_required(self):
        self.assertIsNone(summarize({}, Path("a/manifest.json"), "x"))
        doc = self.document()
        doc["lockedMetadata"]["labels"]["adapter"] = "simulator"
        self.assertIsNone(summarize(doc, Path("a/manifest.json"), "x"))

    def test_unknown_timestamp_not_silently_local(self):
        self.assertIsNone(timestamp("2026-01-01T00:00:00"))
        self.assertIsNone(timestamp("bad"))
        self.assertIsNone(timestamp(123))
        self.assertIsNotNone(timestamp("2026-01-01T00:00:00Z"))

    def test_retry_interval_not_inflated_across_attempts(self):
        events = [self.event("STAGE_STARTED", 0), self.event("STAGE_STARTED", 20),
                  self.event("STAGE_COMPLETED", 25), self.event("STAGE_STARTED", 30),
                  self.event("STAGE_COMPLETED", 33)]
        result = summarize(self.document(events), Path("a/manifest.json"), "x")
        self.assertEqual(5, result["stage_completed_seconds"]["AcquireG3SlitField"])
        self.assertEqual(2, result["completed_attempts_in_retained_event_window"]["AcquireG3SlitField"])

    def test_truncated_events_do_not_invent_duration(self):
        result = summarize(self.document([self.event("STAGE_COMPLETED", 25)]), Path("a/manifest.json"), "x")
        self.assertEqual({}, result["stage_completed_seconds"])

    def test_stage_gap_not_claimed_latest_witness_age(self):
        events = [self.event("STAGE_COMPLETED", 10, "AcquireQhyWideField"),
                  self.event("STAGE_STARTED", 25, "PlaceTargetOnSlit")]
        result = summarize(self.document(events), Path("a/manifest.json"), "x")
        self.assertEqual(15, result["qhy_completed_to_placement_started_seconds"])

    def test_negative_or_absent_samples_excluded_from_quantiles(self):
        self.assertEqual({"n": 0}, quantiles([None, -1]))
        self.assertEqual(2, quantiles([0, 4, None, -1])["n"])

    def test_output_never_inside_observation_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with self.assertRaises(ValueError):
                census(root, root / "derived")
            self.assertFalse((root / "derived").exists())

    def test_output_never_overwrites_existing_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "observations"
            output = Path(temp) / "existing"
            root.mkdir()
            output.mkdir()
            with self.assertRaises(ValueError):
                census(root, output)


if __name__ == "__main__":
    unittest.main()
