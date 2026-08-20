from __future__ import annotations

from uvex_reduce.project import AstroProject, DEFAULT_EQUIPMENT_PRESETS


def test_astro_project_round_trip_preserves_operator_state(tmp_path):
    project = AstroProject(
        name="3C273 May 4",
        data_root=r"C:\data\20260504",
        output_root=r"C:\output\3c273",
        current_stage="wavelength",
        workflow_preset="Vega -> target",
        evaluate_flat=False,
        equipment=DEFAULT_EQUIPMENT_PRESETS[-1],
        last_output=r"C:\output\3c273\final",
        approved_stages=["media", "masters", "geometry"],
        parameters={"selectedStandardIndex": 2, "secondOrderPolicy": "warn-and-retain"},
        manual_calibration_points=[{"pixel": 1200.5, "wavelengthAngstrom": 4861.35}],
    )

    path = project.save(tmp_path / "night.astroproj")
    restored = AstroProject.load(path)

    assert restored.name == project.name
    assert restored.current_stage == "wavelength"
    assert restored.equipment == DEFAULT_EQUIPMENT_PRESETS[-1]
    assert restored.approved_stages == ["media", "masters", "geometry"]
    assert restored.parameters["selectedStandardIndex"] == 2
    assert restored.manual_calibration_points[0]["pixel"] == 1200.5


def test_project_save_adds_astroproj_suffix(tmp_path):
    project = AstroProject(name="test", data_root="data", output_root="output")
    path = project.save(tmp_path / "session")
    assert path.suffix == ".astroproj"
    assert path.is_file()


def test_default_equipment_presets_match_measured_grating_configuration():
    assert {preset.grating_lines_per_mm for preset in DEFAULT_EQUIPMENT_PRESETS} == {300}
    assert {
        preset.estimated_dispersion_angstrom_per_pixel for preset in DEFAULT_EQUIPMENT_PRESETS
    } == {0.94}
    assert all("300 l/mm" in preset.name for preset in DEFAULT_EQUIPMENT_PRESETS)
