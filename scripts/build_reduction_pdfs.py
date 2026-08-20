"""Build the shareable 3C 273 report and manual reduction SOP PDFs."""

from __future__ import annotations

import argparse
import html
from pathlib import Path
import re

from PIL import Image as PilImage
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    BaseDocTemplate,
    Frame,
    HRFlowable,
    Image,
    KeepTogether,
    LongTable,
    PageBreak,
    PageTemplate,
    Paragraph,
    Preformatted,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(__file__).resolve().parents[1]
REDUCTION = ROOT / "reduction"
OUTPUT = ROOT / "output" / "pdf"

REPORT_SOURCE = REDUCTION / "docs" / "20260504-3c273-processing-hbeta-report.md"
SOP_SOURCE = REDUCTION / "docs" / "manual-reduction-sop.md"
REPORT_OUTPUT = OUTPUT / "UVEX-ADV_20260504_3C273_Report_zh-CN.pdf"
SOP_OUTPUT = OUTPUT / "UVEX-ADV_Manual_Reduction_SOP_zh-CN.pdf"

FONT_REGULAR = Path(r"C:\Windows\Fonts\msyh.ttc")
FONT_BOLD = Path(r"C:\Windows\Fonts\msyhbd.ttc")
FONT_MONO = Path(r"C:\Windows\Fonts\consola.ttf")

NAVY = colors.HexColor("#0b1624")
TEAL = colors.HexColor("#0f766e")
TEAL_LIGHT = colors.HexColor("#d8f3ef")
BLUE = colors.HexColor("#2563a6")
TEXT = colors.HexColor("#172333")
MUTED = colors.HexColor("#53657a")
GRID = colors.HexColor("#cad4df")
PANEL = colors.HexColor("#f1f5f9")
WARNING = colors.HexColor("#fff4ce")


def register_fonts() -> None:
    pdfmetrics.registerFont(TTFont("MSYH", str(FONT_REGULAR), subfontIndex=0))
    pdfmetrics.registerFont(TTFont("MSYH-Bold", str(FONT_BOLD), subfontIndex=0))
    pdfmetrics.registerFont(TTFont("Consolas", str(FONT_MONO)))
    pdfmetrics.registerFontFamily(
        "MSYH",
        normal="MSYH",
        bold="MSYH-Bold",
        italic="MSYH",
        boldItalic="MSYH-Bold",
    )


def build_styles() -> dict[str, ParagraphStyle]:
    base = getSampleStyleSheet()
    return {
        "cover_title": ParagraphStyle(
            "CoverTitle",
            parent=base["Title"],
            fontName="MSYH-Bold",
            fontSize=25,
            leading=35,
            textColor=NAVY,
            alignment=TA_LEFT,
            spaceAfter=12,
        ),
        "cover_kicker": ParagraphStyle(
            "CoverKicker",
            parent=base["Normal"],
            fontName="MSYH-Bold",
            fontSize=10,
            leading=15,
            textColor=TEAL,
            tracking=1.2,
            spaceAfter=10,
        ),
        "cover_meta": ParagraphStyle(
            "CoverMeta",
            parent=base["Normal"],
            fontName="MSYH",
            fontSize=9.5,
            leading=16,
            textColor=MUTED,
        ),
        "h1": ParagraphStyle(
            "Heading1Custom",
            parent=base["Heading1"],
            fontName="MSYH-Bold",
            fontSize=17,
            leading=24,
            textColor=NAVY,
            spaceBefore=13,
            spaceAfter=8,
            keepWithNext=True,
        ),
        "h2": ParagraphStyle(
            "Heading2Custom",
            parent=base["Heading2"],
            fontName="MSYH-Bold",
            fontSize=12.5,
            leading=19,
            textColor=TEAL,
            spaceBefore=10,
            spaceAfter=6,
            keepWithNext=True,
        ),
        "h3": ParagraphStyle(
            "Heading3Custom",
            parent=base["Heading3"],
            fontName="MSYH-Bold",
            fontSize=10.5,
            leading=16,
            textColor=BLUE,
            spaceBefore=8,
            spaceAfter=4,
            keepWithNext=True,
        ),
        "body": ParagraphStyle(
            "BodyCustom",
            parent=base["BodyText"],
            fontName="MSYH",
            fontSize=9.1,
            leading=15.2,
            textColor=TEXT,
            alignment=TA_LEFT,
            spaceAfter=6,
            wordWrap="CJK",
        ),
        "bullet": ParagraphStyle(
            "BulletCustom",
            parent=base["BodyText"],
            fontName="MSYH",
            fontSize=8.9,
            leading=14.5,
            textColor=TEXT,
            leftIndent=15,
            firstLineIndent=-9,
            bulletIndent=5,
            spaceAfter=3,
            wordWrap="CJK",
        ),
        "caption": ParagraphStyle(
            "CaptionCustom",
            parent=base["Normal"],
            fontName="MSYH",
            fontSize=7.8,
            leading=11,
            textColor=MUTED,
            alignment=TA_CENTER,
            spaceBefore=3,
            spaceAfter=8,
        ),
        "table": ParagraphStyle(
            "TableCell",
            parent=base["Normal"],
            fontName="MSYH",
            fontSize=7.4,
            leading=10.5,
            textColor=TEXT,
            wordWrap="CJK",
        ),
        "table_header": ParagraphStyle(
            "TableHeader",
            parent=base["Normal"],
            fontName="MSYH-Bold",
            fontSize=7.6,
            leading=11,
            textColor=colors.white,
            wordWrap="CJK",
        ),
        "code": ParagraphStyle(
            "CodeCustom",
            parent=base["Code"],
            fontName="Consolas",
            fontSize=7.5,
            leading=11,
            textColor=TEXT,
            backColor=PANEL,
            borderPadding=7,
        ),
    }


class StructuredDocument(BaseDocTemplate):
    def __init__(self, *args, short_title: str, **kwargs):
        self.short_title = short_title
        self._outline_counter = 0
        super().__init__(*args, **kwargs)

    def afterFlowable(self, flowable) -> None:  # noqa: N802 - ReportLab API
        if not isinstance(flowable, Paragraph):
            return
        level_by_style = {
            "Heading1Custom": 0,
            "Heading2Custom": 1,
            "Heading3Custom": 2,
        }
        level = level_by_style.get(flowable.style.name)
        if level is None:
            return
        self._outline_counter += 1
        key = f"section-{self._outline_counter}"
        title = flowable.getPlainText()
        self.canv.bookmarkPage(key)
        self.canv.addOutlineEntry(title, key, level=level, closed=level > 0)


def inline_markup(value: str) -> str:
    text = html.escape(value.strip())
    text = re.sub(
        r"\[([^\]]+)\]\((https?://[^)]+)\)",
        r'<link href="\2" color="#2563a6"><u>\1</u></link>',
        text,
    )
    text = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", text)
    text = re.sub(r"`([^`]+)`", r'<font name="Consolas" color="#0f766e">\1</font>', text)
    return text


def page_decor(canvas, document: StructuredDocument) -> None:
    canvas.saveState()
    width, height = A4
    if document.page > 1:
        canvas.setFillColor(NAVY)
        canvas.rect(0, height - 1.05 * cm, width, 1.05 * cm, fill=1, stroke=0)
        canvas.setFont("MSYH-Bold", 8)
        canvas.setFillColor(colors.white)
        canvas.drawString(1.55 * cm, height - 0.69 * cm, document.short_title)
        canvas.setFillColor(TEAL)
        canvas.rect(0, height - 1.05 * cm, 0.25 * cm, 1.05 * cm, fill=1, stroke=0)
    canvas.setStrokeColor(GRID)
    canvas.line(1.55 * cm, 1.20 * cm, width - 1.55 * cm, 1.20 * cm)
    canvas.setFillColor(MUTED)
    canvas.setFont("MSYH", 7.5)
    canvas.drawString(1.55 * cm, 0.77 * cm, "OpenAstroSpec — UVEX4  ·  可审计降级处理")
    canvas.drawRightString(width - 1.55 * cm, 0.77 * cm, f"第 {document.page} 页")
    canvas.restoreState()


def fit_image(path: Path, max_width: float = 17.4 * cm, max_height: float = 10.8 * cm) -> Image:
    with PilImage.open(path) as image:
        width, height = image.size
    scale = min(max_width / width, max_height / height)
    return Image(str(path), width=width * scale, height=height * scale)


def image_block(path: Path, caption: str, styles, *, max_height: float = 10.8 * cm):
    if not path.is_file():
        return []
    return [
        KeepTogether(
            [
                Spacer(1, 4),
                fit_image(path, max_height=max_height),
                Paragraph(inline_markup(caption), styles["caption"]),
            ]
        )
    ]


def table_flowable(rows: list[list[str]], styles, available_width: float) -> LongTable:
    column_count = max(len(row) for row in rows)
    normalized = [row + [""] * (column_count - len(row)) for row in rows]
    formatted = []
    for row_index, row in enumerate(normalized):
        style = styles["table_header"] if row_index == 0 else styles["table"]
        formatted.append([Paragraph(inline_markup(cell), style) for cell in row])
    if column_count == 2:
        widths = [available_width * 0.34, available_width * 0.66]
    elif column_count == 3:
        widths = [available_width * 0.23, available_width * 0.25, available_width * 0.52]
    else:
        widths = [available_width / column_count] * column_count
    table = LongTable(formatted, colWidths=widths, repeatRows=1, hAlign="LEFT")
    commands = [
        ("BACKGROUND", (0, 0), (-1, 0), TEAL),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("GRID", (0, 0), (-1, -1), 0.35, GRID),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 5),
        ("RIGHTPADDING", (0, 0), (-1, -1), 5),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]
    for row_index in range(1, len(formatted)):
        if row_index % 2 == 0:
            commands.append(("BACKGROUND", (0, row_index), (-1, row_index), PANEL))
    table.setStyle(TableStyle(commands))
    return table


def cover_story(title: str, metadata: list[str], styles, cover_image: Path, document_label: str):
    story = [Spacer(1, 1.0 * cm)]
    story.append(Paragraph(document_label.upper(), styles["cover_kicker"]))
    story.append(Paragraph(inline_markup(title), styles["cover_title"]))
    story.append(HRFlowable(width="100%", thickness=2.4, color=TEAL, spaceAfter=12))
    meta_rows = []
    for line in metadata:
        match = re.match(r"\*\*([^*]+)：\*\*\s*(.*)", line)
        if match:
            meta_rows.append(
                [
                    Paragraph(inline_markup(match.group(1)), styles["table_header"]),
                    Paragraph(inline_markup(match.group(2)), styles["table"]),
                ]
            )
    if meta_rows:
        meta_table = Table(meta_rows, colWidths=[3.4 * cm, 13.8 * cm], hAlign="LEFT")
        meta_table.setStyle(
            TableStyle(
                [
                    ("BACKGROUND", (0, 0), (0, -1), NAVY),
                    ("BACKGROUND", (1, 0), (1, -1), PANEL),
                    ("GRID", (0, 0), (-1, -1), 0.35, GRID),
                    ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
                    ("LEFTPADDING", (0, 0), (-1, -1), 7),
                    ("RIGHTPADDING", (0, 0), (-1, -1), 7),
                    ("TOPPADDING", (0, 0), (-1, -1), 5),
                    ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
                ]
            )
        )
        story.extend([meta_table, Spacer(1, 12)])
    if cover_image.is_file():
        story.extend(image_block(cover_image, "核心诊断预览", styles, max_height=9.3 * cm))
    story.extend(
        [
            Spacer(1, 4),
            Paragraph(
                "本文件区分专业首选路径、可接受降级与未完成项；任何降级均不得被解释为完整专业标定。",
                styles["cover_meta"],
            ),
            PageBreak(),
        ]
    )
    return story


def markdown_story(source: Path, styles, image_rules: dict[str, tuple[Path, str]]):
    lines = source.read_text(encoding="utf-8").splitlines()
    title = lines[0].lstrip("# ").strip()
    divider = next((index for index, line in enumerate(lines[1:], start=1) if line.strip() == "---"), 1)
    metadata = [line.strip() for line in lines[1:divider] if line.strip()]
    content = lines[divider + 1 :]
    story = []
    paragraph: list[str] = []
    in_code = False
    code_lines: list[str] = []
    index = 0

    def flush_paragraph() -> None:
        if paragraph:
            story.append(Paragraph(inline_markup(" ".join(paragraph)), styles["body"]))
            paragraph.clear()

    while index < len(content):
        raw = content[index]
        stripped = raw.strip()
        if stripped.startswith("```"):
            flush_paragraph()
            if in_code:
                story.append(Preformatted("\n".join(code_lines), styles["code"]))
                code_lines.clear()
                in_code = False
            else:
                in_code = True
            index += 1
            continue
        if in_code:
            code_lines.append(raw)
            index += 1
            continue
        if stripped.startswith("|") and stripped.endswith("|"):
            flush_paragraph()
            table_lines = []
            while index < len(content):
                candidate = content[index].strip()
                if not (candidate.startswith("|") and candidate.endswith("|")):
                    break
                table_lines.append(candidate)
                index += 1
            rows = [
                [cell.strip() for cell in line.strip("|").split("|")]
                for line in table_lines
                if not re.fullmatch(r"\|?[\s:|-]+\|?", line)
            ]
            if rows:
                story.extend(
                    [
                        table_flowable(rows, styles, 17.4 * cm),
                        Spacer(1, 7),
                    ]
                )
            continue
        heading = re.match(r"^(#{2,4})\s+(.+)$", stripped)
        if heading:
            flush_paragraph()
            level = len(heading.group(1)) - 1
            heading_text = heading.group(2)
            style_name = "h1" if level == 1 else "h2" if level == 2 else "h3"
            heading_flowable = Paragraph(inline_markup(heading_text), styles[style_name])
            matched_image = None
            for token, (path, caption) in image_rules.items():
                if token in heading_text:
                    matched_image = (path, caption)
                    break
            if matched_image is not None and matched_image[0].is_file():
                story.append(
                    KeepTogether(
                        [
                            heading_flowable,
                            Spacer(1, 4),
                            fit_image(matched_image[0]),
                            Paragraph(inline_markup(matched_image[1]), styles["caption"]),
                        ]
                    )
                )
            else:
                story.append(heading_flowable)
            index += 1
            continue
        if stripped == "---":
            flush_paragraph()
            story.append(HRFlowable(width="100%", thickness=0.6, color=GRID, spaceBefore=4, spaceAfter=7))
            index += 1
            continue
        bullet = re.match(r"^[-*]\s+(.*)$", stripped)
        numbered = re.match(r"^(\d+)\.\s+(.*)$", stripped)
        if bullet or numbered:
            flush_paragraph()
            value = bullet.group(1) if bullet else numbered.group(2)
            marker = "•" if bullet else f"{numbered.group(1)}."
            story.append(Paragraph(inline_markup(value), styles["bullet"], bulletText=marker))
            index += 1
            continue
        if not stripped:
            flush_paragraph()
            index += 1
            continue
        paragraph.append(stripped.rstrip("  "))
        index += 1
    flush_paragraph()
    return title, metadata, story


def build_pdf(
    source: Path,
    destination: Path,
    short_title: str,
    document_label: str,
    cover_image: Path,
    image_rules: dict[str, tuple[Path, str]],
) -> None:
    styles = build_styles()
    title, metadata, content = markdown_story(source, styles, image_rules)
    destination.parent.mkdir(parents=True, exist_ok=True)
    document = StructuredDocument(
        str(destination),
        pagesize=A4,
        leftMargin=1.55 * cm,
        rightMargin=1.55 * cm,
        topMargin=1.45 * cm,
        bottomMargin=1.48 * cm,
        title=title,
        author="OpenAstroSpec contributors",
        subject=document_label,
        short_title=short_title,
    )
    frame = Frame(
        document.leftMargin,
        document.bottomMargin,
        document.width,
        document.height,
        id="normal",
    )
    document.addPageTemplates([PageTemplate(id="main", frames=[frame], onPage=page_decor)])
    story = cover_story(title, metadata, styles, cover_image, document_label) + content
    document.build(story)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--report-only", action="store_true")
    mode.add_argument("--sop-only", action="store_true")
    arguments = parser.parse_args()
    register_fonts()
    report_images = {
        "3. NGC 6543 波长标定": (
            REDUCTION
            / "output"
            / "NGC6543"
            / "2026-05-05"
            / "diagnostics"
            / "wavelength_residuals.png",
            "图 1  同观测夜 NGC 6543 八线空气波长解；内部 RMS 为 0.186 Angstrom。",
        ),
        "4. 3C 273 处理流程": (
            REDUCTION / "output" / "3C273" / "2026-05-04" / "diagnostics" / "trace.png",
            "图 2  全栈二维谱迹与提取孔径；连续谱源可稳定追踪。",
        ),
        "独立分段复核": (
            REDUCTION / "output" / "3C273" / "2026-05-04" / "diagnostics" / "hbeta.png",
            "图 3  前 3 帧、后 10 帧、全栈和 boxcar 均在目录红移位置恢复 H-beta。",
        ),
        "Hβ 右翼、Fe II 与 [O III]": (
            REDUCTION
            / "output"
            / "3C273"
            / "2026-05-04"
            / "diagnostics"
            / "hbeta_feii_oiii_audit.png",
            "图 4  连续谱位置和平滑尺度审计：5704–5707 Å 肩峰更接近红移后的 Fe II 4924；[O III] 4959 预期在 5744.10 Å。",
        ),
        "身份表述": (
            REDUCTION / "output" / "3C273" / "2026-05-04" / "diagnostics" / "gaia_field.png",
            "图 5  Gaia DR3 视场约束：30 arcsec 内只有 3C 273。",
        ),
        "5. 3C 273 谱线与身份复核": (
            REDUCTION / "output" / "3C273" / "2026-05-04" / "diagnostics" / "identity.png",
            "图 6  同观测夜独立波长解下，静止波长、目录红移位置和 UVEX 实测峰位的关系。",
        ),
        "肉眼可见的宇宙学红移": (
            REDUCTION
            / "output"
            / "3C273"
            / "2026-05-04"
            / "diagnostics"
            / "redshift.png",
            "图 7  Hγ、Hβ 与 [O III] 5007 从实验室波长整体移动到 1.158339 倍波长；红叉是 UVEX 实测窗口峰。",
        ),
        "6. 仍然缺少的校准与降级逻辑": (
            REDUCTION
            / "output"
            / "3C273"
            / "2026-05-04"
            / "diagnostics"
            / "normalized_source.png",
            "图 8  counts-only 伪连续谱归一化产品；旧运行中值作为审计对照，未施加仪器响应或绝对流量校正。",
        ),
    }
    sop_images = {
        "2. 七阶段": (
            REDUCTION / "docs" / "images" / "studio-analyse.png",
            "图 1  OpenAstroSpec Spectral Studio — UVEX4 七阶段工作台中的科学分析页。",
        ),
        "6. 第 1 页": (
            REDUCTION / "docs" / "images" / "studio-media.png",
            "图 2  媒体页：分组、设备预设和原始 2D FITS 即时预览。",
        ),
        "7. 第 2 页": (
            REDUCTION / "docs" / "images" / "studio-masters.png",
            "图 3  主校准页：标准星 2D 预览和可审计质量门。",
        ),
        "8. 第 3 页": (
            REDUCTION / "docs" / "images" / "studio-geometry.png",
            "图 4  几何/提取页：处理节点、即时结果和运行监视器。",
        ),
        "9. 第 4 页": (
            REDUCTION / "docs" / "images" / "studio-wavelength.png",
            "图 5  波长页：arc、人工线对、标准星转移和像素轴降级路径。",
        ),
        "10. 第 5 页": (
            REDUCTION / "docs" / "images" / "studio-response.png",
            "图 6  响应页：绝对、相对、仅归一化和二级光谱风险层级。",
        ),
        "12. 第 7 页": (
            REDUCTION / "docs" / "images" / "studio-deliver.png",
            "图 7  交付页：最终 1D、产物清单、报告和 SOP。",
        ),
    }
    if not arguments.sop_only:
        build_pdf(
            REPORT_SOURCE,
            REPORT_OUTPUT,
            "3C 273 处理与 H-beta 分析",
            "观测处理与专项分析报告",
            REDUCTION / "output" / "3C273" / "2026-05-04" / "diagnostics" / "redshift.png",
            report_images,
        )
        print(REPORT_OUTPUT)
    if not arguments.report_only:
        build_pdf(
            SOP_SOURCE,
            SOP_OUTPUT,
            "UVEX 人工处理 SOP",
            "长缝光谱人工处理标准作业程序",
            REDUCTION / "docs" / "images" / "studio-analyse.png",
            sop_images,
        )
        print(SOP_OUTPUT)


if __name__ == "__main__":
    main()
