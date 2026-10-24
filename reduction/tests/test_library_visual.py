"""Rendered Tk layout/theme contracts; screenshots still need visual review.

These tests map a genuine isolated window. They do not compare a hidden widget
tree and do not assert that geometry alone proves the design looks good.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys

from PIL import ImageColor
import pytest

from uvex_reduce.library_catalog import LibraryCatalog
from uvex_reduce.library_visual_fixture import (
    VISUAL_TITLE,
    build_fixture,
)


def luminance(color):
    channels = [value / 255 for value in ImageColor.getrgb(color)]
    channels = [value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4
                for value in channels]
    return sum(value * weight for value, weight in zip(channels, (0.2126, 0.7152, 0.0722)))


def contrast(foreground, background):
    first, second = sorted((luminance(foreground), luminance(background)))
    return (second + 0.05) / (first + 0.05)


def contained(inner, outer, *, tolerance=2):
    return (
        inner["x"] >= outer["x"] - tolerance
        and inner["y"] >= outer["y"] - tolerance
        and inner["x"] + inner["width"] <= outer["x"] + outer["width"] + tolerance
        and inner["y"] + inner["height"] <= outer["y"] + outer["height"] + tolerance
    )


@pytest.fixture(scope="module", params=[
    ("1380x860", 1.0), ("1100x720", 1.0),
    ("1380x860", 1.25), ("1100x720", 1.25),
])
def visual_report(request, tmp_path_factory):
    # A fresh Tcl interpreter per DPI avoids shared font caches and Windows Tcl
    # finalization races from creating/destroying many Tk roots in one process.
    size, scale = request.param
    path = tmp_path_factory.mktemp("visual-reports") / f"{size}-{scale}.json"
    result = subprocess.run(
        [sys.executable, "-m", "uvex_reduce.library_visual_fixture", "--size", size,
         "--scale", str(scale), "--view", "files", "--report", str(path),
         "--exercise-dialogs", "--close-after-report"],
        capture_output=True, text=True, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert not result.stderr, result.stderr
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.fixture(scope="module", params=[
    ("1380x860", 1.0), ("1100x720", 1.0),
    ("1380x860", 1.25), ("1100x720", 1.25),
])
def project_visual_report(request, tmp_path_factory):
    size, scale = request.param
    path = tmp_path_factory.mktemp("project-visual-reports") / f"{size}-{scale}.json"
    result = subprocess.run(
        [sys.executable, "-m", "uvex_reduce.library_visual_fixture", "--size", size,
         "--scale", str(scale), "--view", "projects", "--report", str(path),
         "--close-after-report"],
        capture_output=True, text=True, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert not result.stderr, result.stderr
    return json.loads(path.read_text(encoding="utf-8"))


def test_visual_fixture_is_deterministic_and_catalogue_is_isolated(tmp_path):
    first = tmp_path / "first"
    second = tmp_path / "second"
    databases = [build_fixture(directory) for directory in (first, second)]

    def raw_hashes(directory):
        return {
            str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in directory.rglob("*.fit")
        }

    assert raw_hashes(first) == raw_hashes(second)
    assert len(raw_hashes(first)) == 12
    with LibraryCatalog(databases[0]) as catalog:
        roots = catalog.list_roots()
        assert len(roots) == 1
        assert Path(roots[0]["path"]).is_relative_to(first)
        rows = catalog.list_files(kind="frame")
        assert {row["target"] for row in rows} == {
            "HD 18552", "HD 21455", "V863 Cas", "HD 224559",
        }
        assert {row["review"] for row in rows} == {"pick", "reject", "unreviewed"}
        assert all(row["source"] == "simulated" for row in rows)


def test_mapped_window_has_readable_dark_readonly_fields_and_localized_choices(visual_report):
    report = visual_report
    assert report["title"] == VISUAL_TITLE
    assert report["selected_workspace"] == "文件浏览"
    assert report["view"] == "files"
    assert report["root"]["mapped"]
    assert report["screenshot_review_required"] is True
    assert report["comboboxes"]
    internal_values = {
        "real", "simulated", "unknown", "spectroscopy", "photometry", "slit",
        "pick", "reject", "unreviewed", "frame", "preview", "metadata", "log",
        "product", "project", "date_desc", "date_asc", "size_desc", "target", "path",
        "science", "standard", "bias", "dark", "flat", "arc",
    }
    for combo in report["comboboxes"]:
        assert not (set(combo["choices"]) & internal_values), combo
        if not combo["mapped"]:
            continue
        assert combo["foreground"] and combo["fieldbackground"], combo
        assert luminance(combo["fieldbackground"]) < 0.12, combo
        assert contrast(combo["foreground"], combo["fieldbackground"]) >= 4.5, combo
        assert combo["height"] >= 24, combo


def test_mapped_main_regions_and_action_text_do_not_clip(visual_report):
    report = visual_report
    required = {"sidebar", "browser", "inspector", "header", "footer", "table", "preview"}
    assert required <= set(report["regions"])
    for name in required:
        region = report["regions"][name]
        assert region["mapped"] and region["width"] > 20 and region["height"] > 20, name
        assert contained(region, report["root"]), (name, region, report["root"])
    assert report["regions"]["table"]["width"] >= 250
    assert report["regions"]["table"]["height"] >= 200
    assert report["regions"]["preview"]["width"] >= 270
    # DPI and clam border rounding may differ by one or two pixels.
    assert report["regions"]["preview"]["height"] + 2 >= 190
    assert {"留片", "不采用", "撤销标记", "星级 / 备注…", "目录与容量", "更新索引",
            "选片篮…", "打开光谱后期", "文件操作…", "定位源文件"} <= {
        button["text"] for button in report["buttons"]
    }
    for button in report["buttons"]:
        assert button["mapped"], button
        assert contained(button, report["root"]), button
        assert button["width"] + 2 >= button["requested_width"], button
        assert button["height"] + 2 >= button["requested_height"], button
    assert report["image_loaded"], "The selected synthetic FITS must really render"


def test_project_view_mapped_layout_has_source_navigation_and_no_clipped_actions(project_visual_report):
    report = project_visual_report
    assert report["selected_workspace"] == "项目与运行"
    assert report["view"] == "projects"
    assert report["project_count"] == 4
    assert report["selected_project"] and report["source_path"]
    assert report["member_count"] == 3
    assert "原位存在" in report["source_presence"]
    assert "无资料库物理清理回执" in report["source_presence"]
    assert "科学帧" in report["project_metrics"]
    assert report["image_loaded"], "Project selection must render an actual synthetic spectrum"
    assert report["screenshot_review_required"] is True
    required = {"header", "workspace", "browser", "inspector", "table", "preview", "source_path"}
    assert required <= set(report["regions"])
    for name in required:
        region = report["regions"][name]
        assert region["mapped"] and region["width"] > 20 and region["height"] > 20, name
        assert contained(region, report["root"]), (name, region, report["root"])
    assert report["regions"]["table"]["width"] >= 250
    assert report["regions"]["table"]["height"] >= 230
    assert report["regions"]["preview"]["width"] >= 270
    assert report["regions"]["preview"]["height"] >= 160
    assert {"定位源文件", "复制路径", "科学帧 → 后期…",
            "目录与容量", "刷新项目", "更新索引"} <= {
                button["text"] for button in report["buttons"]
            }
    for button in report["buttons"]:
        assert contained(button, report["root"]), button
        assert button["width"] + 2 >= button["requested_width"], button
        assert button["height"] + 2 >= button["requested_height"], button
    for combo in report["comboboxes"]:
        if combo["mapped"]:
            assert luminance(combo["fieldbackground"]) < 0.12, combo
            assert contrast(combo["foreground"], combo["fieldbackground"]) >= 4.5, combo
    member_view = report["members_tab_report"]
    assert member_view["title"] == "项目文件"
    assert member_view["table"]["mapped"]
    assert member_view["table"]["width"] >= 270
    assert member_view["table"]["height"] >= 120
    assert contained(member_view["table"], report["root"])
    assert member_view["controls"]
    assert all(control["mapped"] and contained(control, report["root"])
               for control in member_view["controls"])
    assert {"不采用 / 撤销", "关联文件浏览"} <= {
        button["text"] for button in member_view["buttons"]
    }
    for button in member_view["buttons"]:
        assert contained(button, report["root"]), button
        assert button["width"] + 2 >= button["requested_width"], button
        assert button["height"] + 2 >= button["requested_height"], button
    saved_project = report["chain_tab_report"]["open_saved"]
    assert saved_project["mapped"] and contained(saved_project, report["root"])
    assert saved_project["width"] + 2 >= saved_project["requested_width"]
    assert report["tk_scaling"] == pytest.approx(
        (96 / 72) * report["fixture_display_scale"], abs=0.02,
    )


def test_dpi_option_is_explicit(visual_report):
    report = visual_report
    expected = report["fixture_display_scale"]
    # Tcl may round pixel/mm conversion; 0.02 allows that rounding, not a DPI reset.
    assert report["tk_scaling"] == pytest.approx((96 / 72) * expected, abs=0.02)


def test_mapped_dialog_controls_do_not_clip(visual_report):
    assert {item["name"] for item in visual_report["dialogs"]} == {
        "advanced_filters", "basket_dialog", "edit_annotation", "storage",
    }
    assert visual_report["owned_dialogs_after_checks"] == []
    for dialog in visual_report["dialogs"]:
        assert dialog["root"]["mapped"], dialog
        assert dialog["controls"], dialog
        for control in dialog["controls"]:
            assert control["mapped"], (dialog["name"], control)
            assert contained(control, dialog["root"]), (dialog["name"], control)
            if control["class"] == "Treeview":
                # A scrollable directory listing may intentionally be smaller
                # than its total column/content request; controls may not crop.
                assert control["width"] >= 500 and control["height"] >= 120, control
                assert control["row_count"] >= 1, control
                continue
            assert control["height"] + 2 >= control["requested_height"], control
            assert control["width"] + 2 >= control["requested_width"], control
    storage = next(item for item in visual_report["dialogs"] if item["name"] == "storage")
    overview = storage["storage"]
    assert overview["selected_tab"] == "容量概览"
    assert overview["bar_count"] >= 1
    assert overview["root_paths"]
    assert overview["technical_readonly"] and not overview["technical_mapped"]
    for name, region in overview["regions"].items():
        assert region["mapped"], name
        assert contained(region, storage["root"]), (name, region)
        assert region["width"] >= 20 and region["height"] >= 20, (name, region)
    for path in overview["root_paths"]:
        assert path["text"] and path["mapped"]
        assert contained(path, storage["root"]), path


def test_visual_contract_rejects_original_gray_readonly_and_clipped_action():
    # Negative controls keep these tests from silently accepting the old theme
    # or considering a visible-but-cropped button adequate visual coverage.
    assert luminance("#dcdad5") >= 0.12
    assert contrast("#e0e8f0", "#dcdad5") < 4.5
    root = {"x": 0, "y": 0, "width": 1100, "height": 720}
    clipped = {"x": 1010, "y": 690, "width": 140, "height": 40}
    assert not contained(clipped, root)
