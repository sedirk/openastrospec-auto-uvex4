"""Small image-only invariants for the exploratory tracking audit."""
import unittest

import numpy as np

from audit_tracking_20261024 import forced, template_comparison


class TrackingAuditTests(unittest.TestCase):
    @staticmethod
    def image(stars):
        rng = np.random.default_rng(42)
        image = rng.normal(1000, 5, (128, 128))
        yy, xx = np.mgrid[:128, :128]
        for x, y, amplitude in stars:
            image += amplitude*np.exp(-((xx-x)**2+(yy-y)**2)/(2*2.5**2))
        return np.ascontiguousarray(image, dtype=np.float32)

    def test_forced_aperture_does_not_snap_to_a_brighter_neighbour(self):
        point = [60., 60.]
        result = forced(self.image([(60, 60, 50), (78, 60, 10000)]), point, 7)
        self.assertEqual(point, result["position"])
        self.assertGreater(result["backgroundOnlySnr"], 5)
        self.assertFalse(result["centroidAuthority"])
        self.assertFalse(result["identityConfirmed"])
        self.assertFalse(result["photonNoiseCalibrated"])

    def test_template_cannot_chase_a_bright_source_outside_the_small_search(self):
        original = self.image([(60, 60, 300)])
        current = self.image([(62, 60, 250), (82, 60, 10000)])
        result = template_comparison(original, current, [60., 60.], [60., 60.])
        self.assertLessEqual(max(abs(x) for x in result["boundedOffsetPixels"]), 4)
        self.assertFalse(result["positionAuthority"])
        self.assertFalse(result["useForMotion"])

    def test_signal_absence_does_not_create_infinite_snr_or_identity(self):
        result = forced(np.zeros((128, 128), dtype=np.float32), [64., 64.], 7)
        self.assertEqual(0, result["forcedFluxAdu"])
        self.assertIsNone(result["backgroundOnlySnr"])
        self.assertFalse(result["identityConfirmed"])


if __name__ == "__main__":
    unittest.main()
