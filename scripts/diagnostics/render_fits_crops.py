"""Render labelled FITS crops for recorded G3 evidence inspection."""
from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from astropy.io import fits


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frame", action="append", required=True,
                        help="label=path. May be repeated.")
    parser.add_argument("--center", required=True, help="x,y in zero-based FITS pixels")
    parser.add_argument("--half-size", type=int, default=100)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    center = tuple(float(value) for value in args.center.split(",", 1))
    if len(center) != 2 or not all(np.isfinite(center)) or args.half_size < 8:
        parser.error("--center requires finite x,y and --half-size must be >= 8")

    panels = []
    for item in args.frame:
        if "=" not in item:
            parser.error("Each --frame must be label=path")
        label, path = item.split("=", 1)
        data = np.asarray(fits.getdata(path), dtype=np.float64)
        x, y = int(round(center[0])), int(round(center[1]))
        half = args.half_size
        y0, y1 = max(0, y - half), min(data.shape[0], y + half + 1)
        x0, x1 = max(0, x - half), min(data.shape[1], x + half + 1)
        crop = data[y0:y1, x0:x1]
        low, high = np.percentile(crop, [5, 99.7])
        scale = max(1.0, high - low)
        panels.append((label, crop, (x0, x1, y0, y1), low, scale))

    figure, axes = plt.subplots(1, len(panels), figsize=(7 * len(panels), 6), squeeze=False)
    for axis, (label, crop, bounds, low, scale) in zip(axes[0], panels):
        axis.imshow(np.arcsinh(np.maximum(0, (crop - low) / scale)),
                    origin="lower", cmap="gray", interpolation="nearest")
        axis.axvline(center[0] - bounds[0], color="#22d3ee", lw=0.8, ls="--")
        axis.axhline(center[1] - bounds[2], color="#22d3ee", lw=0.8, ls="--")
        axis.set_title(label)
        axis.set_xlabel(f"x {bounds[0]}..{bounds[1] - 1}")
        axis.set_ylabel(f"y {bounds[2]}..{bounds[3] - 1}")
    figure.tight_layout()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(args.output, dpi=150)
    plt.close(figure)
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
