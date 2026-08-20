"""Neutral FITS display stretch and an interactive Tk image viewport."""

from __future__ import annotations

from dataclasses import dataclass
import math
import tkinter as tk
from tkinter import ttk

import numpy as np
from PIL import Image, ImageTk


@dataclass(frozen=True)
class FitsPreview:
    """A display-only image plus the robust ADU limits used to create it."""

    image: Image.Image
    low: float
    high: float
    original_shape: tuple[int, int]
    display_shape: tuple[int, int]


def make_neutral_fits_preview(
    data: np.ndarray,
    *,
    low_percentile: float = 1.0,
    high_percentile: float = 99.7,
    asinh_strength: float = 8.0,
    maximum_dimension: int = 4096,
) -> FitsPreview:
    """Return an RGB image whose three channels are identical.

    Percentiles are estimated from a bounded sample, but the stretch is applied
    to the full image.  The result is intentionally neutral grayscale: display
    colour must not imply information that is absent from a monochrome FITS
    frame.
    """

    array = np.asarray(data, dtype=np.float32)
    if array.ndim != 2:
        raise ValueError(f"expected 2D image, got shape {array.shape}")
    if not 0.0 <= low_percentile < high_percentile <= 100.0:
        raise ValueError("display percentiles must be ordered inside [0, 100]")
    if not np.isfinite(asinh_strength) or asinh_strength <= 0.0:
        raise ValueError("asinh_strength must be positive")
    if maximum_dimension < 256:
        raise ValueError("maximum_dimension must be at least 256 pixels")

    statistics_stride = max(1, int(np.ceil(max(array.shape) / 1500.0)))
    statistics_sample = array[::statistics_stride, ::statistics_stride]
    finite = statistics_sample[np.isfinite(statistics_sample)]
    if finite.size < 100:
        raise ValueError("too few finite pixels")

    low, high = np.nanpercentile(finite, (low_percentile, high_percentile))
    if not np.isfinite(low) or not np.isfinite(high) or high <= low:
        raise ValueError("invalid display range")

    safe = np.nan_to_num(array, nan=low, posinf=high, neginf=low)
    normalized = np.clip((safe - low) / (high - low), 0.0, 1.0)
    stretched = np.arcsinh(asinh_strength * normalized) / np.arcsinh(asinh_strength)
    monochrome = Image.fromarray(np.asarray(stretched * 255.0 + 0.5, dtype=np.uint8))

    if max(monochrome.size) > maximum_dimension:
        resampling = getattr(Image, "Resampling", Image).LANCZOS
        monochrome.thumbnail((maximum_dimension, maximum_dimension), resampling)

    rgb = monochrome.convert("RGB")
    return FitsPreview(
        image=rgb,
        low=float(low),
        high=float(high),
        original_shape=(int(array.shape[0]), int(array.shape[1])),
        display_shape=(int(rgb.height), int(rgb.width)),
    )


class InteractiveImageViewer(ttk.Frame):
    """Scrollable image canvas with cursor-centred zoom and drag-to-pan."""

    _MAX_RENDER_PIXELS = 36_000_000
    _ZOOM_FACTOR = 1.25

    def __init__(
        self,
        parent: tk.Misc,
        *,
        placeholder: str,
        background: str = "#070a0f",
        foreground: str = "#93a4b8",
    ) -> None:
        super().__init__(parent)
        self._background = background
        self._foreground = foreground
        self._placeholder = placeholder
        self._image: Image.Image | None = None
        self._photo: ImageTk.PhotoImage | None = None
        self._image_item: int | None = None
        self._message_item: int | None = None
        self._scale = 1.0
        self._fit_scale = 1.0
        self._fit_mode = True
        self._source = ""
        self._image_origin = (0.0, 0.0)
        self._resize_job: str | None = None

        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)

        toolbar = ttk.Frame(self)
        toolbar.grid(row=0, column=0, columnspan=2, sticky=tk.EW, pady=(0, 5))
        ttk.Button(toolbar, text="−", width=3, command=self.zoom_out).pack(side=tk.LEFT)
        ttk.Button(toolbar, text="+", width=3, command=self.zoom_in).pack(side=tk.LEFT, padx=(4, 0))
        ttk.Button(toolbar, text="适配", width=6, command=self.fit_to_window).pack(
            side=tk.LEFT, padx=(8, 0)
        )
        ttk.Button(toolbar, text="1:1", width=5, command=self.actual_pixels).pack(
            side=tk.LEFT, padx=(4, 0)
        )
        self._zoom_label = ttk.Label(toolbar, text="适配")
        self._zoom_label.pack(side=tk.LEFT, padx=(10, 0))
        ttk.Label(toolbar, text="滚轮缩放 · 左键拖动 · 双击适配").pack(side=tk.RIGHT)

        self.canvas = tk.Canvas(
            self,
            background=background,
            highlightthickness=0,
            borderwidth=0,
            cursor="arrow",
        )
        self.canvas.grid(row=1, column=0, sticky=tk.NSEW)
        vertical = ttk.Scrollbar(self, orient=tk.VERTICAL, command=self._yview)
        vertical.grid(row=1, column=1, sticky=tk.NS)
        horizontal = ttk.Scrollbar(self, orient=tk.HORIZONTAL, command=self._xview)
        horizontal.grid(row=2, column=0, sticky=tk.EW)
        self.canvas.configure(xscrollcommand=horizontal.set, yscrollcommand=vertical.set)

        self._status = ttk.Label(self, text=placeholder, anchor=tk.W)
        self._status.grid(row=3, column=0, columnspan=2, sticky=tk.EW, pady=(4, 0))

        self.canvas.bind("<Configure>", self._on_configure)
        self.canvas.bind("<MouseWheel>", self._on_mousewheel)
        self.canvas.bind("<Button-4>", lambda event: self._zoom_from_event(event, 1))
        self.canvas.bind("<Button-5>", lambda event: self._zoom_from_event(event, -1))
        self.canvas.bind("<ButtonPress-1>", self._start_pan)
        self.canvas.bind("<B1-Motion>", self._drag_pan)
        self.canvas.bind("<ButtonRelease-1>", self._end_pan)
        self.canvas.bind("<Double-Button-1>", lambda _event: self.fit_to_window())
        self.show_message(placeholder)

    @property
    def scale(self) -> float:
        return self._scale

    def set_image(self, image: Image.Image, *, source: str = "") -> None:
        self._image = image.convert("RGB").copy()
        self._source = source
        self._fit_mode = True
        self.canvas.delete("all")
        self._image_item = None
        self._message_item = None
        self._photo = None
        self.after_idle(self.fit_to_window)

    def show_message(self, text: str) -> None:
        self._image = None
        self._photo = None
        self._image_item = None
        self.canvas.delete("all")
        self._message_item = self.canvas.create_text(
            max(self.canvas.winfo_width() / 2, 1),
            max(self.canvas.winfo_height() / 2, 1),
            text=text,
            fill=self._foreground,
            justify=tk.CENTER,
            width=max(self.canvas.winfo_width() - 40, 160),
        )
        self.canvas.configure(scrollregion=(0, 0, 1, 1))
        self._zoom_label.configure(text="—")
        self._status.configure(text=text.replace("\n", " · "))

    def fit_to_window(self) -> None:
        if self._image is None:
            return
        self.update_idletasks()
        width = max(self.canvas.winfo_width() - 4, 1)
        height = max(self.canvas.winfo_height() - 4, 1)
        self._fit_scale = min(width / self._image.width, height / self._image.height, 1.0)
        self._scale = max(self._fit_scale, 0.01)
        self._fit_mode = True
        self._render()
        self.canvas.xview_moveto(0.0)
        self.canvas.yview_moveto(0.0)

    def actual_pixels(self) -> None:
        if self._image is None:
            return
        self._fit_mode = False
        self._zoom_to(1.0, self.canvas.winfo_width() / 2, self.canvas.winfo_height() / 2)

    def zoom_in(self) -> None:
        self._zoom_about_centre(self._ZOOM_FACTOR)

    def zoom_out(self) -> None:
        self._zoom_about_centre(1.0 / self._ZOOM_FACTOR)

    def _zoom_about_centre(self, factor: float) -> None:
        if self._image is None:
            return
        self._zoom_to(
            self._scale * factor,
            self.canvas.winfo_width() / 2,
            self.canvas.winfo_height() / 2,
        )

    def _on_mousewheel(self, event: tk.Event) -> str:
        steps = int(event.delta / 120) if event.delta else 0
        if steps == 0:
            steps = 1 if event.delta > 0 else -1
        self._zoom_from_event(event, steps)
        return "break"

    def _zoom_from_event(self, event: tk.Event, steps: int) -> str:
        if self._image is not None:
            self._zoom_to(
                self._scale * (self._ZOOM_FACTOR**steps),
                float(event.x),
                float(event.y),
            )
        return "break"

    def _zoom_to(self, requested: float, anchor_x: float, anchor_y: float) -> None:
        if self._image is None:
            return
        old_scale = self._scale
        old_origin_x, old_origin_y = self._image_origin
        world_x = self.canvas.canvasx(anchor_x)
        world_y = self.canvas.canvasy(anchor_y)
        source_x = np.clip((world_x - old_origin_x) / old_scale, 0.0, self._image.width)
        source_y = np.clip((world_y - old_origin_y) / old_scale, 0.0, self._image.height)

        pixel_limit_scale = math.sqrt(
            self._MAX_RENDER_PIXELS / max(self._image.width * self._image.height, 1)
        )
        minimum = max(min(self._fit_scale * 0.25, 0.25), 0.01)
        maximum = max(min(pixel_limit_scale, 8.0), 1.0)
        self._scale = float(np.clip(requested, minimum, maximum))
        self._fit_mode = False
        self._render()

        origin_x, origin_y = self._image_origin
        desired_x = origin_x + float(source_x) * self._scale - anchor_x
        desired_y = origin_y + float(source_y) * self._scale - anchor_y
        scroll_width, scroll_height = self._scroll_dimensions()
        self.canvas.xview_moveto(max(desired_x, 0.0) / max(scroll_width, 1.0))
        self.canvas.yview_moveto(max(desired_y, 0.0) / max(scroll_height, 1.0))

    def _render(self) -> None:
        if self._image is None:
            return
        width = max(int(round(self._image.width * self._scale)), 1)
        height = max(int(round(self._image.height * self._scale)), 1)
        resampling_namespace = getattr(Image, "Resampling", Image)
        resampling = resampling_namespace.LANCZOS if self._scale < 1.0 else resampling_namespace.NEAREST
        rendered = self._image.resize((width, height), resampling)
        self._photo = ImageTk.PhotoImage(rendered)

        canvas_width = max(self.canvas.winfo_width(), 1)
        canvas_height = max(self.canvas.winfo_height(), 1)
        origin_x = max((canvas_width - width) / 2.0, 0.0)
        origin_y = max((canvas_height - height) / 2.0, 0.0)
        self._image_origin = (origin_x, origin_y)
        scroll_width = max(float(width), float(canvas_width))
        scroll_height = max(float(height), float(canvas_height))
        self.canvas.configure(scrollregion=(0, 0, scroll_width, scroll_height))

        if self._image_item is None:
            self._image_item = self.canvas.create_image(
                origin_x,
                origin_y,
                image=self._photo,
                anchor=tk.NW,
            )
        else:
            self.canvas.itemconfigure(self._image_item, image=self._photo)
            self.canvas.coords(self._image_item, origin_x, origin_y)

        self._zoom_label.configure(text=f"{self._scale * 100:.0f}%")
        dimensions = f"{self._image.width}×{self._image.height} px"
        self._status.configure(text=f"{dimensions} · {self._source}" if self._source else dimensions)

    def _scroll_dimensions(self) -> tuple[float, float]:
        region = str(self.canvas.cget("scrollregion")).split()
        if len(region) == 4:
            return float(region[2]) - float(region[0]), float(region[3]) - float(region[1])
        return 1.0, 1.0

    def _xview(self, *args: str) -> None:
        self.canvas.xview(*args)

    def _yview(self, *args: str) -> None:
        self.canvas.yview(*args)

    def _start_pan(self, event: tk.Event) -> None:
        if self._image is None:
            return
        self.canvas.scan_mark(event.x, event.y)
        self.canvas.configure(cursor="fleur")

    def _drag_pan(self, event: tk.Event) -> None:
        if self._image is not None:
            self.canvas.scan_dragto(event.x, event.y, gain=1)

    def _end_pan(self, _event: tk.Event) -> None:
        self.canvas.configure(cursor="arrow")

    def _on_configure(self, _event: tk.Event) -> None:
        if self._resize_job is not None:
            self.after_cancel(self._resize_job)
        self._resize_job = self.after(80, self._finish_resize)

    def _finish_resize(self) -> None:
        self._resize_job = None
        if self._image is None:
            if self._message_item is not None:
                self.canvas.coords(
                    self._message_item,
                    max(self.canvas.winfo_width() / 2, 1),
                    max(self.canvas.winfo_height() / 2, 1),
                )
                self.canvas.itemconfigure(
                    self._message_item,
                    width=max(self.canvas.winfo_width() - 40, 160),
                )
            return
        if self._fit_mode:
            self.fit_to_window()
        else:
            self._render()
