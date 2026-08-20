"""DaVinci-inspired staged desktop workbench for UVEX reduction.

The workbench deliberately keeps the proven reduction engine unchanged.  It
adds project/media organization, explicit quality gates, artifact review and a
delivery page around the existing pipeline so an operator can move forward and
backward without losing the scientific context of a run.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk

from PIL import Image

from astropy.io import fits
import numpy as np

from .gui import PROJECT_ROOT, WORKFLOW_PRESETS, UvexReductionApp
from .image_viewer import InteractiveImageViewer, make_neutral_fits_preview
from .project import (
    AstroProject,
    EquipmentPreset,
    load_equipment_presets,
    save_equipment_presets,
)


WORKSPACE_ROOT = PROJECT_ROOT.parent
REPORT_PDF = WORKSPACE_ROOT / "output" / "pdf" / "UVEX-ADV_20260504_3C273_Report_zh-CN.pdf"
SOP_PDF = WORKSPACE_ROOT / "output" / "pdf" / "UVEX-ADV_Manual_Reduction_SOP_zh-CN.pdf"
REPORT_SOURCE = PROJECT_ROOT / "docs" / "20260504-3c273-processing-hbeta-report.md"
SOP_SOURCE = PROJECT_ROOT / "docs" / "manual-reduction-sop.md"
KNOWN_3C273_OUTPUT = PROJECT_ROOT / "output" / "3C273" / "2026-05-04"
USER_SETTINGS_DIRECTORY = Path(os.environ.get("APPDATA", Path.home())) / "UVEX-ADV"
EQUIPMENT_PRESET_STORE = USER_SETTINGS_DIRECTORY / "equipment-presets.json"

STAGES = (
    ("media", "1  媒体"),
    ("masters", "2  主校准"),
    ("geometry", "3  几何/提取"),
    ("wavelength", "4  波长"),
    ("response", "5  响应"),
    ("analyse", "6  分析"),
    ("deliver", "7  交付"),
)

COLORS = {
    "background": "#0b1017",
    "surface": "#121923",
    "surface_alt": "#182230",
    "surface_high": "#202c3b",
    "border": "#2d3b4d",
    "text": "#e8eef7",
    "muted": "#93a4b8",
    "accent": "#2dd4bf",
    "accent_dark": "#0f766e",
    "warning": "#f6c453",
    "danger": "#ef6b73",
    "success": "#6ee7a8",
    "blue": "#60a5fa",
}


def artifact_kind(path: Path) -> str:
    suffix = path.suffix.casefold()
    return {
        ".png": "诊断图",
        ".fits": "FITS",
        ".fit": "FITS",
        ".csv": "表格",
        ".json": "质控",
        ".md": "文档",
        ".pdf": "报告",
    }.get(suffix, "文件")


def artifact_priority(path: Path) -> tuple[int, str]:
    name = path.name.casefold()
    priorities = (
        ("hbeta_diagnostic", 0),
        ("normalised_1d", 1),
        ("normalized_1d", 1),
        ("identity_overlay", 2),
        ("spectrum.png", 3),
        ("trace_overlay", 4),
        ("wavelength_residuals", 5),
        ("alignment", 6),
        ("preprocessed", 7),
    )
    for token, score in priorities:
        if token in name:
            return score, name
    return 50, name


def artifact_is_quarantined(path: Path, root: Path) -> bool:
    """Hide explicitly invalidated legacy/debug products from the workbench."""

    try:
        relative = path.relative_to(root)
    except ValueError:
        relative = path
    return any(
        "invalid" in part.casefold() or "do-not-use" in part.casefold()
        for part in relative.parts[:-1]
    )


class UvexStudioApp(UvexReductionApp):
    """Staged operator interface backed by :class:`UvexReductionApp` workers."""

    def __init__(self, root: tk.Tk):
        self.stage_frames: dict[str, ttk.Frame] = {}
        self.stage_buttons: dict[str, ttk.Button] = {}
        self.stage_state_labels: dict[str, ttk.Label] = {}
        self.current_stage = "media"
        self.project_path: Path | None = None
        self.approved_stages: set[str] = set()
        self.active_run_kind: str | None = None
        self.media_items: dict[str, object] = {}
        self.artifact_paths: dict[str, Path] = {}
        presets = load_equipment_presets(EQUIPMENT_PRESET_STORE)
        self.equipment_presets = {preset.name: preset for preset in presets}
        default_equipment = next(
            preset for preset in presets if preset.slit_micrometre == 35
        )
        self.project_name = tk.StringVar(master=root, value="未命名 UVEX 工程")
        self.equipment_choice = tk.StringVar(master=root, value=default_equipment.name)
        self.equipment_slit = tk.StringVar(master=root, value=str(default_equipment.slit_micrometre))
        self.equipment_dispersion = tk.StringVar(
            master=root,
            value=f"{default_equipment.estimated_dispersion_angstrom_per_pixel:.3f}",
        )
        self.equipment_direction = tk.StringVar(
            master=root,
            value=default_equipment.raw_dispersion_direction,
        )
        super().__init__(root)
        self.root.geometry("1440x900")
        self.root.minsize(1180, 720)
        self.root.protocol("WM_DELETE_WINDOW", self._close_project)
        if KNOWN_3C273_OUTPUT.is_dir():
            self.last_output = KNOWN_3C273_OUTPUT
            self.open_button.configure(state=tk.NORMAL)
            self.refresh_artifacts(KNOWN_3C273_OUTPUT)
        self._update_document_cards()

    def _configure_style(self) -> None:
        self.root.configure(background=COLORS["background"])
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(
            ".",
            background=COLORS["background"],
            foreground=COLORS["text"],
            fieldbackground=COLORS["surface_alt"],
            bordercolor=COLORS["border"],
            lightcolor=COLORS["border"],
            darkcolor=COLORS["border"],
            font=("Microsoft YaHei UI", 10),
        )
        style.configure("Studio.TFrame", background=COLORS["background"])
        style.configure("Panel.TFrame", background=COLORS["surface"])
        style.configure("Raised.TFrame", background=COLORS["surface_alt"])
        style.configure(
            "Title.TLabel",
            background=COLORS["background"],
            foreground=COLORS["text"],
            font=("Microsoft YaHei UI", 20, "bold"),
        )
        style.configure(
            "Subtitle.TLabel",
            background=COLORS["background"],
            foreground=COLORS["muted"],
            font=("Microsoft YaHei UI", 9),
        )
        style.configure(
            "PanelTitle.TLabel",
            background=COLORS["surface"],
            foreground=COLORS["text"],
            font=("Microsoft YaHei UI", 12, "bold"),
        )
        style.configure(
            "CardTitle.TLabel",
            background=COLORS["surface_alt"],
            foreground=COLORS["text"],
            font=("Microsoft YaHei UI", 11, "bold"),
        )
        style.configure(
            "Muted.TLabel",
            background=COLORS["surface"],
            foreground=COLORS["muted"],
        )
        style.configure(
            "CardMuted.TLabel",
            background=COLORS["surface_alt"],
            foreground=COLORS["muted"],
        )
        style.configure(
            "Accent.TButton",
            background=COLORS["accent_dark"],
            foreground="#ffffff",
            padding=(14, 8),
            font=("Microsoft YaHei UI", 10, "bold"),
        )
        style.map(
            "Accent.TButton",
            background=[("active", COLORS["accent"]), ("disabled", COLORS["surface_high"])],
            foreground=[("active", "#06201d"), ("disabled", COLORS["muted"])],
        )
        style.configure(
            "Quiet.TButton",
            background=COLORS["surface_high"],
            foreground=COLORS["text"],
            padding=(11, 7),
        )
        style.map("Quiet.TButton", background=[("active", COLORS["border"])])
        style.configure(
            "Stage.TButton",
            background=COLORS["surface"],
            foreground=COLORS["muted"],
            borderwidth=0,
            padding=(20, 11),
            font=("Microsoft YaHei UI", 10, "bold"),
        )
        style.configure(
            "StageActive.TButton",
            background=COLORS["accent_dark"],
            foreground="#ffffff",
            borderwidth=0,
            padding=(20, 11),
            font=("Microsoft YaHei UI", 10, "bold"),
        )
        style.map("Stage.TButton", background=[("active", COLORS["surface_high"])])
        style.map("StageActive.TButton", background=[("active", COLORS["accent"])])
        style.configure(
            "Status.TLabel",
            background=COLORS["surface_alt"],
            foreground=COLORS["muted"],
            padding=(7, 3),
            font=("Microsoft YaHei UI", 8),
        )
        style.configure(
            "StatusDone.TLabel",
            background="#12372f",
            foreground=COLORS["success"],
            padding=(7, 3),
            font=("Microsoft YaHei UI", 8, "bold"),
        )
        style.configure(
            "StatusBusy.TLabel",
            background="#3d3214",
            foreground=COLORS["warning"],
            padding=(7, 3),
            font=("Microsoft YaHei UI", 8, "bold"),
        )
        style.configure(
            "TEntry",
            fieldbackground=COLORS["surface_alt"],
            foreground=COLORS["text"],
            insertcolor=COLORS["text"],
            padding=6,
        )
        style.configure(
            "TCombobox",
            fieldbackground=COLORS["surface_alt"],
            foreground=COLORS["text"],
            arrowcolor=COLORS["text"],
            padding=5,
        )
        style.map(
            "TCombobox",
            fieldbackground=[("readonly", COLORS["surface_alt"])],
            foreground=[("readonly", COLORS["text"])],
        )
        style.configure(
            "Treeview",
            background=COLORS["surface_alt"],
            fieldbackground=COLORS["surface_alt"],
            foreground=COLORS["text"],
            rowheight=27,
            borderwidth=0,
        )
        style.configure(
            "Treeview.Heading",
            background=COLORS["surface_high"],
            foreground=COLORS["text"],
            font=("Microsoft YaHei UI", 9, "bold"),
            padding=6,
        )
        style.map(
            "Treeview",
            background=[("selected", COLORS["accent_dark"])],
            foreground=[("selected", "#ffffff")],
        )
        style.configure(
            "Horizontal.TProgressbar",
            background=COLORS["accent"],
            troughcolor=COLORS["surface_high"],
            borderwidth=0,
        )

    def _build_ui(self) -> None:
        self._configure_style()
        self.root.title("OpenAstroSpec Spectral Studio — UVEX4")

        shell = ttk.Frame(self.root, style="Studio.TFrame", padding=(16, 12, 16, 10))
        shell.pack(fill=tk.BOTH, expand=True)
        shell.columnconfigure(0, weight=1)
        shell.rowconfigure(1, weight=1)

        top = ttk.Frame(shell, style="Studio.TFrame")
        top.grid(row=0, column=0, sticky=tk.EW, pady=(0, 10))
        top.columnconfigure(1, weight=1)
        ttk.Label(top, text="OpenAstroSpec — UVEX4", style="Title.TLabel").grid(row=0, column=0, sticky=tk.W)
        ttk.Label(
            top,
            text="SPECTRAL STUDIO  ·  非破坏式长缝光谱工作台",
            style="Subtitle.TLabel",
        ).grid(row=1, column=0, sticky=tk.W)
        ttk.Label(top, textvariable=self.project_name, style="Subtitle.TLabel").grid(
            row=0,
            column=1,
            sticky=tk.E,
            padx=18,
        )
        ttk.Label(top, textvariable=self.status, style="Subtitle.TLabel").grid(
            row=1,
            column=1,
            sticky=tk.E,
            padx=18,
        )
        project_actions = ttk.Frame(top, style="Studio.TFrame")
        project_actions.grid(row=0, column=2, rowspan=2, sticky=tk.E, padx=(0, 8))
        ttk.Button(
            project_actions,
            text="打开工程",
            style="Quiet.TButton",
            command=self.load_project,
        ).pack(side=tk.LEFT)
        ttk.Button(
            project_actions,
            text="保存工程",
            style="Quiet.TButton",
            command=self.save_project,
        ).pack(side=tk.LEFT, padx=(5, 0))
        self.open_button = ttk.Button(
            top,
            text="打开当前输出",
            style="Quiet.TButton",
            command=self._open_output,
            state=tk.DISABLED,
        )
        self.open_button.grid(row=0, column=3, rowspan=2, sticky=tk.E)

        self.page_host = ttk.Frame(shell, style="Studio.TFrame")
        self.page_host.grid(row=1, column=0, sticky=tk.NSEW)
        self.page_host.columnconfigure(0, weight=1)
        self.page_host.rowconfigure(0, weight=1)

        self.stage_frames["media"] = self._build_media_page(self.page_host)
        self.stage_frames["masters"] = self._build_calibration_page(self.page_host)
        self.stage_frames["geometry"] = self._build_extraction_page(self.page_host)
        self.stage_frames["wavelength"] = self._build_wavelength_page(self.page_host)
        self.stage_frames["response"] = self._build_response_page(self.page_host)
        self.stage_frames["analyse"] = self._build_analysis_page(self.page_host)
        self.stage_frames["deliver"] = self._build_delivery_page(self.page_host)
        for frame in self.stage_frames.values():
            frame.grid(row=0, column=0, sticky=tk.NSEW)

        process_bar = ttk.Frame(shell, style="Raised.TFrame", padding=(12, 7))
        process_bar.grid(row=2, column=0, sticky=tk.EW, pady=(10, 0))
        process_bar.columnconfigure(0, weight=1)
        ttk.Label(
            process_bar,
            text="处理链  SDK修复  ›  主校准  ›  2D几何/天空  ›  1D提取  ›  波长标定  ›  响应/归一化  ›  科学质控",
            style="CardMuted.TLabel",
        ).grid(row=0, column=0, sticky=tk.W)
        self.progress = ttk.Progressbar(process_bar, mode="indeterminate", length=240)
        self.progress.grid(row=0, column=1, sticky=tk.E)

        navigation = ttk.Frame(shell, style="Panel.TFrame", padding=(6, 4))
        navigation.grid(row=3, column=0, sticky=tk.EW, pady=(7, 0))
        for column, (key, title) in enumerate(STAGES):
            navigation.columnconfigure(column, weight=1)
            slot = ttk.Frame(navigation, style="Panel.TFrame")
            slot.grid(row=0, column=column, sticky=tk.EW, padx=2)
            slot.columnconfigure(0, weight=1)
            button = ttk.Button(
                slot,
                text=title,
                style="Stage.TButton",
                command=lambda selected=key: self.switch_stage(selected),
            )
            button.grid(row=0, column=0, sticky=tk.EW)
            state = ttk.Label(slot, text="待处理", style="Status.TLabel", anchor=tk.CENTER)
            state.grid(row=1, column=0, pady=(2, 0))
            self.stage_buttons[key] = button
            self.stage_state_labels[key] = state
        self.switch_stage("media")

    def _panel(self, parent: tk.Misc, title: str) -> tuple[ttk.Frame, ttk.Frame]:
        outer = ttk.Frame(parent, style="Panel.TFrame", padding=12)
        ttk.Label(outer, text=title, style="PanelTitle.TLabel").pack(anchor=tk.W, pady=(0, 9))
        body = ttk.Frame(outer, style="Panel.TFrame")
        body.pack(fill=tk.BOTH, expand=True)
        return outer, body

    def _build_media_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=315)
        page.columnconfigure(1, weight=1)
        page.columnconfigure(2, weight=0, minsize=310)
        page.rowconfigure(0, weight=1)

        project_panel, project = self._panel(page, "项目与素材库")
        project_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        project.columnconfigure(0, weight=1)
        ttk.Label(project, text="原始数据根目录", style="Muted.TLabel").grid(
            row=0, column=0, sticky=tk.W
        )
        ttk.Entry(project, textvariable=self.data_root).grid(row=1, column=0, sticky=tk.EW, pady=(3, 6))
        path_actions = ttk.Frame(project, style="Panel.TFrame")
        path_actions.grid(row=2, column=0, sticky=tk.EW)
        ttk.Button(path_actions, text="浏览", style="Quiet.TButton", command=self._browse_data).pack(
            side=tk.LEFT
        )
        ttk.Button(path_actions, text="扫描 FITS", style="Accent.TButton", command=self.scan).pack(
            side=tk.LEFT, padx=6
        )
        ttk.Label(project, text="交付输出目录", style="Muted.TLabel").grid(
            row=3, column=0, sticky=tk.W, pady=(18, 0)
        )
        ttk.Entry(project, textvariable=self.output_root).grid(row=4, column=0, sticky=tk.EW, pady=(3, 6))
        ttk.Button(project, text="选择输出目录", style="Quiet.TButton", command=self._browse_output).grid(
            row=5, column=0, sticky=tk.W
        )
        ttk.Separator(project).grid(row=6, column=0, sticky=tk.EW, pady=18)
        self.media_summary = ttk.Label(
            project,
            text="尚未扫描",
            style="Muted.TLabel",
            justify=tk.LEFT,
            wraplength=280,
        )
        self.media_summary.grid(row=7, column=0, sticky=tk.EW)
        ttk.Separator(project).grid(row=8, column=0, sticky=tk.EW, pady=14)
        ttk.Label(project, text="设备预设", style="Muted.TLabel").grid(
            row=9, column=0, sticky=tk.W
        )
        self.equipment_combo = ttk.Combobox(
            project,
            state="readonly",
            textvariable=self.equipment_choice,
            values=list(self.equipment_presets),
        )
        self.equipment_combo.grid(row=10, column=0, sticky=tk.EW, pady=(3, 6))
        self.equipment_combo.bind("<<ComboboxSelected>>", self._apply_equipment_preset)
        equipment_grid = ttk.Frame(project, style="Panel.TFrame")
        equipment_grid.grid(row=11, column=0, sticky=tk.EW)
        equipment_grid.columnconfigure(1, weight=1)
        ttk.Label(equipment_grid, text="狭缝 µm", style="Muted.TLabel").grid(
            row=0, column=0, sticky=tk.W, padx=(0, 8)
        )
        ttk.Entry(equipment_grid, textvariable=self.equipment_slit, width=8).grid(
            row=0, column=1, sticky=tk.EW
        )
        ttk.Label(equipment_grid, text="初始 Å/pixel", style="Muted.TLabel").grid(
            row=1, column=0, sticky=tk.W, padx=(0, 8), pady=(5, 0)
        )
        ttk.Entry(equipment_grid, textvariable=self.equipment_dispersion, width=8).grid(
            row=1, column=1, sticky=tk.EW, pady=(5, 0)
        )
        ttk.Button(
            project,
            text="另存为设备预设",
            style="Quiet.TButton",
            command=self._save_equipment_preset,
        ).grid(row=12, column=0, sticky=tk.EW, pady=(7, 0))

        bin_panel, bin_body = self._panel(page, "媒体池  ·  按观测组组织")
        bin_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=7)
        bin_body.columnconfigure(0, weight=1)
        bin_body.rowconfigure(0, weight=1)
        bin_body.rowconfigure(1, weight=1)
        self.media_tree = ttk.Treeview(
            bin_body,
            columns=("role", "target", "frames", "night", "path"),
            show="headings",
            selectmode="browse",
        )
        headings = {
            "role": ("角色", 85),
            "target": ("目标", 130),
            "frames": ("帧数", 55),
            "night": ("夜次", 105),
            "path": ("相对路径", 310),
        }
        for name, (title, width) in headings.items():
            self.media_tree.heading(name, text=title)
            self.media_tree.column(name, width=width, minwidth=50, stretch=name == "path")
        self.media_tree.grid(row=0, column=0, sticky=tk.NSEW)
        media_scroll = ttk.Scrollbar(bin_body, orient=tk.VERTICAL, command=self.media_tree.yview)
        media_scroll.grid(row=0, column=1, sticky=tk.NS)
        self.media_tree.configure(yscrollcommand=media_scroll.set)
        self.media_tree.bind("<<TreeviewSelect>>", self._show_media_item)
        self.media_preview_label = InteractiveImageViewer(
            bin_body,
            placeholder="选择观测组后显示首帧 2D FITS",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.media_preview_label.grid(
            row=1,
            column=0,
            columnspan=2,
            sticky=tk.NSEW,
            pady=(9, 0),
        )

        inspect_panel, inspector = self._panel(page, "素材检查器")
        inspect_panel.grid(row=0, column=2, sticky=tk.NSEW, padx=(7, 0))
        inspector.rowconfigure(0, weight=1)
        inspector.columnconfigure(0, weight=1)
        self.media_inspector = tk.Text(
            inspector,
            background=COLORS["surface_alt"],
            foreground=COLORS["text"],
            insertbackground=COLORS["text"],
            relief=tk.FLAT,
            wrap=tk.WORD,
            padx=10,
            pady=10,
            font=("Microsoft YaHei UI", 9),
            state=tk.DISABLED,
        )
        self.media_inspector.grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            inspector,
            text="确认素材并进入校准",
            style="Accent.TButton",
            command=self._accept_media,
        ).grid(row=1, column=0, sticky=tk.EW, pady=(10, 0))
        return page

    def _build_calibration_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=340)
        page.columnconfigure(1, weight=1)
        page.columnconfigure(2, weight=0, minsize=340)
        page.rowconfigure(0, weight=1)

        standards_panel, standards = self._panel(page, "标准星与目标配对")
        standards_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        standards.columnconfigure(0, weight=1)
        standards.rowconfigure(0, weight=1)
        self.group_list = tk.Listbox(
            standards,
            exportselection=False,
            background=COLORS["surface_alt"],
            foreground=COLORS["text"],
            selectbackground=COLORS["accent_dark"],
            selectforeground="#ffffff",
            relief=tk.FLAT,
            highlightthickness=0,
            font=("Microsoft YaHei UI", 9),
        )
        self.group_list.grid(row=0, column=0, sticky=tk.NSEW)
        self.group_list.bind("<<ListboxSelect>>", self._show_selection)
        group_scroll = ttk.Scrollbar(standards, orient=tk.VERTICAL, command=self.group_list.yview)
        group_scroll.grid(row=0, column=1, sticky=tk.NS)
        self.group_list.configure(yscrollcommand=group_scroll.set)
        self.details = ttk.Label(
            standards,
            text="",
            style="Muted.TLabel",
            justify=tk.LEFT,
            wraplength=305,
        )
        self.details.grid(row=1, column=0, columnspan=2, sticky=tk.EW, pady=(10, 0))
        ttk.Label(standards, text="套用到同目录目标", style="Muted.TLabel").grid(
            row=2, column=0, columnspan=2, sticky=tk.W, pady=(12, 4)
        )
        self.target_combo = ttk.Combobox(standards, state="readonly")
        self.target_combo.grid(row=3, column=0, columnspan=2, sticky=tk.EW)
        self.target_combo["values"] = ["仅建立标准星波长解"]
        self.target_combo.current(0)

        gate_panel, gate = self._panel(page, "校准质量门")
        gate_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=7)
        gate.columnconfigure(0, weight=1)
        gate.rowconfigure(0, weight=1)
        gate.rowconfigure(1, weight=1)
        self.calibration_preview_label = InteractiveImageViewer(
            gate,
            placeholder="选择标准星后显示 2D FITS",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.calibration_preview_label.grid(
            row=0,
            column=0,
            columnspan=2,
            sticky=tk.NSEW,
            pady=(0, 9),
        )
        self.gate_tree = ttk.Treeview(
            gate,
            columns=("state", "reason"),
            show="tree headings",
            selectmode="browse",
        )
        self.gate_tree.heading("#0", text="检查项")
        self.gate_tree.heading("state", text="状态")
        self.gate_tree.heading("reason", text="依据 / 人工确认")
        self.gate_tree.column("#0", width=170, minwidth=140)
        self.gate_tree.column("state", width=80, minwidth=70, anchor=tk.CENTER)
        self.gate_tree.column("reason", width=410, minwidth=250)
        self.gate_tree.grid(row=1, column=0, sticky=tk.NSEW)
        gate_scroll = ttk.Scrollbar(gate, orient=tk.VERTICAL, command=self.gate_tree.yview)
        gate_scroll.grid(row=1, column=1, sticky=tk.NS)
        self.gate_tree.configure(yscrollcommand=gate_scroll.set)
        ttk.Label(
            gate,
            text="原则：缺失校准不伪装为已完成；可降级继续，但警告必须进入 FITS/JSON/报告。",
            style="Muted.TLabel",
            wraplength=620,
        ).grid(row=2, column=0, columnspan=2, sticky=tk.W, pady=(9, 0))

        preset_panel, preset = self._panel(page, "流程预设")
        preset_panel.grid(row=0, column=2, sticky=tk.NSEW, padx=(7, 0))
        preset.columnconfigure(0, weight=1)
        ttk.Label(preset, text="标准星 → 科学目标", style="Muted.TLabel").grid(
            row=0, column=0, sticky=tk.W
        )
        self.preset_combo = ttk.Combobox(
            preset,
            state="readonly",
            textvariable=self.workflow_preset,
            values=[item.label for item in WORKFLOW_PRESETS],
        )
        self.preset_combo.grid(row=1, column=0, sticky=tk.EW, pady=(4, 10))
        self.preset_combo.bind("<<ComboboxSelected>>", self._update_calibration_gate)
        ttk.Label(preset, text="ISIS 模板目录", style="Muted.TLabel").grid(
            row=2, column=0, sticky=tk.W
        )
        ttk.Entry(preset, textvariable=self.template_directory).grid(
            row=3, column=0, sticky=tk.EW, pady=(4, 4)
        )
        ttk.Button(
            preset,
            text="选择模板目录",
            style="Quiet.TButton",
            command=self._browse_template,
        ).grid(row=4, column=0, sticky=tk.EW, pady=(0, 10))
        ttk.Checkbutton(
            preset,
            text="试算候选平场并执行质量判定",
            variable=self.use_flat,
        ).grid(row=5, column=0, sticky=tk.W)
        ttk.Checkbutton(
            preset,
            text="每张先去除宇宙线（推荐）",
            variable=self.cosmic_ray_clean,
        ).grid(row=6, column=0, sticky=tk.W, pady=(6, 0))
        combine_row = ttk.Frame(preset, style="Panel.TFrame")
        combine_row.grid(row=7, column=0, sticky=tk.EW, pady=(6, 0))
        ttk.Label(combine_row, text="邻帧合成", style="Muted.TLabel").pack(side=tk.LEFT)
        ttk.Combobox(
            combine_row,
            state="readonly",
            width=8,
            textvariable=self.combine_method,
            values=("mean", "median"),
        ).pack(side=tk.RIGHT)
        ttk.Label(
            preset,
            text=(
                "勾选只代表允许试算。候选平场必须通过相机、gain、ROI、饱和比例、"
                "模板相关和响应粗糙度检查后才会采用。"
            ),
            style="Muted.TLabel",
            wraplength=300,
        ).grid(row=8, column=0, sticky=tk.EW, pady=(8, 18))
        self.full_run_button = ttk.Button(
            preset,
            text="运行完整流程",
            style="Accent.TButton",
            command=self.run_full_preset,
        )
        self.full_run_button.grid(row=9, column=0, sticky=tk.EW)
        ttk.Button(
            preset,
            text="人工确认后进入提取页",
            style="Quiet.TButton",
            command=lambda: self._approve_stage("masters", "geometry"),
        ).grid(row=10, column=0, sticky=tk.EW, pady=(8, 0))
        return page

    def _build_extraction_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=300)
        page.columnconfigure(1, weight=1)
        page.rowconfigure(0, weight=1)

        chain_panel, chain = self._panel(page, "处理节点")
        chain_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        chain.columnconfigure(0, weight=1)
        nodes = (
            ("01", "输入/方向", "原始帧完整性、色散方向、坏帧隔离"),
            ("02", "主校准", "bias / dark / flat 兼容性门"),
            ("03", "配准叠加", "单帧去宇宙线、漂移、稳健均值/中位数"),
            ("04", "迹线与天空", "自适应 trace、两侧局部天空"),
            ("05", "1D 提取", "ASPIRED Horne86 + 箱式对照"),
            ("06", "波长", "arc 优先，标准星转移为降级路径"),
            ("07", "响应/归一化", "相对响应、连续谱、二级光风险"),
            ("08", "质控", "FITS/JSON/诊断图和失败原因"),
        )
        for row, (number, title, note) in enumerate(nodes):
            card = ttk.Frame(chain, style="Raised.TFrame", padding=(10, 8))
            card.grid(row=row, column=0, sticky=tk.EW, pady=(0, 5))
            card.columnconfigure(1, weight=1)
            ttk.Label(card, text=number, style="CardTitle.TLabel").grid(
                row=0, column=0, rowspan=2, padx=(0, 10)
            )
            ttk.Label(card, text=title, style="CardTitle.TLabel").grid(row=0, column=1, sticky=tk.W)
            ttk.Label(card, text=note, style="CardMuted.TLabel").grid(row=1, column=1, sticky=tk.W)

        log_panel, log_body = self._panel(page, "运行监视器")
        log_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=(7, 0))
        log_body.columnconfigure(0, weight=1)
        log_body.rowconfigure(0, weight=1)
        log_body.rowconfigure(1, weight=1)
        self.extraction_preview_label = InteractiveImageViewer(
            log_body,
            placeholder="运行前显示最近 1D 光谱；运行后自动更新",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.extraction_preview_label.grid(
            row=0,
            column=0,
            columnspan=2,
            sticky=tk.NSEW,
            pady=(0, 8),
        )
        self.log = tk.Text(
            log_body,
            state=tk.DISABLED,
            background="#090d13",
            foreground="#c7d5e5",
            insertbackground=COLORS["text"],
            selectbackground=COLORS["accent_dark"],
            relief=tk.FLAT,
            wrap=tk.WORD,
            padx=12,
            pady=10,
            font=("Cascadia Mono", 9),
        )
        self.log.grid(row=1, column=0, sticky=tk.NSEW)
        log_scroll = ttk.Scrollbar(log_body, orient=tk.VERTICAL, command=self.log.yview)
        log_scroll.grid(row=1, column=1, sticky=tk.NS)
        self.log.configure(yscrollcommand=log_scroll.set)
        actions = ttk.Frame(log_body, style="Panel.TFrame")
        actions.grid(row=2, column=0, columnspan=2, sticky=tk.EW, pady=(9, 0))
        self.run_button = ttk.Button(
            actions,
            text="高级：快速提取所选组",
            style="Quiet.TButton",
            command=self.run_selected,
        )
        self.run_button.pack(side=tk.LEFT)
        ttk.Button(
            actions,
            text="运行当前完整预设",
            style="Accent.TButton",
            command=self.run_full_preset,
        ).pack(side=tk.LEFT, padx=7)
        ttk.Button(
            actions,
            text="查看已有 3C 273 分析",
            style="Quiet.TButton",
            command=self._show_known_hbeta,
        ).pack(side=tk.RIGHT)
        return page

    def _build_wavelength_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=315)
        page.columnconfigure(1, weight=1)
        page.columnconfigure(2, weight=0, minsize=330)
        page.rowconfigure(0, weight=1)

        ladder_panel, ladder = self._panel(page, "波长标定路径")
        ladder_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        ladder.columnconfigure(0, weight=1)
        methods = (
            ("A  首选", "Arc + 线表", "同光学配置窄线灯；检查线数、覆盖和 RMS"),
            ("B  专业人工", "已知线对", "人工确认 pixel/Å 锚点，保留残差和点表"),
            ("C  当前降级", "标准星模板", "Balmer/恒星模板匹配；精度低于 arc"),
            ("D  转移", "已有解文件", "仅限相同光栅、狭缝、ROI、binning 和安装"),
            ("E  最低", "像素轴", "没有可靠参考时停止标定；不伪造波长"),
        )
        for row, (tier, title, note) in enumerate(methods):
            card = ttk.Frame(ladder, style="Raised.TFrame", padding=(10, 9))
            card.grid(row=row, column=0, sticky=tk.EW, pady=(0, 7))
            ttk.Label(card, text=tier, style="CardMuted.TLabel").pack(anchor=tk.W)
            ttk.Label(card, text=title, style="CardTitle.TLabel").pack(anchor=tk.W, pady=(2, 1))
            ttk.Label(card, text=note, style="CardMuted.TLabel", wraplength=275).pack(anchor=tk.W)

        viewer_panel, viewer = self._panel(page, "即时可视化  ·  波长残差 / 1D")
        viewer_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=7)
        viewer.columnconfigure(0, weight=1)
        viewer.rowconfigure(0, weight=1)
        self.wavelength_preview_label = InteractiveImageViewer(
            viewer,
            placeholder="运行后显示波长残差图；无可靠解时显示像素轴 1D 并标记 NEEDS_REFERENCE",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.wavelength_preview_label.grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            viewer,
            text="刷新当前输出",
            style="Quiet.TButton",
            command=self.refresh_artifacts,
        ).grid(row=1, column=0, sticky=tk.EW, pady=(9, 0))

        review_panel, review = self._panel(page, "人工验收")
        review_panel.grid(row=0, column=2, sticky=tk.NSEW, padx=(7, 0))
        review.columnconfigure(0, weight=1)
        self.wavelength_review = ttk.Label(
            review,
            text=(
                "必须检查\n\n"
                "• 波长轴严格递增\n"
                "• 匹配线数足够且覆盖全幅\n"
                "• RMS 与分辨率相称\n"
                "• 用夜空线/已知天体线复核零点\n"
                "• 标准星转移时确认狭缝、光栅、ROI 未变\n\n"
                "降级原则\n\n"
                "arc 缺失可以继续提取，但只能标记为标准星转移解或像素轴；"
                "不能把内部三点拟合残差写成弧灯级绝对精度。"
            ),
            style="Muted.TLabel",
            justify=tk.LEFT,
            wraplength=290,
        )
        self.wavelength_review.grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            review,
            text="确认波长状态并进入响应",
            style="Accent.TButton",
            command=lambda: self._approve_stage("wavelength", "response"),
        ).grid(row=1, column=0, sticky=tk.EW, pady=(12, 0))
        return page

    def _build_response_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=315)
        page.columnconfigure(1, weight=1)
        page.columnconfigure(2, weight=0, minsize=330)
        page.rowconfigure(0, weight=1)

        ladder_panel, ladder = self._panel(page, "响应与归一化层级")
        ladder_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        ladder.columnconfigure(0, weight=1)
        methods = (
            ("A  首选", "分光光度标准星", "消光 + 响应 + 标准流量；可输出物理流量"),
            ("B  当前降级", "恒星模板相对响应", "标准星/模板比值；仅给相对响应"),
            ("C  最低", "稳健连续谱归一化", "没有响应星时保留 counts 并拟合连续谱"),
            ("风险标记", "二级光谱", ">6800 Å 不直接裁切；保存警戒和经验检测结果"),
        )
        for row, (tier, title, note) in enumerate(methods):
            card = ttk.Frame(ladder, style="Raised.TFrame", padding=(10, 9))
            card.grid(row=row, column=0, sticky=tk.EW, pady=(0, 7))
            ttk.Label(card, text=tier, style="CardMuted.TLabel").pack(anchor=tk.W)
            ttk.Label(card, text=title, style="CardTitle.TLabel").pack(anchor=tk.W, pady=(2, 1))
            ttk.Label(card, text=note, style="CardMuted.TLabel", wraplength=275).pack(anchor=tk.W)

        viewer_panel, viewer = self._panel(page, "即时可视化  ·  响应 / 归一化 1D")
        viewer_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=7)
        viewer.columnconfigure(0, weight=1)
        viewer.rowconfigure(0, weight=1)
        self.response_preview_label = InteractiveImageViewer(
            viewer,
            placeholder="运行后显示相对响应或最终归一化光谱",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.response_preview_label.grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            viewer,
            text="刷新当前输出",
            style="Quiet.TButton",
            command=self.refresh_artifacts,
        ).grid(row=1, column=0, sticky=tk.EW, pady=(9, 0))

        review_panel, review = self._panel(page, "人工验收")
        review_panel.grid(row=0, column=2, sticky=tk.NSEW, padx=(7, 0))
        review.columnconfigure(0, weight=1)
        ttk.Label(
            review,
            text=(
                "必须检查\n\n"
                "• 标准星是否真的是分光光度标准\n"
                "• 响应曲线是否被恒星吸收线污染\n"
                "• 连续谱窗口是否吞掉宽发射线\n"
                "• 相对响应与绝对流量标签是否一致\n"
                "• 红端是否存在二级蓝段污染\n\n"
                "当前 3C 273\n\n"
                "同观测夜 NGC 6543 只提供波长锚点，不是响应标准；"
                "正式产品保持 counts-only。H-gamma、H-beta 与 [O III] 复合区"
                "共同通过身份门，目录红移与实测中值一致到探索级精度。"
            ),
            style="Muted.TLabel",
            justify=tk.LEFT,
            wraplength=290,
        ).grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            review,
            text="确认响应等级并进入科学分析",
            style="Accent.TButton",
            command=lambda: self._approve_stage("response", "analyse"),
        ).grid(row=1, column=0, sticky=tk.EW, pady=(12, 0))
        return page

    def _build_analysis_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=0, minsize=300)
        page.columnconfigure(1, weight=1)
        page.columnconfigure(2, weight=0, minsize=340)
        page.rowconfigure(0, weight=1)

        artifact_panel, artifact_body = self._panel(page, "产物浏览器")
        artifact_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        artifact_body.columnconfigure(0, weight=1)
        artifact_body.rowconfigure(0, weight=1)
        self.artifact_tree = ttk.Treeview(
            artifact_body,
            columns=("kind",),
            show="tree headings",
            selectmode="browse",
        )
        self.artifact_tree.heading("#0", text="文件")
        self.artifact_tree.heading("kind", text="类型")
        self.artifact_tree.column("#0", width=210, minwidth=150)
        self.artifact_tree.column("kind", width=70, minwidth=60, anchor=tk.CENTER)
        self.artifact_tree.grid(row=0, column=0, sticky=tk.NSEW)
        artifact_scroll = ttk.Scrollbar(
            artifact_body,
            orient=tk.VERTICAL,
            command=self.artifact_tree.yview,
        )
        artifact_scroll.grid(row=0, column=1, sticky=tk.NS)
        self.artifact_tree.configure(yscrollcommand=artifact_scroll.set)
        self.artifact_tree.bind("<<TreeviewSelect>>", self._preview_selected_artifact)
        ttk.Button(
            artifact_body,
            text="刷新当前输出",
            style="Quiet.TButton",
            command=self.refresh_artifacts,
        ).grid(row=1, column=0, sticky=tk.EW, pady=(9, 0))

        viewer_panel, viewer = self._panel(page, "光谱查看器")
        viewer_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=7)
        viewer.columnconfigure(0, weight=1)
        viewer.rowconfigure(0, weight=1)
        self.preview_label = InteractiveImageViewer(
            viewer,
            placeholder="选择左侧 PNG 诊断图",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.preview_label.grid(row=0, column=0, sticky=tk.NSEW)
        viewer_actions = ttk.Frame(viewer, style="Panel.TFrame")
        viewer_actions.grid(row=1, column=0, sticky=tk.EW, pady=(9, 0))
        ttk.Button(
            viewer_actions,
            text="在系统中打开",
            style="Quiet.TButton",
            command=self._open_selected_artifact,
        ).pack(side=tk.LEFT)
        ttk.Button(
            viewer_actions,
            text="3C 273 H-beta 同口径诊断",
            style="Accent.TButton",
            command=self._show_known_hbeta,
        ).pack(side=tk.RIGHT)

        metrics_panel, metrics = self._panel(page, "指标与人工判定")
        metrics_panel.grid(row=0, column=2, sticky=tk.NSEW, padx=(7, 0))
        metrics.columnconfigure(0, weight=1)
        metrics.rowconfigure(0, weight=1)
        self.metrics_text = tk.Text(
            metrics,
            background=COLORS["surface_alt"],
            foreground=COLORS["text"],
            insertbackground=COLORS["text"],
            relief=tk.FLAT,
            wrap=tk.WORD,
            padx=11,
            pady=10,
            font=("Microsoft YaHei UI", 9),
            state=tk.DISABLED,
        )
        self.metrics_text.grid(row=0, column=0, sticky=tk.NSEW)
        ttk.Button(
            metrics,
            text="批准当前结果并进入交付",
            style="Accent.TButton",
            command=lambda: self._approve_stage("analyse", "deliver"),
        ).grid(row=1, column=0, sticky=tk.EW, pady=(9, 0))
        return page

    def _build_delivery_page(self, parent: tk.Misc) -> ttk.Frame:
        page = ttk.Frame(parent, style="Studio.TFrame")
        page.columnconfigure(0, weight=1)
        page.columnconfigure(1, weight=1)
        page.rowconfigure(0, weight=1)

        package_panel, package = self._panel(page, "交付包")
        package_panel.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 7))
        package.columnconfigure(0, weight=1)
        package.rowconfigure(0, weight=1)
        self.delivery_preview_label = InteractiveImageViewer(
            package,
            placeholder="最终 1D 光谱预览",
            background="#070a0f",
            foreground=COLORS["muted"],
        )
        self.delivery_preview_label.grid(row=0, column=0, sticky=tk.NSEW, pady=(0, 10))
        self.delivery_summary = ttk.Label(
            package,
            text="尚未选择输出。",
            style="Muted.TLabel",
            justify=tk.LEFT,
            wraplength=560,
        )
        self.delivery_summary.grid(row=1, column=0, sticky=tk.EW)
        ttk.Separator(package).grid(row=2, column=0, sticky=tk.EW, pady=14)
        checklist = (
            "FITS：显式波长、flux、uncertainty、mask",
            "CSV：人工抽查和第三方软件交换",
            "PNG：2D 迹线、配准、波长残差、1D 光谱",
            "JSON：输入帧、质量门、警告、算法参数",
            "报告：本次结果、限制和待同行复核问题",
        )
        for row, text in enumerate(checklist, start=3):
            ttk.Label(package, text=f"✓  {text}", style="Muted.TLabel").grid(
                row=row, column=0, sticky=tk.W, pady=3
            )
        ttk.Button(
            package,
            text="打开当前输出目录",
            style="Accent.TButton",
            command=self._open_output,
        ).grid(row=9, column=0, sticky=tk.EW, pady=(20, 0))

        document_panel, documents = self._panel(page, "报告与 SOP")
        document_panel.grid(row=0, column=1, sticky=tk.NSEW, padx=(7, 0))
        documents.columnconfigure(0, weight=1)
        report_card = ttk.Frame(documents, style="Raised.TFrame", padding=14)
        report_card.grid(row=0, column=0, sticky=tk.EW, pady=(0, 10))
        report_card.columnconfigure(0, weight=1)
        ttk.Label(report_card, text="3C 273 处理与 H-beta 分析报告", style="CardTitle.TLabel").grid(
            row=0, column=0, sticky=tk.W
        )
        self.report_state = ttk.Label(report_card, text="检查中", style="CardMuted.TLabel")
        self.report_state.grid(row=1, column=0, sticky=tk.W, pady=(4, 10))
        ttk.Button(
            report_card,
            text="打开 PDF 报告",
            style="Quiet.TButton",
            command=lambda: self._open_path(REPORT_PDF),
        ).grid(row=2, column=0, sticky=tk.EW)
        ttk.Button(
            report_card,
            text="打开可编辑 Markdown",
            style="Quiet.TButton",
            command=lambda: self._open_path(REPORT_SOURCE),
        ).grid(row=3, column=0, sticky=tk.EW, pady=(6, 0))

        sop_card = ttk.Frame(documents, style="Raised.TFrame", padding=14)
        sop_card.grid(row=1, column=0, sticky=tk.EW)
        sop_card.columnconfigure(0, weight=1)
        ttk.Label(sop_card, text="UVEX 人工处理 SOP（初版）", style="CardTitle.TLabel").grid(
            row=0, column=0, sticky=tk.W
        )
        self.sop_state = ttk.Label(sop_card, text="检查中", style="CardMuted.TLabel")
        self.sop_state.grid(row=1, column=0, sticky=tk.W, pady=(4, 10))
        ttk.Button(
            sop_card,
            text="打开 PDF SOP",
            style="Quiet.TButton",
            command=lambda: self._open_path(SOP_PDF),
        ).grid(row=2, column=0, sticky=tk.EW)
        ttk.Button(
            sop_card,
            text="打开可编辑 Markdown",
            style="Quiet.TButton",
            command=lambda: self._open_path(SOP_SOURCE),
        ).grid(row=3, column=0, sticky=tk.EW, pady=(6, 0))
        return page

    def switch_stage(self, stage: str) -> None:
        if stage not in self.stage_frames:
            raise ValueError(f"Unknown studio stage: {stage}")
        self.current_stage = stage
        self.stage_frames[stage].tkraise()
        for key, button in self.stage_buttons.items():
            button.configure(style="StageActive.TButton" if key == stage else "Stage.TButton")
        if stage in {"geometry", "wavelength", "response", "analyse", "deliver"}:
            self.refresh_artifacts()
        if stage == "deliver":
            self._update_document_cards()

    def scan(self) -> None:
        super().scan()
        self._populate_media_tree()
        self._update_calibration_gate()
        group_count = len(self.science_groups)
        frame_count = sum(len(group.files) for group in self.science_groups)
        self.media_summary.configure(
            text=(
                f"发现 {group_count} 个观测组、{frame_count} 个候选 FITS。\n\n"
                "Inspector 仍会以目录/文件名优先于不可信 OBJECT Header；"
                "最终是否纳入处理由配置和质量门决定。"
            )
        )
        self._set_stage_state("media", f"已扫描 {group_count} 组", done=True)

    def _populate_media_tree(self) -> None:
        self.media_tree.delete(*self.media_tree.get_children())
        self.media_items.clear()
        standard_keys = {
            (group.directory, group.target.casefold()) for group in self.groups
        }
        for index, group in enumerate(self.science_groups):
            role = (
                "标准星"
                if (group.directory, group.target.casefold()) in standard_keys
                else "科学/校准"
            )
            parts = Path(group.relative_directory).parts
            night = parts[0] if parts else "."
            item = self.media_tree.insert(
                "",
                tk.END,
                values=(role, group.target, len(group.files), night, group.relative_directory),
            )
            self.media_items[item] = group
            if index == 0:
                self.media_tree.selection_set(item)
        if self.media_tree.selection():
            self._show_media_item()

    def _show_media_item(self, _event=None) -> None:
        selection = self.media_tree.selection()
        if not selection:
            return
        group = self.media_items.get(selection[0])
        if group is None:
            return
        files = getattr(group, "files", [])
        preview = "\n".join(f"  • {path.name}" for path in files[:12])
        if len(files) > 12:
            preview += f"\n  … 另有 {len(files) - 12} 帧"
        text = (
            f"目标\n{getattr(group, 'target', '未知')}\n\n"
            f"观测目录\n{getattr(group, 'relative_directory', '.')}\n\n"
            f"帧数\n{len(files)}\n\n"
            "文件\n"
            f"{preview}\n\n"
            "人工检查\n"
            "• OBJECT 是否与文件名一致\n"
            "• gain / binning / ROI 是否一致\n"
            "• 谱线是否跨整幅连续、是否存在读出错位\n"
            "• 狭缝、光栅和对焦是否在夜间改变"
        )
        self._replace_text(self.media_inspector, text)
        if files:
            self._display_fits_preview(self.media_preview_label, files[0])

    def _show_selection(self, _event=None) -> None:
        super()._show_selection(_event)
        self._update_calibration_gate()
        group = self._selected_group()
        if group is not None and group.files:
            self._display_fits_preview(
                self.calibration_preview_label,
                group.files[0],
            )

    def _display_fits_preview(
        self,
        label: InteractiveImageViewer,
        path: Path,
    ) -> None:
        try:
            with fits.open(path, memmap=False) as hdul:
                data = np.asarray(hdul[0].data, dtype=np.float32)
            preview = make_neutral_fits_preview(data)
            label.set_image(
                preview.image,
                source=(
                    f"{path.name} · 中性灰度 · {preview.low:.1f}–{preview.high:.1f} ADU"
                ),
            )
        except Exception as error:
            label.show_message(f"2D FITS 预览失败：{error}\n\n{path.name}")

    def _display_context_png(
        self,
        label: InteractiveImageViewer,
        path: Path,
        key: str,
    ) -> None:
        try:
            with Image.open(path) as opened:
                image = opened.convert("RGB")
            self._display_context_image(label, image, f"{key} · {path.name}")
        except Exception as error:
            label.show_message(f"预览失败：{error}\n\n{path.name}")

    @staticmethod
    def _display_context_image(
        label: InteractiveImageViewer,
        image: Image.Image,
        source: str,
    ) -> None:
        label.set_image(image, source=source)

    def _apply_equipment_preset(self, _event=None) -> None:
        preset = self.equipment_presets.get(self.equipment_choice.get())
        if preset is None:
            return
        self.equipment_slit.set(str(preset.slit_micrometre))
        self.equipment_dispersion.set(
            f"{preset.estimated_dispersion_angstrom_per_pixel:.3f}"
        )
        self.equipment_direction.set(preset.raw_dispersion_direction)
        self.status.set(
            f"已套用设备预设：{preset.name}；狭缝和色散初值仍需按当晚 Header/标定复核。"
        )

    def _current_equipment(self, name: str | None = None) -> EquipmentPreset:
        selected = self.equipment_presets.get(self.equipment_choice.get())
        if selected is None:
            selected = next(iter(self.equipment_presets.values()))
        slit = int(float(self.equipment_slit.get()))
        dispersion = float(self.equipment_dispersion.get())
        if slit <= 0 or dispersion <= 0:
            raise ValueError("狭缝和色散初值必须为正数。")
        return EquipmentPreset(
            name=name or selected.name,
            telescope=selected.telescope,
            reducer=selected.reducer,
            spectrograph=selected.spectrograph,
            camera=selected.camera,
            grating_lines_per_mm=selected.grating_lines_per_mm,
            slit_micrometre=slit,
            estimated_dispersion_angstrom_per_pixel=dispersion,
            raw_dispersion_direction=self.equipment_direction.get(),
            second_order_warning_angstrom=selected.second_order_warning_angstrom,
        )

    def _save_equipment_preset(self) -> None:
        name = simpledialog.askstring(
            "保存设备预设",
            "预设名称：",
            initialvalue=self.equipment_choice.get(),
            parent=self.root,
        )
        if not name:
            return
        try:
            preset = self._current_equipment(name.strip())
            self.equipment_presets[preset.name] = preset
            save_equipment_presets(
                EQUIPMENT_PRESET_STORE,
                list(self.equipment_presets.values()),
            )
            self.equipment_combo.configure(values=list(self.equipment_presets))
            self.equipment_choice.set(preset.name)
            self.status.set(f"设备预设已保存：{preset.name}")
        except Exception as error:
            messagebox.showerror("无法保存设备预设", str(error), parent=self.root)

    def save_project(self, path: Path | None = None) -> None:
        destination = path or self.project_path
        if destination is None:
            selected = filedialog.asksaveasfilename(
                title="保存 UVEX 工程",
                defaultextension=".astroproj",
                filetypes=(("UVEX Astro Project", "*.astroproj"),),
                initialfile="UVEX-project.astroproj",
            )
            if not selected:
                return
            destination = Path(selected)
        try:
            project = AstroProject(
                name=self.project_name.get(),
                data_root=self.data_root.get(),
                output_root=self.output_root.get(),
                current_stage=self.current_stage,
                workflow_preset=self.workflow_preset.get(),
                evaluate_flat=bool(self.use_flat.get()),
                equipment=self._current_equipment(),
                last_output=str(self.last_output) if self.last_output is not None else None,
                approved_stages=sorted(self.approved_stages),
                parameters={
                    "selectedStandardIndex": (
                        int(self.group_list.curselection()[0])
                        if self.group_list.curselection()
                        else None
                    ),
                    "selectedScienceIndex": self.target_combo.current(),
                    "secondOrderPolicy": "warn-and-retain",
                    "visualFeedback": "enabled",
                    "cosmicRayClean": bool(self.cosmic_ray_clean.get()),
                    "combineMethod": self.combine_method.get(),
                },
                manual_calibration_points=[],
                notes="Manual calibration points and future algorithm controls are schema-stable fields.",
            )
            self.project_path = project.save(destination)
            self.project_name.set(project.name)
            self.status.set(f"工程已保存：{self.project_path.name}")
        except Exception as error:
            messagebox.showerror("无法保存工程", str(error), parent=self.root)

    def load_project(self) -> None:
        selected = filedialog.askopenfilename(
            title="打开 UVEX 工程",
            filetypes=(("UVEX Astro Project", "*.astroproj"), ("All files", "*.*")),
        )
        if not selected:
            return
        try:
            project = AstroProject.load(selected)
            self.project_path = Path(selected).resolve()
            self.project_name.set(project.name)
            self.data_root.set(project.data_root)
            self.output_root.set(project.output_root)
            if project.workflow_preset in {item.label for item in WORKFLOW_PRESETS}:
                self.workflow_preset.set(project.workflow_preset)
            self.use_flat.set(project.evaluate_flat)
            self.cosmic_ray_clean.set(
                bool(project.parameters.get("cosmicRayClean", True))
            )
            combine_method = str(project.parameters.get("combineMethod", "mean"))
            if combine_method in {"mean", "median"}:
                self.combine_method.set(combine_method)
            self.approved_stages = set(project.approved_stages)
            if project.equipment is not None:
                self.equipment_presets[project.equipment.name] = project.equipment
                self.equipment_combo.configure(values=list(self.equipment_presets))
                self.equipment_choice.set(project.equipment.name)
                self._apply_equipment_preset()
            self.scan()
            standard_index = project.parameters.get("selectedStandardIndex")
            if isinstance(standard_index, int) and 0 <= standard_index < self.group_list.size():
                self.group_list.selection_clear(0, tk.END)
                self.group_list.selection_set(standard_index)
                self.group_list.see(standard_index)
                self._show_selection()
            science_index = project.parameters.get("selectedScienceIndex")
            if isinstance(science_index, int) and 0 <= science_index < len(self.target_choices):
                self.target_combo.current(science_index)
            if project.last_output:
                candidate = Path(project.last_output)
                if candidate.exists():
                    self.last_output = candidate
                    self.open_button.configure(state=tk.NORMAL)
                    self.refresh_artifacts(candidate)
            for stage in self.approved_stages:
                self._set_stage_state(stage, "工程中已批准", done=True)
            stage = project.current_stage if project.current_stage in self.stage_frames else "media"
            self.switch_stage(stage)
            self.status.set(f"工程已恢复：{self.project_path.name}")
        except Exception as error:
            messagebox.showerror("无法打开工程", str(error), parent=self.root)

    def _close_project(self) -> None:
        if self.project_path is not None:
            self.save_project(self.project_path)
        self.root.destroy()

    def _update_calibration_gate(self, _event=None) -> None:
        if not hasattr(self, "gate_tree"):
            return
        self.gate_tree.delete(*self.gate_tree.get_children())
        preset = next(
            (item for item in WORKFLOW_PRESETS if item.label == self.workflow_preset.get()),
            WORKFLOW_PRESETS[0],
        )
        template_directory = self.template_directory.get().strip()
        template_path = (
            Path(template_directory).expanduser() / preset.template_path.name
            if template_directory
            else None
        )
        rows = (
            ("标准星配置", preset.standard_config.is_file(), preset.standard_config.name),
            ("科学目标配置", preset.science_config.is_file(), preset.science_config.name),
            (
                "ISIS 标准模板",
                template_path is not None and template_path.is_file(),
                str(template_path) if template_path is not None else "未配置模板目录",
            ),
            ("帧完整性策略", True, "仅接受已验收逐文件清单，不改写原始 FITS"),
            ("候选平场", bool(self.use_flat.get()), "允许试算；质量门不通过则拒绝"),
            ("暗场", False, "允许跨夜，但必须匹配温度/gain/曝光策略"),
            ("波长零点复核", False, "运行后检查 arc、天空线或已知发射线"),
            ("二级光谱", False, "6800 Å 仅为警戒估计；保留数据并标记"),
        )
        for label, accepted, reason in rows:
            state = "就绪" if accepted else "待确认"
            self.gate_tree.insert("", tk.END, text=label, values=(state, reason))

    def _accept_media(self) -> None:
        self.approved_stages.add("media")
        self._set_stage_state("media", "人工已确认", done=True)
        self._set_stage_state("masters", "待确认", done=False)
        self.switch_stage("masters")

    def _approve_stage(self, current: str, following: str) -> None:
        self.approved_stages.add(current)
        self._set_stage_state(current, "人工已批准", done=True)
        self.switch_stage(following)

    def _set_stage_state(self, stage: str, text: str, *, done: bool = False, busy: bool = False) -> None:
        label = self.stage_state_labels.get(stage)
        if label is None:
            return
        style = "StatusBusy.TLabel" if busy else "StatusDone.TLabel" if done else "Status.TLabel"
        label.configure(text=text, style=style)

    def run_full_preset(self) -> None:
        self.active_run_kind = "full"
        self.switch_stage("geometry")
        self._set_stage_state("geometry", "处理中", busy=True)
        self.progress.start(12)
        super().run_full_preset()
        if str(self.full_run_button.cget("state")) != str(tk.DISABLED):
            self.progress.stop()

    def run_selected(self) -> None:
        self.active_run_kind = "quick"
        self.switch_stage("geometry")
        self._set_stage_state("geometry", "快速提取中", busy=True)
        self.progress.start(12)
        super().run_selected()
        if str(self.run_button.cget("state")) != str(tk.DISABLED):
            self.progress.stop()

    def _drain_events(self) -> None:
        try:
            while True:
                event, payload = self.events.get_nowait()
                self.progress.stop()
                if event == "success":
                    message, output = payload
                    self._append_log(str(message) + "\n")
                    self.last_output = Path(output)
                    self.open_button.configure(state=tk.NORMAL)
                    self.status.set("处理完成。请逐项检查诊断图、指标和警告。")
                    self._set_stage_state("geometry", "处理完成", done=True)
                    if self.active_run_kind == "full":
                        self._set_stage_state("masters", "质量门完成", done=True)
                        self._set_stage_state("wavelength", "已生成/需复核", done=True)
                        self._set_stage_state("response", "相对响应完成", done=True)
                    else:
                        self._set_stage_state("wavelength", "检查解状态", busy=True)
                        self._set_stage_state("response", "未运行", done=False)
                    self._set_stage_state("analyse", "待人工复核", busy=True)
                    self.refresh_artifacts(self.last_output)
                    self.switch_stage("analyse")
                elif event == "error":
                    self._append_log(str(payload))
                    self.status.set("处理失败；错误已保留在运行监视器。")
                    self._set_stage_state("geometry", "失败/需处理", busy=True)
                    messagebox.showerror("处理失败", "处理失败，请查看提取页运行监视器。")
                self.run_button.configure(state=tk.NORMAL)
                self.full_run_button.configure(state=tk.NORMAL)
        except queue.Empty:
            pass
        self.root.after(100, self._drain_events)

    def refresh_artifacts(self, output: Path | None = None) -> None:
        if not hasattr(self, "artifact_tree"):
            return
        root = Path(output) if output is not None else self.last_output
        self.artifact_tree.delete(*self.artifact_tree.get_children())
        self.artifact_paths.clear()
        if root is None or not root.exists():
            self._replace_text(self.metrics_text, "尚无可查看产物。请先运行流程或选择已有输出。")
            return
        files = sorted(
            (
                path
                for path in root.rglob("*")
                if path.is_file()
                and not artifact_is_quarantined(path, root)
                and path.suffix.casefold() in {".png", ".fit", ".fits", ".csv", ".json", ".md", ".pdf"}
            ),
            key=lambda path: (artifact_kind(path), path.name.casefold()),
        )
        for path in files:
            item = self.artifact_tree.insert(
                "",
                tk.END,
                text=path.name,
                values=(artifact_kind(path),),
            )
            self.artifact_paths[item] = path
        self.delivery_summary.configure(
            text=(
                f"当前输出：\n{root}\n\n"
                f"发现 {len(files)} 个可交付/复核文件。人工批准前请至少检查迹线、"
                "天空窗口、波长残差、归一化曲线和 JSON 警告。"
            )
        )
        preview_candidates = sorted(
            (path for path in files if path.suffix.casefold() == ".png"),
            key=artifact_priority,
        )
        self._update_context_output_previews(preview_candidates)
        if preview_candidates:
            chosen = preview_candidates[0]
            item = next(key for key, value in self.artifact_paths.items() if value == chosen)
            self.artifact_tree.selection_set(item)
            self.artifact_tree.see(item)
            self._display_preview(chosen)
        metric_candidates = [
            path
            for path in files
            if path.suffix.casefold() == ".json"
            and ("hbeta" in path.name.casefold() or "identity" in path.name.casefold())
        ]
        if not metric_candidates:
            metric_candidates = [path for path in files if path.name.casefold().endswith("_run.json")]
        if metric_candidates:
            self._display_metrics(metric_candidates[0])
        else:
            self._replace_text(self.metrics_text, "没有发现 JSON 指标文件。")
        self._set_stage_state("analyse", f"{len(files)} 个产物", done=bool(files))

    def _update_context_output_previews(self, images: list[Path]) -> None:
        def choose(*tokens: str) -> Path | None:
            for token in tokens:
                candidate = next(
                    (path for path in images if token in path.name.casefold()),
                    None,
                )
                if candidate is not None:
                    return candidate
            return images[0] if images else None

        mappings = (
            (
                getattr(self, "extraction_preview_label", None),
                choose("trace_overlay", "preprocessed", "spectrum.png"),
                "geometry-output",
            ),
            (
                getattr(self, "wavelength_preview_label", None),
                choose("wavelength_residuals", "identity_overlay", "spectrum.png"),
                "wavelength-output",
            ),
            (
                getattr(self, "response_preview_label", None),
                choose("relative_response", "normalised_1d", "normalized_1d"),
                "response-output",
            ),
            (
                getattr(self, "delivery_preview_label", None),
                choose("normalised_1d", "normalized_1d", "identity_overlay"),
                "delivery-output",
            ),
        )
        for label, path, key in mappings:
            if label is not None and path is not None:
                self._display_context_png(label, path, key)

    def _preview_selected_artifact(self, _event=None) -> None:
        selection = self.artifact_tree.selection()
        if not selection:
            return
        path = self.artifact_paths.get(selection[0])
        if path is None:
            return
        if path.suffix.casefold() == ".png":
            self._display_preview(path)
        elif path.suffix.casefold() == ".json":
            self._display_metrics(path)
        else:
            self.preview_label.show_message(f"{artifact_kind(path)}\n\n{path.name}")

    def _display_preview(self, path: Path) -> None:
        try:
            with Image.open(path) as opened:
                image = opened.convert("RGB")
            self.preview_label.set_image(image, source=path.name)
        except Exception as error:  # UI must still expose the path on a corrupt PNG
            self.preview_label.show_message(f"预览失败：{error}\n\n{path}")

    def _display_metrics(self, path: Path) -> None:
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except Exception as error:
            self._replace_text(self.metrics_text, f"无法读取指标：{error}\n\n{path}")
            return
        if "measurements" in payload and "expectedHbetaAngstrom" in payload:
            hbeta_assessment = payload.get("assessment", {})
            lines = [
                "H-beta 同口径诊断",
                "",
                f"预期位置  {payload['expectedHbetaAngstrom']:.2f} Å",
                "",
            ]
            for label, values in payload["measurements"].items():
                lines.extend(
                    [
                        label,
                        f"  峰/连续谱  {values['peakOverLocalContinuum']:.3f}",
                        (
                            "  预期 H-beta/连续谱  -"
                            if values.get("fluxAtExpectedHbetaOverLocalContinuum") is None
                            else "  预期 H-beta/连续谱  "
                            f"{values['fluxAtExpectedHbetaOverLocalContinuum']:.3f}"
                        ),
                        f"  正超额面积  {values['positiveExcessAreaAngstrom']:.1f} Å",
                        (
                            "  积分统计 S/N  -"
                            if values["integratedPositiveExcessSnr"] is None
                            else f"  积分统计 S/N  {values['integratedPositiveExcessSnr']:.1f}"
                        ),
                        "",
                    ]
                )
            lines.extend(
                [
                    "人工结论",
                    (
                        "至少一个产品通过工程候选门，仍须确认目标、arc 和 Fe II 去混合。"
                        if hbeta_assessment.get("hBetaDetected", False)
                        else "自动质量门未确认 H-beta；请结合本次原始计数、定标和目标身份复核。"
                    ),
                    (
                        "分类："
                        f"{hbeta_assessment.get('classification', '尚未运行自动质量门')}"
                    ),
                    "宽窗正超额不是 H-beta 显著度或物理等效宽度。",
                ]
            )
            text = "\n".join(lines)
        elif "frameIntegrityAudit" in payload:
            summary = payload.get("summary", {})
            failed = int(summary.get("failed", 0))
            text = (
                "ToupSky 输入完整性审计\n\n"
                f"总体状态：{'通过' if payload.get('status') == 'pass' else '失败'}\n"
                f"数据组：{summary.get('groupsAudited', '-')}\n"
                f"运行记录：{summary.get('manifestsAudited', '-')}\n"
                f"通过/失败：{summary.get('passed', '-')} / {failed}\n\n"
                "正式配置只采用经确认的逐文件完整性清单；原始 FITS 保持只读。"
            )
        elif "assessment" in payload:
            assessment = payload.get("assessment", {})
            acquisition = payload.get("acquisition", {})
            field = payload.get("fieldConstraint", {})
            diagnostic_redshift = assessment.get(
                "windowPeakMedianRedshiftDiagnostic",
                assessment.get("constrainedFeatureMedianRedshift", "-"),
            )
            gates = assessment.get("qualityGates", {})
            text = (
                "目标身份诊断\n\n"
                f"分类：{assessment.get('classification', '未知')}\n"
                f"目录红移：{assessment.get('catalogueRedshift', '-')}\n"
                f"窗口峰诊断红移：{diagnostic_redshift}\n"
                f"总曝光：{acquisition.get('totalExposureSeconds', '-')} s\n"
                f"狭缝：{acquisition.get('slitMicrometre', '-')} µm\n\n"
                f"红移门：{gates.get('medianRedshiftGatePassed', '-')}\n"
                f"谱线偏差门：{gates.get('featureOffsetGatePassed', '-')}\n"
                f"参考相关门：{gates.get('referenceCorrelationGatePassed', '-')}\n\n"
                + (
                    "采集判断：在居中获取前提下很可能是 3C 273；"
                    f"30 arcsec 内其他 Gaia 源：{field.get('otherSourcesWithin30Arcsec', '-')}，"
                    "但当前波长解尚未完成独立谱线确认。"
                    if field
                    else "窗口最大值不是谱线证认；当前结果尚未完成独立目标确认。"
                )
            )
        else:
            trace = payload.get("trace", {})
            selection = payload.get("frameSelection", {})
            warnings = payload.get("warnings", [])
            text = (
                f"目标：{payload.get('target', '未知')}\n"
                f"接受帧：{selection.get('acceptedCount', '-')}\n"
                f"提取：{payload.get('extractionBackend', '-')}\n"
                f"迹线 S/N：{trace.get('snr', '-')}\n"
                f"迹线 FWHM：{trace.get('medianFwhmPixels', '-')} px\n\n"
                "警告\n"
                + ("\n".join(f"• {item}" for item in warnings) if warnings else "无")
            )
        self._replace_text(self.metrics_text, text)

    def _open_selected_artifact(self) -> None:
        selection = self.artifact_tree.selection()
        if not selection:
            return
        path = self.artifact_paths.get(selection[0])
        if path is not None:
            self._open_path(path)

    def _show_known_hbeta(self) -> None:
        if not KNOWN_3C273_OUTPUT.is_dir():
            self.status.set("没有找到已完成的 3C 273 输出。")
            return
        self.last_output = KNOWN_3C273_OUTPUT
        self.open_button.configure(state=tk.NORMAL)
        self.refresh_artifacts(KNOWN_3C273_OUTPUT)
        self.switch_stage("analyse")

    def _update_document_cards(self) -> None:
        if not hasattr(self, "report_state"):
            return
        self.report_state.configure(
            text="PDF 与 Markdown 已就绪" if REPORT_PDF.is_file() and REPORT_SOURCE.is_file() else "尚未生成"
        )
        self.sop_state.configure(
            text="PDF 与 Markdown 已就绪" if SOP_PDF.is_file() and SOP_SOURCE.is_file() else "尚未生成"
        )
        documents_ready = REPORT_PDF.is_file() and SOP_PDF.is_file()
        self._set_stage_state("deliver", "文档已就绪" if documents_ready else "待生成", done=documents_ready)

    @staticmethod
    def _replace_text(widget: tk.Text, value: str) -> None:
        widget.configure(state=tk.NORMAL)
        widget.delete("1.0", tk.END)
        widget.insert("1.0", value)
        widget.configure(state=tk.DISABLED)

    @staticmethod
    def _open_path(path: Path) -> None:
        if path.exists():
            os.startfile(path)


def main() -> int:
    root = tk.Tk()
    UvexStudioApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
