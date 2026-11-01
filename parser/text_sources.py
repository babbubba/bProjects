"""Sorgenti testuali (T024): Markdown via Docling con mapping lossless a righe/range, TXT UTF-8/UTF-16 con BOM senza replacement.

Gli offset sono codepoint Unicode sul testo decodificato (BOM escluso, newline originali preservati). Nessun accesso remoto:
immagini e link di Markdown non vengono mai seguiti.
"""
import bisect
import re
from io import BytesIO

from common import FORMATS, CONFIG, ParseError, Parsed, make_block, new_issue

_NEWLINE = re.compile(r"\r\n|\r|\n")
_MAX_BLOCK = CONFIG["txtMaxBlockCodepoints"]


def decode_text(data: bytes) -> str:
    """UTF-8 (con o senza BOM) o UTF-16 con BOM; ogni altra codifica e rifiutata, mai sostituzione silenziosa."""
    try:
        if data.startswith((b"\xff\xfe", b"\xfe\xff")):
            text = data.decode("utf-16")  # consuma il BOM e rispetta l'endianness
        else:
            text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise ParseError("invalid_encoding", 415, "Codifica del testo non ammessa (UTF-8 o UTF-16 con BOM).") from None
    if "\x00" in text:
        raise ParseError("invalid_encoding", 415, "Codifica del testo non ammessa (UTF-8 o UTF-16 con BOM).")
    return text


class Lines:
    """Righe del sorgente: offset di inizio (codepoint) e terminatore preservato."""

    def __init__(self, text: str):
        self.starts = [0]
        self.ends = []  # fine del contenuto della riga, terminatore escluso
        for m in _NEWLINE.finditer(text):
            self.ends.append(m.start())
            self.starts.append(m.end())
        self.ends.append(len(text))
        self.count = len(self.starts)

    def line_of(self, offset: int) -> int:
        """Numero di riga 1-based che contiene l'offset."""
        return bisect.bisect_right(self.starts, offset) - 1 + 1


def _paragraph_spans(text: str, lines: Lines) -> list[tuple[int, int]]:
    """Paragrafi = righe consecutive non vuote; paragrafi oltre il massimo sono spezzati a confine di riga."""
    spans, cur_start, cur_end = [], None, None
    for i in range(lines.count):
        s, e = lines.starts[i], lines.ends[i]
        if text[s:e].strip() == "":
            if cur_start is not None:
                spans.append((cur_start, cur_end))
                cur_start = None
        else:
            if cur_start is None:
                cur_start = s
            cur_end = e
    if cur_start is not None:
        spans.append((cur_start, cur_end))
    out = []
    for s, e in spans:
        if e - s <= _MAX_BLOCK:
            out.append((s, e))
            continue
        pos = s
        while pos < e:
            limit = min(pos + _MAX_BLOCK, e)
            if limit < e:
                last_nl = max((m.start() for m in _NEWLINE.finditer(text, pos, limit)), default=-1)
                cut = last_nl if last_nl > pos else limit
            else:
                cut = limit
            out.append((pos, cut))
            nxt = cut
            m = _NEWLINE.match(text, nxt)
            pos = m.end() if m else nxt
    return out


def convert_txt(data: bytes, limits: dict) -> Parsed:
    from docling_core.types.doc import DocItemLabel, DoclingDocument  # noqa: PLC0415

    text = decode_text(data)
    lines = Lines(text)
    doc = DoclingDocument(name="txt")
    parsed = Parsed(doc=doc, document={"serialization": "text-lossless-1", "content": text}, page_count=None, source_text=text)
    for order, (s, e) in enumerate(_paragraph_spans(text, lines)):
        ref = f"b{order + 1}"
        t = text[s:e]
        item = doc.add_text(label=DocItemLabel.TEXT, text=t, orig=t)
        parsed.ref_to_block[item.self_ref] = [{"blockRef": ref, "itemStart": 0, "itemEnd": len(t), "identity": True}]
        parsed.blocks.append(make_block(ref, order, "paragraph", t, line_start=lines.line_of(s), line_end=lines.line_of(max(s, e - 1)),
                                        src_start=s, src_end=e))
        parsed.inventory.append({"itemRef": f"inv{order + 1}", "kind": "paragraph", "state": "represented",
                                 "blockRefs": [ref], "issueRefs": []})
    if not parsed.blocks:
        parsed.inventory.append({"itemRef": "inv1", "kind": "document", "state": "empty", "blockRefs": [], "issueRefs": []})
    return parsed


_SEPARATOR = re.compile(r"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")


def _locate(source: str, cursor: int, text: str, first: str, last: str) -> tuple[int, int] | None:
    """Intervallo nel sorgente di un blocco Docling: ricerca esatta, poi tollerante agli a capo, poi per estremi (markup inline)."""
    i = source.find(text, cursor)
    if i >= 0:
        return i, i + len(text)
    words = text.split()
    if words:
        m = re.compile(r"\s+".join(re.escape(w) for w in words)).search(source, cursor)
        if m:
            return m.start(), m.end()
    a = source.find(first, cursor)
    if a >= 0:
        b = source.find(last, a)
        if b >= 0:
            return a, b + len(last)
    return None


def _table_span(source: str, lines: Lines, cursor: int) -> tuple[int, int] | None:
    """Prima tabella a pipe a partire dal cursore: riga con '|' seguita da una riga separatore, fino all'ultima riga con '|'."""
    for ln in range(lines.line_of(cursor) - 1, lines.count - 1):
        row = source[lines.starts[ln]:lines.ends[ln]]
        nxt = source[lines.starts[ln + 1]:lines.ends[ln + 1]]
        if "|" in row and "-" in nxt and _SEPARATOR.match(nxt):
            end = ln + 1
            while end + 1 < lines.count and "|" in source[lines.starts[end + 1]:lines.ends[end + 1]]:
                end += 1
            return lines.starts[ln], lines.ends[end]
    return None


def _md_table(source: str, lines: Lines, span: tuple[int, int], item) -> tuple[str, dict]:
    """Blocco tabella = righe sorgente (lossless); celle e righe mappate sulle righe del sorgente."""
    from converters import table_grid  # noqa: PLC0415

    s, e = span
    text = source[s:e]
    rows, cols, cells = table_grid(item)
    local = Lines(text)
    data_lines = [i for i in range(local.count) if not _SEPARATOR.match(text[local.starts[i]:local.ends[i]])]
    row_ranges = []
    for r in range(rows):
        row_cells = [c for c in cells if c["row"] == r]
        if r >= len(data_lines):
            break
        ls, le = local.starts[data_lines[r]], local.ends[data_lines[r]]
        pos = ls
        for c in row_cells:
            k = text.find(c["text"], pos, le) if c["text"] else -1
            if k >= 0:
                c["start"], c["end"] = k, k + len(c["text"])
                pos = c["end"]
        row_ranges.append({"row": r, "start": ls, "end": le, "header": bool(row_cells) and all(c["header"] for c in row_cells)})
    return text, {"rows": rows, "cols": cols, "cells": cells, "rowRanges": row_ranges}


def convert_markdown(data: bytes, limits: dict) -> Parsed:
    from docling.datamodel.base_models import DocumentStream, InputFormat  # noqa: PLC0415
    from docling.document_converter import DocumentConverter  # noqa: PLC0415
    from importlib.metadata import version  # noqa: PLC0415

    import converters  # noqa: PLC0415

    source = decode_text(data)
    lines = Lines(source)
    try:
        result = DocumentConverter(allowed_formats=[InputFormat.MD]).convert(
            DocumentStream(name="source.md", stream=BytesIO(source.encode("utf-8"))), raises_on_error=True)
    except Exception:  # noqa: BLE001
        raise ParseError("corrupt", 422, "Markdown non convertibile.") from None
    doc = result.document
    parsed = Parsed(doc=doc, document={"serialization": f"docling-document-json/{version('docling-core')}", "content": doc.export_to_dict()},
                    page_count=None, source_text=source)

    # gli item di un gruppo inline (testo con collegamenti) formano un solo blocco
    units: list[list] = []
    for item, _lvl in doc.iterate_items(with_groups=False):
        parent = getattr(item, "parent", None)
        group = parent.cref if parent is not None and parent.cref.startswith("#/groups/") and _is_inline(doc, parent) else None
        if group and units and units[-1][0] == group:
            units[-1][1].append(item)
        else:
            units.append([group, [item]])

    hs, cursor, n = converters.HeadingStack(), 0, 0
    for _group, items in units:
        item = items[0]
        btype = converters.block_type(item)
        level = converters.heading_level(item)
        table = None
        if btype == "table":
            span = _table_span(source, lines, cursor)
            if span is None:
                raise ParseError("corrupt", 422, "Tabella Markdown non localizzabile nel sorgente.")
            text, table = _md_table(source, lines, span, item)
            s, e = span
        elif btype == "picture":
            text, s, e = "", cursor, cursor  # immagini Markdown: mai scaricate
        else:
            text = " ".join((i.text or "") for i in items).strip()
            if not text:
                continue
            span = _locate(source, cursor, text, items[0].text or "", items[-1].text or "")
            if span is None:
                s = e = None
            else:
                s, e = span
                text = source[s:e]  # testo = slice del sorgente (a capo originali; per i collegamenti anche la sintassi testuale)
        n += 1
        ref = f"b{n}"
        path, refs = hs.path_for(level)
        links = [{"text": i.text, "target": str(i.hyperlink)} for i in items if getattr(i, "hyperlink", None)]
        b = make_block(ref, n - 1, btype, text, heading_path=path, line_start=lines.line_of(s) if s is not None else None,
                       line_end=lines.line_of(max(s, e - 1)) if s is not None else None, src_start=s, src_end=e, table=table)
        b["headingRefs"] = refs
        b["itemRef"] = item.self_ref
        if links:
            b["links"] = links  # collegamenti conservati come testo, mai seguiti
        parsed.blocks.append(b)
        for it in items:
            itext = getattr(it, "text", "") or ""
            parsed.ref_to_block[it.self_ref] = ([{"blockRef": ref, "itemStart": 0, "itemEnd": len(itext), "identity": itext == text}]
                                                if btype != "picture" else [])
        if s is None:
            new_issue(parsed.issues, "source_mapping_unavailable", severity="warning", block_ref=ref, affects=False,
                      reason="Posizione nel sorgente non determinabile per il blocco.")
        else:
            cursor = e
        if btype == "picture":
            iss = new_issue(parsed.issues, "unprocessed_markdown_image", block_ref=ref,
                            reason="Immagine di Markdown non scaricata ne elaborata.")
            parsed.inventory.append({"itemRef": f"inv{n}", "kind": "picture", "state": "unprocessed", "blockRefs": [ref], "issueRefs": [iss]})
        else:
            parsed.inventory.append({"itemRef": f"inv{n}", "kind": btype, "state": "represented", "blockRefs": [ref], "issueRefs": []})
        if level is not None:
            hs.push(level, items[0].text or "", ref)
    if not parsed.blocks:
        parsed.inventory.append({"itemRef": "inv1", "kind": "document", "state": "empty", "blockRefs": [], "issueRefs": []})
    return parsed


def _is_inline(doc, parent_ref) -> bool:
    try:
        return str(parent_ref.resolve(doc).label.value) == "inline"
    except Exception:  # noqa: BLE001
        return False
