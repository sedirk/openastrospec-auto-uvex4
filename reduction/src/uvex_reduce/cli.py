from __future__ import annotations

import argparse
import logging
from pathlib import Path
import sys

from . import __version__
from .calibration import (
    DEFAULT_NEBULAR_LINES_ANGSTROM,
    apply_response_and_normalize,
    derive_relative_response,
    load_reduced_spectrum,
    refine_emission_zero_point,
    write_calibration_products,
)
from .inspector import inspect_directory, print_report, write_manifest
from .order2 import (
    assess_second_order,
    load_second_order_assessment,
    write_second_order_products,
)
from .pipeline import ReductionPipeline
from .workflow import run_full_workflow


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="uvex-reduce",
        description="Fault-tolerant UVEX 4i long-slit spectroscopy reduction.",
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    subparsers = parser.add_subparsers(dest="command", required=True)

    inspect_parser = subparsers.add_parser("inspect", help="Scan and classify FITS files.")
    inspect_parser.add_argument(
        "root",
        type=Path,
        help="FITS data root to inspect; no workstation-specific default is assumed.",
    )
    inspect_parser.add_argument(
        "--output-prefix",
        type=Path,
        help="Manifest path without extension; writes both JSON and CSV.",
    )
    inspect_parser.add_argument(
        "--summary-only",
        action="store_true",
        help="Do not print the per-file table (manifests are still complete).",
    )

    reduce_parser = subparsers.add_parser("reduce", help="Run a reduction from TOML config.")
    reduce_parser.add_argument("--config", type=Path, required=True)
    reduce_parser.add_argument("--verbose", action="store_true")

    post_parser = subparsers.add_parser(
        "postprocess",
        help="Derive a relative standard-star response, refine the zero point, and normalize 1D science data.",
    )
    post_parser.add_argument("--standard-fits", type=Path, required=True)
    post_parser.add_argument("--science-fits", type=Path, required=True)
    post_parser.add_argument("--template", type=Path, required=True)
    post_parser.add_argument("--standard-name", default="Standard")
    post_parser.add_argument("--target-name")
    post_parser.add_argument("--output-dir", type=Path, required=True)
    post_parser.add_argument(
        "--reference-line",
        type=float,
        action="append",
        dest="reference_lines",
        help="Known air wavelength in Angstrom; repeat for each emission line.",
    )
    post_parser.add_argument("--skip-zero-point", action="store_true")
    post_parser.add_argument("--maximum-zero-point-offset", type=float, default=60.0)
    post_parser.add_argument("--continuum-bin", type=float, default=100.0)
    post_parser.add_argument("--continuum-percentile", type=float, default=35.0)
    post_parser.add_argument(
        "--second-order-start",
        type=float,
        default=6800.0,
        help="Conservative warning threshold; flags data without masking or cutting it.",
    )
    post_parser.add_argument(
        "--second-order-assessment",
        type=Path,
        help="JSON written by order2-test; records empirical status without forcing an onset.",
    )

    order2_parser = subparsers.add_parser(
        "order2-test",
        help="Screen for second-order contamination by comparing hot and cool standards.",
    )
    order2_parser.add_argument(
        "--standard",
        nargs=3,
        action="append",
        metavar=("NAME", "REDUCED_FITS", "TEMPLATE"),
        required=True,
        help="Standard name, reduced wavelength-calibrated FITS, and stellar template.",
    )
    order2_parser.add_argument(
        "--hot",
        action="append",
        required=True,
        help="Name of a blue/hot standard supplied with --standard; repeat if needed.",
    )
    order2_parser.add_argument(
        "--cool",
        required=True,
        help="Name of the cooler comparison standard supplied with --standard.",
    )
    order2_parser.add_argument("--output-dir", type=Path, required=True)
    order2_parser.add_argument(
        "--warning-start",
        type=float,
        default=6800.0,
        help="Conservative visualization/quality-flag threshold, not a measured cutoff.",
    )

    full_parser = subparsers.add_parser(
        "full-run",
        help="Run standard control, flat trial, science stack, response and normalization.",
    )
    full_parser.add_argument("--standard-config", type=Path, required=True)
    full_parser.add_argument("--science-config", type=Path, required=True)
    full_parser.add_argument("--template", type=Path, required=True)
    full_parser.add_argument(
        "--input-root",
        type=Path,
        help=(
            "Override the input root in both configs. Required when using the "
            "published configs that contain <local-data-root>."
        ),
    )
    full_parser.add_argument("--standard-name", required=True)
    full_parser.add_argument("--target-name", required=True)
    full_parser.add_argument("--output-dir", type=Path, required=True)
    full_parser.add_argument("--skip-flat-evaluation", action="store_true")
    full_parser.add_argument("--refine-emission", action="store_true")
    full_parser.add_argument(
        "--reference-line",
        type=float,
        action="append",
        dest="reference_lines",
    )
    full_parser.add_argument("--maximum-zero-point-offset", type=float, default=80.0)
    full_parser.add_argument("--continuum-bin", type=float, default=100.0)
    full_parser.add_argument("--continuum-percentile", type=float, default=35.0)
    full_parser.add_argument("--second-order-start", type=float, default=6800.0)
    full_parser.add_argument("--second-order-assessment", type=Path)
    full_parser.add_argument(
        "--cosmic-ray-clean",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Override both configs and run conservative per-frame L.A.Cosmic.",
    )
    full_parser.add_argument(
        "--combine-method",
        choices=("median", "mean"),
        help="Override both configs after alignment and sigma clipping.",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "inspect":
            records = inspect_directory(args.root)
            if args.summary_only:
                _print_summary(records)
            else:
                print_report(records)
            prefix = args.output_prefix or Path("output") / "inspection" / "spec_manifest"
            json_path, csv_path = write_manifest(records, prefix)
            print(f"JSON manifest: {json_path}")
            print(f"CSV manifest:  {csv_path}")
            return 0

        if args.command == "postprocess":
            standard = load_reduced_spectrum(args.standard_fits)
            science = load_reduced_spectrum(args.science_fits)
            response = derive_relative_response(
                standard,
                args.template,
                args.standard_name,
            )
            zero_point = None
            if not args.skip_zero_point:
                lines = (
                    args.reference_lines
                    if args.reference_lines
                    else DEFAULT_NEBULAR_LINES_ANGSTROM
                )
                zero_point = refine_emission_zero_point(
                    science,
                    lines,
                    maximum_offset_angstrom=args.maximum_zero_point_offset,
                )
            assessment = (
                None
                if args.second_order_assessment is None
                else load_second_order_assessment(args.second_order_assessment)
            )
            warning_start = (
                args.second_order_start
                if assessment is None
                else assessment.warning_start_angstrom
            )
            calibrated = apply_response_and_normalize(
                science,
                response,
                zero_point,
                continuum_bin_angstrom=args.continuum_bin,
                continuum_percentile=args.continuum_percentile,
                second_order_start_angstrom=warning_start,
                second_order_status=(
                    "not_tested" if assessment is None else assessment.status
                ),
                second_order_empirical_onset_angstrom=(
                    None if assessment is None else assessment.empirical_onset_angstrom
                ),
                second_order_diagnostic_marker_angstrom=(
                    7292.0
                    if assessment is None
                    else assessment.balmer_second_order_marker_angstrom
                ),
                second_order_assessment_path=args.second_order_assessment,
            )
            target = args.target_name or str(science.header.get("OBJECT", "science"))
            artifacts = write_calibration_products(
                response,
                calibrated,
                args.output_dir,
                target,
            )
            print(f"Postprocessed target: {target}")
            print("Flux scale: relative (not absolute physical flux)")
            if zero_point is not None:
                print(
                    f"Wavelength refinement ({zero_point.method}): "
                    f"{zero_point.applied_offset_angstrom:+.3f} Angstrom at the "
                    f"{zero_point.pivot_angstrom:.1f} Angstrom pivot, scale "
                    f"{zero_point.scale:.8f}, from "
                    f"{zero_point.reference_wavelengths.size} lines "
                    f"(RMS {zero_point.rms_angstrom:.3f} Angstrom)"
                )
            for name, path in artifacts.items():
                print(f"{name:>18}: {path}")
            return 0

        if args.command == "order2-test":
            response_curves = {}
            for name, reduced_path, template_path in args.standard:
                if name in response_curves:
                    raise ValueError(f"Duplicate standard name: {name}")
                response_curves[name] = derive_relative_response(
                    load_reduced_spectrum(reduced_path),
                    template_path,
                    name,
                    smoothing_angstrom=220.0,
                )
            study = assess_second_order(
                response_curves,
                args.hot,
                args.cool,
                warning_start_angstrom=args.warning_start,
            )
            artifacts = write_second_order_products(study, args.output_dir)
            result = study.assessment
            print(f"Second-order test status: {result.status}")
            print(
                "Empirical onset: "
                + (
                    "not determined"
                    if result.empirical_onset_angstrom is None
                    else f"{result.empirical_onset_angstrom:.1f} Angstrom"
                )
            )
            if result.formal_change_candidate_angstrom is not None:
                print(
                    f"First persistent response change: "
                    f"{result.formal_change_candidate_angstrom:.1f} Angstrom "
                    f"({result.formal_change_sign})"
                )
            print(
                f"Tested range: {result.tested_min_angstrom:.1f}--"
                f"{result.tested_max_angstrom:.1f} Angstrom"
            )
            for name, path in artifacts.items():
                print(f"{name:>18}: {path}")
            return 0

        if args.command == "full-run":
            run = run_full_workflow(
                args.standard_config,
                args.science_config,
                args.template,
                args.output_dir,
                standard_name=args.standard_name,
                target_name=args.target_name,
                input_root=args.input_root,
                evaluate_flat=not args.skip_flat_evaluation,
                refine_emission=args.refine_emission,
                reference_lines_angstrom=(
                    args.reference_lines
                    if args.reference_lines
                    else DEFAULT_NEBULAR_LINES_ANGSTROM
                ),
                maximum_zero_point_offset_angstrom=args.maximum_zero_point_offset,
                continuum_bin_angstrom=args.continuum_bin,
                continuum_percentile=args.continuum_percentile,
                second_order_warning_start_angstrom=args.second_order_start,
                second_order_assessment_path=args.second_order_assessment,
                cosmic_ray_clean=args.cosmic_ray_clean,
                combine_method=args.combine_method,
            )
            print(f"Full workflow target: {args.target_name}")
            print(f"Flat accepted: {run.flat_accepted}")
            print(f"Flat decision: {run.flat_decision}")
            print(
                f"Science frames: {len(run.science_run.result.source_files)} accepted, "
                f"{len(run.science_run.result.rejected_source_files)} rejected"
            )
            if run.zero_point is not None:
                print(
                    f"Wavelength refinement: {run.zero_point.method}, scale "
                    f"{run.zero_point.scale:.8f}, RMS "
                    f"{run.zero_point.rms_angstrom:.3f} Angstrom"
                )
            for name, path in run.artifacts.items():
                print(f"{name:>18}: {path}")
            return 0

        logging.basicConfig(
            level=logging.DEBUG if args.verbose else logging.INFO,
            format="%(levelname)s: %(message)s",
        )
        run = ReductionPipeline.from_config(args.config).run()
        print(f"Reduced target: {run.target_name}")
        print(f"Extraction: {run.result.extraction_backend}")
        print(
            "Spectral axis: "
            + ("wavelength" if run.result.wavelength is not None else "pixel (NEEDS_ARC)")
        )
        for name, path in run.artifacts.items():
            print(f"{name:>10}: {path}")
        for warning in run.result.warnings:
            print(f"WARNING: {warning}")
        return 0
    except Exception as error:
        print(f"ERROR: {type(error).__name__}: {error}", file=sys.stderr)
        if getattr(args, "verbose", False):
            raise
        return 2


def _print_summary(records) -> None:
    from collections import Counter

    counts = Counter(record.frame_type.value for record in records)
    print(f"Scanned {len(records)} FITS file(s).")
    for frame_type, count in sorted(counts.items()):
        print(f"  {frame_type:>8}: {count}")
    conflict_count = sum(bool(record.warnings) for record in records)
    print(f"  warnings: {conflict_count} file(s)")
