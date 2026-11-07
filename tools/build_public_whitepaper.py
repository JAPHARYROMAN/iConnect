"""Build the public PDF from the root-level Markdown edition.

Usage: python tools/build_public_whitepaper.py
Dependency: python -m pip install -r tools/requirements-whitepaper.txt

The small renderer intentionally supports the Markdown constructs used by this
paper. Unknown constructs fail explicitly rather than silently dropping content.
Fonts ship with ReportLab; no system fonts, network access, or absolute paths
are required. Build intermediates belong in tmp/pdfs/.
"""

from __future__ import annotations

import html
import re
from pathlib import Path

import reportlab
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    Flowable,
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "WHITEPAPER_PUBLIC.md"
OUTPUT = ROOT / "output" / "pdf" / "iConnect-Whitepaper-Public.pdf"
PAGE_W, PAGE_H = A4
MARGIN = 54
CONTENT_W = PAGE_W - MARGIN * 2
NAVY = colors.HexColor("#102C3D")
INK = colors.HexColor("#203945")
MUTED = colors.HexColor("#526975")
TEAL = colors.HexColor("#007E88")
CYAN = colors.HexColor("#51DDD7")
PALE = colors.HexColor("#EFF6F7")
RULE = colors.HexColor("#D5E2E5")
REPO = "https://github.com/JAPHARYROMAN/iConnect"


def register_fonts():
    font_dir = Path(reportlab.__file__).parent / "fonts"
    for name, filename in (
        ("Paper", "Vera.ttf"),
        ("Paper-Bold", "VeraBd.ttf"),
        ("Paper-Italic", "VeraIt.ttf"),
        ("Paper-BoldItalic", "VeraBI.ttf"),
    ):
        pdfmetrics.registerFont(TTFont(name, str(font_dir / filename)))
    pdfmetrics.registerFontFamily(
        "Paper", normal="Paper", bold="Paper-Bold", italic="Paper-Italic",
        boldItalic="Paper-BoldItalic",
    )


def inline(text):
    """Escape source text, then translate the paper's inline Markdown."""
    text = html.escape(text, quote=True)

    def link(match):
        label, target = match.groups()
        if not target.startswith("https://"):
            target = f"{REPO}/blob/main/{target}"
        return f'<link href="{target}" color="#007E88"><u>{label}</u></link>'

    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link, text)
    return re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", text)


def make_styles():
    base = ParagraphStyle(
        "body", fontName="Paper", fontSize=10.1, leading=15.1,
        textColor=INK, spaceAfter=10, alignment=TA_LEFT,
        allowWidows=0, allowOrphans=0,
    )
    return {
        "body": base,
        "title": ParagraphStyle(
            "title", parent=base, fontName="Paper-Bold", fontSize=23,
            leading=29, textColor=NAVY, spaceAfter=19, keepWithNext=True,
        ),
        "sub": ParagraphStyle(
            "sub", parent=base, fontName="Paper-Bold", fontSize=11.3,
            leading=16, spaceBefore=7, spaceAfter=7, textColor=TEAL,
            keepWithNext=True,
        ),
        "eyebrow": ParagraphStyle(
            "eyebrow", parent=base, fontName="Paper-Bold", fontSize=8,
            leading=12, textColor=TEAL, spaceAfter=8, keepWithNext=True,
        ),
        "bullet": ParagraphStyle(
            "bullet", parent=base, leftIndent=13, firstLineIndent=0,
            bulletIndent=0, spaceAfter=9,
        ),
        "cell": ParagraphStyle(
            "cell", parent=base, fontSize=9, leading=13.1, spaceAfter=0,
        ),
        "cell_head": ParagraphStyle(
            "cell_head", parent=base, fontName="Paper-Bold", fontSize=8.7,
            leading=12, textColor=colors.white, spaceAfter=0,
        ),
        "reference": ParagraphStyle(
            "reference", parent=base, fontSize=9.1, leading=13.1,
            spaceAfter=9, leftIndent=16, firstLineIndent=-16,
        ),
    }


def arrow(c, x1, y1, x2, y2, color, both=False):
    import math

    c.setStrokeColor(color)
    c.setFillColor(color)
    c.line(x1, y1, x2, y2)
    angle = math.atan2(y2 - y1, x2 - x1)

    def tip(x, y, a):
        p = c.beginPath()
        p.moveTo(x, y)
        p.lineTo(x - 5 * math.cos(a - 0.5), y - 5 * math.sin(a - 0.5))
        p.lineTo(x - 5 * math.cos(a + 0.5), y - 5 * math.sin(a + 0.5))
        p.close()
        c.drawPath(p, fill=1, stroke=0)

    tip(x2, y2, angle)
    if both:
        tip(x1, y1, angle + math.pi)


class ArchitectureDiagram(Flowable):
    """Vector rendition of the source's Mermaid architecture figure."""

    def __init__(self):
        super().__init__()
        self.width = CONTENT_W
        self.height = 190

    def draw(self):
        c, w = self.canv, self.width
        c.setFillColor(PALE)
        c.roundRect(0, 0, w, 186, 9, stroke=0, fill=1)
        c.setFillColor(NAVY)
        c.roundRect(93, 128, w - 186, 45, 6, stroke=0, fill=1)
        c.setFont("Paper-Bold", 10)
        c.setFillColor(colors.white)
        c.drawCentredString(w / 2, 154, "Rendezvous + STUN")
        c.setFont("Paper", 7.8)
        c.drawCentredString(w / 2, 140, "Connection information only")
        c.setStrokeColor(MUTED)
        c.setDash(3, 3)
        c.line(80, 91, 151, 128)
        c.line(w - 80, 91, w - 151, 128)
        c.setDash()
        for x, title, subtitle in (
            (12, "Windows", "Per-user application"),
            (w - 158, "Android", "Active availability"),
        ):
            c.setFillColor(colors.white)
            c.setStrokeColor(RULE)
            c.roundRect(x, 27, 146, 64, 6, stroke=1, fill=1)
            c.setFillColor(NAVY)
            c.setFont("Paper-Bold", 11)
            c.drawCentredString(x + 73, 64, title)
            c.setFillColor(MUTED)
            c.setFont("Paper", 7.8)
            c.drawCentredString(x + 73, 47, subtitle)
        c.setLineWidth(1.6)
        arrow(c, 166, 55, w - 166, 55, TEAL, both=True)
        c.setFont("Paper-Bold", 7.7)
        c.setFillColor(TEAL)
        c.drawCentredString(w / 2, 72, "DIRECT FILE CHANNEL")
        c.setFont("Paper", 7.1)
        c.drawCentredString(w / 2, 38, "Encrypted + authenticated")
        c.setFont("Paper", 7.8)
        c.setFillColor(MUTED)
        c.drawCentredString(w / 2, 11, "No iConnect file relay or central document storage")


class WhitepaperDoc(SimpleDocTemplate):
    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph) and flowable.style.name == "title":
            title = flowable.getPlainText()
            key = f"section-{self.page}"
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(title, key, level=0)


def draw_cover(c, doc, preamble):
    title = re.search(r"^# (.+)$", preamble, re.M).group(1)
    subtitle = re.search(r"^## (.+)$", preamble, re.M).group(1)
    tagline = re.search(r"^> (.+)$", preamble, re.M).group(1)
    meta = re.search(r"^\*\*(Public White Paper.+)\*\*$", preamble, re.M).group(1)
    note = re.search(r"^\*\*Publication status:\*\* (.+)$", preamble, re.M).group(1)
    c.saveState()
    c.setFillColor(NAVY)
    c.rect(0, 0, PAGE_W, PAGE_H, fill=1, stroke=0)
    c.setFillColor(CYAN)
    c.rect(MARGIN, PAGE_H - 79, 40, 4, fill=1, stroke=0)
    c.setFont("Paper-Bold", 9)
    c.drawString(MARGIN, PAGE_H - 108, "PUBLIC WHITE PAPER / CONCEPT STAGE")
    c.setFillColor(colors.white)
    c.setFont("Paper-Bold", 53)
    c.drawString(MARGIN - 3, PAGE_H - 198, title)
    sub_style = ParagraphStyle(
        "cover-sub", fontName="Paper", fontSize=19, leading=28,
        textColor=colors.white,
    )
    para = Paragraph(html.escape(subtitle), sub_style)
    _, h = para.wrap(CONTENT_W - 18, 100)
    para.drawOn(c, MARGIN, PAGE_H - 224 - h)
    c.setFillColor(CYAN)
    c.setFont("Paper", 11)
    c.drawString(MARGIN, 471, tagline)

    # Native vector illustration; no external brand or image assets.
    c.setFillColor(colors.HexColor("#0B2230"))
    c.roundRect(MARGIN, 263, CONTENT_W, 165, 12, stroke=0, fill=1)
    c.setStrokeColor(CYAN)
    c.setLineWidth(1.6)
    c.roundRect(86, 320, 139, 78, 5, stroke=1, fill=0)
    c.line(75, 313, 236, 313)
    c.line(101, 305, 210, 305)
    c.roundRect(426, 298, 53, 109, 8, stroke=1, fill=0)
    c.line(441, 397, 463, 397)
    c.circle(452.5, 308, 2, stroke=1, fill=0)
    arrow(c, 252, 354, 404, 354, CYAN, both=True)
    c.setFillColor(colors.white)
    c.setFont("Paper-Bold", 8)
    c.drawCentredString(328, 374, "DIRECT FILE ACCESS")
    c.setFont("Paper", 8)
    c.drawCentredString(156, 282, "Windows")
    c.drawCentredString(452, 282, "Android")
    c.setFillColor(CYAN)
    c.setFont("Paper", 7.8)
    c.drawCentredString(328, 332, "Encrypted. Owner-approved.")

    note_style = ParagraphStyle(
        "cover-note", fontName="Paper", fontSize=9.2, leading=14,
        textColor=colors.HexColor("#D2E3E9"),
    )
    para = Paragraph("<b>Publication status</b><br/>" + html.escape(note), note_style)
    _, h = para.wrap(CONTENT_W, 100)
    para.drawOn(c, MARGIN, 230 - h)
    c.setStrokeColor(colors.HexColor("#35505F"))
    c.line(MARGIN, 113, PAGE_W - MARGIN, 113)
    c.setFont("Paper", 8)
    c.setFillColor(colors.white)
    c.drawString(MARGIN, 91, meta)
    c.setFillColor(CYAN)
    c.drawString(MARGIN, 71, "github.com/JAPHARYROMAN/iConnect")
    c.linkURL(REPO, (MARGIN, 67, MARGIN + 210, 82), relative=0)
    c.bookmarkPage("cover")
    c.addOutlineEntry("iConnect - Public White Paper", "cover", level=0)
    c.restoreState()


def draw_running(c, doc, version, publication_date):
    c.saveState()
    c.setFillColor(TEAL)
    c.rect(MARGIN, PAGE_H - 48, 19, 3, fill=1, stroke=0)
    c.setFont("Paper-Bold", 7.6)
    c.drawString(MARGIN + 28, PAGE_H - 49, "iConnect / PUBLIC WHITE PAPER")
    c.setFillColor(MUTED)
    c.setFont("Paper", 7.1)
    c.drawRightString(PAGE_W - MARGIN, PAGE_H - 49, f"CONCEPT STAGE / VERSION {version}")
    c.setStrokeColor(RULE)
    c.line(MARGIN, 44, PAGE_W - MARGIN, 44)
    c.setFont("Paper", 7)
    c.drawString(MARGIN, 29, f"iConnect | {publication_date} | Proposed product")
    c.drawRightString(PAGE_W - MARGIN, 29, f"{doc.page:02d}")
    c.restoreState()


def render_table(lines, styles):
    rows = [[cell.strip() for cell in line.strip().strip("|").split("|")] for line in lines]
    if len(rows) < 2 or not all(re.fullmatch(r":?-+:?", x.replace(" ", "")) for x in rows[1]):
        raise ValueError("Malformed Markdown table")
    rows.pop(1)
    cols = len(rows[0])
    if cols != 2 or any(len(row) != cols for row in rows):
        raise ValueError("This edition expects consistent two-column tables")
    cells = [
        [Paragraph(inline(value), styles["cell_head" if i == 0 else "cell"]) for value in row]
        for i, row in enumerate(rows)
    ]
    table = Table(cells, colWidths=[125, CONTENT_W - 125], repeatRows=1, hAlign="LEFT")
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [PALE, colors.white]),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 11),
        ("RIGHTPADDING", (0, 0), (-1, -1), 11),
        ("TOPPADDING", (0, 0), (-1, -1), 7),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
        ("LINEBELOW", (0, 1), (-1, -1), 0.35, RULE),
    ]))
    return [table, Spacer(1, 12)]


def blocks(body, styles, references=False):
    lines = body.strip().splitlines()
    story = []
    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if not line:
            i += 1
            continue
        if line.startswith("```mermaid"):
            i += 1
            while i < len(lines) and lines[i].strip() != "```":
                i += 1
            if i == len(lines):
                raise ValueError("Unclosed Mermaid figure")
            story.extend([ArchitectureDiagram(), Spacer(1, 12)])
            i += 1
            continue
        if line.startswith("|"):
            table_lines = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                table_lines.append(lines[i])
                i += 1
            story.extend(render_table(table_lines, styles))
            continue
        if line.startswith("### "):
            story.append(Paragraph(inline(line[4:]), styles["sub"]))
        elif line.startswith("- "):
            story.append(Paragraph(inline(line[2:]), styles["bullet"], bulletText="-"))
        elif re.match(r"^\d+\. ", line):
            number, text = line.split(". ", 1)
            style = styles["reference"] if references else styles["bullet"]
            story.append(Paragraph(f"{number}. {inline(text)}", style))
        elif line.startswith(("#", "```", ">")):
            raise ValueError(f"Unsupported construct: {line[:60]}")
        else:
            paragraph = [line]
            while i + 1 < len(lines) and lines[i + 1].strip():
                following = lines[i + 1].strip()
                if following.startswith(("#", "|", "- ", "```")):
                    break
                paragraph.append(following)
                i += 1
            story.append(Paragraph(inline(" ".join(paragraph)), styles["body"]))
        i += 1
    return story


def main():
    register_fonts()
    styles = make_styles()
    source = SOURCE.read_text(encoding="utf-8")
    if any(char in source for char in "\u2010\u2011\u2012\u2013\u2014"):
        raise ValueError("Use ASCII hyphens for portable PDF text")
    parts = re.split(r"\n## (?=\d+\.)", source)
    if len(parts) != 9:
        raise ValueError("Expected cover and eight numbered sections")
    metadata = re.search(
        r"\*\*Public White Paper \| Version ([^|]+) \| ([^*]+)\*\*", parts[0],
    )
    if not metadata:
        raise ValueError("Missing public edition version/date metadata")
    version, publication_date = (value.strip() for value in metadata.groups())
    labels = [
        "THE IDEA", "THE EXPERIENCE", "THE ARCHITECTURE", "TRUST AND ACCESS",
        "RELIABILITY", "PRIVACY AND AVAILABILITY", "DEVELOPMENT DIRECTION", "SOURCE NOTES",
    ]
    story = [Spacer(1, 1), PageBreak()]
    for index, part in enumerate(parts[1:], 1):
        title, body = part.split("\n", 1)
        title = re.sub(r"^\d+\. ", "", title)
        story.append(Paragraph(f"{index:02d} / {labels[index - 1]}", styles["eyebrow"]))
        title_html = inline(title)
        # Intentional line breaks keep short clauses together at this measure.
        if ". " in title:
            title_html = title_html.replace(". ", ".<br/>", 1)
        elif ", in both" in title:
            title_html = title_html.replace(", in both", ",<br/>in both", 1)
        elif "complete only" in title:
            title_html = title_html.replace("complete only", "complete<br/>only", 1)
        elif "confidence before" in title:
            title_html = title_html.replace("confidence before", "confidence<br/>before", 1)
        story.append(Paragraph(title_html, styles["title"]))
        story.extend(blocks(body, styles, references=index == 8))
        if index < 8:
            story.append(PageBreak())
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    doc = WhitepaperDoc(
        str(OUTPUT), pagesize=A4, leftMargin=MARGIN, rightMargin=MARGIN,
        topMargin=82, bottomMargin=62,
        title="iConnect - Secure, accountless file access between Windows and Android",
        author="iConnect project", subject=f"Public concept white paper, version {version}",
        creator="iConnect public white paper build", pageCompression=1,
        invariant=1,
    )
    doc.build(
        story, onFirstPage=lambda c, d: draw_cover(c, d, parts[0]),
        onLaterPages=lambda c, d: draw_running(c, d, version, publication_date),
    )
    print(f"Built {OUTPUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
