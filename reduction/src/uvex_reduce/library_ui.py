"""OpenAstroSpec Library: an offline, in-place catalogue, not a device controller."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter import font as tkfont

from .image_viewer import InteractiveImageViewer
from .library_catalog import LibraryCatalog, default_catalog_path
from .library_preview import load_library_preview
from .library_theme import COLORS, apply_library_theme

CHOICES = {
    "source": {"": "全部来源", "real": "真实观测", "simulated": "模拟 / 测试", "unknown": "来源未知"},
    "channel": {"": "全部通道", "spectroscopy": "光谱相机", "photometry": "测光相机", "slit": "盯缝相机", "unknown": "通道未知"},
    "review": {"": "全部标记", "pick": "留片", "reject": "不采用（仅标记）", "unreviewed": "尚未筛选"},
    "kind": {"frame": "原始帧", "": "全部文件", "preview": "预览图", "metadata": "元数据", "log": "日志", "product": "处理成果", "project": "后期工程"},
    "sort": {"date_desc": "最新优先", "date_asc": "最早优先", "size_desc": "容量优先", "target": "目标名称", "path": "文件路径"},
    "role": {"science": "科学帧", "standard": "标准星", "bias": "偏置", "dark": "暗场", "flat": "平场", "arc": "灯谱"},
}


def display_size(value: int) -> str:
    return f"{value / 1024**3:.2f} GiB" if value >= 1024**3 else f"{value / 1024**2:.1f} MiB"


def suggested_roots() -> list[Path]:
    """Only conventional project locations; never discover arbitrary personal folders."""
    local = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local/share")) / "UVEX-ADV"
    program = Path(os.environ.get("PROGRAMDATA", "C:/ProgramData")) / "UVEX-ADV"
    candidates = [
        local / "observations",
        local / "nights",
        local / "photometry",
        program / "qhy/data",
        Path.home() / "Documents/N.I.N.A",
        Path(__file__).resolve().parents[2] / "output",
    ]
    return [path for path in candidates if path.is_dir()]


def reduction_frame(record: dict, role: str, target: str = "") -> dict:
    """Explicit role assignment cannot reinterpret guide/photometry data as slit spectra."""
    path = Path(record["path"])
    if path.suffix.lower() not in {".fits", ".fit", ".fts"}:
        raise ValueError("后期选片篮只接受原始 FITS；日志和已有成果可在资料库回看。")
    if record.get("channel") not in {"spectroscopy", "atr", "unknown", ""}:
        raise ValueError("测光/盯缝帧不能送入光谱提取。首版提供回看，不伪装成光谱帧。")
    if record.get("review") == "reject":
        raise ValueError("选中项含弃片。若确实要使用，请先明确改为留片/未筛。")
    if record.get("missing") or not path.is_file():
        raise ValueError(f"文件缺失或介质离线：{path}")
    from astropy.io import fits

    header = fits.getheader(path, ext=0)
    if header.get("NAXIS") != 2:
        raise ValueError("后期选片篮需要二维原始谱帧，不能把已有一维光谱再次当作原片提取。")
    name = target.strip() or str(record.get("target") or "").strip()
    if not name:
        raise ValueError("目标名未知，请填写后期目标名；不会从 job 目录猜测目标。")
    return {"path": str(path), "target": name, "role": role}


class LibraryApp:
    PAGE_SIZE = 200

    def __init__(self, root: tk.Tk, database: str | Path | None = None) -> None:
        self.root = root
        self.database = Path(database) if database else default_catalog_path()
        self.pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="library")
        self.events: queue.SimpleQueue = queue.SimpleQueue()
        self.callbacks = {}
        self.next_task_id = 0
        self.pump_after = None
        self.cancel_scan = threading.Event()
        self.scanning = False
        self.closed = False
        self.cleanup_windows = []
        self.offset = 0
        self.project_file_ids = None
        self.records: dict[str, dict] = {}
        self.basket: dict[str, dict] = {}
        self.cached_stats: dict | None = None
        self.preview_generation = 0
        self.preview_future = None
        self.filters: dict[str, tk.StringVar] = {}
        self.status = tk.StringVar(value="原地索引 · 不复制原片 · 不连接设备")
        self.summary = tk.StringVar()
        with LibraryCatalog(self.database) as catalog:
            if not catalog.list_roots():
                for path in suggested_roots():
                    catalog.add_root(path)
        self._build()
        self.refresh()
        self.projects.refresh()
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.pump_after = self.root.after(100, self._pump)

    def _combo(self, parent, variable, choices, *, width=14, changed=None):
        """Keep localized display text separate from persistent/API enum values."""
        display = tk.StringVar(parent, value=choices.get(variable.get(), variable.get()))
        widget = ttk.Combobox(
            parent, textvariable=display, values=tuple(choices.values()),
            state="readonly", width=width,
        )

        def selected(_event=None):
            reverse = {label: value for value, label in choices.items()}
            variable.set(reverse[display.get()])
            if changed:
                changed()

        def sync(*_args):
            display.set(choices.get(variable.get(), variable.get()))

        trace = variable.trace_add("write", sync)
        widget.bind("<<ComboboxSelected>>", selected)
        widget.bind("<Destroy>", lambda _event: variable.trace_remove("write", trace))
        return widget

    def _build(self) -> None:
        root = self.root
        root.title("OpenAstroSpec 光谱项目库")
        root.geometry(f"{min(1380, root.winfo_screenwidth() - 40)}x{min(860, root.winfo_screenheight() - 110)}")
        root.minsize(1100, 720)
        self.style = apply_library_theme(root)
        self.filter_widgets = {}
        self.row_actions = []
        self.visual_regions = {}
        self.filters = {
            key: tk.StringVar(value="frame" if key == "kind" else "")
            for key in ("query", "target", "source", "channel", "review", "kind", "date_prefix", "run_id")
        }
        self.sort = tk.StringVar(value="date_desc")
        self.role = tk.StringVar(value="science")
        self.target_name = tk.StringVar()
        self.rating = tk.StringVar(value="0")
        self.notes = tk.StringVar()
        self.selection_title = tk.StringVar(value="文件预览")
        self.selection_caption = tk.StringVar(value="选择左侧文件，查看图像、光谱或光变曲线")
        self.selection_count = tk.StringVar(value="未选择文件")

        top = self.visual_regions["header"] = ttk.Frame(root, padding=(22, 10, 22, 8))
        top.pack(fill=tk.X)
        title = ttk.Frame(top)
        title.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Label(title, text="光谱项目库", style="Title.TLabel").pack(anchor=tk.W)
        ttk.Label(top, textvariable=self.summary, style="Muted.TLabel").pack(side=tk.LEFT, padx=20)
        ttk.Button(top, text="添加目录", command=self.add_root).pack(side=tk.RIGHT)
        self.scan_button = ttk.Button(top, text="更新索引", command=self.scan, style="Primary.TButton")
        self.scan_button.pack(side=tk.RIGHT, padx=8)
        self.stop_scan_button = ttk.Button(
            top, text="停止", command=self.cancel_scan.set, style="Quiet.TButton", state=tk.DISABLED,
        )
        self.stop_scan_button.pack(side=tk.RIGHT)

        self.workspace = ttk.Notebook(root, style="Compact.TNotebook")
        self.workspace.pack(fill=tk.BOTH, expand=True, padx=12, pady=(0, 10))
        from .library_projects_ui import ProjectBrowser

        self.projects = ProjectBrowser(self.workspace, self)
        self.workspace.add(self.projects, text="项目与运行")
        self.file_page = ttk.Frame(self.workspace)
        self.workspace.add(self.file_page, text="文件浏览")
        # File browsing remains a full view, not the primary project model.
        body = ttk.Frame(self.file_page, padding=(10, 8, 10, 0))
        body.pack(fill=tk.BOTH, expand=True)
        sidebar = self.visual_regions["sidebar"] = ttk.Frame(body, width=176)
        sidebar.pack(side=tk.LEFT, fill=tk.Y, padx=(0, 20))
        sidebar.pack_propagate(False)
        ttk.Label(sidebar, text="浏览与筛选", style="Section.TLabel").pack(anchor=tk.W, pady=(0, 12))
        for key, label in (("kind", "文件类型"), ("channel", "相机通道"),
                           ("review", "筛片标记"), ("source", "数据来源")):
            ttk.Label(sidebar, text=label, style="Muted.TLabel").pack(anchor=tk.W, pady=(0, 4))
            combo = self._combo(sidebar, self.filters[key], CHOICES[key], changed=self.filter_changed)
            self.filter_widgets[key] = combo
            combo.pack(fill=tk.X, pady=(0, 8))
        ttk.Button(sidebar, text="更多筛选…", command=self.advanced_filters,
                   style="Quiet.TButton").pack(fill=tk.X)
        ttk.Button(sidebar, text="重置筛选", command=self.reset_filters,
                   style="Quiet.TButton").pack(fill=tk.X, pady=(4, 0))
        ttk.Button(sidebar, text="目录与容量", command=self.storage).pack(side=tk.BOTTOM, fill=tk.X, pady=8)

        self.panes = ttk.Panedwindow(body, orient=tk.HORIZONTAL)
        self.panes.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        left = self.visual_regions["browser"] = ttk.Frame(self.panes, width=470)
        right = self.visual_regions["inspector"] = ttk.Frame(self.panes, width=600)
        self.panes.add(left, weight=4)
        self.panes.add(right, weight=6)
        search = ttk.Frame(left)
        search.pack(fill=tk.X, pady=(0, 12))
        entry = ttk.Entry(search, textvariable=self.filters["query"], width=14)
        entry.pack(side=tk.LEFT, fill=tk.X, expand=True)
        entry.bind("<Return>", lambda _event: self.filter_changed())
        ttk.Button(search, text="搜索", command=self.filter_changed).pack(side=tk.LEFT, padx=(6, 8))
        self.filter_widgets["sort"] = self._combo(
            search, self.sort, CHOICES["sort"], width=9, changed=self.filter_changed,
        )
        self.filter_widgets["sort"].pack(side=tk.RIGHT)

        list_area = ttk.Frame(left)
        list_area.pack(fill=tk.BOTH, expand=True)
        list_area.rowconfigure(0, weight=1)
        list_area.columnconfigure(0, weight=1)
        columns = ("target", "role", "review", "size", "file")
        self.table = ttk.Treeview(
            list_area, columns=columns, show="headings", selectmode="extended",
            displaycolumns=("target", "file", "review", "size"),
        )
        # Full filenames stay in the inspector; they do not dominate every row.
        for key, label, width in zip(
            columns, ("目标", "通道", "标记", "占用", "文件"),
            (125, 95, 55, 70, 170),
        ):
            self.table.heading(key, text=label, anchor=tk.W)
            self.table.column(key, width=width, minwidth=45, anchor=tk.W,
                              stretch=key in {"target", "file"})
        scrollbar = ttk.Scrollbar(list_area, command=self.table.yview)
        self.table.configure(yscrollcommand=scrollbar.set)
        self.table.grid(row=0, column=0, sticky=tk.NSEW)
        scrollbar.grid(row=0, column=1, sticky=tk.NS)
        self.table.bind("<<TreeviewSelect>>", self.select)
        self.table.bind("<Double-1>", lambda _event: self.edit_annotation())
        list_area.bind("<Configure>", lambda event: self._fit_table_columns(event.width - 12))
        navigation = ttk.Frame(left)
        navigation.pack(side=tk.BOTTOM, fill=tk.X, pady=(8, 6), before=list_area)
        self.previous_button = ttk.Button(navigation, text="‹", style="Small.TButton", command=lambda: self.page(-1))
        self.previous_button.pack(side=tk.LEFT)
        self.next_button = ttk.Button(navigation, text="›", style="Small.TButton", command=lambda: self.page(1))
        self.next_button.pack(side=tk.LEFT, padx=4)
        self.page_label = ttk.Label(navigation, style="Muted.TLabel")
        self.page_label.pack(side=tk.RIGHT)

        selection = ttk.Frame(right, padding=(8, 0, 0, 10))
        selection.pack(fill=tk.X)
        ttk.Label(selection, textvariable=self.selection_title, style="Section.TLabel").pack(anchor=tk.W)
        self.filename_label = ttk.Label(selection, textvariable=self.selection_caption,
                                        style="Muted.TLabel", width=1, anchor=tk.W)
        self.filename_label.pack(anchor=tk.W, fill=tk.X, pady=(4, 0))
        self.tabs = ttk.Notebook(right)
        self.tabs.pack(fill=tk.BOTH, expand=True, padx=(8, 0))
        self.viewer = InteractiveImageViewer(
            self.tabs, placeholder="选择一个文件\n原片只读 · 预览不改变数据",
            background="#0b1017", foreground=COLORS["muted"], compact=True,
        )
        self.tabs.add(self.viewer, text="预览")
        self.details = self._text_tab(self.tabs, "元数据")
        self.logs = self._text_tab(self.tabs, "日志与来源")

        review = ttk.Frame(right, padding=(8, 12, 0, 10))
        review.pack(side=tk.BOTTOM, fill=tk.X, before=self.tabs)
        for decision, label in (("pick", "留片"), ("reject", "不采用"), ("unreviewed", "撤销标记")):
            button = ttk.Button(review, text=label, command=lambda d=decision: self.mark(d))
            button.pack(side=tk.LEFT, padx=(0, 6))
            self.row_actions.append(button)
        annotate = ttk.Button(review, text="星级 / 备注…", command=self.edit_annotation,
                              style="Quiet.TButton")
        annotate.pack(side=tk.RIGHT)
        self.row_actions.append(annotate)

        footer = self.visual_regions["footer"] = ttk.Frame(self.file_page, padding=(10, 12, 10, 12))
        # Reserve the footer BEFORE the expanding workspace consumes its parcel.
        footer.pack(side=tk.BOTTOM, fill=tk.X, before=body)
        ttk.Separator(footer).pack(fill=tk.X, pady=(0, 12))
        actions = ttk.Frame(footer)
        actions.pack(fill=tk.X)
        ttk.Label(actions, textvariable=self.selection_count, style="Muted.TLabel").pack(side=tk.LEFT)
        more = ttk.Button(actions, text="文件操作…", command=self.file_actions, style="Quiet.TButton")
        more.pack(side=tk.LEFT, padx=12)
        self.row_actions.append(more)
        reveal = ttk.Button(actions, text="定位源文件", command=self.show_folder,
                            style="Quiet.TButton")
        reveal.pack(side=tk.LEFT)
        self.row_actions.append(reveal)
        self.export_button = ttk.Button(actions, text="打开光谱后期", command=self.export, style="Primary.TButton")
        self.export_button.pack(side=tk.RIGHT)
        self.basket_button = ttk.Button(actions, text="选片篮…", command=self.basket_dialog)
        self.basket_button.pack(side=tk.RIGHT, padx=8)
        self.basket_label = ttk.Label(actions, text="0 帧", style="Accent.TLabel")
        self.basket_label.pack(side=tk.RIGHT, padx=8)
        self.status_label = ttk.Label(footer, textvariable=self.status, style="Muted.TLabel", wraplength=1260)
        self.status_label.pack(anchor=tk.W, fill=tk.X, pady=(10, 0))
        footer.bind("<Configure>", lambda event: self.status_label.configure(
            wraplength=max(event.width - 48, 300)))
        self._update_actions()

    def _dialog(self, title, size="520x390"):
        window = tk.Toplevel(self.root)
        window.title(title)
        window.geometry(size)
        window.transient(self.root)
        window.configure(background=COLORS["background"])
        window.minsize(440, 320)
        content = ttk.Frame(window, padding=22)
        content.pack(fill=tk.BOTH, expand=True)
        ttk.Label(content, text=title, style="Section.TLabel").pack(anchor=tk.W, pady=(0, 16))

        def ready():
            if not window.winfo_exists():
                return
            window.minsize(max(440, content.winfo_reqwidth()), max(320, content.winfo_reqheight()))
            window.grab_set()
            window.focus_set()

        window.after_idle(ready)
        return window, content

    def _fit_table_columns(self, width):
        font = tkfont.Font(self.root, font=self.style.lookup("Treeview", "font"))
        sizes = {"target": max(font.measure("HD 224559") + 14, int(width * 0.27)),
                 "review": font.measure("未筛") + 18, "size": font.measure("100.0 MiB") + 14}
        sizes["file"] = max(width - sum(sizes.values()) - 2, 50)
        for column, pixels in sizes.items():
            self.table.column(column, width=pixels, stretch=False)

    def advanced_filters(self):
        window, content = self._dialog("更多筛选", "520x390")
        for key, label in (("target", "目标名称"), ("date_prefix", "UTC 日期前缀 · 如 2026-10-24"),
                           ("run_id", "运行 ID")):
            ttk.Label(content, text=label, style="Muted.TLabel").pack(anchor=tk.W)
            entry = ttk.Entry(content, textvariable=self.filters[key])
            entry.pack(fill=tk.X, pady=(6, 12))
            entry.bind("<Return>", lambda _event: (self.filter_changed(), window.destroy()))
        ttk.Button(content, text="应用筛选", style="Primary.TButton",
                   command=lambda: (self.filter_changed(), window.destroy())).pack(anchor=tk.E)

    def reset_filters(self):
        self.project_file_ids = None
        for key, variable in self.filters.items():
            variable.set("frame" if key == "kind" else "")
        self.sort.set("date_desc")
        self.filter_changed()

    def edit_annotation(self):
        rows = list(self.selected())
        if not rows:
            return
        window, content = self._dialog("星级与备注", "520x340")
        # Scans can refresh selection even while a modal is open. Keep this draft
        # and its recipients independent of the live inspector's variables.
        rating = tk.StringVar(window, value=self.rating.get())
        notes = tk.StringVar(window, value=self.notes.get())
        ttk.Label(content, text="应用到当前选中的文件；不改写原片。",
                  style="Muted.TLabel").pack(anchor=tk.W, pady=(0, 12))
        self._combo(content, rating, {str(i): "未评分" if i == 0 else "★" * i for i in range(6)}).pack(anchor=tk.W)
        ttk.Label(content, text="备注", style="Muted.TLabel").pack(anchor=tk.W, pady=(14, 6))
        ttk.Entry(content, textvariable=notes).pack(fill=tk.X)
        ttk.Button(content, text="保存", style="Primary.TButton",
                   command=lambda: (self.annotate(rows=rows, rating=rating.get(), notes=notes.get()),
                                    window.destroy())).pack(anchor=tk.E, pady=20)

    def file_actions(self):
        if not self.selected():
            return
        window, content = self._dialog("选中文件", "480x390")
        ttk.Label(content, text=f"已选择 {len(self.selected())} 项", style="Muted.TLabel").pack(anchor=tk.W)
        ttk.Button(content, text="在文件夹中查看", command=self.show_folder).pack(fill=tk.X, pady=12)
        ttk.Button(content, text="归档计划（只读）…", command=self.lifecycle).pack(fill=tk.X)
        ttk.Button(content, text="项目清理（预览后确认）…", command=self.cleanup).pack(fill=tk.X, pady=(8, 0))
        ttk.Label(content, text="归档盘可稍后选择，归档按钮只保存计划。\n项目清理必须另行预览和精确确认。",
                  style="Muted.TLabel").pack(anchor=tk.W, pady=18)

    def basket_dialog(self):
        rows = list(self.selected())
        window, content = self._dialog("光谱后期选片篮", "600x460")
        role = tk.StringVar(window, value=self.role.get())
        target_name = tk.StringVar(window, value=self.target_name.get())
        ttk.Label(content, text=f"当前选中 {len(rows)} 项 · 篮内 {len(self.basket)} 帧",
                  style="Muted.TLabel").pack(anchor=tk.W, pady=(0, 16))
        ttk.Label(content, text="选中帧的处理角色", style="Muted.TLabel").pack(anchor=tk.W)
        self._combo(content, role, CHOICES["role"]).pack(fill=tk.X, pady=(6, 14))
        ttk.Label(content, text="修正目标名称（留空保留原目标）", style="Muted.TLabel").pack(anchor=tk.W)
        ttk.Entry(content, textvariable=target_name).pack(fill=tk.X, pady=(6, 14))
        ttk.Label(content, text="仅光谱原始帧可进入光谱后期。\n测光、盯缝图像与日志仍可在资料库查看。",
                  style="Muted.TLabel").pack(anchor=tk.W, pady=(0, 16))
        def add():
            if self.add_basket(rows=rows, role=role.get(), target_name=target_name.get()):
                window.destroy()

        ttk.Button(content, text="加入选中帧", style="Primary.TButton",
                   state=tk.NORMAL if rows else tk.DISABLED, command=add).pack(fill=tk.X)
        ttk.Button(content, text="查看 / 清空选片篮", command=self.review_basket).pack(fill=tk.X, pady=8)

    def _update_actions(self):
        selected = len(self.selected())
        for button in self.row_actions:
            button.configure(state=tk.NORMAL if selected else tk.DISABLED)
        self.export_button.configure(state=tk.NORMAL if self.basket else tk.DISABLED)
        self.basket_label.configure(text=f"{len(self.basket)} 帧")
        self.selection_count.set(f"已选 {selected} 项" if selected else "未选择文件")

    def _clear_selection(self):
        self.preview_generation += 1
        self.selection_title.set("文件预览")
        self.selection_caption.set("选择左侧文件，查看图像、光谱或光变曲线")
        self.viewer.show_message("选择一个文件\n原片只读 · 预览不改变数据")
        self.rating.set("0")
        self.notes.set("")
        self.target_name.set("")
        self._text(self.details, "")
        self._text(self.logs, "")
        self._update_actions()

    def _text_tab(self, tabs, title):
        frame = ttk.Frame(tabs)
        text = tk.Text(frame, wrap=tk.WORD, background=COLORS["panel"],
                       foreground=COLORS["text"], width=36, relief=tk.FLAT,
                       borderwidth=0, highlightthickness=0, padx=14, pady=14,
                       insertbackground=COLORS["text"], font=("Microsoft YaHei UI", 10),
                       selectbackground=COLORS["selected"])
        scroll = ttk.Scrollbar(frame, command=text.yview)
        text.configure(yscrollcommand=scroll.set, state=tk.DISABLED)
        scroll.pack(side=tk.RIGHT, fill=tk.Y)
        text.pack(fill=tk.BOTH, expand=True)
        tabs.add(frame, text=title)
        return text

    @staticmethod
    def _text(widget, value):
        widget.configure(state=tk.NORMAL)
        widget.delete("1.0", tk.END)
        widget.insert("1.0", value)
        widget.configure(state=tk.DISABLED)

    def _submit(self, work, done):
        # UI callbacks may own Tk variables/widgets. Keep them in this
        # main-thread registry, never in a Future's worker-thread closure.
        self.next_task_id += 1
        task_id = self.next_task_id
        self.callbacks[task_id] = done
        events = self.events
        future = self.pool.submit(work)
        future.add_done_callback(lambda f: events.put((task_id, f)))
        return future

    def _pump(self):
        if self.pump_after:
            self.root.after_cancel(self.pump_after)
            self.pump_after = None
        if self.closed:
            return
        while not self.events.empty():
            task_id, future = self.events.get()
            if future is None:
                self.status.set(str(task_id))
                continue
            done = self.callbacks.pop(task_id, None)
            if done is None:
                continue
            if future.cancelled():
                continue
            try:
                done(future.result())
            except Exception as error:
                self.status.set(f"操作未完成：{error}")
                messagebox.showerror("资料库", str(error), parent=self.root)
        self.pump_after = self.root.after(100, self._pump)

    def refresh(self):
        selection = set(self.table.selection())
        filters = {key: var.get() for key, var in self.filters.items() if var.get()}
        if self.project_file_ids is not None:
            filters["file_ids"] = self.project_file_ids
        with LibraryCatalog(self.database) as catalog:
            rows = catalog.list_files(
                offset=self.offset, limit=self.PAGE_SIZE, sort=self.sort.get(), **filters
            )
            count = catalog.count_files(**filters)
            if self.cached_stats is None:
                self.cached_stats = catalog.stats()
            stats = self.cached_stats
        self.table.delete(*self.table.get_children())
        self.records = {str(row["id"]): row for row in rows}
        for key, row in self.records.items():
            decision = {"pick": "留", "reject": "弃", "unreviewed": "未筛"}.get(
                row.get("review"), "未筛"
            )
            self.table.insert(
                "",
                tk.END,
                iid=key,
                values=(
                    row.get("target") or "未知",
                    CHOICES["channel"].get(row.get("channel"), "未知"),
                    decision,
                    display_size(row.get("size_bytes", 0)),
                    Path(row["path"]).name,
                ),
            )
        self.summary.set(
            f"{stats.get('file_count', 0):,} 个文件    /    {display_size(stats.get('total_bytes', 0))}    /    {stats.get('missing_count', 0)} 项离线"
        )
        self.page_label.configure(
            text=f"{self.offset + 1 if count else 0}–{min(self.offset + len(rows), count)} / {count:,}"
        )
        self.previous_button.configure(state=tk.NORMAL if self.offset else tk.DISABLED)
        self.next_button.configure(state=tk.NORMAL if self.offset + len(rows) < count else tk.DISABLED)
        remaining = [key for key in self.records if key in selection]
        if remaining:
            self.table.selection_set(remaining)
            self.table.see(remaining[0])
        else:
            self._clear_selection()
        self._update_actions()

    def filter_changed(self):
        self.offset = 0
        self.refresh()

    def page(self, direction):
        if direction > 0 and len(self.records) < self.PAGE_SIZE:
            return
        self.offset = max(0, self.offset + direction * self.PAGE_SIZE)
        self.refresh()

    def add_root(self):
        directory = filedialog.askdirectory(
            parent=self.root, title="添加原地索引目录（不会复制文件）"
        )
        if directory:
            with LibraryCatalog(self.database) as catalog:
                catalog.add_root(directory)
            self.status.set(f"已添加 {directory}；点击更新索引。")

    def storage(self):
        from .library_storage_ui import show_storage_window

        with LibraryCatalog(self.database) as catalog:
            summary = {
                "catalog": str(self.database),
                "roots": catalog.list_roots(),
                "stats": catalog.stats(),
            }
        return show_storage_window(self.root, self.database, summary, cleanup=self.cleanup)

    def cleanup(self, project_id=None):
        from .library_cleanup_ui import show_cleanup_window

        return show_cleanup_window(self, project_id)

    def scan(self):
        if self.scanning:
            return
        self.scanning = True
        self.cancel_scan.clear()
        self.scan_button.configure(state=tk.DISABLED)
        self.stop_scan_button.configure(state=tk.NORMAL)
        self.status.set("正在后台更新索引；不读取全部像素，不复制原片……")
        database, events, cancel_scan = self.database, self.events, self.cancel_scan

        def work():
            try:
                with LibraryCatalog(database) as catalog:
                    result = catalog.scan(
                        progress=lambda p: events.put((f"索引中：{p}", None)),
                        cancelled=cancel_scan.is_set,
                    )
                return result, None
            except Exception as error:
                return None, str(error)

        def done(result):
            self.scanning = False
            self.scan_button.configure(state=tk.NORMAL)
            self.stop_scan_button.configure(state=tk.DISABLED)
            self.cached_stats = None
            self.refresh()
            self.projects.refresh()
            self.status.set(
                f"索引结果：{result[0]}" if result[1] is None else f"索引未完成：{result[1]}"
            )

        self._submit(work, done)

    def selected(self):
        return [self.records[key] for key in self.table.selection() if key in self.records]

    def select(self, _event=None):
        rows = self.selected()
        self.preview_generation += 1
        generation = self.preview_generation
        if not rows:
            self._clear_selection()
            return
        row = rows[0]
        self.selection_title.set(row.get("target") or "未知目标")
        channel = CHOICES["channel"].get(row.get("channel"), "未知通道")
        from .library_file_state import inspect_source_file
        from .library_cleanup_execute import latest_file_receipt

        with LibraryCatalog(self.database) as catalog:
            lifecycle = latest_file_receipt(catalog, row["path"])
        source_state = inspect_source_file(row['path'], row.get('review', ''), lifecycle=lifecycle)
        self.selection_caption.set(f"{channel} · {Path(row['path']).name}")
        self.status.set(f"{source_state.presence_label} · {source_state.review_label} · {source_state.lifecycle_label} · 可点击“定位源文件”。")
        self._update_actions()
        with LibraryCatalog(self.database) as catalog:
            detail = catalog.get_file(row["id"]) or row
        self.rating.set(str(detail.get("rating", 0)))
        self.notes.set(detail.get("notes", ""))
        # An automatically filled first target must never rename a multi-target selection.
        self.target_name.set("")
        self._text(self.details, json.dumps(detail, ensure_ascii=False, indent=2, default=str))
        self._text(
            self.logs,
            json.dumps(
                {
                    key: detail.get(key)
                    for key in ("runs", "references", "related_paths", "warnings")
                },
                ensure_ascii=False,
                indent=2,
                default=str,
            ),
        )
        self.viewer.show_message("正在读取选中文件……")
        if self.preview_future:
            self.preview_future.cancel()

        def work():
            try:
                return load_library_preview(row["path"]), None
            except Exception as error:
                return None, str(error)

        def done(result):
            if generation != self.preview_generation:
                return
            preview, error = result
            if error:
                self.viewer.show_message(f"不能预览：{error}\n原文件未改动。")
                return
            if preview.image is not None:
                self.viewer.set_image(preview.image, source=preview.caption)
            else:
                self.viewer.show_message(preview.caption + "\n文本见日志页")
            if preview.text:
                self._text(
                    self.logs,
                    "运行与来源（原始记录，非人工筛片结论）：\n"
                    + json.dumps(
                        {key: detail.get(key) for key in ("runs", "references", "warnings")},
                        ensure_ascii=False,
                        indent=2,
                    )
                    + "\n\n选中文件预览：\n"
                    + preview.text
                    + "\n\n相关来源：\n"
                    + json.dumps(detail.get("related_paths", []), ensure_ascii=False, indent=2),
                )

        self.preview_future = self._submit(work, done)

    def mark(self, decision):
        with LibraryCatalog(self.database) as catalog:
            for row in self.selected():
                current = catalog.get_file(row["id"])
                catalog.set_review(
                    row["id"],
                    decision=decision,
                    rating=current.get("rating", 0),
                    notes=current.get("notes", ""),
                )
                if decision == "reject":
                    self.basket.pop(row["path"], None)
        self.basket_label.configure(text=f"{len(self.basket)} 帧")
        self.refresh()
        self.status.set("本次仅保存人工标记；未删除、未移动、未移入回收站，释放空间 0 B。既往清理操作另看回执。")
        self.projects.refresh()

    def annotate(self, *, rows=None, rating=None, notes=None):
        rows = self.selected() if rows is None else rows
        with LibraryCatalog(self.database) as catalog:
            for row in rows:
                current = catalog.get_file(row["id"])
                if current is None:
                    continue
                catalog.set_review(
                    row["id"],
                    decision=current.get("review", "unreviewed"),
                    rating=int(self.rating.get() if rating is None else rating),
                    notes=self.notes.get() if notes is None else notes,
                )
        self.status.set("星级/备注已保存到资料库。")

    def show_folder(self):
        if self.selected():
            self.reveal_path(self.selected()[0]["path"])

    def reveal_path(self, path):
        from .library_file_state import reveal_source_file

        try:
            result = reveal_source_file(path)
            self.status.set(result.notice)
            return result.notice
        except OSError as error:
            messagebox.showwarning("源文件位置", str(error), parent=self.root)
            return str(error)

    def browse_project_files(self, run_id="", rows=None):
        """Keep the existing file inspector while entering from a specific run."""
        self.reset_filters()
        self.filters["kind"].set("")
        if rows is not None:
            self.project_file_ids = [row["id"] for row in rows]
        else:
            self.filters["run_id"].set(run_id)
        self.filter_changed()
        if rows:
            keys = [str(row["id"]) for row in rows if str(row["id"]) in self.records]
            if keys:
                self.table.selection_set(keys)
                self.table.see(keys[0])
                self.select()
        self.workspace.select(self.file_page)

    def add_basket(self, *, rows=None, role=None, target_name=None):
        try:
            rows = self.selected() if rows is None else rows
            role = self.role.get() if role is None else role
            target_name = self.target_name.get() if target_name is None else target_name
            if not rows:
                return False
            # A scan/manual tag may have changed records since the dialog opened.
            with LibraryCatalog(self.database) as catalog:
                rows = [catalog.get_file(row["id"]) or row for row in rows]
            if target_name.strip() and len({row.get("target") for row in rows}) > 1:
                raise ValueError("选中了多个目标。请清空目标名修正栏以保留各自身份，或分目标加入。")
            if any(row.get("channel") in {"unknown", ""} for row in rows):
                if not messagebox.askyesno(
                    "确认原片通道",
                    "部分帧的相机通道未知。请确认它们是二维光谱/光谱校准帧，而非测光或盯缝图像。\n\n确认加入光谱后期？",
                    parent=self.root,
                ):
                    return False
            frames = [reduction_frame(row, role, target_name) for row in rows]
        except (ValueError, OSError) as error:
            messagebox.showwarning("选片篮", str(error), parent=self.root)
            return False
        for frame in frames:
            self.basket[frame["path"]] = frame
        self.basket_label.configure(text=f"{len(self.basket)} 帧")
        self._update_actions()
        return True

    def review_basket(self):
        text = "\n".join(
            f"{item['role']} | {item['target']} | {Path(item['path']).name}"
            for item in self.basket.values()
        )
        if messagebox.askyesno(
            "选片篮（尚未开始处理）", (text or "选片篮为空") + "\n\n清空选片篮？", parent=self.root
        ):
            self.basket.clear()
            self.basket_label.configure(text="0 帧")
            self._update_actions()

    def export(self):
        if not self.basket:
            messagebox.showinfo("后期工程", "请先将需要处理的光谱帧加入选片篮。", parent=self.root)
            return
        destination = filedialog.asksaveasfilename(
            parent=self.root,
            title="保存精确选片工程",
            defaultextension=".astroproj",
            filetypes=[("后期工程", "*.astroproj")],
        )
        if not destination:
            return
        frames = list(self.basket.values())
        self.status.set("正在核对选片并计算清单哈希；不会开始后期处理……")

        def work():
            from .library_selection import export_library_project

            return export_library_project(
                destination,
                name=Path(destination).stem,
                data_root=str(Path(frames[0]["path"]).parent),
                output_root=str(Path(destination).parent / "reduced"),
                frames=frames,
            )

        def done(path):
            from .library_projects import LibraryProjects

            with LibraryCatalog(self.database) as catalog:
                LibraryProjects(catalog).index_project(path)
            self.projects.refresh()
            subprocess.Popen(
                [sys.executable, "-m", "uvex_reduce.gui", "--project", str(path)], shell=False
            )
            self.status.set(f"已打开精确选片工程：{path}；没有自动开始处理或观测。")

        self._submit(work, done)

    def lifecycle(self):
        from .library_lifecycle import build_lifecycle_plan, save_lifecycle_plan

        rows = self.selected()
        if not rows:
            return
        plan = build_lifecycle_plan([row["path"] for row in rows])
        self._text(self.logs, json.dumps(plan.to_dict(), ensure_ascii=False, indent=2, default=str))
        if messagebox.askyesno(
            "仅预览，没有删除/归档",
            f"选中 {len(rows)} 项。文件写入状态及共享引用仍需核查。\n本次释放空间：0。\n\n保存详细计划供审阅？",
            parent=self.root,
        ):
            destination = filedialog.asksaveasfilename(
                parent=self.root,
                title="保存只读清理/归档计划（不覆盖已有文件）",
                defaultextension=".json",
                filetypes=[("计划", "*.json")],
            )
            if destination:
                try:
                    save_lifecycle_plan(plan, destination)
                    self.status.set(f"计划已保存：{destination}。未执行文件清理。")
                except Exception as error:
                    messagebox.showerror("计划未保存", str(error), parent=self.root)

    def close(self):
        active_cleanup = [window for window in self.cleanup_windows if window.executing]
        if active_cleanup:
            for window in active_cleanup:
                window.cancel_remaining()
            self.status.set("已请求取消清理剩余文件；请等待当前文件和审计回执完成，再关闭资料库。")
            return
        self.closed = True
        self.cancel_scan.set()
        if self.pump_after:
            self.root.after_cancel(self.pump_after)
            self.pump_after = None
        self.callbacks.clear()
        self.pool.shutdown(wait=False, cancel_futures=True)
        self.root.destroy()
        # Destroy removes Tcl commands; break the remaining Python-only owner
        # cycle on the Tk thread rather than leaving it for a later worker GC.
        self.projects.app = None
        self.cleanup_windows.clear()


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="OpenAstroSpec offline in-place observation library"
    )
    parser.add_argument(
        "--catalog", type=Path, help="SQLite catalogue path; source data remain read-only"
    )
    parser.add_argument(
        "--index-only",
        action="store_true",
        help="Update registered/suggested roots without opening a window",
    )
    args = parser.parse_args(argv)
    if args.index_only:
        with LibraryCatalog(args.catalog) as catalog:
            if not catalog.list_roots():
                for path in suggested_roots():
                    catalog.add_root(path)
            result = catalog.scan()
            print(
                json.dumps(
                    {"scan": result, "stats": catalog.stats()}, ensure_ascii=False, default=str
                )
            )
        return
    root = tk.Tk()
    try:
        LibraryApp(root, args.catalog)
    except Exception as error:
        messagebox.showerror("资料库未能启动", str(error), parent=root)
        root.destroy()
        raise
    root.mainloop()


if __name__ == "__main__":
    main()
