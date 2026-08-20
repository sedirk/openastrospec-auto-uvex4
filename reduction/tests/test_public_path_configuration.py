from pathlib import Path

import pytest

from uvex_reduce.cli import build_parser
from uvex_reduce.config import InputConfig, PipelineConfig
from uvex_reduce.pipeline import ReductionPipeline


def test_inspect_requires_an_explicit_data_root() -> None:
    with pytest.raises(SystemExit) as error:
        build_parser().parse_args(["inspect"])

    assert error.value.code == 2


def test_full_run_accepts_an_explicit_input_root() -> None:
    args = build_parser().parse_args(
        [
            "full-run",
            "--standard-config",
            "standard.toml",
            "--science-config",
            "science.toml",
            "--input-root",
            "fixture-data",
            "--template",
            "template.dat",
            "--standard-name",
            "Vega",
            "--target-name",
            "NGC6543",
            "--output-dir",
            "output",
        ]
    )

    assert args.input_root == Path("fixture-data")


def test_pipeline_rejects_a_published_path_placeholder_before_io(tmp_path: Path) -> None:
    config = PipelineConfig(
        inputs=InputConfig(
            root=Path("<local-data-root>"),
            science=["*.fit"],
            output_dir=tmp_path,
        )
    )

    with pytest.raises(ValueError, match="published example config"):
        ReductionPipeline(config).run()
