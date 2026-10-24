"""Shared test infrastructure; never connect to the observing application's UI."""

from __future__ import annotations

import tkinter as tk

import pytest


def pytest_addoption(parser):
    parser.addoption("--run-ui-tests", action="store_true", default=False,
                     help="Explicitly allow isolated Tk windows and visual layout tests.")


def pytest_configure(config):
    config.addinivalue_line("markers", "desktop_ui: creates Tk windows; requires --run-ui-tests")


def requires_desktop_ui(item):
    return bool(item.get_closest_marker("desktop_ui")) or bool(
        {"tk_root", "visual_report", "project_visual_report"}.intersection(item.fixturenames)
    )


def pytest_collection_modifyitems(config, items):
    allowed = config.getoption("--run-ui-tests")
    for item in items:
        if requires_desktop_ui(item):
            item.add_marker(pytest.mark.desktop_ui)
            if not allowed:
                item.add_marker(pytest.mark.skip(
                    reason="Desktop UI tests are opt-in: use --run-ui-tests on a test desktop."
                ))


@pytest.fixture(scope="session")
def tk_root(request):
    """Use one Tcl/Tk interpreter per test process, as Tk expects.

    Each GUI fixture owns a separate Toplevel and must close that window.
    The long Windows regression process reproduced spurious missing-script
    startup errors for files which are installed when separate modules repeatedly
    destroyed/recreated their Tk roots.  A single interpreter removes that
    teardown/reinitialization boundary instead of retrying or hiding the error.
    DPI/layout matrices retain fresh subprocesses rather than changing this
    shared interpreter's scaling or the system's display settings.
    """
    if not request.config.getoption("--run-ui-tests"):
        pytest.skip("Tk windows require explicit --run-ui-tests.")
    root = tk.Tk()
    root.withdraw()
    try:
        yield root
    finally:
        root.destroy()
