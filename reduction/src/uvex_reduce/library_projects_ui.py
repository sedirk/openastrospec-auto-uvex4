"""Project-first offline library, backed by acquisition and reduction provenance.

This view deliberately reuses LibraryApp's preview, exact selection handoff and
catalog. Physical cleanup is delegated to the separate preview/confirmation UI.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from pathlib import Path
import subprocess
import sys
import tkinter as tk
from tkinter import messagebox, ttk

from .image_viewer import InteractiveImageViewer
from .library_catalog import LibraryCatalog
from .library_file_state import inspect_source_file
from .library_preview import load_library_preview
from .library_projects import LibraryProjects, OUTCOME_LABELS
from .library_theme import COLORS


GROUPS = {"night": "按观测夜", "target": "按目标", "outcome_label": "按运行结果",
          "reduction_label": "按后期进度", "source": "按数据来源"}
MEMBERS = {"spectrum": "光谱原片", "products": "后期成果", "guide": "盯缝 / 测光",
           "logs": "日志与证据", "all": "全部关联文件"}
SOURCES = {"real": "真实观测", "": "全部来源", "simulated": "模拟 / 测试", "unknown": "来源未知"}


def member_matches(row, category):
    if category == "spectrum":
        return (row.get("kind") == "frame" and row.get("channel") in {"spectroscopy", "atr"}
                and row.get("library_category") != "product")
    if category == "products":
        return row.get("kind") in {"product", "project"} or row.get("library_category") == "product"
    if category == "guide":
        return row.get("channel") in {"slit", "photometry"}
    if category == "logs":
        return row.get("kind") in {"metadata", "log"}
    return True


def acceptance_label(row):
    accepted = row.get("accepted")
    return "采集合格" if accepted == 1 else "采集未通过" if accepted == 0 else "未作质量结论"


def pretty_duration(seconds):
    return f"{float(seconds or 0) / 60:.1f} min"


def project_label(row):
    target = row.get("target") or "未命名项目"
    if str(row.get("id", "")).startswith("reduction:"):
        # Old reductions may have no timestamp or run ID. Their explicit
        # project directory distinguishes different reductions of one star.
        folder = Path(row.get("manifest_path") or "").parent.name
        return f"{target} · {folder}" if folder else target
    try:
        stamp = datetime.fromisoformat(str(row.get("created_utc") or "").replace("Z", "+00:00"))
        if stamp.tzinfo is not None:
            local = stamp.astimezone(timezone(timedelta(hours=8)))
            return f"{target} · {local:%H:%M}"
    except ValueError:
        pass
    return target


class ProjectBrowser(ttk.Frame):
    PAGE_SIZE = 150

    def __init__(self, parent, app):
        super().__init__(parent, padding=(10, 14, 10, 8))
        self.app = app
        self.rows = {}
        self.detail = None
        self.members = {}
        self.offset = 0
        self.generation = 0
        self.detail_generation = 0
        self.preview_generation = 0
        self.query = tk.StringVar(self)
        self.source = tk.StringVar(self, value="real")
        self.group = tk.StringVar(self, value="night")
        self.category = tk.StringVar(self, value="spectrum")
        self.outcome = tk.StringVar(self)
        self.title = tk.StringVar(self, value="观测项目")
        self.subtitle = tk.StringVar(self, value="按运行日志组织；跨夜同名目标不会被混成同一观测。")
        self.metrics = tk.StringVar(self, value="选择左侧项目，查看光谱、运行结果和后期链路")
        self.notice = tk.StringVar(self, value="人工“不采用”只改标签，不执行删除或回收；物理清理结果请查看文件回执。")
        self.path = tk.StringVar(self)
        self.presence = tk.StringVar(self, value="源文件位置和实际状态会单独显示")
        self._build()

    def _build(self):
        self.columnconfigure(1, weight=4)
        self.columnconfigure(2, weight=7)
        self.rowconfigure(0, weight=1)
        sidebar = ttk.Frame(self, width=158)
        sidebar.grid(row=0, column=0, sticky="ns", padx=(0, 14))
        sidebar.pack_propagate(False)
        ttk.Button(sidebar, text="目录与容量", command=self.app.storage).pack(side="bottom", fill="x", pady=6)
        self.cleanup_button = ttk.Button(sidebar, text="清理预览…", command=self.cleanup,
                                         state="disabled")
        self.cleanup_button.pack(side="bottom", fill="x", pady=6)
        ttk.Button(sidebar, text="刷新项目", command=self.refresh,
                   style="Quiet.TButton").pack(side="bottom", fill="x", pady=6)
        ttk.Label(sidebar, text="项目分类", style="Section.TLabel").pack(anchor="w", pady=(0, 16))
        for label, var, choices in (
            ("组织方式", self.group, GROUPS), ("数据来源", self.source, SOURCES),
            ("运行结果", self.outcome, {"": "全部结果", **OUTCOME_LABELS}),
        ):
            ttk.Label(sidebar, text=label, style="Muted.TLabel").pack(anchor="w", pady=(0, 5))
            self.app._combo(sidebar, var, choices, width=14,
                            changed=self.filter_changed).pack(fill="x", pady=(0, 14))
        ttk.Label(sidebar, text="观测夜按北京时间\n中午至次日中午划分",
                  style="Muted.TLabel", wraplength=156, font=("Microsoft YaHei UI", 9)).pack(anchor="w", pady=4)

        browser = ttk.Frame(self, width=330)
        browser.grid(row=0, column=1, sticky="nsew", padx=(0, 14))
        browser.rowconfigure(1, weight=1)
        browser.columnconfigure(0, weight=1)
        search = ttk.Frame(browser)
        search.grid(row=0, column=0, sticky="ew", pady=(0, 12))
        entry = ttk.Entry(search, textvariable=self.query, width=16)
        entry.pack(side="left", fill="x", expand=True)
        entry.bind("<Return>", lambda _event: self.filter_changed())
        ttk.Button(search, text="搜索", command=self.filter_changed).pack(side="left", padx=(5, 0))
        list_area = ttk.Frame(browser)
        list_area.grid(row=1, column=0, sticky="nsew")
        list_area.columnconfigure(0, weight=1)
        list_area.rowconfigure(0, weight=1)
        self.table = ttk.Treeview(list_area, columns=("result", "frames"), show="tree headings",
                                  selectmode="browse")
        self.table.heading("#0", text="目标 / 观测夜")
        self.table.column("#0", width=185, minwidth=115)
        self.table.heading("result", text="运行")
        self.table.column("result", width=85, minwidth=65, stretch=False)
        self.table.heading("frames", text="科学帧")
        self.table.column("frames", width=68, minwidth=62, stretch=False)
        self.table.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(list_area, command=self.table.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.table.configure(yscrollcommand=scroll.set)
        self.table.bind("<<TreeviewSelect>>", self.select_project)
        paging = ttk.Frame(browser)
        paging.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        self.previous = ttk.Button(paging, text="‹", command=lambda: self.page(-1), style="Small.TButton")
        self.previous.pack(side="left")
        self.next = ttk.Button(paging, text="›", command=lambda: self.page(1), style="Small.TButton")
        self.next.pack(side="left", padx=4)
        self.count = ttk.Label(paging, text="读取项目索引…", style="Muted.TLabel")
        self.count.pack(side="right")

        inspector = ttk.Frame(self)
        inspector.grid(row=0, column=2, sticky="nsew")
        inspector.columnconfigure(0, weight=1)
        inspector.rowconfigure(3, weight=1)
        ttk.Label(inspector, textvariable=self.title, style="Section.TLabel").grid(row=0, column=0, sticky="w")
        sub = ttk.Label(inspector, textvariable=self.subtitle, style="Muted.TLabel", wraplength=490)
        sub.grid(row=1, column=0, sticky="ew", pady=(4, 10))
        metric = ttk.Label(inspector, textvariable=self.metrics, style="Accent.TLabel", wraplength=490)
        metric.grid(row=2, column=0, sticky="ew", pady=(0, 12))
        self.tabs = ttk.Notebook(inspector, style="Compact.TNotebook")
        self.tabs.grid(row=3, column=0, sticky="nsew")
        frames = ttk.Frame(self.tabs, padding=(6, 8, 6, 4))
        self.preview_tab = frames
        self.tabs.add(frames, text="光谱预览")
        frames.columnconfigure(0, weight=1)
        frames.rowconfigure(0, weight=1)
        self.member_tab = ttk.Frame(self.tabs, padding=8)
        self.members_tab = self.member_tab
        self.tabs.add(self.member_tab, text="项目文件")
        self.member_tab.columnconfigure(0, weight=1)
        self.member_tab.rowconfigure(1, weight=1)
        self.app._combo(self.member_tab, self.category, MEMBERS, width=17,
                        changed=self.show_members).grid(row=0, column=0, sticky="w", pady=(0, 6))
        files_area = ttk.Frame(self.member_tab)
        files_area.grid(row=1, column=0, sticky="nsew")
        files_area.columnconfigure(0, weight=1)
        files_area.rowconfigure(0, weight=1)
        self.files = ttk.Treeview(files_area, columns=("file", "role", "quality"),
                                  show="headings", height=4, selectmode="browse")
        for key, label, width in (("file", "文件", 210), ("role", "角色", 80), ("quality", "采集质量", 100)):
            self.files.heading(key, text=label, anchor="w")
            self.files.column(key, width=width, minwidth=60, stretch=key == "file")
        self.files.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(files_area, command=self.files.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.files.configure(yscrollcommand=scroll.set)
        self.files.bind("<<TreeviewSelect>>", self.select_member)
        self.files.bind("<Double-1>", lambda _event: self.tabs.select(self.preview_tab))
        self.viewer = InteractiveImageViewer(frames, compact=True, background="#0b1017",
                                             foreground=COLORS["muted"], placeholder="选择项目查看已有光谱")
        self.viewer.grid(row=0, column=0, sticky="nsew")
        self.log_text = self.app._text_tab(self.tabs, "运行与质量")
        self.chain_text = self.app._text_tab(self.tabs, "后期与来源")
        chain_frame = self.chain_text.master
        chain_actions = ttk.Frame(chain_frame, padding=8)
        chain_actions.pack(side="top", fill="x", before=self.chain_text)
        self.open_saved_button = ttk.Button(chain_actions, text="在后期打开选中工程",
                                            command=self.open_selected_project, state="disabled")
        self.open_saved_button.pack(side="left")
        ttk.Label(chain_actions, text="不自动运行处理", style="Muted.TLabel").pack(side="left", padx=8)

        self.path_entry = ttk.Entry(inspector, textvariable=self.path, state="readonly")
        self.path_entry.grid(row=4, column=0, sticky="ew", pady=(10, 4))
        presence = ttk.Label(self.member_tab, textvariable=self.presence, style="Muted.TLabel", wraplength=490)
        presence.grid(row=2, column=0, sticky="ew", pady=6)
        actions = ttk.Frame(inspector)
        actions.grid(row=6, column=0, sticky="ew", pady=(8, 6))
        self.reveal_button = ttk.Button(actions, text="定位源文件", command=self.reveal, state="disabled")
        self.reveal_button.pack(side="left")
        self.copy_button = ttk.Button(actions, text="复制路径", command=self.copy_path,
                                       state="disabled", style="Quiet.TButton")
        self.copy_button.pack(side="left", padx=4)
        actions2 = ttk.Frame(self.member_tab)
        actions2.grid(row=3, column=0, sticky="ew")
        self.mark_button = ttk.Button(actions2, text="不采用 / 撤销", command=self.mark_member,
                                       state="disabled", style="Quiet.TButton")
        self.mark_button.pack(side="left")
        self.browse_button = ttk.Button(actions2, text="关联文件浏览", command=self.browse,
                                         state="disabled", style="Quiet.TButton")
        self.browse_button.pack(side="right")
        self.handoff_button = ttk.Button(actions, text="科学帧 → 后期…", command=self.handoff,
                                          state="disabled", style="Primary.TButton")
        self.handoff_button.pack(side="right")
        note = ttk.Label(self, textvariable=self.notice, style="Muted.TLabel", wraplength=1000)
        note.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(12, 0))

        def resize(event):
            for label in (sub, metric, presence):
                label.configure(wraplength=max(300, event.width - 10))
        inspector.bind("<Configure>", resize)

    def filter_changed(self):
        self.offset = 0
        self.refresh()

    def page(self, direction):
        self.offset = max(0, self.offset + direction * self.PAGE_SIZE)
        self.refresh()

    def refresh(self):
        self.generation += 1
        generation = self.generation
        filters = {"query": self.query.get().strip(), "source": self.source.get(), "outcome": self.outcome.get()}
        offset = self.offset
        database, page_size = self.app.database, self.PAGE_SIZE
        self.count.configure(text="正在读取…")

        def work():
            with LibraryCatalog(database) as catalog:
                model = LibraryProjects(catalog)
                model.refresh_registered_projects()
                return model.list_projects(offset=offset, limit=page_size, **filters), model.count_projects(**filters)

        def done(result):
            if generation != self.generation:
                return
            rows, count = result
            old = self.table.selection()
            self.table.delete(*self.table.get_children())
            self.rows = {row["id"]: row for row in rows}
            groups = {}
            for row in rows:
                value = str(row.get(self.group.get()) or "未记录")
                value = SOURCES.get(value, value) if self.group.get() == "source" else value
                if value not in groups:
                    groups[value] = self.table.insert("", "end", text=value, open=True)
                label = project_label(row)
                self.table.insert(groups[value], "end", iid=row["id"], text=label,
                                  values=(row.get("outcome_label", "未知"),
                                          f"{row.get('accepted_frames', 0)}/{row.get('science_frames', 0)}"))
            self.count.configure(text=f"{offset + 1 if count else 0}–{min(offset + len(rows), count)} / {count:,}")
            self.previous.configure(state="normal" if offset else "disabled")
            self.next.configure(state="normal" if offset + len(rows) < count else "disabled")
            if old and old[0] in self.rows:
                self.table.selection_set(old[0])
                self.select_project()
            else:
                self.clear()
            if not rows:
                self.notice.set("本筛选没有项目。可切换数据来源；新数据需先点击“更新索引”。")

        self.app._submit(work, done)

    def clear(self):
        self.detail_generation += 1
        self.preview_generation += 1
        self.detail = None
        self.members = {}
        self.title.set("观测项目")
        self.subtitle.set("选择项目，查看采集 → 筛片 → 后期的真实关联")
        self.metrics.set("科学帧显示为“采集合格数 / 科学帧数”；不包含试拍与盯缝帧")
        self.files.delete(*self.files.get_children())
        self.viewer.show_message("选择左侧观测项目")
        self.path.set("")
        self.presence.set("人工标记与实际文件状态分开保存")
        self.app._text(self.log_text, "")
        self.app._text(self.chain_text, "")
        for button in (self.reveal_button, self.copy_button, self.mark_button, self.handoff_button, self.browse_button):
            button.configure(state="disabled")
        self.cleanup_button.configure(state="disabled")
        self.open_saved_button.configure(state="disabled")

    def select_project(self, _event=None):
        selected = self.table.selection()
        if not selected or selected[0] not in self.rows:
            return
        project_id = selected[0]
        self.clear()
        generation = self.detail_generation
        self.title.set(self.rows[project_id].get("target") or "后期工程")
        self.subtitle.set("正在读取本项目的运行与来源记录…")
        database = self.app.database

        def work():
            with LibraryCatalog(database) as catalog:
                return LibraryProjects(catalog).get_project(project_id)

        def done(detail):
            if generation != self.detail_generation or not detail:
                return
            self.detail = detail
            self.subtitle.set(f"{detail.get('night') or '日期未记录'} · {detail.get('outcome_label', '未知')} · {detail.get('reduction_label', '未关联后期')}")
            self.metrics.set(f"科学帧 {detail.get('accepted_frames', 0)} / {detail.get('science_frames', 0)} · 试拍 {detail.get('probe_frames', 0)} · {pretty_duration(detail.get('exposure_seconds'))}")
            self.app._text(self.log_text, self.log_description(detail))
            self.app._text(self.chain_text, self.chain_description(detail))
            self.browse_button.configure(state="normal")
            self.cleanup_button.configure(state="normal")
            self.handoff_button.configure(state="normal" if detail.get("accepted_frames") else "disabled")
            self.show_members()
            warnings = detail.get("warnings", [])
            if detail.get("members_truncated"):
                warnings = [f"关联文件较多：此面板显示 {len(detail.get('files', []))} / {detail.get('member_count')} 项；后期科学帧选择不受此显示上限截断。", *warnings]
            self.notice.set("；".join(str(w) for w in warnings[:2]) if warnings else
                            "原片原位保留；人工“不采用”不删除、不回收。无帧的失败运行也保留日志。")

        self.app._submit(work, done)

    @staticmethod
    def log_description(detail):
        lines = [f"目标：{detail.get('target', '')}",
                 f"运行 ID：{detail.get('run_id') or detail.get('id')}",
                 f"运行结果：{detail.get('outcome_label', '未知')}（不是光谱质量结论）",
                 f"原因：{detail.get('reason') or detail.get('gate') or '未记录'}"]
        if detail.get("logged_accepted_frames") is not None:
            lines.append(f"整夜日志：合格 {detail['logged_accepted_frames']} 帧 · 积分 {pretty_duration(detail.get('logged_accepted_exposure_seconds'))}")
        lines.extend(["", "阶段与光谱质量（原采集判定；人工标记不覆盖此记录）"])
        gates = detail.get("quality_gates", [])
        if isinstance(gates, dict):
            gates = [{"stage": stage, **gate} for stage, gate in gates.items() if isinstance(gate, dict)]
        for gate in gates:
            lines.append(f"{gate.get('stage', '')} · {gate.get('code', '')} · {gate.get('severity', '')}")
            if gate.get("message"):
                lines.append(str(gate["message"]))
            metrics = gate.get("metrics") or {}
            labels = {"continuumSnrPerResolutionElement": "连续谱 SNR / 分辨单元",
                      "lineSnrPerResolutionElement": "谱线 SNR / 分辨单元",
                      "targetToSkyContrast": "目标 / 背景对比",
                      "probeExposureSeconds": "试拍曝光 / s", "exposureSeconds": "曝光 / s",
                      "traceSpatialCenterPixel": "谱迹中心 / px", "slitResidualPixels": "入缝残差 / px",
                      "acceptedFrames": "合格帧数", "reusedProbeFrames": "复用试拍帧数"}
            for key, label in labels.items():
                if key in metrics:
                    value = metrics[key]
                    lines.append(f"  {label}：{value:.3f}" if isinstance(value, float) else f"  {label}：{value}")
            lines.append("")
        lines.extend(["运行时间线", str(detail.get("timeline_note") or "")])
        for event in detail.get("timeline", []):
            if isinstance(event, dict):
                lines.append(" · ".join(str(event.get(key) or "") for key in
                                        ("time", "stage", "code", "message")))
            else:
                lines.append(str(event))
        if not detail.get("timeline"):
            lines.append("尚无可读时间线；请在全部关联文件中查看原始日志。")
        return "\n".join(lines)

    @staticmethod
    def chain_description(detail):
        from .library_storage_ui import format_bytes

        lines = ["关联依据：运行 ID、明确文件引用与后期输入清单；不按同名目标自动合并。", "",
                 f"采集证据索引占用：{format_bytes(detail.get('indexed_bytes', 0))}（不等于含所有后期成果的项目总量）",
                 "跨项目共享原片/校准不可累加算作可释放空间；存储概览按文件去重。", "",
                 "后期工程 / 处理成果"]
        for project in detail.get("reduction_projects", []):
            if isinstance(project, dict):
                lines.extend([str(project.get("name") or Path(project.get("path", "")).name),
                              f"阶段：{project.get('current_stage') or project.get('stage') or '未记录'}",
                              str(project.get("path") or "")])
            else:
                lines.append(str(project))
        for product in detail.get("products", []):
            lines.append(str(product.get("path", product)) if isinstance(product, dict) else str(product))
        lines.extend(["", "源记录路径", *[str(path) for path in detail.get("source_paths", [])]])
        lines.extend(["", "在“项目文件”中选择“后期成果”，可定位对应文件或打开 .astroproj。",
                      "新工程复用精确选片与现有提取/定标流程；不从目录暗中补入未选帧。"])
        return "\n".join(lines)

    def show_members(self):
        self.preview_generation += 1
        self.files.delete(*self.files.get_children())
        rows = self.detail.get("files", []) if self.detail else []
        self.members = {str(row["id"]): row for row in rows if member_matches(row, self.category.get())}
        for key, row in self.members.items():
            self.files.insert("", "end", iid=key, values=(Path(row["path"]).name,
                              row.get("role") or row.get("kind"), acceptance_label(row)))
        if self.members:
            self.files.selection_set(next(iter(self.members)))
            self.select_member()
        else:
            self.viewer.show_message("本项目没有这一类文件\n可切换到日志与证据，或全部关联文件")
            paths = self.detail.get("source_paths", []) if self.detail else []
            self.set_path(paths[0] if paths else "")

    def current_member(self):
        selected = self.files.selection()
        return self.members.get(selected[0]) if selected else None

    def set_path(self, path, review=""):
        self.path.set(str(path))
        if path:
            from .library_cleanup_execute import latest_file_receipt

            with LibraryCatalog(self.app.database) as catalog:
                lifecycle = latest_file_receipt(catalog, path)
            state = inspect_source_file(path, review, lifecycle=lifecycle)
            self.presence.set(f"{state.presence_label} · {state.review_label} · {state.lifecycle_label}")
        else:
            self.presence.set("没有可定位的来源路径")
        self.reveal_button.configure(state="normal" if path else "disabled")
        self.copy_button.configure(state="normal" if path else "disabled")
        self.mark_button.configure(state="normal" if self.current_member() else "disabled")
        self.open_saved_button.configure(state="normal" if path and str(path).lower().endswith(".astroproj") else "disabled")

    def select_member(self, _event=None):
        row = self.current_member()
        if not row:
            return
        self.preview_generation += 1
        generation = self.preview_generation
        self.set_path(row["path"], row.get("review", ""))
        self.viewer.show_message("读取已有文件；不开始光谱处理…")

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
                self.viewer.show_message(f"不能预览：{error}\n可复制路径或定位源文件")
            elif preview.image is not None:
                self.viewer.set_image(preview.image, source=preview.caption)
            else:
                self.viewer.show_message(preview.caption + "\n详细记录在“运行与质量”页")
                if preview.text:
                    self.app._text(self.log_text, self.log_description(self.detail) +
                                   "\n\n当前源文件（只读）\n" + preview.text)

        self.app._submit(work, done)

    def reveal(self):
        if self.path.get():
            self.notice.set(self.app.reveal_path(self.path.get()))

    def copy_path(self):
        if self.path.get():
            self.clipboard_clear()
            self.clipboard_append(self.path.get())
            self.notice.set("已复制完整来源路径；文件未改动。")

    def mark_member(self):
        row = self.current_member()
        if not row:
            return
        with LibraryCatalog(self.app.database) as catalog:
            current = catalog.get_file(row["id"])
            decision = "unreviewed" if current.get("review") == "reject" else "reject"
            catalog.set_review(row["id"], decision=decision, rating=current.get("rating", 0),
                               notes=current.get("notes", ""))
        row["review"] = decision
        if decision == "reject":
            self.app.basket.pop(row["path"], None)
        self.set_path(row["path"], decision)
        self.app.refresh()
        self.notice.set("仅标记不采用；未删除、未进入回收站、未移动，释放 0 B。" if decision == "reject"
                        else "已撤销不采用标记；本次未改动文件，释放 0 B。既往清理操作另看回执。")

    def browse(self):
        if self.detail:
            ids = self.detail.get("member_file_ids")
            rows = [{"id": value} for value in ids] if ids is not None else self.detail.get("files", [])
            self.app.browse_project_files(self.detail.get("run_id", ""), rows)

    def cleanup(self):
        if self.detail:
            return self.app.cleanup(self.detail["id"])

    def handoff(self):
        if not self.detail:
            return
        with LibraryCatalog(self.app.database) as catalog:
            rows = LibraryProjects(catalog).select_science(self.detail["id"])
        if not rows:
            messagebox.showinfo("光谱后期", "没有可用的合格科学帧。可在文件浏览中复核试拍、未知质量帧或人工标记。",
                                parent=self.app.root)
            return
        if self.app.basket and not messagebox.askyesno(
            "替换选片篮", "这将用当前项目的合格科学帧替换尚未保存的选片篮；原片不变。继续？",
            parent=self.app.root,
        ):
            return
        previous = dict(self.app.basket)
        self.app.basket.clear()
        if not self.app.add_basket(rows=rows, role="science", target_name=""):
            self.app.basket = previous
            self.app._update_actions()
            return
        self.app.export()

    def open_project(self, path):
        """Open the existing Studio project, never execute its processing workflow."""
        from .project import AstroProject

        AstroProject.load(path)
        subprocess.Popen([sys.executable, "-m", "uvex_reduce.gui", "--project", str(path)], shell=False)

    def open_selected_project(self):
        if self.path.get().lower().endswith(".astroproj"):
            try:
                self.open_project(self.path.get())
                self.notice.set("已打开已有后期工程；未自动运行处理或观测。")
            except (ValueError, OSError) as error:
                messagebox.showwarning("后期工程", str(error), parent=self.app.root)
