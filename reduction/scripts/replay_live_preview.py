"""Read-only replay of saved ATR manifest frames through the production preview.

Writes only derived plots/JSON below --output; never modifies raw observations.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

from astropy.io import fits
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from uvex_reduce.live_preview import extract_preview

parser = argparse.ArgumentParser()
parser.add_argument("--manifest", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
frames = {}
for item in manifest["evidence"]:
    if item["kind"] not in {"atr-science-fits", "atr-probe-fits"}:
        continue
    meta = item["metadata"]
    if float(meta.get("exposureSeconds", "0")) < 60:
        continue
    frames[item["absolutePath"]] = meta
selected = list(frames.items())[-3:]
if not selected:
    raise ValueError("No eligible saved ATR frames")
output = args.output.resolve()
if any(output == Path(p).parent.resolve() or Path(p).resolve().is_relative_to(output) for p, _ in selected):
    raise ValueError("Output must be separate from raw input directories")
output.mkdir(parents=True, exist_ok=True)
report = []
for index, (path, meta) in enumerate(selected):
    source = Path(path)
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    image, header = fits.getdata(source, header=True)
    start = time.monotonic()
    entry = {"source": str(source), "sha256": before}
    try:
        result = extract_preview(image, float(meta["fullScaleAdu"]),
                                 float(meta["traceSpatialCenterPixel"]),
                                 float(meta["traceSpatialHalfWidthPixels"]))
        entry.update(backend=result["backend"], cosmicPixels=result["cosmicPixels"],
                     maskedColumns=result["maskedColumns"], warnings=result["warnings"])
        raw = np.asarray(result["rawFlux"], dtype=float)
        flux = np.asarray(result["flux"], dtype=float)
        old = np.mean(image[::max(1, image.shape[0] // 240)], axis=0)
        fig, axes = plt.subplots(3, 1, figsize=(13, 8), sharex=True, constrained_layout=True)
        axes[0].plot(old, color="gray", linewidth=.6)
        axes[0].set_title("Old full-ROI row-sampled mean (not trace extraction)")
        axes[0].set_ylabel("ADU / sampled row")
        axes[1].plot(raw, color="gray", linewidth=.7, label="Uncleaned aperture - sky")
        axes[1].plot(flux, color="teal", linewidth=.8, label=result["backend"])
        axes[1].legend()
        axes[1].set_ylabel("ADU / aperture")
        axes[2].plot(flux, color="teal", linewidth=.8)
        axes[2].set_title(f"Preview only, no wavelength/response calibration; {result['cosmicPixels']} candidate pixels cleaned")
        axes[2].set_ylabel("ADU / aperture")
        axes[2].set_xlabel("Original dispersion pixel (not wavelength)")
        for ax in axes:
            ax.grid(alpha=.2)
        fig.savefig(output / f"comparison-{index}.png", dpi=130)
        plt.close(fig)
        (output / f"preview-{index}.json").write_text(json.dumps(result, allow_nan=False), encoding="utf-8")
    except Exception as error:
        entry["error"] = str(error)
    entry["seconds"] = round(time.monotonic() - start, 3)
    entry["sourceUnchanged"] = hashlib.sha256(source.read_bytes()).hexdigest() == before
    assert entry["sourceUnchanged"]
    report.append(entry)
(output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
