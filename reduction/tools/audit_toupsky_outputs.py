"""Audit every retained reduction product whose science input is in ToupSky.

The raw camera-buffer incident ledger is intentionally explicit.  This tool
checks configs, run manifests, source headers, derived-product fingerprints and
the deterministic May validation harness without changing a FITS file.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from fnmatch import fnmatch
import hashlib
import json
import os
from pathlib import Path
import tomllib

from astropy.io import fits


PROJECT_ROOT = Path(__file__).resolve().parents[2]
REDUCTION_ROOT = PROJECT_ROOT / "reduction"
SOURCE_ROOT = Path("<local-data-root>")
OUTPUT_ROOT = REDUCTION_ROOT / "output"
INTERNAL_RUNS_ROOT = OUTPUT_ROOT / "_internal" / "runs"
INTERNAL_QUALITY_ROOT = OUTPUT_ROOT / "_internal" / "quality"
DEFAULT_DESTINATION = (
    INTERNAL_QUALITY_ROOT / "toupsky-audit" / "toupsky_output_audit.json"
)

# Relative paths are the complete commissioned repair ledger for all currently
# retained ToupSky science products.  A missing entry always means no roll.
REPAIR_LEDGER = {
    *(f"20260504/3C273/{name}" for name in (
        "260504212430.fit",
        "260504213430.fit",
        "260504223330.fit",
        "260504224331.fit",
        "260504225331.fit",
        "260504230331.fit",
        "260504231331.fit",
        "260504232331.fit",
        "260504233331.fit",
        "260504234332.fit",
        "260504235332.fit",
    )),
    *(f"20260505/NGC6543/{name}" for name in (
        "260505001743.fit",
        "260505002751.fit",
        "260505004637.fit",
        "260505005637.fit",
        "260505010637.fit",
        "260505011637.fit",
        "260505014331.fit",
        "260505015331.fit",
        "260505020331.fit",
        "260505021331.fit",
        "260505022332.fit",
        "260505023332.fit",
        "260505024332.fit",
        "260505025332.fit",
        "260505030332.fit",
    )),
    *(f"20260506凌晨/Arcturus/{name}" for name in (
        "260506003619.fit",
        "260506003622.fit",
        "260506003625.fit",
        "260506003628.fit",
    )),
    *(f"20260506凌晨/NGC6543/{name}" for name in (
        "260506010745.fit",
        "260506011745.fit",
        "260506012953.fit",
        "260506013453.fit",
        "260506013953.fit",
        "260506014453.fit",
        "260506014954.fit",
        "260506015454.fit",
    )),
    *(f"20260509/HD140573/{name}" for name in (
        "260509011102.fit",
        "260509011202.fit",
        "260509011302.fit",
        "260509011402.fit",
        "260509011502.fit",
    )),
    *(f"20260509/Vega/{name}" for name in (
        "260509014600.fit",
        "260509014714.fit",
        "260509014737.fit",
        "260509014749.fit",
        "260509014751.fit",
        "260509014754.fit",
        "260509014757.fit",
        "260509014759.fit",
    )),
}

MAY_HARNESS_MANIFESTS = {
    "Vega-20260509": (
        INTERNAL_RUNS_ROOT / "may-2026-validation" / "vega_standard_and_flat.json",
        {Path(item).name for item in REPAIR_LEDGER if item.startswith("20260509/Vega/")},
    ),
    "NGC6543-20260506": (
        INTERNAL_RUNS_ROOT
        / "may-2026-validation"
        / "ngc6543"
        / "NGC6543_validation.json",
        {Path(item).name for item in REPAIR_LEDGER if item.startswith("20260506凌晨/NGC6543/")},
    ),
    "HD140573-20260509": (
        INTERNAL_RUNS_ROOT
        / "may-2026-validation"
        / "hd140573"
        / "HD140573_validation.json",
        {Path(item).name for item in REPAIR_LEDGER if item.startswith("20260509/HD140573/")},
    ),
}

SKIP_PARTS = {"archive-invalid"}


def _is_skipped(path: Path) -> bool:
    return any(
        part.casefold() in SKIP_PARTS
        or "invalid" in part.casefold()
        or "do-not-use" in part.casefold()
        for part in path.parts
    )


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _source_key(path: str | Path) -> str | None:
    candidate = Path(path).resolve()
    try:
        return candidate.relative_to(SOURCE_ROOT).as_posix()
    except ValueError:
        return None


def _repair_records(payload: dict) -> list[dict]:
    records = payload.get("sdkWrapRepairs", [])
    return records if isinstance(records, list) else []


def _actual_repair_names(payload: dict) -> set[str]:
    return {
        str(record.get("file"))
        for record in _repair_records(payload)
        if isinstance(record, dict) and record.get("file")
    }


def _expected_repair_names(science: list[str]) -> set[str]:
    expected = set()
    for value in science:
        key = _source_key(value)
        if key in REPAIR_LEDGER:
            expected.add(Path(value).name)
    return expected


def _audit_run_manifests() -> list[dict]:
    audits = []
    for path in sorted(OUTPUT_ROOT.rglob("*_run.json")):
        if _is_skipped(path):
            continue
        payload = _load_json(path)
        science = [str(item) for item in payload.get("inputs", {}).get("science", [])]
        if not any(_source_key(item) is not None for item in science):
            continue
        expected = _expected_repair_names(science)
        actual = _actual_repair_names(payload)
        malformed = [
            record
            for record in _repair_records(payload)
            if record.get("appliedShiftPixels") != -64
            or record.get("direction") != "left"
            or record.get("sourceFitsModified") is not False
        ]
        issues = []
        if expected != actual:
            issues.append("repair-ledger-mismatch")
        if malformed:
            issues.append("invalid-repair-provenance")
        audits.append(
            {
                "manifest": str(path.resolve()),
                "target": payload.get("target"),
                "scienceFrameCount": len(science),
                "expectedRepairFiles": sorted(expected),
                "recordedRepairFiles": sorted(actual),
                "status": "pass" if not issues else "fail",
                "issues": issues,
            }
        )
    return audits


def _expand_science(root: Path, patterns: list[str]) -> list[Path]:
    found: set[Path] = set()
    for pattern in patterns:
        candidate = Path(pattern)
        if candidate.is_absolute():
            parent = candidate.parent
            matches = parent.glob(candidate.name)
        else:
            matches = root.glob(pattern)
        found.update(item.resolve() for item in matches if item.is_file())
    return sorted(found, key=lambda item: str(item).casefold())


def _pattern_matches(path: Path, pattern: str) -> bool:
    candidates = (str(path).casefold(), path.as_posix().casefold(), path.name.casefold())
    folded = str(pattern).casefold()
    return any(fnmatch(candidate, folded) for candidate in candidates)


def _audit_configs() -> tuple[list[dict], set[Path]]:
    audits = []
    all_science: set[Path] = set()
    for path in sorted((REDUCTION_ROOT / "configs").glob("*.toml")):
        with path.open("rb") as stream:
            payload = tomllib.load(stream)
        inputs = payload.get("inputs", {})
        root_value = str(inputs.get("root", "."))
        root = SOURCE_ROOT if root_value == "<local-data-root>" else Path(root_value)
        if root != SOURCE_ROOT and not root.is_absolute():
            root = (path.parent / root).resolve()
        elif root != SOURCE_ROOT:
            root = root.resolve()
        try:
            root.relative_to(SOURCE_ROOT)
        except ValueError:
            continue
        science = _expand_science(root, list(inputs.get("science", [])))
        all_science.update(science)
        preprocess = payload.get("preprocess", {})
        patterns = list(preprocess.get("sdk_wrap_fix_files", []))
        configured = {item.name for item in science if any(_pattern_matches(item, p) for p in patterns)}
        expected = _expected_repair_names([str(item) for item in science])
        auto_enabled = bool(preprocess.get("auto_detect_sdk_wrap", False))
        issues = []
        if auto_enabled:
            issues.append("automatic-mutation-enabled")
        if configured != expected:
            issues.append("explicit-list-mismatch")
        audits.append(
            {
                "config": str(path.resolve()),
                "scienceFrameCount": len(science),
                "automaticMutationEnabled": auto_enabled,
                "expectedRepairFiles": sorted(expected),
                "configuredRepairFiles": sorted(configured),
                "status": "pass" if not issues else "fail",
                "issues": issues,
            }
        )
    return audits, all_science


def _audit_sources(science: set[Path]) -> list[dict]:
    audits = []
    for path in sorted(science, key=lambda item: str(item).casefold()):
        key = _source_key(path)
        if key is None:
            continue
        with fits.open(path, memmap=False) as hdul:
            header = hdul[0].header
            sequence = int(header.get("SEQUENCE", -1))
            history = str(header.get("HISTORY", ""))
        expected = key in REPAIR_LEDGER
        history_modified = (
            "Fixed 64-pixel wrap-around bug" in history
            or "UVEX-ADV SDK wrap fix" in history
        )
        sequence_class = "large" if sequence >= 1_000_000 else "ordinary"
        sequence_consistent = expected == (sequence_class == "large")
        issues = []
        if history_modified:
            issues.append("source-fits-was-rewritten")
        if not sequence_consistent:
            issues.append("header-sequence-disagrees-with-ledger")
        audits.append(
            {
                "source": str(path),
                "relativePath": key,
                "expectedRepair": expected,
                "headerSequence": sequence,
                "sequenceClass": sequence_class,
                "sourceContainsRepairHistory": history_modified,
                "status": "pass" if not issues else "fail",
                "issues": issues,
            }
        )
    return audits


def _audit_may_harness() -> list[dict]:
    audits = []
    for name, (path, expected) in MAY_HARNESS_MANIFESTS.items():
        issues = []
        if not path.is_file():
            actual: set[str] = set()
            issues.append("missing-manifest")
        else:
            payload = _load_json(path)
            actual = set(payload.get("stack", {}).get("sdkWrapRepaired", []))
            if actual != expected:
                issues.append("repair-ledger-mismatch")
        audits.append(
            {
                "name": name,
                "manifest": str(path.resolve()),
                "expectedRepairFiles": sorted(expected),
                "recordedRepairFiles": sorted(actual),
                "status": "pass" if not issues else "fail",
                "issues": issues,
            }
        )
    return audits


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _audit_derived_products() -> list[dict]:
    audits = []
    for path in sorted(OUTPUT_ROOT.rglob("*_calibration.json")):
        if _is_skipped(path):
            continue
        payload = _load_json(path)
        provenance = payload.get("scienceSourceProvenance", {})
        source_value = provenance.get("path") or payload.get("scienceProduct")
        fingerprint = provenance.get("sha256")
        if not source_value or not fingerprint:
            continue
        source = Path(source_value)
        issues = []
        matches = source.is_file() and _sha256(source) == fingerprint
        if not matches:
            issues.append("science-source-fingerprint-mismatch")
        audits.append(
            {
                "manifest": str(path.resolve()),
                "scienceProduct": str(source),
                "fingerprintMatch": matches,
                "status": "pass" if not issues else "fail",
                "issues": issues,
            }
        )
    return audits


def build_audit() -> dict:
    if not SOURCE_ROOT.is_dir():
        raise RuntimeError(
            "ToupSky source root is not configured. Supply --source-root or set "
            "UVEX_ADV_TOUPSKY_ROOT to a readable local directory."
        )
    config_audits, science = _audit_configs()
    source_audits = _audit_sources(science)
    run_audits = _audit_run_manifests()
    harness_audits = _audit_may_harness()
    derived_audits = _audit_derived_products()
    sections = config_audits + source_audits + run_audits + harness_audits + derived_audits
    failed = sum(item["status"] != "pass" for item in sections)
    groups = {
        Path(item["relativePath"]).parts[0:2]
        for item in source_audits
    }
    return {
        "schemaVersion": 1,
        "frameIntegrityAudit": "ToupSky-retained-products",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "sourceRoot": str(SOURCE_ROOT),
        "status": "pass" if failed == 0 else "fail",
        "policy": {
            "automaticMutation": False,
            "repairSelection": "explicit-per-file-ledger",
            "sourceFitsModified": False,
        },
        "summary": {
            "groupsAudited": len(groups),
            "configsAudited": len(config_audits),
            "sourceFramesAudited": len(source_audits),
            "manifestsAudited": len(run_audits) + len(harness_audits),
            "derivedProductsAudited": len(derived_audits),
            "passed": len(sections) - failed,
            "failed": failed,
        },
        "repairLedger": sorted(REPAIR_LEDGER),
        "configAudits": config_audits,
        "sourceAudits": source_audits,
        "runManifestAudits": run_audits,
        "mayHarnessAudits": harness_audits,
        "derivedProductAudits": derived_audits,
    }


def main() -> int:
    global SOURCE_ROOT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source-root",
        type=Path,
        default=os.environ.get("UVEX_ADV_TOUPSKY_ROOT"),
        help="Local ToupSky source root (or set UVEX_ADV_TOUPSKY_ROOT).",
    )
    parser.add_argument("--write", type=Path, default=DEFAULT_DESTINATION)
    args = parser.parse_args()
    if args.source_root is None:
        parser.error(
            "--source-root is required when UVEX_ADV_TOUPSKY_ROOT is not set"
        )
    SOURCE_ROOT = args.source_root.expanduser().resolve()
    if not SOURCE_ROOT.is_dir():
        parser.error(f"source root does not exist or is not a directory: {SOURCE_ROOT}")
    audit = build_audit()
    destination = args.write.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(audit, ensure_ascii=False, indent=2), encoding="utf-8")
    print(
        f"ToupSky output audit: {audit['status']} "
        f"({audit['summary']['passed']} passed, {audit['summary']['failed']} failed)"
    )
    print(destination)
    return 0 if audit["status"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
