"""Read-only historical target tracking/forced-photometry audit, never a controller.

No bright-peak substitution: field registration excludes the target. Forced
apertures stay at the externally anchored/registered coordinates. A signal in
such an aperture does NOT establish identity, centroid, single-star morphology
or permission for slit motion. Uses the existing star-detection environment.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import time

from astropy.io import fits
import numpy as np
from scipy import ndimage
from scipy.optimize import minimize
import sep

from replay_sparse_g3 import apply, register

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/"star-detection"))
from sep_detector import Settings, detect  # noqa: E402


CASES = [
    dict(name="hd219937_oct14_success", run="UVEX-20261014T112329Z-1862faa86ec84de",
         prefixes=["00049-", "00051-", "00053-"], point=[857.0677419354839, 462.258064516129],
         source="First residual's measured target position; previous on-sky run accepted two 300s spectra",
         identity="historical-catalogue-chain", aperture=12),
    dict(name="hd219937_oct23_false_peak", run="UVEX-20261023T123550Z-ed9c198f39b64e7",
         prefixes=["00075-", "00080-", "00082-", "00084-", "00086-"], point=[970.597222222222, 420.675925925926],
         source="Audited bright body in selection frame, NOT independently proven HD219937 catalogue identity",
         identity="body-continuity-only-not-catalogue-id", aperture=12),
    dict(name="hd21641_oct14_first_anchor_failure", run="UVEX-20261014T185823Z-4f3592c3a03d4af",
         prefixes=["00080-", "00087-", "00089-", "00091-"], point=[790.4858757062146, 411.40112994350284],
         source="Measured pre-guide target in RecordedPlacementContinuityTests, independent of subsequent mistaken peak",
         identity="historical-catalogue-chain", aperture=12),
    dict(name="hd218393_oct21_short_misses", run="UVEX-20261021T140225Z-e9a326e30a0c404",
         prefixes=["00044-", "00046-", "00048-"], point=[863.4387056541422, 402.7035622373459],
         source="First 10ms SEP parent measurement, later two native short frames were unmeasured",
         identity="one-anchor-not-an-independent-repeat", aperture=7),
    dict(name="hd224559_oct21_multilobe", run="UVEX-20261021T140550Z-203e6c44012c406",
         prefixes=["00050-", "00052-"], point=[828.184, 445.047],
         source="Audited whole parent in first 90ms exposure; persistent multi-lobe rejection must remain",
         identity="unresolved-parent-not-single-star-proof", aperture=16),
]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def finite(value):
    if isinstance(value, dict):
        return {k: finite(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [finite(v) for v in value]
    if isinstance(value, np.generic):
        value = value.item()
    if isinstance(value, float) and not np.isfinite(value):
        return None
    return value


def extract_parents(image, point):
    """Mirror production CatalogFieldRegistration.ReferenceStars, then 24-star cap."""
    measured = detect(image, Settings(saturation=65535))
    rows = []
    for parent in measured["parent_components"]:
        x, y = parent["x"], parent["y"]
        box = parent["bbox"]
        if (parent["sep_flags"] == 0 and parent["flux"] > 0 and parent["snr"] >= 5
                and parent["saturated_fraction"] == 0 and parent["npix"] >= 9
                and parent["raw_support_pixels"] >= 3 and box[0] >= 8 and box[1] >= 8
                and box[0]+box[2] <= image.shape[1]-8 and box[1]+box[3] <= image.shape[0]-8
                and 1-parent["b"]/parent["a"] <= .65 and np.linalg.norm(np.array([x, y])-point) > 100):
            rows.append(dict(x=x, y=y, snr=parent["snr"], flux=parent["flux"]))
    rows.sort(key=lambda star: star["snr"], reverse=True)
    return dict(stars=rows[:24], allUsableFieldParents=len(rows),
                parentCount=len(measured["parent_components"]),
                localParents=[dict(x=p["x"], y=p["y"], snr=p["snr"], npix=p["npix"],
                                   flags=p["sep_flags"], saturated=p["saturated_fraction"])
                              for p in measured["parent_components"]
                              if np.linalg.norm(np.array([p["x"], p["y"]])-point) <= 30])


def spread_supported(registration, width, height):
    if not registration.get("accepted"):
        return False
    triangle = np.array([registration["pairs"][i]["reference"] for i in registration["trainingIndices"]])
    left, right = triangle[1]-triangle[0], triangle[2]-triangle[0]
    area = abs(float(left[0]*right[1]-left[1]*right[0]))
    registration["trainingTriangleDoubleArea"] = area
    if area < max(400, width*height*.005):
        registration["accepted"] = False
        registration["reason"] = "Insufficient two-dimensional training-star spread"
    return registration["accepted"]


def recorded_measurements(folder, path):
    answer = []
    for source in folder.glob("*.json"):
        try:
            document = json.loads(source.read_text(encoding="utf-8-sig"))
            bound = (document.get("source") or {}).get("absolutePath", "")
            if Path(bound).name.lower() != path.name.lower():
                continue
            payload = document.get("payload") or {}
            item = dict(file=source.name, kind=document.get("kind"),
                        inputSha256=document.get("source", {}).get("sha256"),
                        accepted=payload.get("accepted"))
            if "measurement" in payload:
                measurement = payload["measurement"]
                item["recordedTarget"] = measurement.get("targetCentroid")
                item["recordedPrimary"] = measurement.get("guideStar")
                item["measurementGate"] = measurement.get("gate", {}).get("code")
                item["identification"] = measurement.get("identification")
            if "shortTarget" in payload:
                item["shortTarget"] = payload["shortTarget"]
                item["confirmationGate"] = payload.get("confirmation", {}).get("gate", {}).get("code")
            if "stellar" in payload:
                item["rejectedStellar"] = payload["stellar"]
            if "expectedTargetLocal" in payload:
                item["oldPrediction"] = payload["expectedTargetLocal"]
            answer.append(item)
        except (OSError, ValueError):
            continue
    return answer


def measured_stamp(image, point, radius=20):
    yy, xx = np.mgrid[-radius:radius+1, -radius:radius+1]
    return ndimage.map_coordinates(image, [yy+point[1], xx+point[0]], order=1, mode="constant", cval=np.nan)


def forced(image, point, radius):
    bg = sep.Background(image, bw=64, bh=64, fw=3, fh=3)
    clean = image-bg.back()
    flux, error, flag = sep.sum_circle(clean, [point[0]], [point[1]], radius, err=bg.rms(), subpix=5)
    # These are fixed-coordinate aperture diagnostics, not a newly selected peak.
    local = measured_stamp(clean, point, radius=radius)
    mask = np.hypot(*np.mgrid[-radius:radius+1, -radius:radius+1]) <= radius
    positive = np.maximum(local, 0)*mask
    mass = np.nansum(positive)
    offsets = np.mgrid[-radius:radius+1, -radius:radius+1]
    photocenter = [point[0]+float(np.nansum(positive*offsets[1])/mass),
                  point[1]+float(np.nansum(positive*offsets[0])/mass)] if mass > 0 else None
    raw_local = measured_stamp(image, point, radius)
    # Local maxima count is a diagnostic of possible multi-lobes/companions,
    # not a label assigning any peak to the intended object.
    smooth = ndimage.gaussian_filter(local, 1)
    peaks = (smooth == ndimage.maximum_filter(smooth, size=5)) & mask
    peaks &= smooth >= max(0., 5*float(bg.globalrms))
    return dict(position=point, radiusPixels=radius, forcedFluxAdu=float(flux[0]),
                backgroundNoiseAdu=float(bg.globalrms), backgroundOnlyErrorAdu=float(error[0]),
                backgroundOnlySnr=float(flux[0]/error[0]) if error[0] else None,
                apertureFlags=int(flag[0]), peakAdu=float(np.nanmax(raw_local)),
                saturatedPixelCount=int(np.sum(raw_local[mask] >= 65520)),
                positivePhotocenterDiagnostic=photocenter, significantLocalMaxima=int(np.sum(peaks)),
                centroidAuthority=False, photonNoiseCalibrated=False, identityConfirmed=False)


def template_comparison(reference, current, original_point, prediction):
    template = measured_stamp(reference, original_point)
    template = template-np.nanmedian(template)
    norm = np.linalg.norm(template)
    if not np.isfinite(norm) or norm < 1:
        return dict(usable=False, reason="No supported original template")
    template = template/norm

    def cost(offset):
        sample = measured_stamp(current, np.asarray(prediction)+offset)
        sample = sample-np.nanmedian(sample)
        denominator = np.linalg.norm(sample)
        if not np.isfinite(denominator) or denominator < 1:
            return 2.
        return 1-float(np.nansum(sample*template)/denominator)

    fit = minimize(cost, [0., 0.], method="Powell", bounds=[(-4, 4), (-4, 4)],
                   options=dict(maxiter=40, xtol=.025, ftol=1e-5))
    return dict(usable=bool(fit.success), correlation=float(1-fit.fun),
                boundedOffsetPixels=fit.x.tolist(), maximumAllowedOffsetPixels=4,
                identitySource="fixed original stamp, no peak/brightness reassignment",
                positionAuthority=False, useForMotion=False)


def analyze(case, observations, production_registration=None):
    folder = observations/case["run"]/"evidence"
    paths = []
    for prefix in case["prefixes"]:
        candidates = sorted(folder.glob(prefix+"*.fit"))
        if len(candidates) != 1:
            raise ValueError(f"Expected exactly one source {case['run']}/{prefix}: {candidates}")
        paths.append(candidates[0])
    hashes = {str(p): digest(p) for p in paths}
    images = [np.ascontiguousarray(fits.getdata(p), dtype=np.float32) for p in paths]
    headers = [fits.getheader(p) for p in paths]
    extractions, extraction_seconds = [], []
    for image in images:
        started = time.monotonic()
        extractions.append(extract_parents(image, case["point"]))
        extraction_seconds.append(time.monotonic()-started)
    transforms = [np.eye(3)]
    frames = []
    for index, (path, image, header, extraction) in enumerate(zip(paths, images, headers, extractions)):
        start = time.monotonic()
        registration = None
        transform = np.eye(3) if index == 0 else None
        matched_to = None
        if index:
            for prior in range(index-1, -1, -1):
                if transforms[prior] is None:
                    continue
                candidate = register(extractions[prior], extraction, minimum_holdout=2, maximum_rotation=5)
                if spread_supported(candidate, image.shape[1], image.shape[0]):
                    registration = candidate
                    transform = np.asarray(candidate["matrix"])@transforms[prior]
                    matched_to = prior
                    break
        if production_registration is not None:
            native = next(f for f in production_registration[case["name"]] if f["file"] == path.name)
            if native["sourceSha256"].upper() != hashes[str(path)]:
                raise ValueError("C# registration report belongs to different immutable source pixels")
            transform = np.asarray(native["cumulativeMatrix"]) if native["cumulativeMatrix"] is not None else None
            registration = native["registration"]
            matched_to = native["referenceIndex"]
        if index:
            transforms.append(transform)
        registered = transform is not None
        # Unregistered forced aperture remains at the old position and is
        # explicitly NOT evidence that the object did not drift.
        prediction = apply(transform, np.asarray([case["point"]]))[0].tolist() if registered else case["point"]
        result = dict(file=path.name, sha256=hashes[str(path)],
                      extractionSeconds=extraction_seconds[index],
                      exposureSeconds=header.get("EXPOSURE", header.get("EXPTIME")),
                      exposureStartUtc=header.get("DATE-OBS"), fieldStars=len(extraction["stars"]),
                      registrationStars=extraction["stars"],
                      width=image.shape[1], height=image.shape[0],
                      allUsableFieldParents=extraction["allUsableFieldParents"], localParents=extraction["localParents"],
                      independentRegistration=registered and index > 0,
                      registrationReferenceIndex=matched_to, registration=registration,
                      position=prediction, positionWasIndependentlyUpdated=registered and index > 0,
                      oldAnchorOnly=not registered and index > 0,
                      forced=forced(image, prediction, case["aperture"]),
                      template=template_comparison(images[0], image, case["point"], prediction),
                      originalReceipts=recorded_measurements(folder, path))
        if production_registration is not None:
            result["productionRegistrationMilliseconds"] = native["registrationMilliseconds"]
        result["recordedAlternativeDiagnostics"] = []
        if registered and index > 0:
            seen = set()
            for receipt in result["originalReceipts"]:
                old = receipt.get("recordedTarget")
                if not isinstance(old, dict) or not all(k in old for k in ["x", "y"]):
                    continue
                point = [old["x"], old["y"]]
                if tuple(point) in seen or np.linalg.norm(np.array(point)-prediction) <= 6:
                    continue
                seen.add(tuple(point))
                result["recordedAlternativeDiagnostics"].append(dict(
                    receipt=receipt["file"], oldPoint=point,
                    displacementFromRegisteredBody=float(np.linalg.norm(np.array(point)-prediction)),
                    forced=forced(image, point, case["aperture"]),
                    template=template_comparison(images[0], image, case["point"], point),
                    notACompanionClassification=True))
        result["analysisSeconds"] = time.monotonic()-start
        frames.append(result)
    after = {str(p): digest(p) for p in paths}
    return dict(**case, frames=frames, sourceHashesUnchanged=hashes == after,
                sourceVerification=[dict(path=str(p),before=hashes[str(p)],after=after[str(p)]) for p in paths],
                originalPixelsWritten=False, algorithmDoesNotGrantMotion=True)


def inventory(root):
    files = list(root.rglob("*.fit"))+list(root.rglob("*.fits"))
    runs = {p.relative_to(root).parts[0] for p in files}
    return dict(fits=len(files), runsWithFits=len(runs),
                g3Fits=sum("g3-" in p.name for p in files),
                lockResidualFits=sum("g3-phd2-lock-residual.fit" in p.name for p in files),
                inventoryIsNotAnOnSkySuccessCount=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--observations", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--registration-report", type=Path)
    args = parser.parse_args()
    root, out = args.observations.resolve(), args.output.resolve()
    if out.is_relative_to(root) or out.exists():
        parser.error("Use a new output directory outside immutable observations")
    out.mkdir(parents=True)
    production_registration = json.loads(args.registration_report.read_text()) if args.registration_report else None
    report = dict(schema=1, actorScope="offline-history-replay", noHardwareIO=True,
                  motionAuthority=False, identityAuthority=False,
                  scriptSha256=digest(Path(__file__)), sepVersion=sep.__version__,
                  sepModuleSha256=digest(Path(__file__).resolve().parents[1]/"star-detection"/"sep_detector.py"),
                  registrationBackend="production C# image-only core" if production_registration else "Python offline prototype",
                  registrationReportSha256=digest(args.registration_report) if args.registration_report else None,
                  registrationContext=production_registration.get("_context") if production_registration else None,
                  utc=datetime.now(timezone.utc).isoformat(), inventory=inventory(root),
                  cases=[analyze(case, root, production_registration) for case in CASES])
    (out/"report.json").write_text(json.dumps(finite(report), indent=2, allow_nan=False), encoding="utf-8")
    summary = dict(inventory=report["inventory"], cases=[dict(name=case["name"],
        unchanged=case["sourceHashesUnchanged"], frames=[dict(file=f["file"],stars=f["fieldStars"],
            registered=f["independentRegistration"],position=f["position"],
            forcedSnr=f["forced"]["backgroundOnlySnr"],flux=f["forced"]["forcedFluxAdu"],
            peaks=f["forced"]["significantLocalMaxima"],correlation=f["template"].get("correlation"))
            for f in case["frames"]]) for case in report["cases"]])
    (out/"summary.json").write_text(json.dumps(finite(summary), indent=2, allow_nan=False), encoding="utf-8")
    print(json.dumps(finite(summary), indent=2))


if __name__ == "__main__":
    main()
