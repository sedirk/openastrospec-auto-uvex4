"""Deterministic, isolated window for observation-library visual acceptance.

This creates small synthetic inputs and a temporary catalogue only. It has no
connection to production data, devices or the normal catalogue. Screenshots are
deliberately left to the supported desktop inspection workflow; the JSON report
records geometry and theme contracts, not a claim of pixel-level acceptance.
"""

from __future__ import annotations

import argparse
from concurrent.futures import Future
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile
import tkinter as tk
from tkinter import ttk

from astropy.io import fits
import numpy as np

from .library_catalog import LibraryCatalog
from .library_ui import LibraryApp


VISUAL_TITLE = "OpenAstroSpec Library — VISUAL TEST — synthetic data only"


def build_fixture(directory: Path) -> Path:
    """Build reproducible tiny samples, never registering any external root."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    data = directory / "synthetic-observations"
    data.mkdir()
    rng = np.random.default_rng(20261024)
    yy, xx = np.mgrid[:180, :768]
    profiles = (
        ("HD 18552", "ATR585M", "science", "Completed"),
        ("HD 21455", "ATR585M", "science", "Failed"),
        ("V863 Cas", "QHYminiCam8M", "photometry", "Completed"),
        ("HD 224559", "G3M2210M", "g3-guide", "Completed"),
    )
    for index, (target, camera, role, state) in enumerate(profiles):
        folder = data / f"visual-run-{index + 1:02d}"
        folder.mkdir()
        run_id = f"VISUAL-ONLY-{index + 1:02d}"
        manifest = {
            "observationRunId": run_id,
            "simulator": True,
            "createdUtc": "2026-10-24T12:00:00Z",
            "plan": {"target": {"name": target}, "expectedAtrCameraId": "SIM-ATR"},
            "terminalState": state,
            "failureCode": "SYNTHETIC_QUALITY_WARNING" if state == "Failed" else "",
        }
        evidence = []
        for number in range(1, 4):
            image = rng.normal(180, 5, xx.shape)
            if camera == "ATR585M":
                continuum = 1000 + 180 * np.sin(xx / 90)
                continuum += 2500 * np.exp(-((xx - 410) / 5) ** 2)
                image += continuum * np.exp(-((yy - 88 - xx * 0.008) / 2.3) ** 2)
            else:
                for x, y, strength in ((360, 82, 3200), (192, 57, 1800), (592, 121, 2400)):
                    image += strength * np.exp(-((xx - x) ** 2 + (yy - y) ** 2) / 5)
            hdu = fits.PrimaryHDU(image.astype(np.float32))
            hdu.header["OBJECT"] = target
            hdu.header["INSTRUME"] = camera
            hdu.header["UVEXSTG"] = role
            hdu.header["OBSRUNID"] = run_id
            hdu.header["EXPTIME"] = 120.0 if camera == "ATR585M" else 2.0
            hdu.header["DATE-OBS"] = f"2026-10-24T12:{index:02d}:{number:02d}"
            hdu.header["CCD-TEMP"] = -10.0
            frame = folder / f"{number:03d}_{role}_synthetic.fit"
            hdu.writeto(frame)
            evidence.append({
                "absolutePath": str(frame), "kind": "atr-frame" if camera == "ATR585M" else role,
                "metadata": {
                    "observationRunId": run_id, "targetName": target, "stageRole": role,
                    "exposureSeconds": 120.0 if camera == "ATR585M" else 2.0,
                    "qualityAccepted": state == "Completed" and number < 3,
                    "qualityCode": "SYNTHETIC_ACCEPTED" if number < 3 else "SYNTHETIC_WARNING",
                },
            })
        manifest["evidence"] = evidence
        (folder / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        (folder / "observation.log").write_text(
            "Visual acceptance fixture only. No acquisition or hardware was used.\n"
            f"Target: {target}; terminal state: {state}.\n",
            encoding="utf-8",
        )
    # Stable file order keeps the selected sample and screenshot reproducible.
    stamp = datetime(2026, 10, 24, 12, tzinfo=timezone.utc).timestamp()
    for index, path in enumerate(sorted(data.rglob("*"))):
        if path.is_file():
            os.utime(path, (stamp + index, stamp + index))
    database = directory / "visual-catalog.sqlite3"
    with LibraryCatalog(database) as catalog:
        catalog.add_root(data, "视觉测试 / 合成数据")
        catalog.scan()
        for row in catalog.list_files(kind="frame"):
            if row["target"] == "HD 18552":
                catalog.set_review(row["id"], decision="pick", rating=4, notes="合成光谱示例")
            elif row["target"] == "HD 21455":
                catalog.set_review(row["id"], decision="reject", notes="仅演示人工标记")
    return database


def descendants(widget):
    for child in widget.winfo_children():
        yield child
        yield from descendants(child)


def _box(widget) -> dict:
    return {
        "x": widget.winfo_rootx(),
        "y": widget.winfo_rooty(),
        "width": widget.winfo_width(),
        "height": widget.winfo_height(),
        "requested_width": widget.winfo_reqwidth(),
        "requested_height": widget.winfo_reqheight(),
        "mapped": bool(widget.winfo_ismapped()),
    }


def layout_report(app: LibraryApp) -> dict:
    """Report actual mapped Tk geometry and resolved readonly colors.

    No screenshot is captured here. These assertions catch clipped controls and
    low-contrast theme regressions but cannot replace a human screenshot review.
    """
    root = app.root
    root.update_idletasks()
    root.update()
    style = ttk.Style(root)
    widgets = list(descendants(root))
    combinations = []
    buttons = []
    for widget in widgets:
        if isinstance(widget, ttk.Combobox):
            name = widget.cget("style") or "TCombobox"
            combinations.append({
                "widget": str(widget),
                "style": str(name),
                "value": widget.get(),
                "choices": list(widget.cget("values")),
                "foreground": str(style.lookup(name, "foreground", ("readonly",))),
                "fieldbackground": str(style.lookup(name, "fieldbackground", ("readonly",))),
                **_box(widget),
            })
        elif isinstance(widget, ttk.Button) and widget.winfo_ismapped():
            buttons.append({"text": widget.cget("text"), **_box(widget)})
    view = getattr(app, "visual_test_view", "files")
    if view == "projects":
        projects = app.projects
        regions = {
            "header": _box(app.visual_regions["header"]),
            "workspace": _box(projects),
            "browser": _box(projects.table.master.master),
            "inspector": _box(projects.path_entry.master),
            "table": _box(projects.table),
            "members": _box(projects.files),
            "preview": _box(projects.viewer.canvas),
            "source_path": _box(projects.path_entry),
        }
        preview = projects.viewer
        extra = {
            "selected_project": projects.detail.get("id") if projects.detail else None,
            "source_path": projects.path.get(),
            "source_presence": projects.presence.get(),
            "project_notice": projects.notice.get(),
            "project_metrics": projects.metrics.get(),
            "project_count": len(projects.rows),
            "member_count": len(projects.members),
        }
        selected_tab = projects.tabs.select()
        members_tab = getattr(projects, "members_tab", projects.files.master.master)
        projects.tabs.select(members_tab)
        root.update_idletasks()
        root.update()
        extra["members_tab_report"] = {
            "title": projects.tabs.tab(projects.tabs.select(), "text"),
            "table": _box(projects.files),
            "controls": [_box(widget) for widget in descendants(members_tab)
                         if isinstance(widget, ttk.Combobox)],
            "buttons": [{"text": widget.cget("text"), **_box(widget)}
                        for widget in descendants(members_tab)
                        if isinstance(widget, ttk.Button) and widget.winfo_ismapped()],
        }
        projects.tabs.select(projects.chain_text.master)
        root.update_idletasks()
        root.update()
        extra["chain_tab_report"] = {
            "title": projects.tabs.tab(projects.tabs.select(), "text"),
            "open_saved": _box(projects.open_saved_button),
        }
        projects.tabs.select(selected_tab)
        root.update_idletasks()
        root.update()
    else:
        regions = {key: _box(widget) for key, widget in getattr(app, "visual_regions", {}).items()}
        regions.setdefault("table", _box(app.table))
        regions.setdefault("preview", _box(app.viewer.canvas))
        preview, extra = app.viewer, {}
    return {
        "title": root.title(),
        "tk_scaling": float(root.tk.call("tk", "scaling")),
        "fixture_display_scale": getattr(app, "visual_test_scale", None),
        "view": view,
        "selected_workspace": app.workspace.tab(app.workspace.select(), "text"),
        "root": _box(root),
        "regions": regions,
        "comboboxes": combinations,
        "buttons": buttons,
        "image_loaded": preview._image is not None,
        "screenshot_review_required": True,
        **extra,
    }


def dialog_reports(app: LibraryApp) -> list[dict]:
    """Render the real application's non-mutating dialogs in the test window."""
    reports = []
    for name in ("advanced_filters", "basket_dialog", "edit_annotation", "storage"):
        existing = {widget for widget in descendants(app.root) if isinstance(widget, tk.Toplevel)}
        try:
            getattr(app, name)()
            windows = [widget for widget in descendants(app.root)
                       if isinstance(widget, tk.Toplevel) and widget not in existing]
            if len(windows) != 1:
                raise AssertionError(f"{name} must create exactly one owned test dialog")
            window = windows[0]
            window.update_idletasks()
            window.update()
            controls = []
            for widget in descendants(window):
                if not isinstance(widget, (ttk.Button, ttk.Entry, ttk.Combobox, ttk.Treeview)):
                    continue
                control = {"class": widget.winfo_class(), "text": str(widget.cget("text"))
                           if isinstance(widget, ttk.Button) else "", **_box(widget)}
                if isinstance(widget, ttk.Treeview):
                    control["row_count"] = len(widget.get_children())
                    control["columns"] = [str(widget.heading(column, "text"))
                                          for column in widget.cget("columns")]
                controls.append(control)
            report = {"name": name, "title": window.title(), "root": _box(window),
                      "controls": controls}
            if name == "storage":
                hooks = window.storage_ui
                report["storage"] = {
                    "regions": {key: _box(hooks[key]) for key in (
                        "metrics", "overview", "footer", "close", "root_panel", "types_panel",
                    )},
                    "root_paths": [{"text": str(path.cget("text")), **_box(path)}
                                   for path in hooks["root_paths"]],
                    "bar_count": len(hooks["bars"]),
                    "selected_tab": hooks["tabs"].tab(hooks["tabs"].select(), "text"),
                    "technical_mapped": bool(hooks["technical_text"].winfo_ismapped()),
                    "technical_readonly": str(hooks["technical_text"].cget("state")) == "disabled",
                }
            reports.append(report)
        finally:
            # Only children created by this explicit test action are owned here.
            # Never enumerate, close or activate any other application's window.
            for window in list(descendants(app.root)):
                if isinstance(window, tk.Toplevel) and window not in existing:
                    window.destroy()
    return reports


def create_window(directory: Path, *, size="1380x860", scale=1.0, view="files") -> LibraryApp:
    if view not in {"files", "projects"}:
        raise ValueError("Visual view must be files or projects")
    database = build_fixture(directory)
    root = tk.Tk()
    root.withdraw()
    # Tk uses points/inch; 96/72 is 100% Windows display scaling.
    root.tk.call("tk", "scaling", (96.0 / 72.0) * scale)
    app = LibraryApp(root, database)
    # Keep visual fixtures deterministic and self-contained. The production UI's
    # worker/thread contract is covered separately in test_library_ui.py; here a
    # tiny synthetic preview is decoded immediately on this window's Tk thread.
    def submit(work, done):
        future = Future()
        try:
            value = work()
            future.set_result(value)
            done(value)
        except Exception as error:
            future.set_exception(error)
            raise
        return future

    app._submit = submit
    app.visual_test_scale = scale
    app.visual_test_view = view
    app.projects.source.set("simulated")
    app.projects.refresh()
    app.workspace.select(app.projects if view == "projects" else app.file_page)
    root.title(VISUAL_TITLE)
    root.geometry(size)
    root.deiconify()
    root.update_idletasks()
    preferred = next(
        (row for row in app.records.values() if row.get("target") == "HD 18552"), None
    )
    if preferred:
        iid = str(preferred["id"])
        app.table.selection_set(iid)
        app.table.see(iid)
        app.select()
    preferred_project = next(
        (row for row in app.projects.rows.values() if row.get("target") == "HD 18552"), None
    )
    if preferred_project:
        app.projects.table.selection_set(preferred_project["id"])
        app.projects.table.see(preferred_project["id"])
        app.projects.select_project()
    return app


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", default="1380x860", help="Client size, e.g. 1100x720")
    parser.add_argument("--scale", type=float, default=1.0, help="Windows-equivalent DPI multiplier")
    parser.add_argument("--view", choices=("files", "projects"), default="files",
                        help="Workspace to inspect; production opens projects by default")
    parser.add_argument("--title-suffix", default="",
                        help="Distinguish a new inspection window without touching an older one")
    parser.add_argument("--report", type=Path, help="Optional geometry/theme report (not a screenshot)")
    parser.add_argument("--exercise-dialogs", action="store_true",
                        help="Render non-mutating dialogs and include their geometry in the report")
    parser.add_argument("--close-after-report", action="store_true",
                        help="Normal close after report, for isolated subprocess regression tests")
    args = parser.parse_args(argv)
    if not 0.8 <= args.scale <= 2.0:
        parser.error("--scale must be in 0.8..2.0")
    if (args.close_after_report or args.exercise_dialogs) and not args.report:
        parser.error("--close-after-report and --exercise-dialogs require --report")
    with tempfile.TemporaryDirectory(prefix="uvex-library-visual-") as directory:
        app = create_window(Path(directory), size=args.size, scale=args.scale, view=args.view)
        if args.title_suffix:
            app.root.title(f"{VISUAL_TITLE} — {args.title_suffix}")
        if args.report:
            def report():
                try:
                    result = layout_report(app)
                    if args.exercise_dialogs:
                        result["dialogs"] = dialog_reports(app)
                        result["owned_dialogs_after_checks"] = [
                            str(widget) for widget in descendants(app.root)
                            if isinstance(widget, tk.Toplevel)
                        ]
                    args.report.parent.mkdir(parents=True, exist_ok=True)
                    args.report.write_text(
                        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
                    )
                finally:
                    if args.close_after_report:
                        app.close()
            app.root.after(800, report)
        app.root.mainloop()


if __name__ == "__main__":
    main()
