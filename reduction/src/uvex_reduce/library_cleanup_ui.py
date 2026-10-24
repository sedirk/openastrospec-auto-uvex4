"""Explicit, project-first cleanup: inventory, exact preview, then confirmation.

Opening this window never mutates source files. The executor is reachable only
after a separate confirmation dialog for a fresh, immutable exact-file plan.
"""

from __future__ import annotations

from pathlib import Path
import queue
import threading
import tkinter as tk
from tkinter import ttk

from .library_catalog import LibraryCatalog
from .library_cleanup_plan import CleanupPlanner
from .library_cleanup_execute import CleanupExecutor, confirmation_phrase
from .library_storage_ui import format_bytes
from .library_theme import COLORS


ACTIONS = {"recycle": "移入系统回收站", "mark": "仅标记不采用", "permanent": "永久删除"}
ACTION_NOTES = {
    "mark": "仅修改资料库标记；原文件不变，预计释放 0 B。标记不授予后续删除权限。",
    "recycle": "移入 Windows 系统回收站，可尝试从回收站还原；同盘仍占空间，预计立即释放 0 B。不会清空回收站。",
    "permanent": "永久删除无法从系统回收站还原；预计释放量为保守估算，不等于已释放。科学原片和后期依赖仍受保护。",
}
CATEGORY_LABELS = {"raw": "原始帧", "result": "后期成果", "project": "后期工程", "record": "来源记录",
                   "log": "日志", "preview": "可再生预览", "unknown": "用途未知", "science": "科学原片"}
REASON_LABELS = {
    "immutable_raw_science_photometry_calibration_or_evidence": "原始科学 / 测光 / 校准或证据帧受保护",
    "retained_reduction_product": "保留后期成果", "retained_project_record": "保留后期工程",
    "retained_provenance_or_runtime_record": "保留来源或运行记录", "retained_log": "保留运行日志",
    "unknown_file_purpose": "用途未知，保留复核", "no_explicit_rebuildable_preview_proof": "无明确可重建预览依据",
    "catalog_warning_requires_review": "索引警告需复核", "missing_or_offline": "文件缺失或介质离线",
    "stale_unparsed_or_missing_metadata": "来源记录过期、未解析或缺失",
    "unreadable_reduction_dependency_document": "后期依赖文件无法读取",
    "frame_index_unreadable_or_still_writing": "帧索引无法读取或仍在写入",
    "paired_raw_missing_changed_or_unverified": "对应原始帧缺失、变化或未核验",
    "terminal_run_manifest_unverified": "运行结束记录未核验",
    "active_or_unknown_related_run": "关联运行仍活动或状态未知",
    "reduction_or_legacy_input_dependency": "后期工程或旧处理流程仍依赖",
    "referenced_dependency_or_runtime_model": "仍被依赖记录或运行模型引用",
    "symbolic_link_reparse_or_inaccessible_ancestor": "链接、重解析点或上级目录无法核验",
    "filesystem_access_unverified": "文件系统访问未核验", "not_regular_file": "不是普通文件",
    "hardlink_or_link_count_unverified": "存在硬链接或链接数无法核验",
    "sparse_or_compressed_allocation_unverified": "稀疏 / 压缩文件的实际占用未核验",
    "source_changed_since_index": "文件自索引后发生变化",
}
STATUS_LABELS = {"completed": "完成", "partial": "部分完成", "cancelled": "已取消", "failed": "失败",
                 "rejected": "已拒绝", "recycled": "已移入系统回收站", "deleted": "已永久删除",
                 "marked": "仅标记不采用", "uncertain": "结果未确认"}


def reason_text(row):
    reasons = row.get("reasons", [])
    return "；".join(REASON_LABELS.get(str(code), str(code)) for code in reasons) if reasons else \
        "可由保留的原始文件重建；不会删除原片"


def selectable(item: dict, action: str) -> bool:
    """A review flag may apply to protected data; it never waives protection."""
    return action == "mark" or bool(item.get("candidate") and not item.get("protected"))


def selected_summary(plan: dict | None, selected_ids: set[int], action: str) -> tuple[int, int, int]:
    """Count each catalog identity once, even when many projects share it."""
    rows = {int(row["id"]): row for row in (plan or {}).get("items", [])}
    chosen = [row for file_id, row in rows.items()
              if file_id in selected_ids and selectable(row, action)]
    return (len(chosen), sum(int(row.get("size_bytes", 0)) for row in chosen),
            sum(int(row.get("estimated_release_bytes", 0)) for row in chosen)
            if action == "permanent" else 0)


def _table(parent, columns, *, height=8, selectmode="browse"):
    area = ttk.Frame(parent)
    area.rowconfigure(0, weight=1)
    area.columnconfigure(0, weight=1)
    table = ttk.Treeview(area, columns=tuple(key for key, *_ in columns),
                         show="headings", height=height, selectmode=selectmode)
    for key, title, width in columns:
        table.heading(key, text=title, anchor="w")
        table.column(key, width=width, minwidth=min(width, 90), stretch=key in {"target", "path", "reason"})
    table.grid(row=0, column=0, sticky="nsew")
    vertical = ttk.Scrollbar(area, command=table.yview)
    vertical.grid(row=0, column=1, sticky="ns")
    horizontal = ttk.Scrollbar(area, command=table.xview, orient="horizontal")
    horizontal.grid(row=1, column=0, sticky="ew")
    table.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
    return area, table


class CleanupConfirmation(tk.Toplevel):
    """A second deliberate boundary; constructing it cannot execute a plan."""

    PAGE_SIZE = 100

    def __init__(self, owner, plan: dict):
        super().__init__(owner)
        self.owner = owner
        self.plan = plan
        self.items = [row for row in plan.get("items", []) if row.get("selected")]
        self.offset = 0
        self.accepted = False
        self.title("确认清理 · " + ACTIONS[plan["action"]])
        self.geometry("940x590")
        self.minsize(780, 510)
        self.transient(owner)
        self.configure(background=COLORS["background"])
        self.columnconfigure(0, weight=1)
        self.rowconfigure(2, weight=1)
        header = ttk.Frame(self, padding=(18, 16, 18, 8))
        header.grid(row=0, column=0, sticky="ew")
        ttk.Label(header, text=ACTIONS[plan["action"]], style="Section.TLabel").pack(anchor="w")
        self.warning = ttk.Label(header, text=ACTION_NOTES[plan["action"]],
                                 style="Muted.TLabel", wraplength=850)
        self.warning.pack(anchor="w", fill="x", pady=(5, 0))
        identity = ttk.Frame(self, padding=(18, 0, 18, 8))
        identity.grid(row=1, column=0, sticky="ew")
        ttk.Label(identity, text=f"精确选择 {len(self.items):,} 个文件 · {format_bytes(plan.get('selected_bytes', 0))} · "
                  f"预计立即释放 {format_bytes(plan.get('estimated_release_bytes', 0))}",
                  style="Accent.TLabel").pack(anchor="w")
        listing, self.files = _table(self, (("path", "将处理的完整文件路径", 650),
                                            ("size", "大小", 110)), height=6)
        listing.grid(row=2, column=0, sticky="nsew", padx=18)
        paging = ttk.Frame(self, padding=(18, 4, 18, 6))
        paging.grid(row=3, column=0, sticky="ew")
        self.previous = ttk.Button(paging, text="‹", style="Small.TButton", command=lambda: self.page(-1))
        self.previous.pack(side="left")
        self.next = ttk.Button(paging, text="›", style="Small.TButton", command=lambda: self.page(1))
        self.next.pack(side="left", padx=5)
        self.page_text = ttk.Label(paging, style="Muted.TLabel")
        self.page_text.pack(side="left", padx=8)
        confirmation = ttk.Frame(self, padding=(18, 4, 18, 14))
        confirmation.grid(row=4, column=0, sticky="ew")
        confirmation.columnconfigure(0, weight=1)
        self.cancel_button = ttk.Button(confirmation, text="取消，保留全部文件", command=self.destroy,
                                         style="Quiet.TButton")
        self.cancel_button.grid(row=0, column=0, sticky="e", padx=(0, 10))
        labels = {"recycle": "确认移入回收站", "permanent": "确认永久删除", "mark": "确认仅标记"}
        self.confirm_button = ttk.Button(confirmation, text=labels[plan["action"]], command=self.confirm)
        self.confirm_button.grid(row=0, column=1)
        self.bind("<Escape>", lambda _event: self.destroy())
        self.bind("<Configure>", lambda event: self.warning.configure(wraplength=max(400, event.width - 40))
                  if event.widget is self else None)
        self.page(0)
        self.grab_set()
        self.cancel_button.focus_set()

    def page(self, direction):
        self.offset = max(0, self.offset + direction * self.PAGE_SIZE)
        self.files.delete(*self.files.get_children())
        rows = self.items[self.offset:self.offset + self.PAGE_SIZE]
        for row in rows:
            self.files.insert("", "end", values=(row["path"], format_bytes(row.get("size_bytes", 0))))
        self.page_text.configure(text=f"{self.offset + 1 if rows else 0}–{self.offset + len(rows)} / {len(self.items):,}")
        self.previous.configure(state="normal" if self.offset else "disabled")
        self.next.configure(state="normal" if self.offset + len(rows) < len(self.items) else "disabled")

    def confirm(self):
        if (self.accepted or self.owner.busy or self.owner.consumed or self.owner.plan is not self.plan
                or self.plan.get("action") != self.owner.action.get()):
            return
        self.accepted = True
        self.confirm_button.configure(state="disabled")
        # The user confirms the displayed exact snapshot with one explicit
        # action button. Its integrity token remains an internal executor guard.
        self.owner.execute_confirmed(self.plan, confirmation_phrase(self.plan))
        self.destroy()


class CleanupWindow(tk.Toplevel):
    """The physical-cleanup entry point, with worker-thread inspection only."""

    PAGE_SIZE = 100

    def __init__(self, app, project_id: str | None = None):
        super().__init__(app.root)
        self.app = app
        self.initial_project = project_id
        self.inventory = []
        self.plan = None
        self.selected_ids: set[int] = set()
        self.offset = 0
        self.project_offset = 0
        self.generation = 0
        self.busy = False
        self.confirmation = None
        self.receipt = None
        self.consumed = False
        self.executing = False
        self.cancel_execution = threading.Event()
        self.progress_events = queue.SimpleQueue()
        self.action = tk.StringVar(self, value="recycle")
        self.query = tk.StringVar(self)
        self.sort = tk.StringVar(self, value="candidate")
        self.show_protected = tk.BooleanVar(self, value=True)
        self.status = tk.StringVar(self, value="正在只读统计项目；不会执行清理。")
        self.summary = tk.StringVar(self, value="尚未生成文件清理预览")
        self.mode_note = tk.StringVar(self, value=ACTION_NOTES["recycle"])
        self.path = tk.StringVar(self)
        self.project_identity = tk.StringVar(self, value="选择项目查看完整运行 ID")
        self.file_note = tk.StringVar(self, value="选中文件可查看保护原因并在资源管理器定位。")
        self.title("光谱项目库 · 清理预览")
        self.configure(background=COLORS["background"])
        self.geometry("1100x720")
        self.minsize(920, 640)
        self.transient(app.root)
        self._build()
        self.protocol("WM_DELETE_WINDOW", self.close)
        if not hasattr(app, "cleanup_windows"):
            app.cleanup_windows = []
        app.cleanup_windows.append(self)
        self.bind("<Destroy>", self._forget_window, add=True)
        self.progress_after = self.after(100, self._pump_progress)
        self.refresh_inventory()

    def _build(self):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(2, weight=1)
        header = ttk.Frame(self, padding=(18, 14, 18, 8))
        header.grid(row=0, column=0, sticky="ew")
        ttk.Label(header, text="项目清理", style="Section.TLabel").pack(side="left")
        ttk.Label(header, text="先看占用与保护，再确认精确文件", style="Muted.TLabel").pack(side="left", padx=14)
        self.refresh_button = ttk.Button(header, text="刷新统计", command=self.refresh_inventory)
        self.refresh_button.pack(side="right")
        mode = ttk.Frame(self, padding=(18, 0, 18, 8))
        mode.grid(row=1, column=0, sticky="ew")
        mode.columnconfigure(1, weight=1)
        self.mode_combo = self.app._combo(mode, self.action, ACTIONS, width=18, changed=self.change_mode)
        self.mode_combo.grid(row=0, column=0, sticky="w", padx=(0, 14))
        self.mode_label = ttk.Label(mode, textvariable=self.mode_note, style="Muted.TLabel", wraplength=760)
        self.mode_label.grid(row=0, column=1, sticky="ew")
        mode.bind("<Configure>", lambda event: self.mode_label.configure(wraplength=max(event.width - 230, 350)))
        self.tabs = ttk.Notebook(self, style="Compact.TNotebook")
        self.tabs.grid(row=2, column=0, sticky="nsew", padx=18)
        overview = ttk.Frame(self.tabs, padding=(0, 8, 0, 0))
        overview.rowconfigure(1, weight=1)
        overview.columnconfigure(0, weight=1)
        self.tabs.add(overview, text="项目占用")
        filters = ttk.Frame(overview)
        filters.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        entry = ttk.Entry(filters, textvariable=self.query, width=20)
        entry.pack(side="left")
        entry.bind("<Return>", lambda _event: self.filter_projects())
        ttk.Button(filters, text="搜索", command=self.filter_projects).pack(side="left", padx=5)
        self.app._combo(filters, self.sort, {"candidate": "可清理量优先", "occupied": "项目占用优先"},
                         width=14, changed=self.filter_projects).pack(side="left", padx=6)
        self.preview_button = ttk.Button(filters, text="预览所选项目…", command=self.preview_projects,
                                         state="disabled", style="Primary.TButton")
        self.preview_button.pack(side="right")
        area, self.projects = _table(overview, (
            ("target", "项目 / 运行", 280), ("files", "文件", 70), ("occupied", "索引占用", 120),
            ("candidate", "可清理占用", 120), ("protected", "受保护", 120), ("shared", "跨项目共享", 120),
        ), selectmode="extended")
        area.grid(row=1, column=0, sticky="nsew")
        self.projects.bind("<<TreeviewSelect>>", self.select_project)
        self.projects.bind("<Double-1>", lambda _event: self.preview_projects())
        self.project_paging = self._paging(overview, self.project_page, row=2)
        ttk.Entry(overview, textvariable=self.project_identity, state="readonly", width=1).grid(
            row=3, column=0, sticky="ew", pady=(6, 0))
        ttk.Label(overview, text="按文件身份去重；共享占用不可跨项目相加。这里是索引逻辑大小，精确预览会重新检查文件和依赖。",
                  style="Muted.TLabel", wraplength=960).grid(row=4, column=0, sticky="ew", pady=6)
        self.detail_tab = ttk.Frame(self.tabs, padding=(0, 8, 0, 0))
        self.detail_tab.rowconfigure(1, weight=1)
        self.detail_tab.columnconfigure(0, weight=1)
        self.tabs.add(self.detail_tab, text="文件与保护")
        choices = ttk.Frame(self.detail_tab)
        choices.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        self.select_all = ttk.Button(choices, text="全选可处理项", command=self.select_candidates,
                                     state="disabled")
        self.select_all.pack(side="left")
        self.clear_button = ttk.Button(choices, text="清空勾选", command=self.clear_selection, state="disabled")
        self.clear_button.pack(side="left", padx=5)
        ttk.Checkbutton(choices, text="显示保护项", variable=self.show_protected,
                        command=lambda: self.file_page(0, reset=True)).pack(side="left", padx=12)
        ttk.Label(choices, text="单击“勾选”列切换，空格也可切换", style="Muted.TLabel").pack(side="right")
        area, self.files = _table(self.detail_tab, (
            ("check", "勾选", 65), ("path", "文件", 370), ("size", "大小", 105),
            ("category", "类别", 110), ("reason", "保护 / 可清理依据", 390),
        ))
        area.grid(row=1, column=0, sticky="nsew")
        self.files.tag_configure("protected", foreground=COLORS["muted"])
        self.files.bind("<<TreeviewSelect>>", self.select_file)
        self.files.bind("<ButtonRelease-1>", self.click_file)
        self.files.bind("<space>", lambda _event: self.toggle_file())
        self.file_paging = self._paging(self.detail_tab, self.file_page, row=2)
        self.path_entry = ttk.Entry(self.detail_tab, textvariable=self.path, state="readonly", width=1)
        self.path_entry.grid(row=3, column=0, sticky="ew", pady=(6, 4))
        detail = ttk.Frame(self.detail_tab)
        detail.grid(row=4, column=0, sticky="ew")
        detail.columnconfigure(0, weight=1)
        self.file_note_label = ttk.Label(detail, textvariable=self.file_note, style="Muted.TLabel", wraplength=780)
        self.file_note_label.grid(row=0, column=0, sticky="ew", padx=(0, 8))
        self.reveal_button = ttk.Button(detail, text="定位源文件", command=self.reveal, state="disabled")
        self.reveal_button.grid(row=0, column=1)
        detail.bind("<Configure>", lambda event: self.file_note_label.configure(wraplength=max(400, event.width - 150)))
        footer = ttk.Frame(self, padding=(18, 10, 18, 14))
        footer.grid(row=3, column=0, sticky="ew")
        footer.columnconfigure(0, weight=1)
        self.summary_label = ttk.Label(footer, textvariable=self.summary, style="Accent.TLabel", wraplength=800)
        self.summary_label.grid(row=0, column=0, sticky="ew", padx=(0, 12))
        self.confirm_button = ttk.Button(footer, text="核对并确认…", command=self.request_confirmation,
                                         state="disabled", style="Primary.TButton")
        self.confirm_button.grid(row=0, column=2)
        self.cancel_button = ttk.Button(footer, text="取消剩余操作", command=self.cancel_remaining,
                                        state="disabled", style="Quiet.TButton")
        self.cancel_button.grid(row=0, column=1, padx=(0, 8))
        self.status_label = ttk.Label(footer, textvariable=self.status, style="Muted.TLabel", wraplength=970)
        self.status_label.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(6, 0))
        footer.bind("<Configure>", lambda event: (self.summary_label.configure(wraplength=max(350, event.width - 330)),
                                                 self.status_label.configure(wraplength=max(400, event.width - 8))))

    @staticmethod
    def _paging(parent, command, *, row):
        area = ttk.Frame(parent)
        area.grid(row=row, column=0, sticky="ew", pady=(6, 0))
        previous = ttk.Button(area, text="‹", style="Small.TButton", command=lambda: command(-1))
        previous.pack(side="left")
        next_button = ttk.Button(area, text="›", style="Small.TButton", command=lambda: command(1))
        next_button.pack(side="left", padx=5)
        label = ttk.Label(area, style="Muted.TLabel")
        label.pack(side="left", padx=8)
        return previous, next_button, label

    def _alive(self, generation):
        return self.winfo_exists() and generation == self.generation

    def _work(self, work, done):
        self.generation += 1
        generation = self.generation
        self.busy = True
        self._update_buttons()

        def safe_work():
            try:
                return work(), None
            except Exception as error:
                return None, str(error)

        def safe_done(result):
            if not self._alive(generation):
                return
            self.busy = False
            was_executing = self.executing
            self.executing = False
            value, error = result
            if error:
                if was_executing:
                    self.consumed = True
                    self.selected_ids.clear()
                    self.status.set("清理未完整确认；不自动重试或升级删除。请核对审计回执与文件现状，刷新后再预览：" + error)
                else:
                    self.status.set("预览未完成，未执行文件操作：" + error)
            else:
                done(value)
            self._update_buttons()

        self.app._submit(safe_work, safe_done)

    def refresh_inventory(self):
        if self.busy:
            return
        self.plan = None
        self.consumed = False
        self.selected_ids.clear()
        self.summary.set("正在统计；未生成清理计划，未处理文件。")
        self.status.set("只读统计项目占用、可清理内容和保护项…")
        database = self.app.database

        def work():
            with LibraryCatalog(database) as catalog:
                return CleanupPlanner(catalog).inventory()

        def done(rows):
            self.inventory = rows
            self.project_offset = 0
            self.render_projects()
            self.files.delete(*self.files.get_children())
            self.summary.set("项目统计已完成；尚未生成清理计划，未处理文件。")
            self.status.set("统计完成；选择一个或多个项目生成精确文件预览。共享容量不可重复相加。")
            if self.initial_project:
                project_id, self.initial_project = self.initial_project, None
                indexes = [index for index, row in enumerate(self.filtered_projects())
                           if str(row.get("project_id", row.get("id"))) == project_id]
                if indexes:
                    self.project_offset = indexes[0] // self.PAGE_SIZE * self.PAGE_SIZE
                    self.render_projects()
                    self.projects.selection_set(project_id)
                    self.projects.see(project_id)
                    self.preview_projects()

        self._work(work, done)

    def filtered_projects(self):
        query = self.query.get().strip().casefold()
        rows = [row for row in self.inventory if not query or query in
                f"{row.get('target', '')} {row.get('project_id', row.get('id', ''))}".casefold()]
        key = "candidate_bytes" if self.sort.get() == "candidate" else "indexed_bytes"
        return sorted(rows, key=lambda row: (-int(row.get(key, 0)), str(row.get("project_id", row.get("id")))))

    def filter_projects(self):
        self.project_offset = 0
        self.render_projects()

    def project_page(self, direction):
        self.project_offset = max(0, self.project_offset + direction * self.PAGE_SIZE)
        self.render_projects()

    def render_projects(self):
        from .library_projects_ui import project_label

        rows = self.filtered_projects()
        self.projects.delete(*self.projects.get_children())
        for row in rows[self.project_offset:self.project_offset + self.PAGE_SIZE]:
            project_id = str(row.get("project_id", row.get("id")))
            label = project_label({**row, "id": project_id}) if row.get("target") else "未归属项目"
            if row.get("night"):
                label += " · " + row["night"]
            self.projects.insert("", "end", iid=project_id, values=(label, f"{row.get('file_count', 0):,}",
                format_bytes(row.get("indexed_bytes", 0)), format_bytes(row.get("candidate_bytes", 0)),
                format_bytes(row.get("protected_bytes", 0)), format_bytes(row.get("shared_bytes", 0))))
        self._show_paging(self.project_paging, self.project_offset, len(rows))
        self._update_buttons()

    def select_project(self, _event=None):
        self.project_identity.set("；".join(self.projects.selection()) or "选择项目查看完整运行 ID")
        self._update_buttons()

    def preview_projects(self):
        project_ids = list(self.projects.selection())
        if self.busy or not project_ids:
            return
        action = self.action.get()
        database = self.app.database
        self.status.set("检查精确文件、依赖和当前大小；尚未执行清理…")

        def work():
            with LibraryCatalog(database) as catalog:
                return CleanupPlanner(catalog).preview(project_ids, action=action)

        def done(plan):
            self.plan = plan
            self.receipt = None
            self.consumed = False
            self.selected_ids = {int(item["id"]) for item in plan.get("items", [])
                                 if item.get("selected") and selectable(item, action)}
            self.file_page(0, reset=True)
            self.tabs.select(self.detail_tab)
            self.status.set("这是预览，文件全部保留。可取消勾选；只有下一步精确确认后才会执行。")

        self._work(work, done)

    def change_mode(self):
        if self.executing:
            return
        self.mode_note.set(ACTION_NOTES[self.action.get()])
        self.generation += 1
        self.busy = False
        self.plan = None
        self.selected_ids.clear()
        self.files.delete(*self.files.get_children())
        self.offset = 0
        self._show_paging(self.file_paging, 0, 0)
        self.path.set("")
        self.summary.set("操作已切换；请重新选择项目并生成预览。")
        self.status.set("切换操作不会执行任何文件变更，也不会沿用上一个操作的确认。")
        self._update_buttons()

    def _visible_items(self):
        return [row for row in (self.plan or {}).get("items", [])
                if self.show_protected.get() or selectable(row, self.action.get())]

    def file_page(self, direction, *, reset=False):
        self.offset = 0 if reset else max(0, self.offset + direction * self.PAGE_SIZE)
        rows = self._visible_items()
        self.files.delete(*self.files.get_children())
        for row in rows[self.offset:self.offset + self.PAGE_SIZE]:
            eligible = selectable(row, self.action.get())
            check = "☑" if int(row["id"]) in self.selected_ids and eligible else "☐" if eligible else "—"
            reason = reason_text(row)
            self.files.insert("", "end", iid=str(row["id"]), values=(check, Path(row["path"]).name,
                format_bytes(row.get("size_bytes", 0)), CATEGORY_LABELS.get(row.get("category"), "用途未知"), reason),
                tags=() if eligible else ("protected",))
        self._show_paging(self.file_paging, self.offset, len(rows))
        self._selection_changed()

    def _show_paging(self, controls, offset, count):
        previous, next_button, label = controls
        previous.configure(state="normal" if offset else "disabled")
        next_button.configure(state="normal" if offset + self.PAGE_SIZE < count else "disabled")
        label.configure(text=f"{offset + 1 if count else 0}–{min(offset + self.PAGE_SIZE, count)} / {count:,} · 每页 {self.PAGE_SIZE}")

    def _item(self, file_id):
        return next((row for row in (self.plan or {}).get("items", []) if str(row["id"]) == str(file_id)), None)

    def select_file(self, _event=None):
        selection = self.files.selection()
        row = self._item(selection[0]) if selection else None
        self.path.set(row["path"] if row else "")
        message = reason_text(row) if row else "选择文件查看保护或清理依据。"
        if row and self.receipt:
            result = next((entry for entry in self.receipt.get("results", []) if entry.get("id") == row["id"]), None)
            if result:
                message += f" · 最近操作：{ACTIONS[self.receipt['action']]} / {STATUS_LABELS.get(result.get('status'), '未知')} · {result.get('message', '')}"
        self.file_note.set(message)
        self.reveal_button.configure(state="normal" if row else "disabled")

    def click_file(self, event):
        if self.files.identify_column(event.x) == "#1":
            row_id = self.files.identify_row(event.y)
            if row_id:
                self.toggle_file(row_id)

    def toggle_file(self, file_id=None):
        if self.busy or self.consumed:
            return "break"
        selection = self.files.selection()
        row = self._item(file_id or (selection[0] if selection else ""))
        if row and selectable(row, self.action.get()):
            key = int(row["id"])
            if key in self.selected_ids:
                self.selected_ids.remove(key)
            else:
                self.selected_ids.add(key)
            self.files.set(str(key), "check", "☑" if key in self.selected_ids else "☐")
            self._selection_changed()
        return "break"

    def select_candidates(self):
        if not self.busy and not self.consumed:
            self.selected_ids = {int(row["id"]) for row in (self.plan or {}).get("items", [])
                                 if selectable(row, self.action.get())}
            self.file_page(0)

    def clear_selection(self):
        if not self.busy and not self.consumed:
            self.selected_ids.clear()
            self.file_page(0)

    def _selection_changed(self):
        count, size, release = selected_summary(self.plan, self.selected_ids, self.action.get())
        protected = (self.plan or {}).get("protected_bytes", 0)
        self.summary.set(f"已勾选 {count:,} 个唯一文件 · {format_bytes(size)} · 物理保护 {format_bytes(protected)} · "
                         f"预计立即释放 {format_bytes(release)}")
        self._update_buttons()

    def _update_buttons(self):
        ready = self.plan is not None and not self.busy and not self.consumed
        count = selected_summary(self.plan, self.selected_ids, self.action.get())[0]
        self.confirm_button.configure(state="normal" if ready and count else "disabled")
        self.select_all.configure(state="normal" if ready else "disabled")
        self.clear_button.configure(state="normal" if ready and count else "disabled")
        self.preview_button.configure(state="normal" if not self.busy and self.projects.selection() else "disabled")
        self.refresh_button.configure(state="disabled" if self.busy else "normal")
        self.mode_combo.configure(state="disabled" if self.busy else "readonly")
        self.cancel_button.configure(state="normal" if self.executing else "disabled")

    def request_confirmation(self):
        if self.busy or self.consumed or not self.plan or not self.selected_ids:
            return
        project_ids = list(self.plan.get("project_ids", []))
        action = self.action.get()
        ids = sorted(self.selected_ids)
        database = self.app.database
        self.status.set("确认前再次检查精确选择；检查完成才显示独立确认窗口。")

        def work():
            with LibraryCatalog(database) as catalog:
                return CleanupPlanner(catalog).preview(project_ids, action=action, file_ids=ids)

        def done(plan):
            self.plan = plan
            eligible = {int(row["id"]) for row in plan.get("items", [])
                        if row.get("selected") and selectable(row, action)}
            if eligible != set(ids):
                self.selected_ids = eligible
                self.file_page(0)
                self.status.set("文件或保护状态发生变化，未执行；请先重新核对更新后的预览。")
                return
            self.confirmation = CleanupConfirmation(self, plan)
            self.status.set("等待独立确认；关闭或取消确认窗口将保留所有文件。")

        self._work(work, done)

    def execute_confirmed(self, plan, phrase):
        if self.busy or self.consumed or self.plan is not plan or plan.get("action") != self.action.get():
            return
        self.status.set("正在处理已确认的精确文件；逐项复查，失败不转为更强的删除方式。")
        self.executing = True
        self.cancel_execution.clear()
        database = self.app.database
        cancel_execution, progress_events = self.cancel_execution, self.progress_events

        def work():
            with LibraryCatalog(database) as catalog:
                return CleanupExecutor(catalog, CleanupPlanner(catalog)).execute(
                    plan, phrase, cancelled=cancel_execution.is_set,
                    progress=lambda done, count, item: progress_events.put((done, count, item)))

        def done(receipt):
            self.receipt = receipt
            self.consumed = True
            self.selected_ids.clear()
            errors = "；".join(str(error) for error in receipt.get("errors", [])[:2])
            self.status.set(f"操作{STATUS_LABELS.get(receipt.get('status'), '未知')} · 逻辑移除 {format_bytes(receipt.get('logical_removed_bytes', 0))} · "
                            f"预计释放 {format_bytes(receipt.get('estimated_release_bytes', 0))} · 审计：{receipt.get('audit_path', '')}" +
                            (f" · 需检查：{errors}" if errors else ""))
            self.file_page(0)
            self.app.cached_stats = None
            self.app.refresh()
            self.app.projects.refresh()
            self.select_all.configure(state="disabled")
            self._update_buttons()

        self._work(work, done)

    def _pump_progress(self):
        if not self.winfo_exists():
            return
        latest = None
        while not self.progress_events.empty():
            latest = self.progress_events.get()
        if latest and self.executing:
            done, count, result = latest
            self.status.set(f"已处理 {done:,} / {count:,} · {STATUS_LABELS.get(result.get('status'), '未知')} · "
                            f"{Path(result.get('path', '')).name}；可取消尚未开始的文件，不能撤销已完成操作。")
        self.progress_after = self.after(100, self._pump_progress)

    def cancel_remaining(self):
        if self.executing:
            self.cancel_execution.set()
            self.status.set("已请求取消剩余文件；正在等待当前文件操作及回执完成。已完成的操作不会自动撤销。")

    def _forget_window(self, event):
        if event.widget is self:
            if self in self.app.cleanup_windows:
                self.app.cleanup_windows.remove(self)
            if self.progress_after:
                self.after_cancel(self.progress_after)
                self.progress_after = None

    def reveal(self):
        if self.path.get():
            self.status.set(self.app.reveal_path(self.path.get()))

    def close(self):
        if self.busy:
            if self.executing:
                self.cancel_remaining()
                return
            self.status.set("后台检查或已确认操作进行中；请等待结果后关闭。")
            return
        self.generation += 1
        self.destroy()


def show_cleanup_window(app, project_id: str | None = None) -> CleanupWindow:
    return CleanupWindow(app, project_id)
