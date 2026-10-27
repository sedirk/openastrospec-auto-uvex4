"""Read-only raw-frame benchmark; no observatory clients, config writes or uploads.

Run with the existing SEP runtime, not the pinned reduction environment. Public
solver binaries/catalogues are supplied explicitly in an ignored experiment dir.
Inputs are hashed before/after. All external solvers see disposable derived files
only. A solver's success flag is a candidate, never target/motion authority.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import shutil
import subprocess
import time
import warnings

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from photutils.detection import DAOStarFinder, IRAFStarFinder
from scipy import ndimage
import sep

from replay_sparse_g3 import sha, inside


def validate_output(output):
    output = output.resolve()
    temporary = Path(__file__).resolve().parents[2]/"tmp"
    if output == temporary or not inside(output, temporary):
        raise ValueError("Diagnostic output must be a dedicated repository tmp subdirectory")
    return output


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")


def angular_distance(ra, dec, other_ra, other_dec):
    a, b = np.radians([dec, other_dec])
    return float(np.degrees(2 * np.arcsin(np.sqrt(np.clip(
        np.sin((a-b)/2)**2 + np.cos(a)*np.cos(b)*np.sin(np.radians(ra-other_ra)/2)**2, 0, 1)))))


def prepare(root, output):
    # Deliberately include difficult frames, same-night formal controls and
    # earlier successful controls. No cherry-picking based on benchmark outcome.
    selection = [
        ("latest18552_a", "UVEX-20261025T191054Z-db9ce157515f4a8", "00035-"),
        ("latest18552_b", "UVEX-20261025T191054Z-db9ce157515f4a8", "00047-"),
        ("latest18552_mid", "UVEX-20261025T191054Z-db9ce157515f4a8", "00186-"),
        ("latest18552_last", "UVEX-20261025T191054Z-db9ce157515f4a8", "00361-"),
        ("latest21455_a", "UVEX-20261025T192046Z-015433735e24480", "00036-"),
        ("latest21455_b", "UVEX-20261025T192046Z-015433735e24480", "00046-"),
        ("latest21455_solved", "UVEX-20261025T192046Z-015433735e24480", "00368-"),
        ("latest21455_last", "UVEX-20261025T192046Z-015433735e24480", "00427-"),
        ("moon18552_5s", "UVEX-20261025T180350Z-4dcf62581f764dd", "00029-"),
        ("moon18552_15s", "UVEX-20261025T180350Z-4dcf62581f764dd", "00047-"),
        ("moon21455_5s", "UVEX-20261025T180711Z-f103f559def94e6", "00035-"),
        ("moon18552_solved", "UVEX-20261025T171448Z-7166783820b44fe", "00034-"),
        ("moon18552_core", "UVEX-20261025T171448Z-7166783820b44fe", "00046-"),
        ("moon21455_solved", "UVEX-20261025T171824Z-2d23e25e9e414a2", "00047-"),
        ("mid224559_skipped", "UVEX-20261025T161820Z-3eb35419525e4fa", "00110-"),
        ("historical18552", "UVEX-20261021T182813Z-73b715bb6c9343b", "00033-"),
        ("historical21455", "UVEX-20261021T185545Z-8111b0fe665b4a9", "00033-"),
    ]
    samples = []
    for label, run, prefix in selection:
        folder = root/run/"evidence"
        paths = list(folder.glob(prefix+"*.fit"))
        if len(paths) != 1:
            raise ValueError(f"Ambiguous/missing raw: {label}: {paths}")
        path = paths[0].resolve()
        receipts = [json.loads(p.read_text(encoding="utf-8-sig")) for p in sorted(folder.glob("*plate-solve-ladder-attempt.json"))]
        receipt = next((r for r in receipts if Path(r["source"]["absolutePath"]).name == path.name), None)
        if receipt is None and label == "mid224559_skipped":
            # The recorded dispatch exception skipped this solver call. The
            # earlier formal solve is only a hint, not ground truth for this raw.
            receipt = next(r for r in reversed(receipts) if r["source"]["absolutePath"].split("\\")[-1] < path.name
                           and r["payload"].get("solveSucceeded"))
        if receipt is None:
            raise ValueError(f"No recorded hint for {label}")
        p = receipt["payload"]
        ra = p.get("solveHintRaDegrees") or p.get("solvedRaDegrees")
        dec = p.get("solveHintDecDegrees") or p.get("solvedDecDegrees")
        if label == "mid224559_skipped":
            ra, dec = p["solvedRaDegrees"], p["solvedDecDegrees"]
        raw = fits.getdata(path)
        samples.append(dict(label=label, run=run, path=str(path), sha256=sha(path),
            shape=list(raw.shape), ra=ra, dec=dec, scale=.3837484767386058,
            recordedSolved=bool(p.get("solveSucceeded")) if label != "mid224559_skipped" else False,
            recordedCenter=[p.get("solvedRaDegrees"), p.get("solvedDecDegrees")],
            originalSolverWasSkipped=label == "mid224559_skipped",
            sourceBytes=path.stat().st_size))
    write_json(output/"samples.json", samples)
    return samples


def bin_mean(data, factor):
    if data.shape[0] % factor or data.shape[1] % factor:
        raise ValueError("Do not silently crop coordinate support")
    h, w = data.shape
    return np.ascontiguousarray(data.reshape(h//factor, factor, w//factor, factor).mean(axis=(1, 3)), dtype=np.float32)


def background(data):
    bg = sep.Background(np.ascontiguousarray(data, dtype=np.float32), bw=64, bh=64, fw=3, fh=3)
    return bg.back(), np.maximum(bg.rms(), 1e-3)


def filter_rows(rows, shape, factor):
    result = []
    for r in rows:
        if not all(math.isfinite(v) for v in r.values()):
            continue
        if min(r["x"], r["y"], shape[1]-1-r["x"], shape[0]-1-r["y"]) < 8:
            continue
        if r.get("fwhm", 3) < .9 or r.get("fwhm", 3)*factor > 32 or r.get("elongation", 1) > 3:
            continue
        r = dict(r)
        r["x"], r["y"] = (r["x"]+.5)*factor-.5, (r["y"]+.5)*factor-.5
        result.append(r)
    return sorted(result, key=lambda r: r["flux"], reverse=True)[:300]


def extract_methods(data):
    methods = {}
    for factor in (1, 2, 4):
        image = bin_mean(data, factor)
        bg, rms = background(image)
        residual = image-bg
        for threshold in (3.5, 5.0):
            sources = sep.extract(residual, threshold, err=rms, minarea=5,
                                  deblend_nthresh=32, deblend_cont=.005)
            rows = [dict(x=float(s["x"]), y=float(s["y"]), flux=float(s["flux"]),
                         fwhm=2.35482*float(np.sqrt((s["a"]**2+s["b"]**2)/2)),
                         elongation=float(s["a"]/max(s["b"], .01))) for s in sources]
            methods[f"sep_b{factor}_t{threshold}"] = filter_rows(rows, image.shape, factor)
        if factor in (1, 2):
            whitened = residual/rms
            for name, cls in (("dao", DAOStarFinder), ("iraf", IRAFStarFinder)):
                for fwhm in (3.0, 5.0):
                    with warnings.catch_warnings():
                        warnings.simplefilter("ignore")
                        table = cls(threshold=4.0, fwhm=fwhm, exclude_border=True)(whitened)
                    rows = [] if table is None else [dict(x=float(s["xcentroid"]), y=float(s["ycentroid"]),
                                                         flux=float(s["flux"])) for s in table]
                    methods[f"{name}_b{factor}_f{fwhm}"] = filter_rows(rows, image.shape, factor)
            # Explicit matched-filter experiment: normalized per-pixel noise,
            # finite Gaussian kernel, L2 normalization makes white-noise sigma 1.
            # Correlated noise may violate that model; catalogue validation is
            # required and this result does NOT enter the production detector.
            for sigma in (1.0, 2.0, 3.0):
                radius = math.ceil(4*sigma)
                y, x = np.mgrid[-radius:radius+1, -radius:radius+1]
                kernel = np.exp(-(x*x+y*y)/(2*sigma*sigma))
                kernel /= np.sqrt(np.sum(kernel*kernel))
                # Separable equivalent of the finite 2-D Gaussian convolution.
                snr = ndimage.gaussian_filter(whitened, sigma, radius=radius,
                                               mode="reflect") * kernel.sum()
                peaks = (snr > 5.0) & (snr == ndimage.maximum_filter(snr, size=2*radius+1))
                yy, xx = np.nonzero(peaks)
                rows = [dict(x=float(x), y=float(y), flux=float(snr[y, x])) for y, x in zip(yy, xx)]
                methods[f"matched_b{factor}_s{sigma}"] = filter_rows(rows, image.shape, factor)
    return methods


def prepare_variants(sample, folder):
    folder.mkdir(exist_ok=True)
    data = fits.getdata(sample["path"]).astype(np.float32)
    bg, rms = background(data)
    original = folder/"original.fit"
    if not original.exists():
        shutil.copy2(sample["path"], original)
    if sha(original) != sample["sha256"]:
        raise ValueError("Original-copy control does not match source")
    variants = {"original": original}
    arrays = {"raw": data, "background": data-bg+4096,
              "smooth_background": ndimage.gaussian_filter(data-bg, 1.0)+4096,
              "bin2_raw": bin_mean(data, 2),
              "bin2_background": bin_mean(data-bg, 2)+4096,
              "bin4_background": bin_mean(data-bg, 4)+4096}
    for name, pixels in arrays.items():
        path = folder/(name+".fit")
        if not path.exists():
            hdr = fits.Header()
            hdr["RA"], hdr["DEC"], hdr["EQUINOX"] = sample["ra"], sample["dec"], 2000.0
            hdr["HINTONLY"], hdr["RAWSHA"] = True, sample["sha256"]
            fits.writeto(path, pixels.astype(np.float32), hdr)
        variants[name] = path
    return data, bg, rms, variants


def run_process(command, cwd, timeout=30):
    start = time.monotonic()
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    try:
        p = subprocess.run([str(a) for a in command], cwd=cwd, capture_output=True,
                           text=True, errors="replace", timeout=timeout, startupinfo=startup)
        return dict(exitCode=p.returncode, stdout=p.stdout, stderr=p.stderr,
                    seconds=time.monotonic()-start, timeout=False)
    except subprocess.TimeoutExpired:
        return dict(exitCode=None, seconds=time.monotonic()-start, timeout=True)


def parse_pl3(text, sample, factor):
    result = dict(nativeSuccess=False, plausible=False)
    lines = text.strip().splitlines()
    if not lines or lines[0].strip() != "True":
        return result
    result["nativeSuccess"] = True
    try:
        ra, dec = map(float, lines[1].split(",")[:2])
        pixels_per_rad, rotation = map(float, lines[2].split(",")[:2])
        ra, dec = math.degrees(ra), math.degrees(dec)
        scale = 206264.806247/abs(pixels_per_rad)/factor
        distance = angular_distance(ra, dec, sample["ra"], sample["dec"])
        stats = list(map(float, lines[6].split(",")))
        result.update(ra=ra, dec=dec, rawPixelScale=scale, rotation=rotation,
                      hintDistanceDegrees=distance, matched=stats[-1], plateStars=stats[-2], wrms=stats[1],
                      plausible=distance < 2 and abs(scale/sample["scale"]-1) < .1 and stats[-1] >= 5)
    except (ValueError, IndexError):
        result["parseError"] = True
    return result


def solve_pl3(sample, variants, folder, installed):
    isolated = folder/"pl3"
    isolated.mkdir(exist_ok=True)
    before = {n: sha(installed/n) for n in ("PlateSolve3.80.exe", "SRSLib.dll", "PlateSolve.cfg")}
    for name in before:
        if not (isolated/name).exists():
            shutil.copy2(installed/name, isolated/name)
    results = {}
    for name, path in variants.items():
        record = isolated/(name+".json")
        if record.exists():
            results[name] = json.loads(record.read_text())
            continue
        dest = isolated/(name+".fit")
        shutil.copy2(path, dest)
        h, w = sample["shape"]
        factor = w/fits.getheader(dest)["NAXIS1"]
        command = [isolated/"PlateSolve3.80.exe", dest, math.radians(sample["ra"]),
                   math.radians(sample["dec"]), math.radians(w*sample["scale"]/3600),
                   math.radians(h*sample["scale"]/3600)]
        r = run_process(command, isolated)
        txt = dest.with_name(dest.stem+"_PS3.txt")
        r["resultText"] = txt.read_text(errors="replace") if txt.exists() else ""
        r.update(parse_pl3(r["resultText"], sample, factor))
        write_json(record, r)
        results[name] = r
        print(sample["label"], "PL3", name, r.get("plausible"), round(r["seconds"], 2), flush=True)
    if any(sha(installed/n) != digest for n, digest in before.items()):
        raise RuntimeError("Installed solver unexpectedly changed")
    return results


def solve_astap(sample, variants, folder, executable, database):
    results = {}
    for name, path in variants.items():
        # Both native standard filtering and an explicitly lowered minimum star
        # diameter: our 0.384 arcsec pixels are much smaller than the default.
        for minimum in (1.5, .6):
            key = f"{name}_m{minimum}".replace(".", "p")
            record = folder/("astap_"+key+".json")
            if record.exists():
                results[key] = json.loads(record.read_text())
                continue
            prefix = folder/("astap_"+key)
            r = run_process([executable, "-f", path, "-ra", sample["ra"]/15,
                "-spd", sample["dec"]+90, "-fov", sample["shape"][0]*sample["scale"]/3600,
                "-r", 2, "-z", 1, "-m", minimum, "-d", database, "-D", "d80",
                "-o", prefix, "-wcs", "-log"], folder)
            ini = prefix.with_suffix(".ini")
            r["ini"] = ini.read_text(errors="replace") if ini.exists() else ""
            r["nativeSuccess"] = "PLTSOLVD=T" in r["ini"]
            r["plausible"] = False
            wcsfile = prefix.with_suffix(".wcs")
            if r["nativeSuccess"] and wcsfile.exists():
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore")
                    wcs = WCS(fits.getheader(wcsfile))
                scale = float(np.sqrt(abs(np.linalg.det(wcs.pixel_scale_matrix)))*3600)
                factor = sample["shape"][1]/fits.getheader(path)["NAXIS1"]
                ra, dec = map(float, wcs.wcs.crval)
                distance = angular_distance(ra, dec, sample["ra"], sample["dec"])
                r.update(ra=ra, dec=dec, rawPixelScale=scale/factor, hintDistanceDegrees=distance,
                         wcsPath=str(wcsfile), factor=factor,
                         plausible=distance < 2 and abs(scale/factor/sample["scale"]-1) < .1)
            write_json(record, r)
            results[key] = r
            print(sample["label"], "ASTAP", key, r["plausible"], round(r["seconds"], 2), flush=True)
    return results


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--observations", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--phase", choices=("prepare", "extract", "pl3", "astap"), required=True)
    p.add_argument("--pl3", type=Path)
    p.add_argument("--astap", type=Path)
    p.add_argument("--database", type=Path)
    p.add_argument("--labels", nargs="*")
    a = p.parse_args()
    root, output = a.observations.resolve(), validate_output(a.output)
    if inside(output, root) or inside(root, output):
        raise ValueError("Raw archive and experiment output must be disjoint")
    output.mkdir(parents=True, exist_ok=True)
    if a.phase == "prepare":
        samples = prepare(root, output)
        print(f"Selected {len(samples)} immutable sources", flush=True)
        return
    samples = json.loads((output/"samples.json").read_text(encoding="utf-8"))
    for sample in samples:
        if a.labels and sample["label"] not in a.labels:
            continue
        if sha(Path(sample["path"])) != sample["sha256"]:
            raise RuntimeError("Source hash changed before test")
        folder = output/sample["label"]
        data, bg, rms, variants = prepare_variants(sample, folder)
        if a.phase == "extract":
            r = dict(noHardwareIO=True, motionAuthority=False, sample=sample,
                     median=float(np.median(data)), noise=float(np.median(rms)),
                     backgroundRange=list(map(float, np.percentile(bg, [5, 95]))),
                     clippedFraction=float(np.mean(data >= 65520)), methods=extract_methods(data))
            write_json(folder/"detections.json", r)
            print(sample["label"], {k:len(v) for k, v in r["methods"].items()}, flush=True)
        elif a.phase == "pl3":
            solve_pl3(sample, variants, folder, a.pl3.resolve())
        elif a.phase == "astap":
            solve_astap(sample, variants, folder, a.astap.resolve(), a.database.resolve())
        if sha(Path(sample["path"])) != sample["sha256"]:
            raise RuntimeError("Source hash changed after test")


if __name__ == "__main__":
    main()
