"""One deliberately complete dark palette for the offline library's Tk surface."""

from tkinter import ttk

COLORS = {
    "background": "#10161e",
    "panel": "#18212c",
    "input": "#202c3a",
    "border": "#334255",
    "text": "#e8eef6",
    "muted": "#a9b8ca",
    "accent": "#72ddc5",
    "accent_text": "#10251f",
    "selected": "#28574f",
    "hover": "#2e4052",
    "disabled": "#8190a3",
}


def apply_library_theme(root):
    c = COLORS
    style = ttk.Style(root)
    style.theme_use("clam")
    font = ("Microsoft YaHei UI", 10)
    style.configure(".", background=c["background"], foreground=c["text"], font=font,
                    bordercolor=c["border"], lightcolor=c["border"], darkcolor=c["border"],
                    troughcolor=c["background"], focuscolor=c["accent"])
    style.configure("TFrame", background=c["background"])
    style.configure("Panel.TFrame", background=c["panel"])
    style.configure("TLabel", padding=0)
    style.configure("Muted.TLabel", foreground=c["muted"])
    style.configure("Title.TLabel", font=("Microsoft YaHei UI", 21, "bold"))
    style.configure("Section.TLabel", font=("Microsoft YaHei UI", 10, "bold"))
    style.configure("Accent.TLabel", foreground=c["accent"])
    style.configure("TButton", background=c["input"], borderwidth=0, relief="flat",
                    padding=(12, 7), focuscolor=c["accent"], focusthickness=1, width=0)
    style.map("TButton", background=[("disabled", c["panel"]), ("active", c["hover"])],
              foreground=[("disabled", c["disabled"])],
              bordercolor=[("focus", c["accent"])])
    style.configure("Primary.TButton", background=c["accent"], foreground=c["accent_text"])
    style.map("Primary.TButton",
              background=[("disabled", c["input"]), ("active", "#98ead8")],
              foreground=[("disabled", c["disabled"]), ("!disabled", c["accent_text"])])
    style.configure("Quiet.TButton", background=c["background"], foreground=c["muted"])
    style.configure("Small.TButton", padding=(7, 4), width=3)
    for name in ("TEntry", "TCombobox", "TSpinbox"):
        style.configure(name, fieldbackground=c["input"], foreground=c["text"],
                        background=c["input"], insertcolor=c["text"],
                        selectbackground=c["selected"], selectforeground=c["text"],
                        borderwidth=1, padding=6, relief="flat", arrowsize=12,
                        arrowcolor=c["muted"])
        style.map(name, fieldbackground=[("disabled", c["panel"]), ("readonly", c["input"])],
                  foreground=[("disabled", c["disabled"]), ("readonly", c["text"])],
                  background=[("active", c["hover"]), ("readonly", c["input"])],
                  bordercolor=[("focus", c["accent"]), ("!focus", c["border"])],
                  lightcolor=[("focus", c["accent"]), ("!focus", c["border"])],
                  darkcolor=[("focus", c["accent"]), ("!focus", c["border"])],
                  selectbackground=[("!disabled", c["selected"])],
                  selectforeground=[("!disabled", c["text"])])
    # The popup is a classic Tk Listbox, not the themed combobox field.
    for option, value in {
        "background": c["input"], "foreground": c["text"],
        "selectBackground": c["selected"], "selectForeground": c["text"],
        "font": font, "relief": "flat", "borderWidth": 0,
    }.items():
        root.option_add(f"*TCombobox*Listbox.{option}", value)
    style.configure("Treeview", background=c["panel"], fieldbackground=c["panel"],
                    foreground=c["text"], rowheight=34, borderwidth=0, relief="flat")
    style.map("Treeview", background=[("selected", c["selected"])],
              foreground=[("selected", "#ffffff")])
    style.configure("Treeview.Heading", background=c["input"], foreground=c["muted"],
                    relief="flat", borderwidth=0, padding=(9, 9),
                    font=("Microsoft YaHei UI", 9, "bold"))
    style.map("Treeview.Heading", background=[("active", c["hover"])])
    style.configure("TNotebook", background=c["background"], borderwidth=0, tabmargins=(0, 0, 0, 8))
    style.configure("TNotebook.Tab", background=c["background"], foreground=c["muted"],
                    padding=(14, 8), borderwidth=0, lightcolor=c["background"],
                    darkcolor=c["background"], bordercolor=c["background"])
    style.map("TNotebook.Tab", background=[("selected", c["input"]), ("active", c["panel"])],
              foreground=[("selected", c["accent"])],
              lightcolor=[("selected", c["input"]), ("!selected", c["background"])],
              padding=[("selected", (14, 8))])
    style.configure("Compact.TNotebook", tabmargins=(0, 0, 0, 4))
    style.configure("Compact.TNotebook.Tab", padding=(9, 4))
    style.map("Compact.TNotebook.Tab", padding=[("selected", (9, 4))])
    for name in ("Vertical.TScrollbar", "Horizontal.TScrollbar"):
        style.configure(name, background=c["border"], troughcolor=c["panel"],
                        borderwidth=0, arrowsize=11, arrowcolor=c["muted"],
                        lightcolor=c["border"], darkcolor=c["border"], gripcount=0)
        style.map(name, background=[("active", c["hover"]), ("pressed", c["muted"])])
    style.configure("TPanedwindow", background=c["background"], sashwidth=10)
    style.configure("TSeparator", background=c["border"])
    root.configure(background=c["background"])
    return style
