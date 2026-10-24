"""Offline saved-frame experiment, never a hardware or motion-authority client.

Use the existing star-detection environment (numpy/scipy/astropy/SEP). Inputs are
immutable; derived FITS, isolated solver files and JSON receipts go to --output.
No dependency changes, network calls, device connections or production settings.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import time

import numpy as np
from astropy.io import fits
from scipy import ndimage
from scipy.spatial import cKDTree
import sep


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inside(path, parent):
    return path.resolve().is_relative_to(parent.resolve())


def extract(path, threshold, exclude):
    image = np.ascontiguousarray(fits.getdata(path), dtype=np.float32)
    if image.ndim != 2 or not np.all(np.isfinite(image)):
        raise ValueError("Expected finite 2-D FITS")
    bg = sep.Background(image, bw=64, bh=64, fw=3, fh=3)
    sources = sep.extract(image-bg.back(), threshold, err=bg.rms(), minarea=5,
                          deblend_nthresh=32, deblend_cont=.005)
    rows = []
    for s in sources:
        x, y = float(s["x"]), float(s["y"])
        fwhm = 2.35482 * float(np.sqrt((s["a"]**2+s["b"]**2)/2))
        elong = float(s["a"]/s["b"]) if s["b"] else 999
        if x < 16 or y < 16 or x > image.shape[1]-16 or y > image.shape[0]-16:
            continue
        if exclude and exclude[0] <= x <= exclude[0]+exclude[2] and exclude[1] <= y <= exclude[1]+exclude[3]:
            continue
        if 1.5 <= fwhm <= 18 and elong <= 2.5:
            rows.append(dict(x=x, y=y, flux=float(s["flux"]), fwhm=fwhm,
                             elongation=elong, flag=int(s["flag"])))
    rows.sort(key=lambda r: r["flux"], reverse=True)
    return dict(path=str(path), sha256=sha(path), shape=list(image.shape),
                background=float(bg.globalback), noise=float(bg.globalrms),
                median=float(np.median(image)), fractionClipped=float(np.mean(image >= 65520)),
                detected=len(sources), stars=rows[:120])


def similarity(a, b):
    """Least-squares orientation-preserving similarity, column-vector matrix."""
    ca, cb = a.mean(axis=0), b.mean(axis=0)
    x, y = a-ca, b-cb
    u, s, vt = np.linalg.svd(x.T@y)
    rot = vt.T@u.T
    if np.linalg.det(rot) < 0:
        vt[-1] *= -1
        s[-1] *= -1
        rot = vt.T@u.T
    denom = float(np.sum(x*x))
    if denom <= 1e-9:
        raise ValueError("Degenerate fit")
    scale = float(np.sum(s)/denom)
    m = np.eye(3)
    m[:2, :2] = scale*rot
    m[:2, 2] = cb-m[:2, :2]@ca
    return m


def apply(m, points):
    return points@m[:2, :2].T+m[:2, 2]


def area(first, second):
    return abs(float(first[0]*second[1]-first[1]*second[0]))


def triangles(points):
    result = []
    for t in itertools.combinations(range(min(36, len(points))), 3):
        p = points[list(t)]
        opposite = np.array([np.linalg.norm(p[1]-p[2]), np.linalg.norm(p[0]-p[2]), np.linalg.norm(p[0]-p[1])])
        order = np.argsort(opposite)
        lengths = opposite[order]
        if lengths[0] < 20 or lengths[-1]/lengths[0] > 12:
            continue
        result.append((lengths[:2]/lengths[2], tuple(t[i] for i in order)))
    return result


def register(first, second, minimum_holdout, maximum_rotation):
    a = np.asarray([[s["x"], s["y"]] for s in first["stars"]])
    b = np.asarray([[s["x"], s["y"]] for s in second["stars"]])
    ta, tb = triangles(a), triangles(b)
    best = dict(accepted=False, reason="No independent field-star registration", pairs=[])
    if not ta or not tb:
        return best
    tree = cKDTree(np.asarray([t[0] for t in tb]))
    candidates = []
    for descriptor, triple in ta:
        distance, index = tree.query(descriptor)
        if distance <= .0025:
            candidates.append((distance, triple, tb[index][1]))
    btree = cKDTree(b)
    alternatives = []
    alternative_support = 0
    for _, ia, ib in sorted(candidates)[:1200]:
        m = similarity(a[list(ia)], b[list(ib)])
        scale = float(np.linalg.norm(m[:2, 0]))
        rotation = math.degrees(math.atan2(m[1, 0], m[0, 0]))
        if not .97 <= scale <= 1.03 or abs(rotation) > maximum_rotation:
            continue
        distances, indices = btree.query(apply(m, a))
        pairs = []
        used = set()
        for i in np.argsort(distances):
            k = int(indices[i])
            if distances[i] <= 4 and k not in used:
                used.add(k)
                pairs.append((int(i), k))
        if len(pairs) < 3:
            continue
        residuals = [float(distances[i]) for i, _ in pairs]
        if len(pairs) < len(best["pairs"]):
            continue
        # Refit exactly three spread-out field stars; all remaining matched
        # stars are genuine leave-out checks, not the fitting triangle itself.
        train = max(itertools.combinations(range(len(pairs)), 3), key=lambda t:
                    area(a[pairs[t[1]][0]]-a[pairs[t[0]][0]], a[pairs[t[2]][0]]-a[pairs[t[0]][0]]))
        m = similarity(np.asarray([a[pairs[i][0]] for i in train]), np.asarray([b[pairs[i][1]] for i in train]))
        scale = float(np.linalg.norm(m[:2, 0]))
        rotation = math.degrees(math.atan2(m[1, 0], m[0, 0]))
        if not .97 <= scale <= 1.03 or abs(rotation) > maximum_rotation:
            continue
        held = [i for i in range(len(pairs)) if i not in train]
        validation = [float(np.linalg.norm(apply(m, a[[pairs[i][0]]])[0]-b[pairs[i][1]])) for i in held]
        accepted = len(held) >= minimum_holdout and max(validation, default=math.inf) <= 2.5
        if accepted:
            if len(pairs) > alternative_support:
                alternatives = []
                alternative_support = len(pairs)
            if len(pairs) == alternative_support:
                # Distinct equally supported geometry is not resolved by the
                # deterministic ordering of triangle hypotheses. Repeated star
                # patterns must fail rather than silently choose one copy.
                corners = np.asarray([a.min(axis=0), a.max(axis=0), a.mean(axis=0)])
                if all(float(np.max(np.linalg.norm(apply(m, corners)-apply(other, corners), axis=1))) > 8 for other in alternatives):
                    alternatives.append(m.copy())
        if len(pairs) == len(best["pairs"]) and np.median(residuals) >= best.get("medianResidual", math.inf):
            continue
        best = dict(accepted=accepted, reason="Independent held-out stars pass" if accepted else "Insufficient/failed held-out stars",
                    pairs=[dict(reference=a[i].tolist(), current=b[k].tolist()) for i, k in pairs],
                    matrix=m.tolist(), fittedStars=3, heldOutStars=len(held),
                    heldOutResiduals=validation, medianResidual=float(np.median(residuals)),
                    scale=scale, rotationDegrees=rotation, trainingIndices=list(train))
    best["equallySupportedDistinctTransforms"] = len(alternatives)
    if len(alternatives) > 1:
        best["accepted"] = False
        best["ambiguous"] = True
        best["reason"] = "Equally supported, independently validated but incompatible field transforms"
        best["alternateMatrices"] = [m.tolist() for m in alternatives]
    return best


def native_success(result):
    return (result.get("exitCode") == 0 and not result.get("timedOut", True)
            and bool(result.get("resultText"))
            and result["resultText"].splitlines()[0].strip() == "True")


def solve(source, args, out):
    installed = args.solver_dir.resolve()
    solver = out/"isolated-solver"
    solver.mkdir()
    config_hashes = {}
    for name in ["PlateSolve3.80.exe", "SRSLib.dll", "PlateSolve.cfg"]:
        original = installed/name
        config_hashes[name] = sha(original)
        shutil.copy2(original, solver/name)
    data = fits.getdata(source).astype(float)
    h, w = data.shape
    f = args.binning
    if h % f or w % f:
        raise ValueError("No cropping allowed: complete blocks required")
    if args.normalize:
        data = data-ndimage.gaussian_filter(data, 35)+4096
    if f != 1:
        data = np.rint(data.reshape(h//f, f, w//f, f).mean(axis=(1, 3)))
    header = fits.Header()
    rh, d = args.ra/15, abs(args.dec)
    header["OBJCTRA"] = f"{int(rh):02d} {int(rh%1*60):02d} {rh*3600%60:.7f}"
    header["OBJCTDEC"] = ("-" if args.dec < 0 else "+")+f"{int(d):02d} {int(d%1*60):02d} {d*3600%60:.7f}"
    header["RA"], header["DEC"], header["EQUINOX"] = args.ra, args.dec, 2000.0
    header["XPIXSZ"], header["YPIXSZ"] = 4.0*f, 4.0*f
    header["FOCALLEN"] = 206264.806247*.004/args.scale
    header["SWBIN"], header["HINTONLY"] = f, True
    dest = out/"solver-input.fit"
    fits.writeto(dest, np.clip(data, 0, 65535).astype(np.uint16), header=header)
    rad = math.pi/180
    command = [str(solver/"PlateSolve3.80.exe"), str(dest), str(args.ra*rad), str(args.dec*rad),
               str(w*args.scale/3600*rad), str(h*args.scale/3600*rad)]
    started = time.monotonic()
    result = dict(command=command, solverHashes=config_hashes, timeoutSeconds=args.timeout,
                  inputSha256=sha(dest), rawPixelsRewritten=False, disposableSolverInput=True)
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    try:
        p = subprocess.run(command, cwd=solver, capture_output=True, text=True, timeout=args.timeout, startupinfo=startup)
        result["exitCode"] = p.returncode
        result["timedOut"] = False
    except subprocess.TimeoutExpired:
        result["timedOut"] = True
    output = dest.with_name(dest.stem+"_PS3.txt")
    result["resultText"] = output.read_text(errors="replace") if output.exists() else None
    result["success"] = native_success(result)
    result["elapsedSeconds"] = time.monotonic()-started
    result["installedSolverUnchanged"] = all(sha(installed/name) == digest for name, digest in config_hashes.items())
    return result


def experiment(args, sources, out, report):
    if args.mode == "solve":
        if len(sources) != 1 or args.solver_dir is None or any(x is None for x in [args.ra, args.dec, args.scale]):
            raise ValueError("solve needs one source, solver-dir, ra, dec and scale")
        report["solve"] = solve(sources[0], args, out)
    else:
        report["extractions"] = [extract(s, args.threshold, args.exclude_roi) for s in sources]
        for e in report["extractions"][1:]:
            r = register(report["extractions"][0], e, args.minimum_holdout, args.maximum_rotation)
            if args.target_point is not None and r.get("matrix"):
                r["propagatedHeldOutPoint"] = apply(np.asarray(r["matrix"]), np.asarray([args.target_point]))[0].tolist()
            report["registrations"].append(r)
        if args.mode in ["stack", "mosaic"]:
            valid = len(sources) >= 2 and all(r["accepted"] for r in report["registrations"])
            report["productGenerated"] = valid
            if valid:
                matrices = [np.eye(3)]+[np.asarray(r["matrix"]) for r in report["registrations"]]
                offset = np.zeros(2)
                shape = fits.getdata(sources[0]).shape
                if args.mode == "mosaic":
                    corners = []
                    for source, m in zip(sources, matrices):
                        h, w = fits.getdata(source).shape
                        bounds = np.array([[0., 0.], [w, 0.], [0., h], [w, h]])
                        corners.append(apply(np.linalg.inv(m), bounds))
                    corners = np.vstack(corners)
                    offset = np.floor(corners.min(axis=0))
                    size = np.ceil(corners.max(axis=0))-offset
                    if size.max() > 10000:
                        raise ValueError("Diagnostic mosaic bound exceeded")
                    shape = (int(size[1]), int(size[0]))
                images = []
                for source, m in zip(sources, matrices):
                    image = fits.getdata(source).astype(float)
                    off = m[:2, 2]+m[:2, :2]@offset
                    images.append(ndimage.affine_transform(image, m[:2, :2][::-1, ::-1], offset=off[::-1], output_shape=shape, order=1, cval=np.nan))
                with np.errstate(invalid="ignore"):
                    product = np.nanmean(np.asarray(images), axis=0)
                product = np.nan_to_num(product, nan=float(np.nanmedian(product)))
                dest = out/(args.mode+"-derived.fit")
                fits.writeto(dest, product.astype(np.float32))
                report["product"] = dict(path=str(dest), sha256=sha(dest), derivedNotRaw=True,
                                          referencePixelOffset=offset.tolist(), shape=list(shape))
            else:
                report["productRefused"] = "No independently validated overlapping field; fitting triangles alone are not matches"


def run(args, sources, out):
    """Preserve the original error and a receipt after a validated output exists."""
    report = dict(schema=1, status="Running", mode=args.mode, noHardwareIO=True, motionAuthority=False,
                  inputs={}, parameters={k: str(v) if isinstance(v, Path) else v for k, v in vars(args).items() if k != "source"},
                  scriptSha256=sha(Path(__file__)),
                  pythonExecutable=sys.executable, pythonVersion=sys.version,
                  sepVersion=sep.__version__, extractions=[], registrations=[])
    original_error = None
    try:
        for source in sources:
            report["inputs"][str(source)] = sha(source)
        experiment(args, sources, out, report)
        report["status"] = "Completed"
    except BaseException as error:
        original_error = error
        report["status"] = "Failed"
        report["failure"] = dict(type=type(error).__name__, message=str(error))
        raise
    finally:
        checks = []
        # Recheck every requested source even when one read/hash/solver failed;
        # one additional verification failure must not hide the original error.
        for source in sources:
            check = dict(path=str(source), before=report["inputs"].get(str(source)))
            try:
                check["after"] = sha(source)
                check["unchanged"] = check["before"] is not None and check["before"] == check["after"]
            except BaseException as error:
                check["unchanged"] = False
                check["verificationError"] = dict(type=type(error).__name__, message=str(error))
            checks.append(check)
        report["sourceVerification"] = checks
        report["sourcesUnchanged"] = all(c["unchanged"] for c in checks)
        if not report["sourcesUnchanged"]:
            report["status"] = "Failed"
            report.setdefault("failure", dict(type="SourceVerificationFailed", message="One or more immutable input checks failed"))
        try:
            (out/"report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
        except BaseException as error:
            if original_error is None:
                raise
            print(f"Additional receipt-write error: {type(error).__name__}: {error}", file=sys.stderr)
    if report["status"] == "Failed":
        raise RuntimeError(report["failure"]["message"])
    print(json.dumps(dict(output=str(out), mode=args.mode, sourcesUnchanged=report["sourcesUnchanged"],
                          solve=report.get("solve"), registrations=report["registrations"],
                          productGenerated=report.get("productGenerated")), allow_nan=False))
    return report


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("mode", choices=["extract", "register", "stack", "mosaic", "solve"])
    p.add_argument("--observations", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--source", type=Path, action="append", required=True)
    p.add_argument("--solver-dir", type=Path)
    p.add_argument("--threshold", type=float, default=4)
    p.add_argument("--exclude-roi", type=float, nargs=4)
    p.add_argument("--target-point", type=float, nargs=2)
    p.add_argument("--minimum-holdout", type=int, default=3)
    p.add_argument("--maximum-rotation", type=float, default=5)
    p.add_argument("--ra", type=float)
    p.add_argument("--dec", type=float)
    p.add_argument("--scale", type=float)
    p.add_argument("--binning", type=int, default=2)
    p.add_argument("--normalize", action="store_true")
    p.add_argument("--timeout", type=float, default=55)
    args = p.parse_args()
    sources = [s.resolve() for s in args.source]
    out = args.output.resolve()
    if inside(out, args.observations) or any(inside(out, s.parent) for s in sources):
        p.error("Output must not be inside the observations tree or source directory")
    if out.exists():
        p.error("Use a new output directory; existing evidence is never overwritten")
    if not 1 <= args.binning <= 8 or not 2 <= args.minimum_holdout <= 20 or not .1 <= args.timeout <= 300:
        p.error("Invalid bounded replay parameter")
    out.mkdir(parents=True)
    run(args, sources, out)


if __name__ == "__main__":
    main()
