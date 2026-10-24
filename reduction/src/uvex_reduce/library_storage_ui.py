"""Human-readable, read-only storage overview for the observation library.

The window renders an existing catalogue summary. It neither rescans source
directories nor estimates that indexed bytes are safe to delete.
"""

from __future__ import annotations

from datetime import datetime, timezone
import json
from pathlib import Path
import tkinter as tk
from tkinter import ttk

from .library_theme import COLORS


KIND_LABELS = {
    "frame": "FITS 帧", "preview": "预览图", "metadata": "元数据",
    "log": "日志", "product": "处理成果", "project": "后期工程",
}


def format_bytes(value: int | float) -> str:
    """Use IEC units consistently with the library's capacity accounting."""
    amount = max(float(value or 0), 0.0)
    for unit in ("B", "KiB", "MiB", "GiB", "TiB"):
        if amount < 1024 or unit == "TiB":
            return f"{int(amount):,} B" if unit == "B" else f"{amount:,.1f} {unit}"
        amount /= 1024
    raise AssertionError("unreachable")


def capacity_rows(stats: dict) -> list[dict]:
    """Translate kind totals without inventing per-root or reclaimable sizes."""
    result = []
    for item in stats.get("kind", []):
        name = str(item.get("name") or "unknown")
        result.append({
            "name": name, "label": KIND_LABELS.get(name, "其他文件"),
            "files": max(int(item.get("files") or 0), 0),
            "bytes": max(int(item.get("bytes") or 0), 0),
        })
    return sorted(result, key=lambda item: item["bytes"], reverse=True)


def root_title(root: dict) -> str:
    """Prefer explicit labels; improve only the conventional generic names."""
    path = str(root.get("path") or "")
    normalized = path.replace("\\", "/").lower().rstrip("/")
    label = str(root.get("label") or "")
    conventional = {
        "observations": "观测运行与证据", "nights": "整夜序列记录",
        "photometry": "测光观测", "output": "后期处理输出",
        "n.i.n.a": "N.I.N.A. 图像",
    }
    generic = normalized.rsplit("/", 1)[-1]
    if label and label.lower() != generic:
        return label
    if normalized.endswith("/qhy/data"):
        return "QHY 测光与取场"
    return conventional.get(generic, label or generic or "数据目录")


def root_scan_text(root: dict) -> str:
    """Do not equate a stored scan timestamp with current device availability."""
    scanned = str(root.get("last_scan_utc") or "")
    if not scanned:
        return "尚未扫描"
    try:
        moment = datetime.fromisoformat(scanned.replace("Z", "+00:00"))
        if moment.tzinfo is not None:
            moment = moment.astimezone(timezone.utc)
        display = moment.strftime("%Y-%m-%d %H:%M:%S")
    except ValueError:
        display = scanned
    return "上次扫描  " + display + " · UTC"


class _ScrollablePanel(ttk.Frame):
    """A small nested viewport; long paths stay readable without growing a window."""

    def __init__(self, parent):
        super().__init__(parent, style="Panel.TFrame")
        self.canvas = tk.Canvas(
            self, background=COLORS["panel"], highlightthickness=0, borderwidth=0,
        )
        scroll = ttk.Scrollbar(self, command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=scroll.set)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)
        self.content = ttk.Frame(self.canvas, style="Panel.TFrame")
        self.item = self.canvas.create_window(0, 0, window=self.content, anchor=tk.NW)
        self.content.bind("<Configure>", self._update_region)
        self.canvas.bind("<Configure>", self._resize)
        self.canvas.bind("<MouseWheel>", self._wheel)

    def enable_mousewheel(self):
        # Bind only this panel's children, never the interpreter-wide bind_all.
        # Labels cover most of the viewport, so canvas-only binding is not enough.
        def visit(widget):
            widget.bind("<MouseWheel>", self._wheel)
            for child in widget.winfo_children():
                visit(child)

        visit(self.content)

    def _update_region(self, _event=None):
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _resize(self, event):
        self.canvas.itemconfigure(self.item, width=max(event.width, 1))

    def _wheel(self, event):
        self.canvas.yview_scroll(-1 if event.delta > 0 else 1, "units")
        return "break"


def _panel_label(parent, text, *, muted=False, **kwargs):
    return ttk.Label(
        parent, text=text, background=COLORS["panel"],
        foreground=COLORS["muted"] if muted else COLORS["text"], **kwargs,
    )


def _metric(parent, column, title, value, subtitle):
    panel = ttk.Frame(parent, style="Panel.TFrame", padding=(16, 12))
    panel.grid(row=0, column=column, sticky=tk.NSEW, padx=(0 if not column else 8, 0))
    parent.columnconfigure(column, weight=1, uniform="metrics")
    _panel_label(panel, title, muted=True).pack(anchor=tk.W)
    _panel_label(panel, value, font=("Microsoft YaHei UI", 18, "bold")).pack(anchor=tk.W, pady=(5, 2))
    caption = _panel_label(panel, subtitle, muted=True, font=("Microsoft YaHei UI", 9),
                           wraplength=240, justify=tk.LEFT)
    caption.pack(anchor=tk.W, fill=tk.X)
    panel.bind("<Configure>", lambda event:
               caption.configure(wraplength=max(event.width - 32, 100)))


def show_storage_window(parent, database: str | Path, summary: dict, *, cleanup=None) -> tk.Toplevel:
    """Open a themed overview from supplied facts, without touching source files."""
    stats = summary.get("stats") or {}
    roots = summary.get("roots") or []
    rows = capacity_rows(stats)
    total = max(int(stats.get("total_bytes", stats.get("indexed_bytes", 0)) or 0), 0)
    files = max(int(stats.get("file_count", stats.get("files", 0)) or 0), 0)
    missing = max(int(stats.get("missing_count", stats.get("missing_files", 0)) or 0), 0)
    window = tk.Toplevel(parent)
    window.title("观测资料库 · 目录与容量")
    window.configure(background=COLORS["background"])
    window.transient(parent)
    window.geometry("900x660")
    window.minsize(780, 580)
    window.rowconfigure(2, weight=1)
    window.columnconfigure(0, weight=1)

    header = ttk.Frame(window, padding=(22, 18, 22, 12))
    header.grid(row=0, column=0, sticky=tk.EW)
    ttk.Label(header, text="目录与容量", style="Title.TLabel").pack(anchor=tk.W)
    ttk.Label(header, text="看清数据分布 · 原地索引，不复制原片", style="Muted.TLabel").pack(anchor=tk.W, pady=(5, 0))
    metrics = ttk.Frame(window, padding=(22, 0, 22, 16))
    metrics.grid(row=1, column=0, sticky=tk.EW)
    _metric(metrics, 0, "已索引容量", format_bytes(total), "索引中的逻辑文件大小")
    _metric(metrics, 1, "文件数量", f"{files:,}", f"来自 {len(roots)} 个索引目录")
    _metric(metrics, 2, "缺失 / 离线", f"{missing:,}", "以最近一次索引记录为准")

    tabs = ttk.Notebook(window)
    tabs.grid(row=2, column=0, sticky=tk.NSEW, padx=22)
    overview = ttk.Frame(tabs)
    tabs.add(overview, text="容量概览")
    overview.rowconfigure(0, weight=1)
    overview.columnconfigure(0, weight=4)
    overview.columnconfigure(1, weight=5)
    types = _ScrollablePanel(overview)
    types.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 12))
    root_panel = _ScrollablePanel(overview)
    root_panel.grid(row=0, column=1, sticky=tk.NSEW)
    type_content = ttk.Frame(types.content, style="Panel.TFrame", padding=16)
    type_content.pack(fill=tk.X)
    _panel_label(type_content, "按文件类型", font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W, pady=(0, 14))
    if not rows:
        _panel_label(type_content, "暂无文件分布\n返回主窗口更新索引后即可查看。", muted=True,
                     justify=tk.LEFT).pack(anchor=tk.W, pady=12)
    bars = []
    for item in rows:
        row = ttk.Frame(type_content, style="Panel.TFrame")
        row.pack(fill=tk.X, pady=(0, 14))
        heading = ttk.Frame(row, style="Panel.TFrame")
        heading.pack(fill=tk.X)
        _panel_label(heading, item["label"]).pack(side=tk.LEFT)
        _panel_label(heading, format_bytes(item["bytes"]), muted=True).pack(side=tk.RIGHT)
        bar = tk.Canvas(row, height=5, background=COLORS["input"], highlightthickness=0)
        bar.pack(fill=tk.X, pady=(7, 5))
        fraction = min(item["bytes"] / total, 1.0) if total else 0.0
        rect = bar.create_rectangle(0, 0, 0, 5, fill=COLORS["accent"], width=0)
        bar.bind("<Configure>", lambda event, canvas=bar, shape=rect, ratio=fraction:
                 canvas.coords(shape, 0, 0, max(event.width * ratio, 0), 5))
        bars.append(bar)
        share = f"{fraction:.1%}" if total else "—"
        _panel_label(row, f"{item['files']:,} 个文件  ·  {share}", muted=True,
                     font=("Microsoft YaHei UI", 9)).pack(anchor=tk.W)

    root_content = ttk.Frame(root_panel.content, style="Panel.TFrame", padding=16)
    root_content.pack(fill=tk.X)
    _panel_label(root_content, f"索引目录  /  {len(roots)}", font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W, pady=(0, 14))
    paths = []
    if not roots:
        _panel_label(root_content, "尚未添加目录\n请在主窗口选择“添加目录”。", muted=True,
                     justify=tk.LEFT).pack(anchor=tk.W, pady=12)
    for index, item in enumerate(roots):
        card = ttk.Frame(root_content, style="Panel.TFrame")
        card.pack(fill=tk.X, pady=(0, 14))
        _panel_label(card, root_title(item)).pack(anchor=tk.W)
        path = _panel_label(card, str(item.get("path") or "路径未知"), muted=True,
                            wraplength=320, justify=tk.LEFT)
        path.pack(anchor=tk.W, fill=tk.X, pady=(5, 5))
        paths.append(path)
        card.bind("<Configure>", lambda event, label=path:
                  label.configure(wraplength=max(event.width - 2, 120)))
        scan = _panel_label(card, root_scan_text(item), muted=True,
                            wraplength=320, justify=tk.LEFT, font=("Microsoft YaHei UI", 9))
        scan.pack(anchor=tk.W, fill=tk.X)
        scan.bind("<Configure>", lambda event, label=scan:
                  label.configure(wraplength=max(event.width - 2, 120)))
        if item.get("warning"):
            warning = _panel_label(card, "需检查 · " + str(item["warning"]),
                                   wraplength=320, justify=tk.LEFT)
            warning.pack(anchor=tk.W, fill=tk.X, pady=(5, 0))
            warning.bind("<Configure>", lambda event, label=warning:
                         label.configure(wraplength=max(event.width - 2, 120)))
        if index < len(roots) - 1:
            ttk.Separator(root_content).pack(fill=tk.X, pady=(0, 14))

    types.enable_mousewheel()
    root_panel.enable_mousewheel()

    technical = ttk.Frame(tabs)
    technical.rowconfigure(0, weight=1)
    technical.columnconfigure(0, weight=1)
    tabs.add(technical, text="技术详情")
    text = tk.Text(
        technical, wrap=tk.WORD, background=COLORS["panel"], foreground=COLORS["text"],
        relief=tk.FLAT, borderwidth=0, highlightthickness=0, padx=14, pady=14,
        font=("Consolas", 10), selectbackground=COLORS["selected"],
        selectforeground=COLORS["text"],
    )
    text.grid(row=0, column=0, sticky=tk.NSEW)
    scroll = ttk.Scrollbar(technical, command=text.yview)
    scroll.grid(row=0, column=1, sticky=tk.NS)
    text.configure(yscrollcommand=scroll.set)
    details = {**summary, "catalog": summary.get("catalog") or str(database)}
    text.insert("1.0", json.dumps(details, ensure_ascii=False, indent=2, default=str))
    text.configure(state=tk.DISABLED)

    footer = ttk.Frame(window, padding=(22, 14, 22, 16))
    footer.grid(row=3, column=0, sticky=tk.EW)
    close = ttk.Button(footer, text="完成", command=window.destroy)
    close.pack(side=tk.RIGHT, padx=(16, 0))
    if cleanup is not None:
        ttk.Button(footer, text="按项目清理预览…", command=cleanup).pack(side=tk.RIGHT, padx=(8, 0))
    note = ttk.Label(footer, text="索引容量不是可删除容量 · 本次释放 0 B\n原片只读；此窗口不扫描、不删除、不移动文件。",
                     style="Muted.TLabel", justify=tk.LEFT, wraplength=650)
    note.pack(side=tk.LEFT, fill=tk.X, expand=True)
    footer.bind("<Configure>", lambda event: note.configure(wraplength=max(event.width - 350, 250)))
    window.bind("<Escape>", lambda _event: window.destroy())
    # Stable hooks for isolated layout checks and genuine screenshot acceptance.
    window.storage_ui = {
        "tabs": tabs, "overview": overview, "metrics": metrics, "bars": bars,
        "root_paths": paths, "root_panel": root_panel, "types_panel": types,
        "technical_text": text, "footer": footer, "close": close,
    }
    return window
