"""SEP-based image measurements, not a target-identity or motion authority.

Input is an un-stretched monochrome array. SEP owns background, segmentation,
moments and aperture photometry. Our policy preserves connected parent regions
alongside SEP's deblended children instead of treating every knot as a star.
Coordinates are zero-based pixel centres in the original image.
"""
from dataclasses import asdict, dataclass
import math
import numpy as np
import sep

VERSION = "sep-parent-energy-v3"
MAX_PIXELS = 32_000_000


@dataclass(frozen=True)
class Settings:
    threshold_sigma: float = 3.5
    minimum_area: int = 5
    saturation: float = 65520.0
    maximum_sources: int = 1000


def detect(image, settings=Settings()):
    data = np.array(image, dtype=np.float32, order="C", copy=True)
    if data.ndim != 2 or min(data.shape) < 16 or data.size > MAX_PIXELS:
        raise ValueError("Expected a bounded monochrome image (minimum 16 x 16).")
    if not np.isfinite(data).all():
        raise ValueError("Non-finite pixels require an explicit bad-pixel mask; not silently filled.")
    if not (1.5 <= settings.threshold_sigma <= 20 and 3 <= settings.minimum_area <= 100):
        raise ValueError("Invalid detection threshold or area.")
    if not math.isfinite(settings.saturation) or settings.saturation <= 0:
        raise ValueError("Invalid ADU saturation level.")
    if not 1 <= settings.maximum_sources <= 2000:
        raise ValueError("Invalid source limit.")
    # Large diffuse artifacts are still reported, never promoted to point targets.
    background = sep.Background(data, bw=64, bh=64, fw=3, fh=3)
    background_map = background.back()
    residual = data - background_map
    noise = np.maximum(background.rms(), 1e-3)
    parents, segmentation = sep.extract(
        residual, settings.threshold_sigma, err=noise,
        minarea=settings.minimum_area, deblend_cont=1.0, clean=False,
        segmentation_map=True,
    )
    children, child_segmentation = sep.extract(residual, settings.threshold_sigma, err=noise,
                           minarea=settings.minimum_area, deblend_cont=0.005,
                           deblend_nthresh=32, clean=False, segmentation_map=True)
    child_groups = {}
    components = []
    for child_index, child in enumerate(children):
        x, y = int(child["xpeak"]), int(child["ypeak"])
        parent_id = int(segmentation[y, x])
        child_groups.setdefault(parent_id, []).append({
            "x": float(child["x"]), "y": float(child["y"]),
            "flux": float(child["flux"]), "flags": int(child["flag"]),
        })
        # The deblended SEP segmentation, not our former peak/contour finder,
        # defines each measured component. Keep non-circular PSFs. A MERGED
        # flag (bit 1) means deblended, not a failed measurement.
        xmin, xmax = int(child['xmin']), int(child['xmax'])
        ymin, ymax = int(child['ymin']), int(child['ymax'])
        region = child_segmentation[ymin:ymax+1, xmin:xmax+1] == child_index + 1
        values = data[ymin:ymax+1, xmin:xmax+1][region]
        signal = residual[ymin:ymax+1, xmin:xmax+1][region]
        errors = noise[ymin:ymax+1, xmin:xmax+1][region]
        support = int(np.count_nonzero(signal > settings.threshold_sigma * errors))
        # Reject single-pixel impulses enlarged by SEP's convolution kernel.
        # Do this before the protocol cap; never truncate by proximity to the
        # expected target or silently claim all candidates were considered.
        if len(values) < 9 or support < 3:
            continue
        components.append({
            'x': float(child['x']), 'y': float(child['y']),
            'a': float(child['a']), 'b': float(child['b']),
            'flux': float(child['flux']), 'peak': float(values.max()),
            'snr': float(child['flux'] / np.sqrt(np.sum(errors ** 2))),
            'saturated_fraction': float(np.mean(values >= settings.saturation)),
            'raw_support_pixels': support, 'npix': len(values),
            'bbox': [xmin, ymin, xmax-xmin+1, ymax-ymin+1],
            'sep_flags': int(child['flag']), 'parent_id': parent_id,
        })
    parent_components = []
    for index, obj in enumerate(parents):
        xmin, xmax = int(obj['xmin']), int(obj['xmax'])
        ymin, ymax = int(obj['ymin']), int(obj['ymax'])
        region = segmentation[ymin:ymax+1, xmin:xmax+1] == index + 1
        values = data[ymin:ymax+1, xmin:xmax+1][region]
        signal = residual[ymin:ymax+1, xmin:xmax+1][region]
        errors = noise[ymin:ymax+1, xmin:xmax+1][region]
        support = int(np.count_nonzero(signal > settings.threshold_sigma * errors))
        if len(values) < 9 or support < 3:
            continue
        parent_components.append({
            'x': float(obj['x']), 'y': float(obj['y']), 'a': float(obj['a']), 'b': float(obj['b']),
            'flux': float(obj['flux']), 'peak': float(values.max()),
            'snr': float(obj['flux'] / np.sqrt(np.sum(errors ** 2))),
            'saturated_fraction': float(np.mean(values >= settings.saturation)),
            'raw_support_pixels': support, 'npix': len(values),
            'bbox': [xmin,ymin,xmax-xmin+1,ymax-ymin+1],
            'sep_flags': int(obj['flag']), 'parent_id': int(index+1),
        })
    selected = np.argsort(parents["flux"])[::-1][:settings.maximum_sources]
    height, width = data.shape
    sources = []
    for index in selected:
        obj = parents[index]
        sid = int(index + 1)
        x, y = float(obj["x"]), float(obj["y"])
        a, b = float(obj["a"]), float(obj["b"])
        xmin, xmax = int(obj["xmin"]), int(obj["xmax"])
        ymin, ymax = int(obj["ymin"]), int(obj["ymax"])
        radius = min(128.0, max(6.0, 4.0 * a))
        edge = min(x, y, width - 1 - x, height - 1 - y) < radius
        flux, error, aperture_flags = sep.sum_circle(
            residual, np.array([x]), np.array([y]), radius, err=noise,
            segmap=segmentation, seg_id=np.array([sid], dtype=np.int32), subpix=5)
        # SEP's flux-radius integration uses background-subtracted pixels, NOT
        # stretched display intensities or a fitted circular Gaussian.
        radii, radius_flags = sep.flux_radius(
            residual, np.array([x]), np.array([y]), np.array([radius]), [0.5, 0.8],
            normflux=flux, segmap=segmentation,
            seg_id=np.array([sid], dtype=np.int32), subpix=5)
        r50, r80 = map(float, radii[0])
        region = segmentation[ymin:ymax + 1, xmin:xmax + 1] == sid
        values = data[ymin:ymax + 1, xmin:xmax + 1][region]
        saturated = float(np.mean(values >= settings.saturation))
        snr = float(flux[0] / error[0]) if error[0] > 0 else 0.0
        support = int(np.count_nonzero(
            residual[ymin:ymax + 1, xmin:xmax + 1][region]
            > settings.threshold_sigma * noise[ymin:ymax + 1, xmin:xmax + 1][region]))
        parts = child_groups.get(sid, [])
        flags = []
        if edge:
            flags.append("aperture_at_edge")
        if saturated > 0:
            flags.append("saturated")
        if len(parts) > 1:
            flags.append("multiple_deblend_components")
        if a > 32 or radius >= 128:
            flags.append("extended_or_aperture_limited")
        if int(aperture_flags[0]) or int(radius_flags[0]):
            flags.append("aperture_flagged")
        if snr < 10:
            flags.append("low_snr")
        if int(obj["npix"]) < 9 or support < 3:
            flags.append("undersampled_region")
        finite_energy = math.isfinite(r50) and math.isfinite(r80) and 0 < r50 <= r80 < radius
        if not finite_energy:
            flags.append("energy_radius_unreliable")
        sources.append({
            "id": sid, "x": x, "y": y, "a": a, "b": b,
            "theta": float(obj["theta"]), "axis_ratio": b / a if a > 0 else 0.0,
            "npix": int(obj["npix"]), "raw_support_pixels": support,
            "flux": float(flux[0]), "snr": snr,
            "peak": float(values.max()), "background": float(background_map[int(y), int(x)]),
            "r50": r50 if math.isfinite(r50) else None,
            "r80": r80 if math.isfinite(r80) else None,
            "aperture_radius": radius, "saturated_fraction": saturated,
            "bbox": [xmin, ymin, xmax - xmin + 1, ymax - ymin + 1],
            "sep_flags": int(obj["flag"]), "flags": flags, "children": parts,
            # Detection survives shape/quality failures; only its use in an
            # unlabelled focus curve is withheld. Never a motion decision.
            "focus_eligible": not flags,
        })
    good = [s["r50"] for s in sources if s["focus_eligible"]]
    median = float(np.median(good)) if good else None
    return {
        "schema": 1, "algorithm": VERSION, "sep_version": sep.__version__,
        "settings": asdict(settings), "width": width, "height": height,
        "background": float(background.globalback), "noise": float(background.globalrms),
        "total_parent_regions": len(parents), "truncated": len(parents) > len(sources),
        "sources": sources, "focus_count": len(good), "median_r50": median,
        "components": sorted(components, key=lambda c: c['flux'], reverse=True)[:2000],
        "components_truncated": len(components) > 2000,
        "parent_components": sorted(parent_components, key=lambda c: c['flux'], reverse=True)[:2000],
        "parent_components_truncated": len(parent_components) > 2000,
        "mad_r50": float(np.median(np.abs(np.array(good) - median))) if good else None,
        "target_identity_confirmed": False, "motion_authorized": False,
        "snr_definition": "aperture_flux_over_background_error_no_source_poisson_gain",
    }
