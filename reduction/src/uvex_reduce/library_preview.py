"""On-demand, memory-only library previews. Never modify the source observation."""

from __future__ import annotations

import csv
from dataclasses import dataclass
import io
from pathlib import Path

import numpy as np
from PIL import Image

from .image_viewer import make_neutral_fits_preview


@dataclass
class LibraryPreview:
    image: Image.Image | None = None
    text: str = ""
    caption: str = ""


def _plot(x, y, *, xlabel: str, ylabel: str, title: str, invert_y=False) -> Image.Image:
    from matplotlib.backends.backend_agg import FigureCanvasAgg
    from matplotlib.figure import Figure

    figure = Figure(figsize=(8, 4), dpi=120, facecolor="#101b28")
    FigureCanvasAgg(figure)
    axes = figure.add_subplot(111, facecolor="#101b28")
    axes.plot(x, y, color="#55d5d0", linewidth=0.8)
    axes.set(xlabel=xlabel, ylabel=ylabel, title=title)
    if invert_y:
        axes.invert_yaxis()
    axes.tick_params(colors="#bbcbdc")
    for spine in axes.spines.values():
        spine.set_color("#506074")
    for item in (axes.xaxis.label, axes.yaxis.label, axes.title):
        item.set_color("#e0e8f1")
    axes.grid(alpha=0.15)
    figure.tight_layout()
    buffer = io.BytesIO()
    figure.savefig(buffer, format="png")
    buffer.seek(0)
    with Image.open(buffer) as image:
        return image.convert("RGB").copy()


def load_library_preview(path: str | Path) -> LibraryPreview:
    """Load one selected file only; no persistent thumbnails or eager FITS scans."""
    source = Path(path)
    suffix = source.suffix.lower()
    if suffix in {".png", ".jpg", ".jpeg", ".tif", ".tiff"}:
        with Image.open(source) as image:
            image.thumbnail((1600, 1600))
            return LibraryPreview(image=image.convert("RGB").copy(), caption=source.name)
    if suffix in {".fit", ".fits", ".fts"}:
        return _fits_preview(source)
    if suffix in {".csv", ".tsv"}:
        result = _curve_preview(source)
        if result is not None:
            return result
    with source.open("r", encoding="utf-8-sig", errors="replace") as stream:
        text = stream.read(32_001)
    if len(text) > 32_000:
        text = text[:32_000] + "\n\n[预览已截断；原文件未改动]"
    return LibraryPreview(text=text, caption=f"{source.name} · 只读文本预览")


def _fits_preview(source: Path) -> LibraryPreview:
    from astropy.io import fits

    with fits.open(source, memmap=False) as hdus:
        if "SPECTRUM" in hdus:
            table = hdus["SPECTRUM"]
            if "FLUX" not in table.columns.names:
                # Final response/continuum products have a different established
                # contract from the extraction-stage FLUX/UNCERTAINTY product.
                field = next(
                    (
                        key
                        for key in ("RELATIVE_FLUX", "NORMALIZED_FLUX")
                        if key in table.columns.names
                    ),
                    None,
                )
                if field is None or "WAVELENGTH" not in table.columns.names:
                    raise ValueError("Unsupported SPECTRUM columns; no flux semantics inferred")
                flux = np.asarray(table.data[field], dtype=float).copy()
                if "MASK" in table.columns.names:
                    flux[np.asarray(table.data["MASK"], dtype=bool)] = np.nan
                unit = table.columns[field].unit or "dimensionless"
                risk = "ORDER2_RISK" in table.columns.names and np.any(table.data["ORDER2_RISK"])
                return LibraryPreview(
                    image=_plot(
                        table.data["WAVELENGTH"],
                        flux,
                        xlabel=table.columns["WAVELENGTH"].unit or "Angstrom",
                        ylabel=f"{field} ({unit})",
                        title=source.stem,
                    ),
                    text=repr(hdus[0].header),
                    caption="已有相对/归一化光谱 · 非绝对流量；保留掩膜"
                    + (" · 含二级光谱风险区（未裁掉）" if risk else ""),
                )
            # Use the established product reader, including its uncertainty/mask contract.
            from .products import read_spectrum

            spectrum = read_spectrum(source)
            flux = np.asarray(spectrum.flux.value, dtype=float).copy()
            if spectrum.mask is not None:
                flux[np.asarray(spectrum.mask, dtype=bool)] = np.nan
            return LibraryPreview(
                image=_plot(
                    spectrum.spectral_axis.value,
                    flux,
                    xlabel=str(spectrum.spectral_axis.unit),
                    ylabel=str(spectrum.flux.unit),
                    title=source.stem,
                ),
                text=repr(hdus[0].header),
                caption="已有 1D 光谱产品 · 保留像素/波长单位与掩膜；未重新处理",
            )
        # Read a bounded FITS section, not the full QHY array followed by several
        # full-size float stretch buffers. This is a display sample, not reduction.
        shape = hdus[0].shape
        header = repr(hdus[0].header)
        if shape is not None and len(shape) == 2:
            stride = max(1, int(np.ceil(max(shape) / 1600)))
            # Contiguous columns avoid Astropy issuing a tiny read for each pixel.
            # Row selection bounds memory for the supported camera image sizes.
            if shape[1] > 32_768:
                raise ValueError(
                    "Image row exceeds bounded preview width; header remains inspectable"
                )
            data = hdus[0].section[::stride, :][:, ::stride]
            preview = make_neutral_fits_preview(data, maximum_dimension=1600)
            return LibraryPreview(
                image=preview.image,
                text=header,
                caption=f"原始 2D · 显示采样 1/{stride} · 仅拉伸 {preview.low:.1f}–{preview.high:.1f} ADU",
            )
        if shape is not None and len(shape) == 1:
            stride = max(1, int(np.ceil(shape[0] / 20_000)))
            data = hdus[0].section[::stride]
            return LibraryPreview(
                image=_plot(
                    np.arange(data.size) * stride,
                    data,
                    xlabel="pixel (no WCS applied)",
                    ylabel=str(hdus[0].header.get("BUNIT", "stored value")),
                    title=source.stem,
                ),
                text=header,
                caption=f"1D 数组预览 · 采样 1/{stride} · 未解释波长标定",
            )
        return LibraryPreview(text=header, caption="FITS Header · 无支持的图像 HDU")


def _curve_preview(source: Path) -> LibraryPreview | None:
    with source.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t" if source.suffix.lower() == ".tsv" else ",")
        names = {name.strip().lower(): name for name in reader.fieldnames or []}
        xkey = next(
            (
                names[k]
                for k in (
                    "bjd",
                    "bjd_tdb",
                    "jd",
                    "mjd",
                    "time",
                    "wavelength",
                    "wavelength_angstrom",
                    "pixel",
                )
                if k in names
            ),
            None,
        )
        ykey = next(
            (
                names[k]
                for k in (
                    "differential_mag",
                    "relative_flux",
                    "relative_flux_adu_per_s",
                    "normalized_flux",
                    "magnitude",
                    "mag",
                    "flux",
                    "flux_adu",
                )
                if k in names
            ),
            None,
        )
        if xkey is None or ykey is None:
            return None
        x, y = [], []
        truncated = False
        for index, row in enumerate(reader):
            if index >= 20_000:
                truncated = True
                break
            try:
                a, b = float(row[xkey]), float(row[ykey])
            except (TypeError, ValueError, KeyError):
                continue
            if np.isfinite(a) and np.isfinite(b):
                x.append(a)
                # Keep gaps: masking a spectrum must not visually reconnect rejected samples.
                masked = str(row.get(names.get("mask", ""), "")).strip().lower()
                y.append(np.nan if masked in {"1", "true", "yes"} else b)
    if not x:
        return None
    return LibraryPreview(
        image=_plot(
            x, y, xlabel=xkey, ylabel=ykey, title=source.stem, invert_y="mag" in ykey.lower()
        ),
        caption=f"已有表格产品 · {len(x)} 点" + (" · 仅前 20000 行" if truncated else ""),
        text=f"横轴：{xkey}\n纵轴：{ykey}\n按列名展示；不推断定标质量或时间制。",
    )
