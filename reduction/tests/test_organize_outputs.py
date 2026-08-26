from pathlib import Path

from reduction.tools.organize_outputs import _specs
from uvex_reduce.config import load_config


def test_catalog_includes_20260825_nova_and_same_session_vega() -> None:
    specs = {(spec.target, spec.date): spec for spec in _specs()}

    nova = specs[("PNV-J19450648+1822422", "2026-08-25")]
    assert nova.display_name == "PNV J19450648+1822422 / Nova Sge 2026"
    assert nova.source_csv.name == "Nova_Sge_2026_calibrated_1d.csv"
    assert "input_inspection.json" in nova.metadata
    assert "response.fits" in nova.calibration

    vega = specs[("Vega", "2026-08-25")]
    assert vega.source_csv.name == "Vega_spectrum.csv"
    assert "standard-flat-trial" in vega.source_csv.parts


def test_20260826_configs_preserve_manifest_and_calibration_roles() -> None:
    config_directory = Path(__file__).resolve().parents[1] / "configs"
    vega = load_config(config_directory / "20260826-vega-standard.toml")
    nova = load_config(config_directory / "20260826-nova-sge-2026-science.toml")

    assert vega.inputs.science == ["Vega/1s*.fit"]
    assert vega.wavelength.mode == "stellar_template"
    assert vega.preprocess.use_dark is False
    assert vega.preprocess.use_flat is True

    assert nova.inputs.science == ["Nova Sge 2026/*.fit"]
    assert nova.wavelength.mode == "solution_file"
    assert nova.preprocess.use_dark is True
    assert nova.preprocess.use_flat is True
    assert nova.preprocess.maximum_dark_temperature_delta_c == 1.5
