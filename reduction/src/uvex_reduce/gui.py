from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import queue
import re
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
import traceback

from .config import load_config
from .inspector import infer_object_from_filename
from .pipeline import ReductionPipeline
from .workflow import run_full_workflow


DEFAULT_DATA_ROOT = os.environ.get("UVEX_ADV_DATA_ROOT", "").strip()
PROJECT_ROOT = Path(__file__).resolve().parents[2]
BASE_CONFIG = PROJECT_ROOT / "configs" / "spec-regulus.toml"
ISIS_TEMPLATE_DIRECTORY = Path(
    os.environ.get(
        "UVEX_ADV_ISIS_TEMPLATE_DIRECTORY",
        "<local-isis-template-directory>",
    ).strip()
)
HOT_STANDARDS = {
    "regulus": "Regulus",
    "castor": "Castor",
    "procyon": "Procyon",
    "sirius": "Sirius",
    "vega": "Vega",
    "altair": "Altair",
    "rigel": "Rigel",
    "bellatrix": "Bellatrix",
}


@dataclass(slots=True)
class ObservationGroup:
    directory: Path
    target: str
    files: list[Path]
    relative_directory: str

    @property
    def label(self) -> str:
        return f"{self.relative_directory}  ·  {self.target}  ·  {len(self.files)} 帧"


@dataclass(frozen=True, slots=True)
class WorkflowPreset:
    label: str
    standard_config: Path
    science_config: Path
    template_path: Path
    standard_name: str
    target_name: str
    output_name: str
    refine_emission: bool


WORKFLOW_PRESETS = (
    WorkflowPreset(
        label="2026-05：Vega → NGC 6543（推荐验收组）",
        standard_config=PROJECT_ROOT / "configs" / "20260509-vega-standard.toml",
        science_config=PROJECT_ROOT / "configs" / "20260506-ngc6543-science.toml",
        template_path=ISIS_TEMPLATE_DIRECTORY / "p_a0v.dat",
        standard_name="Vega",
        target_name="NGC6543",
        output_name="202605-ngc6543-full",
        refine_emission=True,
    ),
    WorkflowPreset(
        label="2026-05：Vega → HD 140573（暗弱恒星压力测试）",
        standard_config=PROJECT_ROOT / "configs" / "20260509-vega-standard.toml",
        science_config=PROJECT_ROOT / "configs" / "20260509-hd140573-science.toml",
        template_path=ISIS_TEMPLATE_DIRECTORY / "p_a0v.dat",
        standard_name="Vega",
        target_name="HD140573",
        output_name="202605-hd140573-full",
        refine_emission=False,
    ),
    WorkflowPreset(
        label="2026-02：Regulus → NGC 2392",
        standard_config=PROJECT_ROOT / "configs" / "20260221-regulus-wavelength.toml",
        science_config=PROJECT_ROOT / "configs" / "20260221-ngc2392-preferred.toml",
        template_path=ISIS_TEMPLATE_DIRECTORY / "p_b8v.dat",
        standard_name="Regulus",
        target_name="NGC2392",
        output_name="20260221-ngc2392-full",
        refine_emission=True,
    ),
)


class UvexReductionApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("OpenAstroSpec Spectral Studio — UVEX4")
        self.root.geometry("1100x800")
        self.root.minsize(940, 680)
        self.groups: list[ObservationGroup] = []
        self.science_groups: list[ObservationGroup] = []
        self.target_choices: list[ObservationGroup | None] = [None]
        self.events: queue.Queue[tuple[str, object]] = queue.Queue()
        self.last_output: Path | None = None

        self.data_root = tk.StringVar(value=DEFAULT_DATA_ROOT)
        self.template_directory = tk.StringVar(
            value=os.environ.get("UVEX_ADV_ISIS_TEMPLATE_DIRECTORY", "").strip()
        )
        self.output_root = tk.StringVar(
            value=str(PROJECT_ROOT / "output" / "_internal" / "runs" / "gui")
        )
        self.use_flat = tk.BooleanVar(value=True)
        self.cosmic_ray_clean = tk.BooleanVar(value=True)
        self.combine_method = tk.StringVar(value="mean")
        self.workflow_preset = tk.StringVar(value=WORKFLOW_PRESETS[0].label)
        self.status = tk.StringVar(value="请选择标准星组，然后点击“开始处理”。")
        self._build_ui()
        self.root.after(100, self._drain_events)
        self.scan()

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.root, padding=14)
        outer.pack(fill=tk.BOTH, expand=True)

        title = ttk.Label(outer, text="OpenAstroSpec 光谱处理 — UVEX4", font=("Microsoft YaHei UI", 20, "bold"))
        title.pack(anchor=tk.W, pady=(0, 12))

        paths = ttk.LabelFrame(outer, text="数据与输出", padding=10)
        paths.pack(fill=tk.X)
        paths.columnconfigure(1, weight=1)
        ttk.Label(paths, text="SPEC 数据目录").grid(row=0, column=0, sticky=tk.W, padx=(0, 8))
        ttk.Entry(paths, textvariable=self.data_root).grid(row=0, column=1, sticky=tk.EW)
        ttk.Button(paths, text="浏览…", command=self._browse_data).grid(row=0, column=2, padx=6)
        ttk.Button(paths, text="重新扫描", command=self.scan).grid(row=0, column=3)
        ttk.Label(paths, text="ISIS 模板目录").grid(
            row=1, column=0, sticky=tk.W, padx=(0, 8), pady=(8, 0)
        )
        ttk.Entry(paths, textvariable=self.template_directory).grid(
            row=1, column=1, sticky=tk.EW, pady=(8, 0)
        )
        ttk.Button(paths, text="浏览…", command=self._browse_template).grid(
            row=1, column=2, padx=6, pady=(8, 0)
        )
        ttk.Label(paths, text="输出目录").grid(row=2, column=0, sticky=tk.W, padx=(0, 8), pady=(8, 0))
        ttk.Entry(paths, textvariable=self.output_root).grid(row=2, column=1, sticky=tk.EW, pady=(8, 0))
        ttk.Button(paths, text="浏览…", command=self._browse_output).grid(
            row=2, column=2, padx=6, pady=(8, 0)
        )

        workflow = ttk.LabelFrame(outer, text="一键完整流程", padding=10)
        workflow.pack(fill=tk.X, pady=(12, 0))
        workflow.columnconfigure(0, weight=1)
        self.preset_combo = ttk.Combobox(
            workflow,
            state="readonly",
            textvariable=self.workflow_preset,
            values=[preset.label for preset in WORKFLOW_PRESETS],
        )
        self.preset_combo.grid(row=0, column=0, sticky=tk.EW, padx=(0, 8))
        self.full_run_button = ttk.Button(
            workflow,
            text="运行标准星 → 平场试算 → 科学帧 → 归一化",
            command=self.run_full_preset,
        )
        self.full_run_button.grid(row=0, column=1, sticky=tk.E)
        ttk.Label(
            workflow,
            text=(
                "会自动修复 SDK 横向错位、按曝光时间归一、配准叠加，并保留 FITS/CSV/PNG；"
                "候选平场只有通过标准星质量门才会进入首选结果。"
            ),
            foreground="#555555",
            wraplength=940,
        ).grid(row=1, column=0, columnspan=2, sticky=tk.W, pady=(6, 0))

        body = ttk.Frame(outer)
        body.pack(fill=tk.BOTH, expand=True, pady=12)
        body.columnconfigure(0, weight=2)
        body.columnconfigure(1, weight=3)
        body.rowconfigure(0, weight=1)

        selection = ttk.LabelFrame(body, text="可用标准星观测组", padding=10)
        selection.grid(row=0, column=0, sticky=tk.NSEW, padx=(0, 6))
        selection.rowconfigure(0, weight=1)
        selection.columnconfigure(0, weight=1)
        self.group_list = tk.Listbox(selection, exportselection=False, font=("Microsoft YaHei UI", 10))
        self.group_list.grid(row=0, column=0, sticky=tk.NSEW)
        scrollbar = ttk.Scrollbar(selection, orient=tk.VERTICAL, command=self.group_list.yview)
        scrollbar.grid(row=0, column=1, sticky=tk.NS)
        self.group_list.configure(yscrollcommand=scrollbar.set)
        self.group_list.bind("<<ListboxSelect>>", self._show_selection)
        self.details = ttk.Label(selection, text="", justify=tk.LEFT, wraplength=350)
        self.details.grid(row=1, column=0, columnspan=2, sticky=tk.EW, pady=(10, 0))
        ttk.Label(selection, text="可选：套用到同目录目标").grid(
            row=2, column=0, columnspan=2, sticky=tk.W, pady=(10, 3)
        )
        self.target_combo = ttk.Combobox(selection, state="readonly")
        self.target_combo.grid(row=3, column=0, columnspan=2, sticky=tk.EW)
        self.target_combo["values"] = ["仅处理并建立标准星波长解"]
        self.target_combo.current(0)
        ttk.Label(
            selection,
            text="只有确认光栅角度、狭缝、ROI 和 binning 未改变时才能套用。",
            foreground="#8a5a00",
            wraplength=350,
        ).grid(row=4, column=0, columnspan=2, sticky=tk.W, pady=(4, 0))

        log_frame = ttk.LabelFrame(body, text="运行记录", padding=10)
        log_frame.grid(row=0, column=1, sticky=tk.NSEW, padx=(6, 0))
        log_frame.rowconfigure(0, weight=1)
        log_frame.columnconfigure(0, weight=1)
        self.log = tk.Text(log_frame, state=tk.DISABLED, wrap=tk.WORD, font=("Consolas", 9))
        self.log.grid(row=0, column=0, sticky=tk.NSEW)
        log_scroll = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=self.log.yview)
        log_scroll.grid(row=0, column=1, sticky=tk.NS)
        self.log.configure(yscrollcommand=log_scroll.set)

        controls = ttk.Frame(outer)
        controls.pack(fill=tk.X)
        ttk.Checkbutton(
            controls,
            text="试算候选平场并自动质量判定（不会强制套用）",
            variable=self.use_flat,
        ).pack(side=tk.LEFT)
        ttk.Checkbutton(
            controls,
            text="单帧去宇宙线",
            variable=self.cosmic_ray_clean,
        ).pack(side=tk.LEFT, padx=(14, 0))
        ttk.Combobox(
            controls,
            state="readonly",
            width=9,
            textvariable=self.combine_method,
            values=("mean", "median"),
        ).pack(side=tk.LEFT, padx=(6, 0))
        self.open_button = ttk.Button(
            controls,
            text="打开输出目录",
            command=self._open_output,
            state=tk.DISABLED,
        )
        self.open_button.pack(side=tk.RIGHT)
        self.run_button = ttk.Button(
            controls,
            text="高级：快速提取所选组",
            command=self.run_selected,
        )
        self.run_button.pack(side=tk.RIGHT, padx=8)

        ttk.Separator(outer).pack(fill=tk.X, pady=(12, 8))
        ttk.Label(outer, textvariable=self.status).pack(anchor=tk.W)

    def _browse_data(self) -> None:
        current = self.data_root.get().strip()
        selected = filedialog.askdirectory(**({"initialdir": current} if current else {}))
        if selected:
            self.data_root.set(selected)
            self.scan()

    def _browse_template(self) -> None:
        current = self.template_directory.get().strip()
        selected = filedialog.askdirectory(**({"initialdir": current} if current else {}))
        if selected:
            self.template_directory.set(selected)
            update_gate = getattr(self, "_update_calibration_gate", None)
            if callable(update_gate):
                update_gate()

    def _browse_output(self) -> None:
        selected = filedialog.askdirectory(initialdir=self.output_root.get())
        if selected:
            self.output_root.set(selected)

    def scan(self) -> None:
        root_text = self.data_root.get().strip()
        if not root_text:
            self.groups = []
            self.science_groups = []
            self.group_list.delete(0, tk.END)
            self.details.configure(text="请选择本机 FITS 数据根目录后再扫描。")
            self.status.set(
                "未配置数据目录；可浏览选择，或在启动前设置 UVEX_ADV_DATA_ROOT。"
            )
            return
        root = Path(root_text).expanduser()
        if not root.is_dir():
            messagebox.showerror("目录不存在", f"找不到数据目录：\n{root}")
            return
        self.groups = discover_standard_groups(root)
        self.science_groups = discover_science_groups(root)
        self.group_list.delete(0, tk.END)
        for group in self.groups:
            self.group_list.insert(tk.END, group.label)
        if self.groups:
            preferred = next(
                (
                    index
                    for index, group in enumerate(self.groups)
                    if group.target == "Regulus" and len(group.files) >= 3
                ),
                0,
            )
            self.group_list.selection_set(preferred)
            self.group_list.see(preferred)
            self._show_selection()
            self.status.set(f"发现 {len(self.groups)} 个可做 Balmer/模板标定的标准星组。")
        else:
            self.details.configure(text="未找到 Regulus、Castor、Procyon 等热标准星序列。")
            self.status.set("没有可用标准星组。")

    def _show_selection(self, _event=None) -> None:
        group = self._selected_group()
        if group is None:
            return
        file_names = "、".join(path.name for path in group.files)
        template = self.template_directory.get().strip() or "未配置"
        self.details.configure(
            text=(
                f"目标：{group.target}\n"
                f"文件：{file_names}\n"
                f"模板库：{template}\n"
                "方向由恒星线自动复核；最终输出统一为波长递增。"
            )
        )
        candidates = [
            candidate
            for candidate in self.science_groups
            if (
                candidate.directory == group.directory
                or group.directory in candidate.directory.parents
            )
            and not (
                candidate.target.casefold() == group.target.casefold()
                and candidate.files == group.files
            )
        ]
        self.target_choices = [None, *candidates]
        self.target_combo["values"] = [
            "仅处理并建立标准星波长解",
            *(f"{candidate.target} · {len(candidate.files)} 帧" for candidate in candidates),
        ]
        self.target_combo.current(0)

    def _selected_group(self) -> ObservationGroup | None:
        selection = self.group_list.curselection()
        if not selection:
            return None
        return self.groups[int(selection[0])]

    def run_selected(self) -> None:
        group = self._selected_group()
        if group is None:
            messagebox.showwarning("尚未选择", "请先选择一个标准星观测组。")
            return
        if not BASE_CONFIG.is_file():
            messagebox.showerror("配置缺失", f"找不到基础配置：\n{BASE_CONFIG}")
            return
        template_text = self.template_directory.get().strip()
        if not template_text:
            messagebox.showerror(
                "模板目录未配置",
                "请选择 ISIS 标准模板目录，或设置 UVEX_ADV_ISIS_TEMPLATE_DIRECTORY。",
            )
            return
        template_directory = Path(template_text).expanduser().resolve()
        if not template_directory.is_dir():
            messagebox.showerror("模板目录不存在", f"找不到 ISIS 模板目录：\n{template_directory}")
            return
        self.run_button.configure(state=tk.DISABLED)
        self.open_button.configure(state=tk.DISABLED)
        self._clear_log()
        self._append_log(
            f"开始高级快速提取：{group.label}\n"
            "此入口只做提取/波长解转移，不试算平场或生成响应归一化产品；"
            "日常处理请用上方一键完整流程。\n"
        )
        self.status.set("正在预处理、提取并匹配 ISIS 标准星模板…")
        thread = threading.Thread(
            target=self._run_worker,
            args=(
                group,
                self._selected_science_group(),
                Path(self.data_root.get()).expanduser().resolve(),
                Path(self.output_root.get()).expanduser().resolve(),
                template_directory,
                bool(self.cosmic_ray_clean.get()),
                self.combine_method.get(),
            ),
            daemon=True,
        )
        thread.start()

    def run_full_preset(self) -> None:
        preset = next(
            (
                candidate
                for candidate in WORKFLOW_PRESETS
                if candidate.label == self.workflow_preset.get()
            ),
            None,
        )
        if preset is None:
            messagebox.showwarning("尚未选择", "请先选择一个完整流程预设。")
            return
        data_root_text = self.data_root.get().strip()
        if not data_root_text:
            messagebox.showerror(
                "数据目录未配置",
                "请选择本机 FITS 数据根目录，或设置 UVEX_ADV_DATA_ROOT。",
            )
            return
        template_text = self.template_directory.get().strip()
        if not template_text:
            messagebox.showerror(
                "模板目录未配置",
                "请选择 ISIS 标准模板目录，或设置 UVEX_ADV_ISIS_TEMPLATE_DIRECTORY。",
            )
            return
        data_root = Path(data_root_text).expanduser().resolve()
        if not data_root.is_dir():
            messagebox.showerror("数据目录不存在", f"找不到 FITS 数据目录：\n{data_root}")
            return
        template_path = Path(template_text).expanduser().resolve() / preset.template_path.name
        missing = [
            path
            for path in (
                preset.standard_config,
                preset.science_config,
                template_path,
            )
            if not path.is_file()
        ]
        if missing:
            messagebox.showerror(
                "文件缺失",
                "完整流程缺少以下文件：\n" + "\n".join(str(path) for path in missing),
            )
            return
        destination = (
            Path(self.output_root.get()).expanduser().resolve() / preset.output_name
        )
        self.run_button.configure(state=tk.DISABLED)
        self.full_run_button.configure(state=tk.DISABLED)
        self.open_button.configure(state=tk.DISABLED)
        self._clear_log()
        self._append_log(f"开始完整流程：{preset.label}\n")
        self.status.set("正在处理标准星、候选平场、科学帧与一维归一化产品…")
        threading.Thread(
            target=self._run_full_worker,
            args=(
                preset,
                data_root,
                template_path,
                destination,
                bool(self.use_flat.get()),
                bool(self.cosmic_ray_clean.get()),
                self.combine_method.get(),
            ),
            daemon=True,
        ).start()

    def _run_full_worker(
        self,
        preset: WorkflowPreset,
        data_root: Path,
        template_path: Path,
        destination: Path,
        evaluate_flat: bool,
        cosmic_ray_clean: bool,
        combine_method: str,
    ) -> None:
        try:
            run = run_full_workflow(
                preset.standard_config,
                preset.science_config,
                template_path,
                destination,
                standard_name=preset.standard_name,
                target_name=preset.target_name,
                input_root=data_root,
                evaluate_flat=evaluate_flat,
                refine_emission=preset.refine_emission,
                cosmic_ray_clean=cosmic_ray_clean,
                combine_method=combine_method,
            )
            solution = run.standard_run.result.wavelength
            accepted = len(run.science_run.result.source_files)
            rejected = len(run.science_run.result.rejected_source_files)
            lines = [
                f"标准星：{preset.standard_name}",
                (
                    f"波长范围：{solution.wavelength_angstrom[0]:.1f}–"
                    f"{solution.wavelength_angstrom[-1]:.1f} Å"
                    if solution is not None
                    else "波长范围：不可用"
                ),
                f"候选平场：{'采用' if run.flat_accepted else '未采用'}",
                f"判定：{run.flat_decision}",
                f"响应曲线残差散度：{run.response.fractional_scatter:.4f}",
                f"科学帧：接受 {accepted}，拒绝 {rejected}",
                (
                    "降噪："
                    f"{'单帧 L.A.Cosmic；' if cosmic_ray_clean else '未做单帧 L.A.Cosmic；'}"
                    f"{combine_method} 合成"
                ),
            ]
            if run.zero_point is not None:
                lines.extend(
                    [
                        f"发射线复标：{run.zero_point.method}",
                        f"复标 RMS：{run.zero_point.rms_angstrom:.3f} Å",
                        f"匹配线数：{run.zero_point.reference_wavelengths.size}",
                    ]
                )
            lines.extend(
                [
                    "绝对流量标定：否（当前为标准星相对响应校正）",
                    "6800 Å 以上：保留，并标记二级光谱污染风险",
                    f"\n输出：{destination}",
                ]
            )
            self.events.put(("success", ("\n".join(lines), destination)))
        except Exception:
            self.events.put(("error", traceback.format_exc()))

    def _selected_science_group(self) -> ObservationGroup | None:
        index = self.target_combo.current()
        if index < 0 or index >= len(self.target_choices):
            return None
        return self.target_choices[index]

    def _run_worker(
        self,
        group: ObservationGroup,
        science_group: ObservationGroup | None,
        data_root: Path,
        output_root: Path,
        template_directory: Path,
        cosmic_ray_clean: bool,
        combine_method: str,
    ) -> None:
        try:
            config = load_config(BASE_CONFIG)
            config.inputs.root = data_root
            config.inputs.science = [str(path) for path in group.files]
            config.inputs.target_name = group.target
            flat_files = sorted(group.directory.glob("LED-*.fit"))
            config.inputs.flat = [str(path) for path in flat_files]
            config.preprocess.use_flat = False
            config.preprocess.cosmic_ray_clean = cosmic_ray_clean
            config.preprocess.combine_method = combine_method
            config.inputs.output_dir = (
                output_root / _safe_name(f"{group.relative_directory}-{group.target}")
            )
            config.wavelength.template_directory = template_directory
            config.wavelength.template_path = None
            config.wavelength.template_star = group.target
            run = ReductionPipeline(config).run()
            solution = run.result.wavelength
            lines = [
                f"目标：{run.target_name}",
                f"提取：{run.result.extraction_backend}",
            ]
            if solution is None:
                lines.append("波长标定：失败（产物保留为像素轴）")
            else:
                lines.extend(
                    [
                        f"波长范围：{solution.wavelength_angstrom[0]:.1f}–"
                        f"{solution.wavelength_angstrom[-1]:.1f} Å",
                        f"方法：{solution.method}",
                        f"匹配线数：{solution.matched_pixels.size}",
                        (
                            f"内部 RMS：{solution.rms_angstrom:.3f} Å"
                            if solution.rms_angstrom == solution.rms_angstrom
                            else "内部 RMS：不可由恰好两条定标线独立估计"
                        ),
                        f"模板相关：{solution.template_correlation:.3f}",
                    ]
                )
            if run.result.warnings:
                lines.append("\n警告：")
                lines.extend(f"• {warning}" for warning in run.result.warnings)
            lines.append(f"\n输出：{config.inputs.output_dir}")
            final_output = config.inputs.output_dir
            if science_group is not None:
                if solution is None:
                    raise RuntimeError("标准星未产生可靠波长解，不能套用到科学目标。")
                lines.append(
                    f"\n开始套用到：{science_group.target}（{len(science_group.files)} 帧）"
                )
                science_config = load_config(BASE_CONFIG)
                science_config.inputs.root = data_root
                science_config.inputs.science = [str(path) for path in science_group.files]
                science_config.inputs.target_name = science_group.target
                science_config.inputs.flat = [str(path) for path in flat_files]
                science_config.preprocess.use_flat = False
                science_config.preprocess.cosmic_ray_clean = cosmic_ray_clean
                science_config.preprocess.combine_method = combine_method
                science_config.inputs.output_dir = (
                    output_root
                    / _safe_name(
                        f"{science_group.relative_directory}-{science_group.target}"
                    )
                )
                science_config.wavelength.mode = "solution_file"
                science_config.wavelength.solution_path = run.artifacts["fits"]
                science_config.wavelength.template_directory = None
                science_config.wavelength.template_path = None
                target_run = ReductionPipeline(science_config).run()
                target_solution = target_run.result.wavelength
                lines.extend(
                    [
                        f"目标提取：{target_run.result.extraction_backend}",
                        (
                            f"目标波长范围：{target_solution.wavelength_angstrom[0]:.1f}–"
                            f"{target_solution.wavelength_angstrom[-1]:.1f} Å"
                            if target_solution is not None
                            else "目标波长套用失败，保留像素轴产物"
                        ),
                    ]
                )
                if target_run.result.warnings:
                    lines.append("目标警告：")
                    lines.extend(f"• {warning}" for warning in target_run.result.warnings)
                final_output = science_config.inputs.output_dir
                lines.append(f"目标输出：{final_output}")
            self.events.put(("success", ("\n".join(lines), final_output)))
        except Exception:
            self.events.put(("error", traceback.format_exc()))

    def _drain_events(self) -> None:
        try:
            while True:
                event, payload = self.events.get_nowait()
                if event == "success":
                    message, output = payload
                    self._append_log(str(message) + "\n")
                    self.last_output = Path(output)
                    self.open_button.configure(state=tk.NORMAL)
                    self.status.set("处理完成。请检查光谱图、残差图与运行警告。")
                elif event == "error":
                    self._append_log(str(payload))
                    self.status.set("处理失败；详细错误已写入运行记录。")
                    messagebox.showerror("处理失败", "处理失败，请查看右侧运行记录。")
                self.run_button.configure(state=tk.NORMAL)
                self.full_run_button.configure(state=tk.NORMAL)
        except queue.Empty:
            pass
        self.root.after(100, self._drain_events)

    def _open_output(self) -> None:
        if self.last_output is not None and self.last_output.is_dir():
            os.startfile(self.last_output)

    def _clear_log(self) -> None:
        self.log.configure(state=tk.NORMAL)
        self.log.delete("1.0", tk.END)
        self.log.configure(state=tk.DISABLED)

    def _append_log(self, text: str) -> None:
        self.log.configure(state=tk.NORMAL)
        self.log.insert(tk.END, text)
        self.log.see(tk.END)
        self.log.configure(state=tk.DISABLED)


def discover_standard_groups(root: str | Path) -> list[ObservationGroup]:
    data_root = Path(root).expanduser().resolve()
    grouped: dict[tuple[Path, str], list[Path]] = {}
    for pattern in ("*.fit", "*.fits", "*.fts"):
        for path in data_root.rglob(pattern):
            relative_parts = {part.casefold() for part in path.relative_to(data_root).parts}
            if {"isis_6_1_1", "dark", "offset"} & relative_parts:
                continue
            inferred = infer_object_from_filename(path)
            if not inferred:
                continue
            normalized = re.sub(r"[^a-z0-9]+", "", inferred.casefold())
            target = HOT_STANDARDS.get(normalized)
            if target is None:
                continue
            grouped.setdefault((path.parent, target), []).append(path.resolve())
    groups = [
        ObservationGroup(
            directory=directory,
            target=target,
            files=sorted(files, key=lambda path: path.name.casefold()),
            relative_directory=str(directory.relative_to(data_root)) or ".",
        )
        for (directory, target), files in grouped.items()
    ]
    return sorted(groups, key=lambda group: (group.relative_directory, group.target.casefold()))


def discover_science_groups(root: str | Path) -> list[ObservationGroup]:
    data_root = Path(root).expanduser().resolve()
    grouped: dict[tuple[Path, str], tuple[str, list[Path]]] = {}
    for pattern in ("*.fit", "*.fits", "*.fts"):
        for path in data_root.rglob(pattern):
            relative_parts = {part.casefold() for part in path.relative_to(data_root).parts}
            if {"isis_6_1_1", "dark", "offset"} & relative_parts:
                continue
            inferred = infer_object_from_filename(path)
            if not inferred:
                continue
            normalized = re.sub(r"[^a-z0-9]+", "", inferred.casefold())
            if not normalized:
                continue
            key = (path.parent, normalized)
            canonical, files = grouped.setdefault(key, (inferred, []))
            files.append(path.resolve())
            grouped[key] = (canonical, files)
    groups = [
        ObservationGroup(
            directory=directory,
            target=canonical,
            files=sorted(files, key=lambda path: path.name.casefold()),
            relative_directory=str(directory.relative_to(data_root)) or ".",
        )
        for (directory, _normalized), (canonical, files) in grouped.items()
    ]
    return sorted(groups, key=lambda group: (group.relative_directory, group.target.casefold()))


def _safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", value).strip("._") or "standard"


def main() -> int:
    # Imported lazily to keep the proven worker/controller code in this module
    # reusable while the staged workbench subclasses it without an import cycle.
    from .studio import UvexStudioApp

    root = tk.Tk()
    UvexStudioApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
