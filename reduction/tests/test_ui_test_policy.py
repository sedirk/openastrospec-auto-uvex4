"""Ordinary regression must not open windows on the operator's desktop."""

from types import SimpleNamespace

import pytest

from conftest import pytest_collection_modifyitems, requires_desktop_ui


def item(fixtures=(), marked=False):
    marks = []
    return SimpleNamespace(fixturenames=fixtures,
                           get_closest_marker=lambda _: marked,
                           add_marker=marks.append, marks=marks)


@pytest.mark.parametrize("fixture", ["tk_root", "visual_report", "project_visual_report"])
def test_transitive_gui_fixtures_require_desktop_permission(fixture):
    assert requires_desktop_ui(item(["tmp_path", fixture]))


def test_explicit_subprocess_marker_requires_permission():
    assert requires_desktop_ui(item(marked=True))
    assert not requires_desktop_ui(item(["tmp_path"]))


@pytest.mark.parametrize("allowed", [False, True])
def test_default_skips_before_fixture_creation_but_explicit_run_retains_coverage(allowed):
    gui, plain = item(["tk_root"]), item(["tmp_path"])
    pytest_collection_modifyitems(SimpleNamespace(getoption=lambda _: allowed), [gui, plain])
    names = [mark.name for mark in gui.marks]
    assert ("skip" in names) is not allowed
    assert "desktop_ui" in names
    assert not plain.marks
