"""Conversione PDF e DOCX (T023) con Docling 2.129.0: DoclingDocument originale, blocchi canonici con locator, inventario e issue.

PDF: OCR, VLM, enrichment, plugin e servizi remoti disabilitati; pagina e bbox nativi (origine PDF). DOCX: percorso strutturale
(`body/p[n]`, `body/tbl[k]/tr[i]/tc[j]`) con page=null, mai pagine simulate; le tabelle sono lette dalla struttura OOXML perche
Docling puo ometterle in silenzio. Gli elementi informativi non trattati (immagini, oggetti incorporati, parti con testo non estratto)
producono issue bloccanti: il risultato e Incomplete, mai un Ready fittizio.
"""
import re
import shutil
import zipfile
from io import BytesIO
from pathlib import Path

from common import MODELS_DIR, ParseError, Parsed, make_block, new_issue

_WS = re.compile(r"\s+")
_W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
_PICTURE_AREA_RATIO = 0.10      # immagine >= 10% della pagina (o pagina senza testo) = potenzialmente informativa
_NOTE_PARTS = re.compile(r"^word/(footnotes|endnotes|comments)\.xml$")
_FURNITURE_PARTS = re.compile(r"^word/(header|footer)\d*\.xml$")


def norm(s: str) -> str:
    return _WS.sub(" ", s).strip()


# ---------------------------------------------------------------- blocchi comuni da DoclingDocument

def heading_level(item) -> int | None:
    name = type(item).__name__
    if name == "TitleItem":
        return 0
    if name == "SectionHeaderItem":
        return int(getattr(item, "level", 1) or 1)
    return None


def block_type(item) -> str:
    name = type(item).__name__
    if name in ("TitleItem", "SectionHeaderItem"):
        return "heading"
    return {"ListItem": "list", "CodeItem": "code", "TableItem": "table", "PictureItem": "picture",
            "TextItem": "paragraph", "FormulaItem": "paragraph"}.get(name, "other")


class HeadingStack:
    def __init__(self):
        self.stack: list[tuple[int, str, str]] = []  # (level, text, blockRef)

    def path_for(self, level: int | None) -> tuple[list[str], list[str]]:
        """Titoli antenati (per un heading di livello `level`) oppure tutti i titoli aperti (per un contenuto)."""
        s = self.stack if level is None else [h for h in self.stack if h[0] < level]
        return [h[1] for h in s], [h[2] for h in s]

    def push(self, level: int, text: str, ref: str) -> None:
        self.stack = [h for h in self.stack if h[0] < level]
        self.stack.append((level, text, ref))


def table_grid(item) -> tuple[int, int, list[dict]]:
    """Celle della tabella Docling come lista ordinata (riga, colonna) con span e header."""
    data = item.data
    cells = []
    for c in data.table_cells:
        cells.append({"row": c.start_row_offset_idx, "col": c.start_col_offset_idx, "rowSpan": max(1, c.end_row_offset_idx - c.start_row_offset_idx),
                      "colSpan": max(1, c.end_col_offset_idx - c.start_col_offset_idx), "header": bool(c.column_header or c.row_header),
                      "text": norm(c.text or ""), "path": None})
    cells.sort(key=lambda c: (c["row"], c["col"]))
    return data.num_rows, data.num_cols, cells


def table_text_and_rows(rows: int, cols: int, cells: list[dict]) -> tuple[str, list[dict]]:
    """Testo canonico (celle di una riga unite da ' | ', righe da newline) e intervalli per riga; assegna start/end a ogni cella."""
    lines, row_ranges, pos = [], [], 0
    for r in range(rows):
        row_cells = [c for c in cells if c["row"] == r]
        parts = []
        cur = pos
        for i, c in enumerate(row_cells):
            if i:
                cur += 3
            c["start"], c["end"] = cur, cur + len(c["text"])
            cur = c["end"]
            parts.append(c["text"])
        line = " | ".join(parts)
        if line.strip(" |"):
            row_ranges.append({"row": r, "start": pos, "end": pos + len(line), "header": all(c["header"] for c in row_cells) and bool(row_cells)})
        lines.append(line)
        pos += len(line) + 1
    return "\n".join(lines), row_ranges


def bbox_dict(bbox) -> dict:
    return {"l": round(float(bbox.l), 2), "t": round(float(bbox.t), 2), "r": round(float(bbox.r), 2), "b": round(float(bbox.b), 2)}


def split_by_provenance(item) -> list[tuple[int, int, object]]:
    """Segmenti (inizio, fine, prov) del testo di un item che occupa piu pagine; un solo segmento se non distinguibili."""
    text = getattr(item, "text", "") or ""
    prov = list(item.prov or [])
    if len(prov) > 1 and all(p.charspan and 0 <= p.charspan[0] < p.charspan[1] <= len(text) for p in prov):
        segs = sorted(((p.charspan[0], p.charspan[1], p) for p in prov), key=lambda s: s[0])
        if all(a[1] <= b[0] for a, b in zip(segs, segs[1:])):
            return segs
    return [(0, len(text), prov[0])] if prov else [(0, len(text), None)]


# ---------------------------------------------------------------- preflight

def _check_pdf(data: bytes, limits: dict) -> tuple[int, list[bool]]:
    import pypdfium2 as pdfium  # noqa: PLC0415

    if data[:4] == b"PK\x03\x04":
        raise ParseError("format_mismatch", 415, "Il contenuto non corrisponde al formato PDF dichiarato.")
    if b"/Encrypt" in data:
        raise ParseError("protected", 422, "Documento protetto o cifrato.")
    try:
        pdf = pdfium.PdfDocument(data)
        n = len(pdf)
        if n > limits["maxPages"]:
            raise ParseError("parse_limit_exceeded", 422, "Numero di pagine oltre il limite.")
        empty = []
        for i in range(n):
            page = pdf[i]
            try:
                empty.append(sum(1 for _ in page.get_objects()) == 0)
            finally:
                page.close()
        pdf.close()
    except ParseError:
        raise
    except Exception as e:  # noqa: BLE001
        msg = str(e).lower()
        raise ParseError("protected" if "password" in msg else "corrupt", 422,
                         "Documento protetto o cifrato." if "password" in msg else "Documento PDF corrotto o non leggibile.") from None
    if n == 0:
        raise ParseError("corrupt", 422, "Documento PDF senza pagine.")
    return n, empty


def _check_docx(data: bytes, limits: dict) -> zipfile.ZipFile:
    if data[:4] == b"%PDF":
        raise ParseError("format_mismatch", 415, "Il contenuto non corrisponde al formato DOCX dichiarato.")
    if data[:8] == bytes.fromhex("D0CF11E0A1B11AE1"):
        raise ParseError("protected", 422, "Documento protetto o cifrato.")
    try:
        z = zipfile.ZipFile(BytesIO(data))
        infos = z.infolist()
    except zipfile.BadZipFile:
        raise ParseError("corrupt", 422, "Archivio DOCX corrotto.") from None
    if len(infos) > limits["maxZipEntries"]:
        raise ParseError("parse_limit_exceeded", 422, "Troppe voci nell'archivio.")
    total = 0
    for i in infos:
        n = i.filename
        if n.startswith(("/", "\\")) or re.match(r"^[A-Za-z]:", n) or ".." in n.replace("\\", "/").split("/") or "\\" in n:
            raise ParseError("unsafe_archive", 422, "Archivio con percorsi non ammessi.")
        if i.flag_bits & 0x1:
            raise ParseError("protected", 422, "Documento protetto o cifrato.")
        total += i.file_size
        if total > limits["maxExpandedBytes"]:
            raise ParseError("parse_limit_exceeded", 422, "Dimensione espansa oltre il limite.")
    names = {i.filename for i in infos}
    if "word/document.xml" not in names or "[Content_Types].xml" not in names:
        raise ParseError("corrupt", 422, "Archivio non e un documento DOCX.")
    if "word/vbaProject.bin" in names or b"macroEnabled" in z.read("[Content_Types].xml"):
        raise ParseError("macro_enabled_not_allowed", 415, "Formato con macro non ammesso.")
    return z


# ---------------------------------------------------------------- Docling

def _converter():
    from docling.datamodel.base_models import InputFormat  # noqa: PLC0415
    from docling.datamodel.pipeline_options import AcceleratorOptions, PdfPipelineOptions  # noqa: PLC0415
    from docling.document_converter import DocumentConverter, PdfFormatOption  # noqa: PLC0415

    po = PdfPipelineOptions(
        do_ocr=False, do_table_structure=True, do_code_enrichment=False, do_formula_enrichment=False,
        do_picture_classification=False, do_picture_description=False, generate_page_images=False,
        generate_picture_images=False, enable_remote_services=False, artifacts_path=str(MODELS_DIR),
        accelerator_options=AcceleratorOptions(num_threads=2, device="cpu"))
    return DocumentConverter(allowed_formats=[InputFormat.PDF, InputFormat.DOCX],
                             format_options={InputFormat.PDF: PdfFormatOption(pipeline_options=po)})


def _run_docling(fmt: str, data: bytes, workdir: Path, limits: dict):
    src = workdir / f"source.{fmt}"
    shutil.copyfile(workdir / "input.bin", src)
    try:
        result = _converter().convert(src, raises_on_error=True, max_num_pages=limits["maxPages"], max_file_size=len(data) + 1)
    except Exception:  # noqa: BLE001 - nessun dettaglio del contenuto nell'errore
        raise ParseError("corrupt", 422, "Documento non convertibile.") from None
    return result


def convert_binary(fmt: str, data: bytes, workdir: Path, limits: dict) -> Parsed:
    if fmt == "pdf":
        pages, empty_pages = _check_pdf(data, limits)
        zf = None
    else:
        zf = _check_docx(data, limits)
    result = _run_docling(fmt, data, workdir, limits)
    doc = result.document
    parsed = Parsed(doc=doc, document={"serialization": f"docling-document-json/{_core_version()}", "content": doc.export_to_dict()})
    if str(getattr(result.status, "value", result.status)) not in ("success",):
        new_issue(parsed.issues, "conversion_partial", reason="Conversione parziale riportata dal parser.")
    if fmt == "pdf":
        _pdf_blocks(parsed, doc, pages, empty_pages)
    else:
        _docx_blocks(parsed, doc, zf)
    return parsed


def _core_version() -> str:
    from importlib.metadata import version  # noqa: PLC0415
    return version("docling-core")


# ---------------------------------------------------------------- PDF

def _add_block(parsed: Parsed, item, text: str, *, order_box: list, hs: HeadingStack, **loc) -> str:
    n = order_box[0] + 1
    order_box[0] = n
    ref = f"b{n}"
    level = heading_level(item)
    path, refs = hs.path_for(level)
    b = make_block(ref, n - 1, loc.pop("btype", block_type(item)), text, heading_path=path, **loc)
    b["headingRefs"] = refs
    b["itemRef"] = item.self_ref
    parsed.blocks.append(b)
    return ref


def _pdf_blocks(parsed: Parsed, doc, page_count: int, empty_pages: list[bool]) -> None:
    from docling_core.types.doc import ContentLayer  # noqa: PLC0415

    parsed.page_count = page_count
    hs, order = HeadingStack(), [0]
    by_page: dict[int, list[str]] = {p: [] for p in range(1, page_count + 1)}
    pictures: list[tuple[int, str, float]] = []
    text_pages: set[int] = set()
    furniture_pages: set[int] = set()

    for item, _lvl in doc.iterate_items(included_content_layers={ContentLayer.BODY, ContentLayer.FURNITURE}, with_groups=False):
        if getattr(item, "content_layer", ContentLayer.BODY) == ContentLayer.FURNITURE:
            furniture_pages.update(p.page_no for p in (item.prov or []))
            continue
        btype = block_type(item)
        level = heading_level(item)
        if btype == "table":
            rows, cols, cells = table_grid(item)
            text, row_ranges = table_text_and_rows(rows, cols, cells)
            pv = item.prov[0] if item.prov else None
            ref = _add_block(parsed, item, text, order_box=order, hs=hs, page=pv.page_no if pv else None,
                             bbox=bbox_dict(pv.bbox) if pv else None, coord=_coord(pv), table={"rows": rows, "cols": cols, "cells": cells, "rowRanges": row_ranges},
                             pages=sorted({p.page_no for p in item.prov}))
            parsed.ref_to_block[item.self_ref] = [{"blockRef": ref, "itemStart": 0, "itemEnd": len(text), "identity": True}]
            for p in {p.page_no for p in item.prov}:
                by_page.setdefault(p, []).append(ref)
                text_pages.add(p)
            continue
        if btype == "picture":
            pv = item.prov[0] if item.prov else None
            page = pv.page_no if pv else None
            ref = _add_block(parsed, item, "", order_box=order, hs=hs, page=page, bbox=bbox_dict(pv.bbox) if pv else None, coord=_coord(pv))
            parsed.ref_to_block[item.self_ref] = []
            area = 0.0
            if pv is not None and page in doc.pages:
                ps = doc.pages[page].size
                area = abs((pv.bbox.r - pv.bbox.l) * (pv.bbox.t - pv.bbox.b)) / max(1.0, ps.width * ps.height)
            if page is not None:
                by_page.setdefault(page, []).append(ref)
            pictures.append((page or 0, ref, area))
            continue
        text = getattr(item, "text", "") or ""
        if not text.strip():
            continue
        segs = split_by_provenance(item)
        mapping = []
        for s, e, pv in segs:
            page = pv.page_no if pv else None
            ref = _add_block(parsed, item, text[s:e], order_box=order, hs=hs, page=page,
                             bbox=bbox_dict(pv.bbox) if pv else None, coord=_coord(pv))
            if len(segs) > 1:
                parsed.blocks[-1]["pieceOf"] = item.self_ref
            mapping.append({"blockRef": ref, "itemStart": s, "itemEnd": e, "identity": True})
            if page is not None:
                by_page.setdefault(page, []).append(ref)
                text_pages.add(page)
        parsed.ref_to_block[item.self_ref] = mapping
        if level is not None:
            hs.push(level, text, mapping[0]["blockRef"])
        _link_parents(parsed, item, doc)

    for page, area_ref_list in ((p, [x for x in pictures if x[0] == p]) for p in range(1, page_count + 1)):
        refs = by_page.get(page, [])
        issue_refs = []
        state = "represented" if refs else "empty"
        for _p, pref, area in area_ref_list:
            if area >= _PICTURE_AREA_RATIO or page not in text_pages:
                issue_refs.append(new_issue(parsed.issues, "unprocessed_image_page", page=page, block_ref=pref,
                                            reason="Immagine potenzialmente informativa non elaborata (OCR e VLM disabilitati)."))
                state = "unprocessed"
            else:
                new_issue(parsed.issues, "small_picture_not_processed", severity="warning", page=page, block_ref=pref, affects=False,
                          reason="Immagine di piccole dimensioni non elaborata: ritenuta decorativa dal criterio di layout.")
        if not refs and not issue_refs:
            if empty_pages[page - 1]:
                state = "empty"
            else:
                issue_refs.append(new_issue(parsed.issues, "page_without_extractable_text", page=page,
                                            reason="Pagina con contenuto grafico ma senza testo estraibile (OCR disabilitato)."))
                state = "unprocessed"
        if page in furniture_pages:
            new_issue(parsed.issues, "page_furniture_omitted", severity="warning", page=page, affects=False,
                      reason="Intestazione o pie di pagina ricorrente esclusa dal corpo del testo.")
        parsed.inventory.append({"itemRef": f"page{page}", "kind": "page", "page": page, "state": state, "blockRefs": refs, "issueRefs": issue_refs})


def _coord(pv) -> str | None:
    return f"pdf-points-{pv.bbox.coord_origin.value.lower()}" if pv is not None else None


def _link_parents(parsed: Parsed, item, doc) -> None:
    """parentRef = blocco dell'antenato piu vicino che e un blocco (i gruppi Docling non sono blocchi)."""
    cur = getattr(item, "parent", None)
    for _ in range(64):
        if cur is None:
            return
        segs = parsed.ref_to_block.get(cur.cref)
        if segs:
            for b in parsed.blocks:
                if b.get("itemRef") == item.self_ref:
                    b["parentRef"] = segs[0]["blockRef"]
            return
        try:
            cur = cur.resolve(doc).parent
        except Exception:  # noqa: BLE001
            return


# ---------------------------------------------------------------- DOCX

def _docx_body(zf: zipfile.ZipFile):
    """Sequenza ordinata dei figli diretti di w:body: paragrafi (ordinale 1-based) e tabelle con celle e percorsi."""
    from lxml import etree  # noqa: PLC0415

    parser = etree.XMLParser(resolve_entities=False, no_network=True, load_dtd=False, huge_tree=False)
    root = etree.fromstring(zf.read("word/document.xml"), parser)
    body = root.find(_W + "body")
    entries, p_ord, t_ord = [], 0, 0
    for child in body:
        if child.tag == _W + "p":
            p_ord += 1
            text = "".join(t.text or "" for t in child.iter(_W + "t"))
            entries.append({"kind": "p", "ordinal": p_ord, "text": text, "drawings": _count_drawings(child)})
        elif child.tag == _W + "tbl":
            t_ord += 1
            rows = []
            for ri, tr in enumerate(child.findall(_W + "tr"), start=1):
                cells = []
                col = 0
                is_header = tr.find(f"{_W}trPr/{_W}tblHeader") is not None
                for ci, tc in enumerate(tr.findall(_W + "tc"), start=1):
                    span_el = tc.find(f"{_W}tcPr/{_W}gridSpan")
                    span = int(span_el.get(_W + "val", "1")) if span_el is not None else 1
                    txt = norm(" ".join("".join(t.text or "" for t in p.iter(_W + "t")) for p in tc.iter(_W + "p")))
                    cells.append({"col": col, "colSpan": max(1, span), "text": txt, "path": f"body/tbl[{t_ord}]/tr[{ri}]/tc[{ci}]", "header": is_header})
                    col += max(1, span)
                rows.append(cells)
            entries.append({"kind": "tbl", "ordinal": t_ord, "rows": rows, "drawings": _count_drawings(child)})
    return entries


def _count_drawings(el) -> int:
    n = 0
    for e in el.iter():
        tag = e.tag if isinstance(e.tag, str) else ""
        if tag in (_W + "drawing", _W + "pict", _W + "object") or tag.endswith("}imagedata"):
            n += 1
    return n


def _docx_blocks(parsed: Parsed, doc, zf: zipfile.ZipFile) -> None:
    from docling_core.types.doc import ContentLayer, TableCell, TableData  # noqa: PLC0415

    parsed.page_count = None
    last_item: list = [None]  # ultimo item Docling emesso nell'ordine finale (ancora per l'inserimento delle tabelle)
    entries = _docx_body(zf)
    paragraphs = [e for e in entries if e["kind"] == "p"]
    tables = [e for e in entries if e["kind"] == "tbl"]
    hs, order = HeadingStack(), [0]
    emitted_tables: set[int] = set()
    cursor = 0

    def emit_table(entry) -> None:
        cells, ncols = [], 0
        for r, row in enumerate(entry["rows"]):
            for c in row:
                cells.append({"row": r, "col": c["col"], "rowSpan": 1, "colSpan": c["colSpan"], "header": c["header"], "text": c["text"], "path": c["path"]})
                ncols = max(ncols, c["col"] + c["colSpan"])
        text, row_ranges = table_text_and_rows(len(entry["rows"]), ncols, cells)
        n = order[0] + 1
        order[0] = n
        ref = f"b{n}"
        path, refs = hs.path_for(None)
        b = make_block(ref, n - 1, "table", text, heading_path=path, path=f"body/tbl[{entry['ordinal']}]",
                       table={"rows": len(entry["rows"]), "cols": ncols, "cells": cells, "rowRanges": row_ranges})
        b["headingRefs"] = refs
        # la tabella OOXML entra anche nel DoclingDocument di lavoro (non in quello serializzato nel manifest) per il chunker
        td = TableData(num_rows=len(entry["rows"]), num_cols=ncols, table_cells=[
            TableCell(text=c["text"], start_row_offset_idx=c["row"], end_row_offset_idx=c["row"] + 1, start_col_offset_idx=c["col"],
                      end_col_offset_idx=c["col"] + c["colSpan"], column_header=c["header"]) for c in cells])
        if last_item[0] is not None:
            titem = doc.insert_table(last_item[0], td, after=True)
        elif doc.texts:
            titem = doc.insert_table(doc.texts[0], td, after=False)
        else:
            titem = doc.add_table(data=td)
        last_item[0] = titem
        b["itemRef"] = titem.self_ref
        parsed.blocks.append(b)
        parsed.ref_to_block[b["itemRef"]] = [{"blockRef": ref, "itemStart": 0, "itemEnd": len(text), "identity": True}]
        parsed.inventory.append({"itemRef": f"tbl{entry['ordinal']}", "kind": "table", "state": "represented", "blockRefs": [ref], "issueRefs": []})
        emitted_tables.add(entry["ordinal"])

    body_pos = {id(e): i for i, e in enumerate(entries)}
    pictures_refs: list[str] = []
    for item, _lvl in list(doc.iterate_items(included_content_layers={ContentLayer.BODY}, with_groups=False)):
        btype = block_type(item)
        if btype == "table":
            continue  # le tabelle sono lette dall'OOXML (vedi docstring del modulo)
        if btype == "picture":
            n = order[0] + 1
            order[0] = n
            ref = f"b{n}"
            b = make_block(ref, n - 1, "picture", "", heading_path=hs.path_for(None)[0])
            b["headingRefs"] = hs.path_for(None)[1]
            b["itemRef"] = item.self_ref
            parsed.blocks.append(b)
            parsed.ref_to_block[item.self_ref] = []
            pictures_refs.append(ref)
            last_item[0] = item
            continue
        text = getattr(item, "text", "") or ""
        if not text.strip():
            continue
        matched = None
        for idx in range(cursor, len(paragraphs)):
            if norm(paragraphs[idx]["text"]) == norm(text):
                matched, cursor = paragraphs[idx], idx + 1
                break
        if matched is not None:  # prima i blocchi tabella che nell'OOXML precedono questo paragrafo
            for t in tables:
                if t["ordinal"] not in emitted_tables and body_pos[id(t)] < body_pos[id(matched)]:
                    emit_table(t)
        level = heading_level(item)
        n = order[0] + 1
        order[0] = n
        ref = f"b{n}"
        path, refs = hs.path_for(level)
        b = make_block(ref, n - 1, btype, text, heading_path=path, path=f"body/p[{matched['ordinal']}]" if matched else None)
        b["headingRefs"] = refs
        b["itemRef"] = item.self_ref
        parsed.blocks.append(b)
        parsed.ref_to_block[item.self_ref] = [{"blockRef": ref, "itemStart": 0, "itemEnd": len(text), "identity": True}]
        last_item[0] = item
        parsed.inventory.append({"itemRef": f"p{matched['ordinal'] if matched else ref}", "kind": "paragraph", "state": "represented", "blockRefs": [ref], "issueRefs": []})
        if level is not None:
            hs.push(level, text, ref)
        _link_parents(parsed, item, doc)
        if matched is None:
            new_issue(parsed.issues, "docx_path_unavailable", severity="warning", block_ref=ref, affects=False,
                      reason="Percorso strutturale non determinabile per il paragrafo.")
    for t in tables:
        if t["ordinal"] not in emitted_tables:
            emit_table(t)
    for e in paragraphs:  # paragrafi con testo che Docling non ha emesso
        if e["text"].strip() and not any(b["locator"]["path"] == f"body/p[{e['ordinal']}]" for b in parsed.blocks):
            new_issue(parsed.issues, "docx_paragraph_not_extracted", block_ref=None,
                      reason=f"Paragrafo body/p[{e['ordinal']}] con testo non estratto dal convertitore.")

    # elementi informativi non trattati: immagini/oggetti incorporati, note e commenti, relazioni esterne
    total_drawings = sum(e["drawings"] for e in entries)
    for i in range(total_drawings):
        ref = pictures_refs[i] if i < len(pictures_refs) else None
        iss = new_issue(parsed.issues, "unprocessed_embedded_image", block_ref=ref,
                        reason="Immagine o oggetto incorporato potenzialmente informativo non elaborato (OCR e VLM disabilitati).")
        parsed.inventory.append({"itemRef": f"picture{i + 1}", "kind": "picture", "state": "unprocessed", "blockRefs": [ref] if ref else [], "issueRefs": [iss]})
    for name in zf.namelist():
        data = zf.read(name) if name.endswith(".xml") else b""
        has_text = bool(re.search(rb"<w:t(?:\s[^>]*)?>[^<]*\S[^<]*</w:t>", data))
        if _NOTE_PARTS.match(name) and has_text:
            iss = new_issue(parsed.issues, "unprocessed_note_part", reason=f"Parte {name.split('/')[-1]} con testo non estratto.")
            parsed.inventory.append({"itemRef": name, "kind": "part", "state": "unprocessed", "blockRefs": [], "issueRefs": [iss]})
        elif _FURNITURE_PARTS.match(name) and has_text:
            new_issue(parsed.issues, "page_furniture_omitted", severity="warning", affects=False, reason=f"Intestazione/pie di pagina {name.split('/')[-1]} esclusa dal corpo.")
        elif name.startswith("word/embeddings/"):
            iss = new_issue(parsed.issues, "unprocessed_embedded_object", reason="Oggetto incorporato non eseguito ne elaborato.")
            parsed.inventory.append({"itemRef": name, "kind": "embedded", "state": "unprocessed", "blockRefs": [], "issueRefs": [iss]})
    for name in zf.namelist():
        if name.endswith(".rels") and b'TargetMode="External"' in zf.read(name):
            new_issue(parsed.issues, "external_relationship_not_fetched", severity="warning", affects=False,
                      reason="Relazione esterna presente e non seguita.")
    if not parsed.inventory:
        parsed.inventory.append({"itemRef": "document", "kind": "document", "state": "empty", "blockRefs": [], "issueRefs": []})
