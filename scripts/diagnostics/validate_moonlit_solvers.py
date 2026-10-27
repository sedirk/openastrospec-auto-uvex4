"""Offline catalogue cross-check and detector-only render experiments.

Point renders contain only measured centroids, NOT restored astronomical images.
They must never substitute for raw science data or direct target measurements.
The benchmark's isolated solvers see generated files only; raw inputs are hashed.
"""
from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import numpy as np
from astropy.io import fits
from astropy.wcs import WCS
from scipy import ndimage
from scipy.spatial import cKDTree

from benchmark_moonlit_solvers import inside, sha, solve_astap, solve_pl3, validate_output, write_json
from replay_sparse_g3 import register


def catalogue(sample, output):
    group = next(g for g in ("18552", "21455", "224559") if g in sample["label"])
    with (output/f"gaia{group}.csv").open(encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    # DR3 positions are at J2016.0; linear propagation to the observation epoch
    # is ample at the several-pixel cross-check tolerance. Missing PM stays 0
    # and is recorded, never implicitly treated as measured zero proper motion.
    years = 2026.815-2016.0
    stars = []
    for r in rows:
        ra, dec = float(r["ra"]), float(r["dec"])
        pmra, pmdec = float(r["pmra"] or 0), float(r["pmdec"] or 0)
        stars.append(dict(ra=ra+years*pmra/3600000/np.cos(np.radians(dec)),
                          dec=dec+years*pmdec/3600000, magnitude=float(r["phot_g_mean_mag"]),
                          id=r["source_id"], missingPm=not r["pmra"] or not r["pmdec"]))
    return stars


def unique_matches(predicted, measured, radius=3.0):
    if not len(predicted) or not len(measured):
        return []
    distances, indices = cKDTree(measured).query(predicted)
    pairs, used = [], set()
    for i in np.argsort(distances):
        k = int(indices[i])
        if distances[i] <= radius and k not in used:
            used.add(k)
            pairs.append(dict(catalogueIndex=int(i), detectionIndex=k, residual=float(distances[i])))
    return pairs


def check_candidate(record, sample, detections, stars):
    wcs = WCS(fits.getheader(record["wcsPath"]))
    xy = wcs.all_world2pix(np.array([[s["ra"], s["dec"]] for s in stars]), 0)
    xy = (xy+.5)*record["factor"]-.5
    h, w = sample["shape"]
    keep = np.isfinite(xy).all(axis=1) & (xy[:, 0] > 8) & (xy[:, 0] < w-8) & (xy[:, 1] > 8) & (xy[:, 1] < h-8)
    xy = xy[keep]
    visible = [s for s, k in zip(stars, keep) if k]
    methods = {}
    for method, rows in detections.items():
        pairs = unique_matches(xy, [[s["x"], s["y"]] for s in rows])
        points = xy[[p["catalogueIndex"] for p in pairs]]
        methods[method] = dict(count=len(pairs),
            medianResidual=float(np.median([p["residual"] for p in pairs])) if pairs else None,
            spanX=float(np.ptp(points[:, 0])) if pairs else 0,
            spanY=float(np.ptp(points[:, 1])) if pairs else 0, pairs=pairs)
    # Coordinates are predicted from the candidate WCS, not fitted to these
    # measurements. This validates geometry but is not a disjoint-catalogue
    # test: ASTAP D80 itself derives from Gaia. Report that limitation explicitly.
    best_name, best = max(methods.items(), key=lambda kv: kv[1]["count"])
    measured = np.array([[s["x"], s["y"]] for s in detections[best_name]])
    # Fixed-seed displaced-catalogue negative controls, not a formal FAP: the
    # positive detector was selected from the declared set of experiments.
    rng = np.random.default_rng(2601026)
    negatives = []
    for _ in range(100):
        shift = rng.uniform(40, 350, 2)*rng.choice([-1, 1], 2)
        negatives.append(len(unique_matches((xy+shift) % [w, h], measured)))
    validated = (record["plausible"] and best["count"] >= 5 and best["spanX"] >= w*.2
                 and best["spanY"] >= h*.2 and max(negatives) < best["count"])
    return dict(catalogueCrossChecked=bool(validated), independentFit=False,
        catalogueFamilyAlsoUsedByAstap=True, bestMethod=best_name, best=best,
        displacedCatalogueCounts=negatives,
        catalogueInFrame=len(xy), stars=[dict(**s, x=float(p[0]), y=float(p[1])) for s, p in zip(visible, xy)],
        methods=methods, record=record)


def point_render(rows, shape):
    image = np.zeros(shape, dtype=np.float32)
    for r in rows[:60]:
        x, y = r["x"], r["y"]
        ix, iy = int(x), int(y)
        fx, fy = x-ix, y-iy
        # Bilinear placement keeps the measured subpixel position. Uniform
        # amplitudes deliberately remove internal threshold/brightness effects.
        image[iy, ix] += (1-fx)*(1-fy)
        image[iy, ix+1] += fx*(1-fy)
        image[iy+1, ix] += (1-fx)*fy
        image[iy+1, ix+1] += fx*fy
    return ndimage.gaussian_filter(image, 2.0)*300000+1000


def pair_experiment(samples, out):
    from benchmark_moonlit_solvers import prepare_variants, extract_methods
    by_name = {s["label"]: s for s in samples}
    for group in ("18552", "21455"):
        first, second = [by_name[f"latest{group}_{suffix}"] for suffix in ("a", "b")]
        for s in (first, second):
            if sha(Path(s["path"])) != s["sha256"]:
                raise ValueError("Changed pair input")
        ds = [json.loads((out/s["label"]/"detections.json").read_text())["methods"] for s in (first, second)]
        trials = {name: register(dict(stars=ds[0][name]), dict(stars=ds[1][name]), 2, 3)
                  for name in ("sep_b2_t3.5", "dao_b2_f3.0", "matched_b2_s1.0", "matched_b2_s2.0")}
        folder = out/("pair"+group)
        folder.mkdir(exist_ok=True)
        write_json(folder/"registration.json", trials)
        # Separate, explicitly exploratory 3.5 px coadd criterion. Never change
        # the production/shared registration policy. Require >=3 withheld stars.
        for r in trials.values():
            r["exploratoryCoaddAccepted"] = (r.get("heldOutStars", 0) >= 3
                and max(r.get("heldOutResiduals", [float("inf")])) <= 3.5
                and not r.get("ambiguous", False))
        write_json(folder/"registration.json", trials)
        valid = [(name, r) for name, r in trials.items() if r.get("exploratoryCoaddAccepted")]
        print("pair", group, [(name, r.get("accepted"), len(r["pairs"])) for name, r in trials.items()], flush=True)
        if not valid:
            continue
        name, registration = max(valid, key=lambda kv: len(kv[1]["pairs"]))
        m = np.array(registration["matrix"])
        images = [fits.getdata(s["path"]).astype(np.float32) for s in (first, second)]
        aligned = ndimage.affine_transform(images[1], m[:2, :2][::-1, ::-1], offset=m[:2, 2][::-1],
                                           output_shape=images[0].shape, order=1, cval=np.nan)
        stack = np.nanmean(np.array([images[0], aligned]), axis=0)
        path = folder/"stack.fit"
        if not path.exists():
            hdr = fits.Header()
            hdr["DERIVED"] = True
            hdr["RAW1SHA"], hdr["RAW2SHA"] = first["sha256"], second["sha256"]
            fits.writeto(path, stack, hdr)
        sample = dict(first, label="pair"+group, path=str(path), sha256=sha(path), derived=True)
        write_json(folder/"pair-sample.json", dict(sample=sample, sources=[first, second], method=name))
        data, _, _, variants = prepare_variants(sample, folder)
        detections = extract_methods(data)
        write_json(folder/"detections.json", dict(methods=detections))
        results = solve_astap(sample, variants, folder, out/"tools/astap-cli/astap_cli.exe",
                              out/"tools/d80-unpack/opt/astap")
        stars = catalogue(sample, out)
        checks = {k: check_candidate(r, sample, detections, stars) for k, r in results.items() if r["plausible"]}
        write_json(folder/"catalogue-checks.json", checks)
        for s in (first, second):
            if sha(Path(s["path"])) != s["sha256"]:
                raise ValueError("Changed pair input")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--phase", choices=("points", "points-pl3", "validate", "pairs"), required=True)
    p.add_argument("--pl3", type=Path)
    p.add_argument("--labels", nargs="*")
    a = p.parse_args()
    out = validate_output(a.output)
    samples = json.loads((out/"samples.json").read_text(encoding="utf-8"))
    if any(inside(out, Path(s["path"]).parent.parent) or inside(Path(s["path"]), out) for s in samples):
        raise ValueError("Experiment output must be disjoint from raw input")
    if a.phase == "pairs":
        pair_experiment(samples, out)
        return
    for sample in samples:
        if a.labels and sample["label"] not in a.labels:
            continue
        raw = Path(sample["path"])
        if inside(out, raw.parent.parent) or inside(raw, out):
            raise ValueError("Experiment output must be disjoint from raw input")
        if sha(raw) != sample["sha256"]:
            raise ValueError("Source changed before experiment")
        folder = out/sample["label"]
        det = json.loads((folder/"detections.json").read_text(encoding="utf-8"))["methods"]
        if a.phase in ("points", "points-pl3"):
            variants = {}
            names = ("sep_b2_t3.5", "sep_b4_t3.5", "dao_b2_f3.0", "iraf_b2_f5.0",
                     "matched_b2_s1.0", "matched_b2_s2.0", "matched_b2_s3.0")
            for method in names:
                name = "points_"+method.replace(".", "p")
                if a.phase == "points-pl3":
                    name += "_hinted"
                path = folder/(name+".fit")
                if not path.exists():
                    hdr = fits.Header()
                    hdr["SYNTHET"] = True
                    hdr["MEASURED"] = True
                    hdr["RAWSHA"] = sample["sha256"]
                    hdr["METHOD"] = method
                    hdr["RA"], hdr["DEC"], hdr["EQUINOX"] = sample["ra"], sample["dec"], 2000.0
                    rh, d = sample["ra"]/15, abs(sample["dec"])
                    hdr["OBJCTRA"] = f"{int(rh):02d} {int(rh%1*60):02d} {rh*3600%60:.7f}"
                    hdr["OBJCTDEC"] = ("-" if sample["dec"] < 0 else "+")+f"{int(d):02d} {int(d%1*60):02d} {d*3600%60:.7f}"
                    hdr["XPIXSZ"], hdr["YPIXSZ"] = 4., 4.
                    hdr["FOCALLEN"] = 206264.806247*.004/sample["scale"]
                    hdr["HINTONLY"] = True
                    fits.writeto(path, point_render(det[method], tuple(sample["shape"])), hdr)
                variants[name] = path
            if a.phase == "points":
                solve_astap(sample, variants, folder, out/"tools/astap-cli/astap_cli.exe",
                            out/"tools/d80-unpack/opt/astap")
            else:
                solve_pl3(sample, variants, folder, a.pl3.resolve())
        else:
            stars = catalogue(sample, out)
            checks = {}
            for path in sorted(folder.glob("astap_*.json")):
                record = json.loads(path.read_text(encoding="utf-8"))
                if record.get("plausible"):
                    checks[path.stem] = check_candidate(record, sample, det, stars)
            write_json(folder/"catalogue-checks.json", checks)
            print(sample["label"], [(k, v["best"]["count"], v["catalogueCrossChecked"]) for k, v in checks.items()], flush=True)
        if sha(raw) != sample["sha256"]:
            raise ValueError("Source changed after experiment")


if __name__ == "__main__":
    main()
