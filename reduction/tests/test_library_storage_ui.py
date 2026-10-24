from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys

import pytest

from uvex_reduce.library_storage_ui import (
    capacity_rows, format_bytes, root_scan_text, root_title, show_storage_window,
)
from uvex_reduce.library_theme import apply_library_theme


def example_summary():
    return {
        "catalog": "C:/Library/catalog.sqlite3",
        "roots": [
            {"path": "C:/ProgramData/UVEX-ADV/qhy/data", "label": "data",
             "last_scan_utc": "2026-10-24T09:30:00Z", "warning": ""},
            {"path": "C:/Library/observations",
             "label": "observations", "last_scan_utc": None, "warning": ""},
        ],
        "stats": {"file_count": 177926, "total_bytes": 254122480614, "missing_count": 0,
                  "kind": [{"name": "frame", "files": 26454, "bytes": 246000000000},
                           {"name": "metadata", "files": 151472, "bytes": 8122480614}]},
    }


def test_capacity_display_uses_logical_bytes_and_localized_known_types():
    assert format_bytes(0) == "0 B"
    assert format_bytes(1024) == "1.0 KiB"
    assert format_bytes(254122480614) == "236.7 GiB"
    assert format_bytes(-1) == "0 B"
    rows = capacity_rows(example_summary()["stats"])
    assert rows[0]["label"] == "FITS 帧"
    assert rows[1]["label"] == "元数据"
    assert capacity_rows({}) == []
    assert capacity_rows({"kind": [{"name": "new", "files": 1, "bytes": None}]})[0]["label"] == "其他文件"


def test_directory_titles_keep_explicit_labels_and_do_not_claim_current_availability():
    roots = example_summary()["roots"]
    assert root_title(roots[0]) == "QHY 测光与取场"
    assert root_title(roots[1]) == "观测运行与证据"
    assert root_title({**roots[0], "label": "我的冷备盘"}) == "我的冷备盘"
    assert root_scan_text(roots[0]).endswith("· UTC")
    assert "2026-10-24 09:30:00" in root_scan_text(roots[0])
    assert root_scan_text(roots[1]) == "尚未扫描"
    assert root_scan_text({"last_scan_utc": "2026-10-24T09:30:00.1234567Z"}) == (
        "上次扫描  2026-10-24 09:30:00 · UTC"
    )


@pytest.fixture
def storage_window(tk_root):
    root = tk_root
    apply_library_theme(root)
    summary = example_summary()
    original = copy.deepcopy(summary)
    window = show_storage_window(root, Path("C:/does-not-exist/catalog.sqlite3"), summary)
    yield root, window, summary, original
    window.destroy()


def test_storage_window_is_summary_only_and_raw_json_is_secondary_tab(storage_window):
    _root, window, summary, original = storage_window
    ui = window.storage_ui
    assert summary == original
    assert ui["tabs"].index("end") == 2
    assert ui["tabs"].tab(0, "text") == "容量概览"
    assert ui["tabs"].tab(1, "text") == "技术详情"
    assert ui["tabs"].select() == str(ui["overview"])
    assert json.loads(ui["technical_text"].get("1.0", "end-1c")) == summary
    assert ui["technical_text"].cget("state") == "disabled"
    assert len(ui["bars"]) == 2
    assert len(ui["root_paths"]) == 2
    assert "只读" not in ui["root_paths"][0].cget("text")
    assert ui["root_paths"][0].bind("<MouseWheel>")


@pytest.mark.parametrize("scale", [1.0, 1.25])
@pytest.mark.desktop_ui
def test_storage_900x660_visible_frame_and_footer_fit(scale):
    # Real mapped Tk geometry in a separate interpreter, not desktop input.
    code = r'''
import tkinter as tk
from uvex_reduce.library_storage_ui import show_storage_window
from uvex_reduce.library_theme import apply_library_theme
root = tk.Tk()
root.title("Isolated storage layout test")
root.geometry("1x1+0+0")
root.tk.call("tk", "scaling", (96 / 72) * SCALE)
apply_library_theme(root)
summary = {"roots": [{"path": "C:/very-long-directory/" * 5, "label": "观测目录", "last_scan_utc": "2026-10-24T09:30:00Z"}] * 6,
           "stats": {"files": 177926, "indexed_bytes": 254122480614,
                     "kind": [{"name": "frame", "files": 26454, "bytes": 254122480614}]}}
window = show_storage_window(root, "not-opened.sqlite3", summary)
window.geometry("900x660")
for _ in range(5):
    root.update()
ui = window.storage_ui
assert window.winfo_width() == 900 and window.winfo_height() == 660
assert ui["overview"].winfo_ismapped()
assert ui["overview"].winfo_height() >= 200
for name in ("metrics", "footer", "close"):
    item = ui[name]
    assert item.winfo_ismapped(), name
    assert item.winfo_rooty() + item.winfo_height() <= window.winfo_rooty() + 660, name
assert ui["close"].winfo_width() >= ui["close"].winfo_reqwidth()
assert ui["close"].winfo_height() >= ui["close"].winfo_reqheight()
assert ui["root_panel"].canvas.yview()[1] < 1.0
root.destroy()
'''.replace("SCALE", repr(scale))
    completed = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, timeout=20)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert completed.stderr == ""
