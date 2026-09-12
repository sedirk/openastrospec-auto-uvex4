"""Read-only historical replay. Outputs go outside observations, with input hashes.

No self-labelled real-frame accuracy scores: catalogue/WCS ROIs are context, not
centroid truth. Synthetic injected shapes have exact intensity-centroid truth.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
import warnings
import numpy as np
from astropy.io import fits
from photutils.detection import DAOStarFinder
import sep
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from sep_detector import detect
from test_detector import shape_frame


def sha(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest()


def legacy(image, command):
    header = json.dumps({"width": image.shape[1], "height": image.shape[0]}) + "\n"
    call = subprocess.run(command, input=header.encode() + image.astype("<u2").tobytes(),
                          capture_output=True, check=True, timeout=60)
    return json.loads(call.stdout)


def compare(image, command):
    started = time.perf_counter()
    ours = detect(image)
    elapsed = time.perf_counter()-started
    data = np.array(image, dtype=np.float32, order="C")
    background = sep.Background(data)
    residual = data-background.back()
    vanilla = sep.extract(residual, 3.5, err=np.maximum(background.rms(), 1e-3), minarea=5)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        dao = DAOStarFinder(3.5 * max(background.globalrms, 1e-3), fwhm=4)(residual)
    coords = {
        "legacy": legacy(image, command),
        "sep_default": [{"x": float(s["x"]), "y": float(s["y"])} for s in vanilla],
        "dao_default": [] if dao is None else [{"x": float(s["xcentroid"]), "y": float(s["ycentroid"])} for s in dao],
        "sep_parent": ours["sources"],
    }
    return ours, coords, elapsed


def select_frames(root):
    # Two focus-evidence runs per night, immutable LED-OFF source only. Not a focus
    # sweep: these may all have the SAME focuser position.
    by_day = {}
    samples = []
    positions = set()
    for path in sorted(root.glob("*/evidence/*g3-c11-main-focus-analysis.json")):
        evidence = json.loads(path.read_text(encoding="utf-8-sig"))
        day = path.parts[-3][5:13]
        if by_day.get(day, 0) >= 2:
            continue
        source = Path(evidence.get("source", {}).get("absolutePath", ""))
        if not source.is_file() or "led-on" in source.name:
            continue
        positions.add(evidence.get("payload", {}).get("ownerBefore", {}).get("positionSteps"))
        samples.append(source)
        by_day[day] = by_day.get(day, 0) + 1
    # The reported September 12 failures are a separately identified incident
    # regression set, NOT independent held-out precision/recall labels.
    for run in ["UVEX-20260912T155749Z-03bb3f3b605248d", "UVEX-20260912T161744Z-77f1f722a68045e",
                "UVEX-20260912T164515Z-ed147393ba0047b"]:
        samples.extend(sorted((root/run/"evidence").glob("*g3-catalog-short-position.fit")))
        samples.extend(sorted((root/run/"evidence").glob("*g3-plate-solve-probe-01-*.fit")))
    return list(dict.fromkeys(samples)), sorted(p for p in positions if p is not None)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--observations", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--legacy-dll", required=True)
    parser.add_argument("--manifest", type=Path, help="Replay an existing manifest with exact input SHA-256 checks")
    args = parser.parse_args()
    root, output = args.observations.resolve(), args.output.resolve()
    if output == root or root in output.parents:
        raise ValueError("Report directory must be outside immutable observations.")
    output.mkdir(parents=True, exist_ok=True)
    command = [args.dotnet, args.legacy_dll]
    synthetic = []
    fig, axes = plt.subplots(2, 3, figsize=(12, 8), constrained_layout=True)
    for ax, shape in zip(axes.flat, ["round", "donut", "triangle", "shuttlecock", "chestnut", "elongated"]):
        frame, truth = shape_frame(shape)
        image = np.clip(np.rint(frame), 0, 65535).astype(np.uint16)
        measured, catalogs, _ = compare(image, command)
        stats = {}
        for algorithm, sources in catalogs.items():
            distances = [float(np.hypot(s["x"]-truth[0], s["y"]-truth[1])) for s in sources]
            stats[algorithm] = {"count": len(sources), "nearest_centroid_error": min(distances) if distances else None,
                                "within_1px": sum(d <= 1 for d in distances)}
        synthetic.append({"shape": shape, "truth": truth, "algorithms": stats})
        ax.imshow(image, origin="lower", cmap="gray", vmin=1000, vmax=1700)
        ax.plot(*truth, "+", color="lime")
        ax.scatter([s["x"] for s in catalogs["legacy"]], [s["y"] for s in catalogs["legacy"]],
                   facecolors="none", edgecolors="orange", label="legacy")
        ax.scatter([s["x"] for s in measured["sources"]], [s["y"] for s in measured["sources"]],
                   marker="x", c="cyan", label="SEP parent")
        ax.set(xlim=(70, 125), ylim=(70, 125), title=shape)
    axes.flat[0].legend(fontsize=8)
    fig.savefig(output/"synthetic.png", dpi=130)
    plt.close(fig)
    if args.manifest:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
        paths = [(root/entry["path"]).resolve() for entry in manifest["frames"]]
        for path, entry in zip(paths, manifest["frames"]):
            if root not in path.parents or sha(path) != entry["sha256"]:
                raise ValueError("Manifest path/hash does not match immutable observations")
        focus_positions = manifest["focus_positions"]
    else:
        paths, focus_positions = select_frames(root)
    reports = []
    thumbnails = []
    for index, path in enumerate(paths):
        digest = sha(path)
        image = fits.getdata(path, memmap=False)
        if image.ndim != 2 or image.min() < 0 or image.max() > 65535 or not np.isfinite(image).all():
            raise ValueError(f"Unsupported raw range: {path.name}")
        image = image.astype(np.uint16)
        result, catalogs, elapsed = compare(image, command)
        assert digest == sha(path), "Raw observation changed during replay"
        sample = {"path": str(path.relative_to(root)), "sha256": digest,
                  "counts": {k: len(v) for k, v in catalogs.items()}, "seconds": elapsed,
                  "measurements": result, "catalogs": catalogs}
        reports.append(sample)
        strongest = result["sources"][0] if result["sources"] else None
        if strongest:
            thumbnails.append((path.name, image, strongest, catalogs))
        print(f"{index+1}/{len(paths)} {path.name}: {sample['counts']}", flush=True)
    for start in range(0, len(thumbnails), 12):
        fig, axes = plt.subplots(3, 4, figsize=(15, 11), constrained_layout=True)
        for ax in axes.flat:
            ax.axis("off")
        for ax, (name, image, star, catalogs) in zip(axes.flat, thumbnails[start:start+12]):
            x, y = int(star["x"]), int(star["y"])
            radius = max(24, min(80, int(star["aperture_radius"])))
            xmin, xmax = max(0, x-radius), min(image.shape[1], x+radius)
            ymin, ymax = max(0, y-radius), min(image.shape[0], y+radius)
            crop = image[ymin:ymax, xmin:xmax]
            ax.imshow(crop, origin="lower", extent=(xmin, xmax, ymin, ymax), cmap="gray",
                      vmin=np.percentile(crop, 10), vmax=np.percentile(crop, 99.5))
            for key, color, marker in [("legacy", "orange", "o"), ("sep_parent", "cyan", "x")]:
                points = [s for s in catalogs[key] if xmin <= s["x"] < xmax and ymin <= s["y"] < ymax]
                ax.scatter([s["x"] for s in points], [s["y"] for s in points], s=25, c=color, marker=marker,
                           alpha=0.7)
            ax.set(xlim=(xmin,xmax), ylim=(ymin,ymax), title=f"{name[:27]}\nR50={star['r50']:.2f}, parts={len(star['children'])}")
        fig.savefig(output/f"real-{start//12+1}.png", dpi=120)
        plt.close(fig)
    report = {"synthetic": synthetic, "real_frames": reports, "focus_positions": focus_positions,
              "limitations": ["No real-frame centroid truth labels", "No focus sweep or on-sky focus optimum proven",
                              "Default comparison settings, not tuned competitors", "Brightest region is not verified target identity"]}
    (output/"report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    manifest = {"schema":1,"focus_positions":focus_positions,
                "frames":[{"path":r["path"],"sha256":r["sha256"]} for r in reports]}
    (output/"manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"frames": len(reports), "focus_positions": focus_positions,
                      "raw_hashes_unchanged": True, "report": str(output/"report.json")}))


if __name__ == "__main__":
    main()
