"""ADR-0048: image-only, bounded measured-source field-WCS recovery.

The point render is NOT a science image or a target measurement. Catalogue
verification returns to raw-derived centroids. No device/network APIs are used.
D80 decoding follows https://www.hnsky.org/astap.htm appendix 3 (1476-5).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import time

import numpy as np
import sep
from astropy.io import fits
from astropy.wcs import WCS
from scipy import ndimage
from scipy.spatial import cKDTree

ALGORITHM = "sep-bin2-astap-raw-verified-v1"
RING_CELLS = [1, 3, 9, 15, 21, 27, 33, 38, 43, 48, 52, 56, 60, 63, 65, 67, 68, 69]
RING_CELLS += RING_CELLS[::-1]
RING_EDGES = np.r_[-90., np.arange(-17, 18)*90/17.5, 90.]


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def detect(data):
    h, w = data.shape
    if min(h, w) < 64 or h*w > 8_000_000 or h % 2 or w % 2 or not np.isfinite(data).all():
        raise ValueError("unsupported_raw_geometry")
    image = np.ascontiguousarray(data.reshape(h//2, 2, w//2, 2).mean((1, 3)), dtype=np.float32)
    bg = sep.Background(image, bw=64, bh=64, fw=3, fh=3)
    sources = sep.extract(image-bg.back(), 3.5, err=np.maximum(bg.rms(), .001),
                          minarea=5, deblend_nthresh=32, deblend_cont=.005)
    rows = []
    for s in sources:
        x, y, a, b, flux = [float(s[k]) for k in ("x", "y", "a", "b", "flux")]
        fwhm = 2.35482*math.sqrt((a*a+b*b)/2)
        if (not all(math.isfinite(v) for v in (x, y, a, b, flux)) or flux <= 0
                or min(x, y, w/2-1-x, h/2-1-y) < 8
                or not .9 <= fwhm <= 16 or a/max(b, .01) > 3):
            continue
        rows.append(dict(x=(x+.5)*2-.5, y=(y+.5)*2-.5, flux=flux,
                         rawFwhm=2*fwhm, sepFlags=int(s["flag"])))
    return sorted(rows, key=lambda r: r["flux"], reverse=True)[:300]


def point_render(rows, shape):
    data = np.zeros(shape, dtype=np.float32)
    for r in rows[:60]:
        x, y = r["x"], r["y"]
        ix, iy = int(x), int(y)
        fx, fy = x-ix, y-iy
        data[iy, ix] += (1-fx)*(1-fy)
        data[iy, ix+1] += fx*(1-fy)
        data[iy+1, ix] += (1-fx)*fy
        data[iy+1, ix+1] += fx*fy
    return ndimage.gaussian_filter(data, 2.)*300000+1000


def read_tile(path):
    payload = path.read_bytes()
    if len(payload) < 115 or len(payload) > 20_000_000 or payload[109] != 5 or (len(payload)-110) % 5:
        raise ValueError("unsupported_catalogue_tile")
    records = np.frombuffer(payload, dtype=np.uint8, offset=110).reshape(-1, 5).astype(np.int32)
    header = (records[:, :3] == 255).all(axis=1)
    if not header[0]:
        raise ValueError("catalogue_header_missing")
    prior = np.maximum.accumulate(np.where(header, np.arange(len(records)), 0))
    ra = (records[:, 0]+256*records[:, 1]+65536*records[:, 2])*360/16777215
    dec = (records[:, 3]+256*records[:, 4]+65536*(records[prior, 3]-128))*90/8388607
    mag = (records[prior, 4]-16)/10
    return np.column_stack((ra[~header], dec[~header], mag[~header])), hashlib.sha256(payload).hexdigest(), payload[:109].decode("ascii")


def catalogue(database, ra, dec, radius):
    stars, tiles = [], []
    # Conservative spherical cap bounds; include all RA near a pole.
    ra_radius = 180. if abs(dec)+radius >= 90 else math.degrees(math.asin(min(1., math.sin(math.radians(radius))/math.cos(math.radians(dec)))))
    for ring, cells in enumerate(RING_CELLS):
        if RING_EDGES[ring] > dec+radius or RING_EDGES[ring+1] < dec-radius:
            continue
        for cell in range(cells):
            center = (cell+.5)*360/cells
            distance = abs((center-ra+180) % 360-180)
            if distance > ra_radius+180/cells:
                continue
            path = database/f"d80_{ring+1:02d}{cell+1:02d}.1476"
            data, digest, epoch = read_tile(path)
            # Crop conservatively before projecting; read all relevant tiles,
            # never mistake a missing tile for an empty region.
            keep = (np.abs(data[:, 1]-dec) <= radius) & (np.abs((data[:, 0]-ra+180) % 360-180) <= ra_radius)
            stars.append(data[keep])
            tiles.append(dict(file=path.name, sha256=digest, description=epoch.strip()))
    return np.concatenate(stars) if stars else np.empty((0, 3)), tiles


def matches(predicted, measured, radius=3.):
    if not len(predicted) or not len(measured):
        return []
    distances, indices = cKDTree(measured).query(predicted)
    used, result = set(), []
    for i in np.argsort(distances):
        k = int(indices[i])
        if distances[i] <= radius and k not in used:
            used.add(k)
            result.append(dict(catalogueIndex=int(i), detectionIndex=k, residualPixels=float(distances[i])))
    return result


def verify(wcs, rows, shape, database, expected_scale, hint, radius):
    h, w = shape
    cd = wcs.pixel_scale_matrix
    center = wcs.wcs.crval
    scales = np.linalg.norm(cd, axis=0)*3600
    delta = np.radians(center-np.array(hint))
    sep_deg = math.degrees(2*math.asin(math.sqrt(min(1., math.sin(delta[1]/2)**2
        +math.cos(math.radians(center[1]))*math.cos(math.radians(hint[1]))*math.sin(delta[0]/2)**2))))
    if (list(wcs.wcs.ctype) != ["RA---TAN", "DEC--TAN"] or wcs.sip is not None
            or not np.isfinite(cd).all() or not np.isfinite(center).all()
            or abs(center[1]) > 90 or sep_deg > radius
            or np.any(np.abs(scales/expected_scale-1) > .1)
            or abs(np.linalg.det(cd)) < 1e-14):
        return dict(accepted=False, reason="implausible_wcs")
    stars, tiles = catalogue(database, *center, float(np.linalg.norm(shape)*max(scales)/3600))
    xy = wcs.all_world2pix(stars[:, :2], 0)
    inside = np.isfinite(xy).all(axis=1) & (xy[:, 0] > 8) & (xy[:, 0] < w-8) & (xy[:, 1] > 8) & (xy[:, 1] < h-8)
    xy, stars = xy[inside], stars[inside]
    measured = np.array([[r["x"], r["y"]] for r in rows])
    pairs = matches(xy, measured)
    for p in pairs:
        p.update(catalogueRa=float(stars[p["catalogueIndex"], 0]), catalogueDec=float(stars[p["catalogueIndex"], 1]),
                 predicted=xy[p["catalogueIndex"]].tolist(), measured=measured[p["detectionIndex"]].tolist())
    selected = xy[[p["catalogueIndex"] for p in pairs]]
    span = np.ptp(selected, axis=0) if len(selected) else np.zeros(2)
    rng = np.random.default_rng(40848)
    offsets = rng.uniform(40, 350, (100, 2))*rng.choice([-1, 1], (100, 2))
    negatives = [len(matches(xy+offset, measured)) for offset in offsets]
    accepted = len(pairs) >= 5 and span[0] >= .2*w and span[1] >= .2*h and max(negatives) < len(pairs)
    return dict(accepted=bool(accepted), reason="raw_catalogue_verified" if accepted else "raw_support_insufficient",
                matches=pairs, spanPixels=span.tolist(), negativeCounts=negatives, catalogueTiles=tiles,
                independentCatalogue=False, rawScale=float(np.sqrt(abs(np.linalg.det(cd)))*3600),
                ra=float(center[0]), dec=float(center[1]), crpix=(wcs.wcs.crpix-1).tolist(), cd=cd.tolist())


def recover(request, output):
    started = time.monotonic()
    raw = Path(request["sourcePath"]).resolve(strict=True)
    if raw == output or raw.is_relative_to(output) or output.is_relative_to(raw.parent):
        raise ValueError("output_must_be_disjoint_from_raw_directory")
    digest = sha(raw)
    if digest.lower() != request["sourceSha256"].lower():
        raise ValueError("source_hash_changed")
    result = dict(schema=1, algorithm=ALGORITHM, accepted=False, sourceSha256=digest,
                  targetIdentityConfirmed=False, motionAuthorized=False)
    try:
        data = fits.getdata(raw).astype(np.float32)
        if data.ndim != 2:
            raise ValueError("monochrome_required")
        result.update(width=data.shape[1], height=data.shape[0], clippedFraction=float(np.mean(data >= 65520)))
        if result["clippedFraction"] > .5:
            result["reason"] = "majority_clipped"
            return result
        rows = detect(data)
        result["detections"] = rows
        if len(rows) < 5:
            result["reason"] = "too_few_measured_sources"
            return result
        header = fits.Header()
        header["SYNTHET"], header["MEASURED"], header["RAWSHA"] = True, True, digest
        header["METHOD"] = ALGORITHM
        points = output/"solver-only-points.fit"
        fits.writeto(points, point_render(rows, data.shape), header)
        exe = Path(request["astapPath"]).resolve(strict=True)
        if sha(exe).lower() != request["astapSha256"].lower():
            raise ValueError("solver_hash_changed")
        db = Path(request["databasePath"]).resolve(strict=True)
        prefix = output/"astap"
        radius = min(2., request["radiusDegrees"])
        command = [str(exe), "-f", str(points), "-ra", str(request["ra"]/15), "-spd", str(request["dec"]+90),
                   "-fov", str(data.shape[0]*request["scale"]/3600), "-r", str(radius), "-z", "1", "-m", "1.5",
                   "-d", str(db), "-D", "d80", "-o", str(prefix), "-wcs", "-log"]
        child = subprocess.run(command, capture_output=True, text=True, errors="replace", timeout=22,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        (output/"solver-output.txt").write_text(child.stdout+child.stderr, encoding="utf-8")
        result.update(solverExitCode=child.returncode, solverSha256=request["astapSha256"], pointRenderSha256=sha(points))
        if child.returncode != 0 or not prefix.with_suffix(".wcs").is_file():
            result["reason"] = "solver_no_solution"
            return result
        result.update(verify(WCS(fits.getheader(prefix.with_suffix(".wcs"))), rows, data.shape, db,
                             request["scale"], (request["ra"], request["dec"]), radius))
        return result
    finally:
        result["elapsedSeconds"] = time.monotonic()-started
        if sha(raw) != digest:
            raise ValueError("source_hash_changed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(exist_ok=False, parents=True)
    try:
        result = recover(json.loads(args.request.read_text(encoding="utf-8-sig")), output)
    except Exception as error:
        result = dict(schema=1, accepted=False, reason=str(error), algorithm=ALGORITHM,
                      targetIdentityConfirmed=False, motionAuthorized=False)
    (output/"result.json").write_text(json.dumps(result, ensure_ascii=False, allow_nan=False), encoding="utf-8")


if __name__ == "__main__":
    main()
