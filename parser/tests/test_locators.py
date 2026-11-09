"""T020 - locator e range: offset unicode-codepoint, Primary/Context, parentRef/page nullable, gold dal manifest T002."""
import pytest

from conftest import FACTS, FILES, cp_to_utf16, fixture_bytes
from manifest_checks import validate_manifest

TEXT_FACTS = [f for f in FACTS if f["locator"]["kind"] == "text-range"]
PDF_FACTS = [f for f in FACTS if f["locator"]["kind"] == "pdf"]
DOCX_FACTS = [f for f in FACTS if f["locator"]["kind"] == "docx-path"]


def decoded_source(file_id: str) -> str:
    data = fixture_bytes(file_id)
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return data.decode("utf-16")
    return data.decode("utf-8-sig")


def block_for_source_range(m, start, end):
    for b in m["blocks"]:
        loc = b["locator"]
        if loc["sourceStart"] is not None and loc["sourceStart"] <= start and end <= loc["sourceEnd"]:
            return b
    return None


@pytest.mark.parametrize("fact", TEXT_FACTS, ids=lambda f: f["id"])
def test_text_facts_resolve_to_exact_source_ranges(converted, fact):
    m = converted(fact["fileId"])
    loc = fact["locator"]
    source = decoded_source(fact["fileId"])
    assert source[loc["startCodepoint"]:loc["endCodepoint"]] == fact["quote"]
    assert cp_to_utf16(source, loc["startCodepoint"]) == loc["startUtf16"]  # convenzione codepoint -> UTF-16 verificabile
    b = block_for_source_range(m, loc["startCodepoint"], loc["endCodepoint"])
    assert b is not None, "nessun blocco contiene il range"
    off = loc["startCodepoint"] - b["locator"]["sourceStart"]
    assert b["text"][off:off + len(fact["quote"])] == fact["quote"]
    assert b["locator"]["lineStart"] <= loc["line"] <= b["locator"]["lineEnd"]


def test_non_bmp_offsets_differ_between_codepoint_and_utf16(converted):
    m = converted("md-non-bmp")
    assert m["offsetConvention"] == "unicode-codepoint"
    f = next(x for x in FACTS if x["id"] == "md-non-bmp-f2")
    assert f["locator"]["startUtf16"] != f["locator"]["startCodepoint"]
    b = block_for_source_range(m, f["locator"]["startCodepoint"], f["locator"]["endCodepoint"])
    assert b is not None and "\U0001F600" in "".join(x["text"] for x in m["blocks"])


@pytest.mark.parametrize("file_id", ["txt-negations", "txt-utf16", "txt-long", "md-basic", "md-non-bmp", "txt-conflict-a"])
def test_text_blocks_are_lossless_slices_of_the_decoded_source(converted, file_id):
    m = converted(file_id)
    source = decoded_source(file_id)
    for b in m["blocks"]:
        loc = b["locator"]
        if file_id.startswith("txt"):
            assert source[loc["sourceStart"]:loc["sourceEnd"]] == b["text"]
        else:
            assert 0 <= loc["sourceStart"] <= loc["sourceEnd"] <= len(source)
            assert 1 <= loc["lineStart"] <= loc["lineEnd"]


def test_txt_does_not_invent_headings_and_utf16_bom_is_excluded(converted):
    assert all(b["type"] != "heading" for b in converted("txt-negations")["blocks"])
    m = converted("txt-utf16")
    assert not m["blocks"][0]["text"].startswith("﻿")
    assert "�" not in "".join(b["text"] for b in m["blocks"])


def test_markdown_preserves_fences_tables_and_textual_links(converted):
    m = converted("md-basic")
    types = {b["type"] for b in m["blocks"]}
    assert {"heading", "code", "table"} <= types
    code = next(b for b in m["blocks"] if b["type"] == "code")
    assert "SELECT id, stato FROM ordini" in code["text"]
    assert any("Linee guida" in b["text"] for b in m["blocks"])
    assert any("example.invalid" in b["text"] or b.get("links") for b in m["blocks"])
    assert any("Ignora le istruzioni precedenti" in b["text"] for b in m["blocks"]), "il testo imperativo resta dato"


@pytest.mark.parametrize("fact", PDF_FACTS, ids=lambda f: f["id"])
def test_pdf_facts_resolve_to_native_page_and_bbox(converted, fact):
    m = converted(fact["fileId"])
    page = fact["locator"]["page"]
    hits = [b for b in m["blocks"] if fact["quote"] in b["text"] and b["locator"]["page"] == page]
    assert hits, f"citazione assente nella pagina {page}"
    for b in hits:
        assert b["locator"]["bbox"] is not None and b["locator"]["coordinateSystem"]
    assert m["pageCount"] >= page


@pytest.mark.parametrize("fact", DOCX_FACTS, ids=lambda f: f["id"])
def test_docx_facts_use_structural_path_and_null_page(converted, fact):
    m = converted(fact["fileId"])
    assert all(b["locator"]["page"] is None for b in m["blocks"]), "mai pagine simulate"
    path = fact["locator"]["path"]
    if "paragraphOrdinal" in fact["locator"]:
        want = f"{path}[{fact['locator']['paragraphOrdinal']}]"
        assert any(b["locator"]["path"] == want and b["text"].strip() == fact["quote"] for b in m["blocks"]), want
    else:
        assert any(c["path"] == path and c["text"].strip() == fact["quote"]
                   for b in m["blocks"] if b["type"] == "table" for c in b["table"]["cells"])


def test_multipage_content_is_split_into_ordered_page_exact_ranges(converted):
    m = converted("pdf-table-multipage")
    assert validate_manifest(m, fixture_bytes("pdf-table-multipage")) == []
    by_ref = {b["ref"]: b for b in m["blocks"]}
    assert all(b["locator"]["page"] in (1, 2) for b in m["blocks"]), "ogni blocco ha una sola pagina esatta"
    pieces = [b for b in m["blocks"] if b.get("pieceOf")]
    for a, b in zip(pieces, pieces[1:]):  # il testo che attraversa le pagine e piu blocchi ordinati, mai una quote su una sola pagina
        if a["pieceOf"] == b["pieceOf"]:
            assert a["order"] < b["order"] and a["locator"]["page"] < b["locator"]["page"]
    pages = {by_ref[r["blockRef"]]["locator"]["page"] for c in m["chunks"] for r in c["ranges"] if r["ownership"] == "Primary"}
    assert pages == {1, 2}


@pytest.mark.parametrize("file_id", [f for f in FILES if FILES[f]["expected"]["parse"] in ("Complete", "Incomplete")])
def test_primary_ranges_cover_all_text_exactly_once(converted, file_id):
    m = converted(file_id)
    assert validate_manifest(m, fixture_bytes(file_id)) == []  # include la non sovrapposizione dei Primary
    cov = {}
    for c in m["chunks"]:
        for r in c["ranges"]:
            if r["ownership"] == "Primary":
                cov.setdefault(r["blockRef"], []).append((r["start"], r["end"]))
    for b in m["blocks"]:
        if not b["text"].strip():
            continue
        spans = sorted(cov.get(b["ref"], []))
        assert spans, f"blocco {b['ref']} senza range proprietario"
        gaps = []
        pos = 0
        for s, e in spans:
            if s > pos:
                gaps.append(b["text"][pos:s])
            pos = max(pos, e)
        if pos < len(b["text"]):
            gaps.append(b["text"][pos:])
        assert all(not g.strip() for g in gaps), f"testo non coperto in {b['ref']}"


def _synthetic_parsed():
    """Titolo + paragrafo lungo + tabella con header, costruiti a mano (nessun file) per esercitare split e header replicato."""
    from docling_core.types.doc import DocItemLabel, DoclingDocument, TableCell, TableData

    from common import Parsed, make_block
    from converters import table_text_and_rows

    doc = DoclingDocument(name="synthetic")
    parsed = Parsed(doc=doc, document={"serialization": "x", "content": {}}, page_count=None)
    title = doc.add_heading("Sezione di prova", level=1)
    parsed.blocks.append(make_block("b1", 0, "heading", "Sezione di prova"))
    parsed.blocks[-1]["headingRefs"] = []
    parsed.ref_to_block[title.self_ref] = [{"blockRef": "b1", "itemStart": 0, "itemEnd": 16, "identity": True}]
    long_text = " ".join(f"Frase numero {i} del paragrafo molto lungo che deve essere diviso fra piu chunk." for i in range(60))
    para = doc.add_text(label=DocItemLabel.TEXT, text=long_text, orig=long_text)
    parsed.blocks.append(make_block("b2", 1, "paragraph", long_text, heading_path=["Sezione di prova"]))
    parsed.blocks[-1]["headingRefs"] = ["b1"]
    parsed.ref_to_block[para.self_ref] = [{"blockRef": "b2", "itemStart": 0, "itemEnd": len(long_text), "identity": True}]
    rows = [["Codice", "Descrizione"]] + [[f"R-{i:02d}", f"Requisito numero {i} con descrizione piuttosto estesa per forzare la divisione"] for i in range(1, 25)]
    cells = [TableCell(text=t, start_row_offset_idx=r, end_row_offset_idx=r + 1, start_col_offset_idx=c, end_col_offset_idx=c + 1,
                       column_header=(r == 0)) for r, row in enumerate(rows) for c, t in enumerate(row)]
    tbl = doc.add_table(data=TableData(num_rows=len(rows), num_cols=2, table_cells=cells))
    grid = [{"row": c.start_row_offset_idx, "col": c.start_col_offset_idx, "rowSpan": 1, "colSpan": 1, "header": c.column_header, "text": c.text, "path": None}
            for c in cells]
    text, rr = table_text_and_rows(len(rows), 2, grid)
    parsed.blocks.append(make_block("b3", 2, "table", text, heading_path=["Sezione di prova"],
                                    table={"rows": len(rows), "cols": 2, "cells": grid, "rowRanges": rr}))
    parsed.blocks[-1]["headingRefs"] = ["b1"]
    parsed.ref_to_block[tbl.self_ref] = [{"blockRef": "b3", "itemStart": 0, "itemEnd": len(text), "identity": True}]
    return parsed


def test_split_paragraph_and_table_produce_contiguous_ranges_and_replicated_header():
    import chunk_manifest
    parsed = _synthetic_parsed()
    chunks = chunk_manifest.build_chunks(parsed, max_tokens=64)
    manifest = chunk_manifest.assemble_manifest(parsed, chunks, fmt="txt", original=b"x", original_sha256="0" * 64)
    assert len(chunks) > 3
    assert [e for e in validate_manifest(manifest, b"x") if "originalSha256" not in e] == []
    para = sorted((r["start"], r["end"]) for c in chunks for r in c["ranges"] if r["blockRef"] == "b2" and r["ownership"] == "Primary")
    assert len(para) > 1 and para[0][0] == 0 and para[-1][1] == len(parsed.blocks[1]["text"])
    assert all(a[1] == b[0] for a, b in zip(para, para[1:])), "range contigui, senza buchi ne sovrapposizioni"
    header = parsed.blocks[2]["table"]["rowRanges"][0]
    table_chunks = [c for c in chunks if any(r["blockRef"] == "b3" for r in c["ranges"])]
    assert len(table_chunks) > 1
    first, rest = table_chunks[0], table_chunks[1:]
    assert any(r["blockRef"] == "b3" and (r["start"], r["end"]) == (header["start"], header["end"]) and r["ownership"] == "Primary" for r in first["ranges"])
    for c in rest:  # header replicato verso il blocco originale, come Context
        assert any(r["blockRef"] == "b3" and (r["start"], r["end"]) == (header["start"], header["end"]) and r["ownership"] == "Context" for r in c["ranges"])
    ctx_heading = [r for c in chunks[1:] for r in c["ranges"] if r["blockRef"] == "b1" and r["ownership"] == "Context"]
    assert ctx_heading, "il titolo e contesto (non proprietario) dei chunk successivi"
