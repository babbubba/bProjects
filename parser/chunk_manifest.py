"""Chunk e range (T025) e assemblaggio del manifest document-parse-1.

HybridChunker con tokenizer locale (nessun download a runtime) propone gruppi strutturali. L'unita di provenienza e il range del
blocco originale: ogni chunk elenca `{rangeKey, blockRef, start, end, ownership, sourceTextSha256}` con offset in codepoint Unicode
sul testo del blocco. Il contesto aggiunto (titoli, header di tabella) e `Context`, mai citabile da solo; il testo di un blocco e
`Primary` in un solo chunk (frasi/righe suddivise = range contigui senza sovrapposizione). TokenCount e una stima del tokenizer
locale, non una misura del runtime DGX.
"""
import re

from common import (CONFIG, MODELS_DIR, OFFSET_CONVENTION, PARSER_VERSION, SCHEMA_VERSION, Parsed, config_hash, dependency_lock_hash,
                    load_model_artifacts, model_artifacts_hash, new_issue, sha256_bytes)

TOKENIZER_DIR = "tokenizer--all-MiniLM-L6-v2"
_WS = re.compile(r"\s+")


def _tokenizer_id() -> str:
    for s in load_model_artifacts().get("sources", []):
        if s["dir"] == TOKENIZER_DIR:
            return f"{s['repo']}@{s['revision']}"
    return TOKENIZER_DIR


def _chunker(max_tokens: int):
    from docling_core.transforms.chunker.hybrid_chunker import HybridChunker  # noqa: PLC0415
    from docling_core.transforms.chunker.tokenizer.huggingface import HuggingFaceTokenizer  # noqa: PLC0415
    from transformers import AutoTokenizer  # noqa: PLC0415

    tok = HuggingFaceTokenizer(tokenizer=AutoTokenizer.from_pretrained(str(MODELS_DIR / TOKENIZER_DIR), local_files_only=True),
                               max_tokens=max_tokens)
    return HybridChunker(tokenizer=tok, merge_peers=CONFIG["chunker"]["mergePeers"]), tok


def _norm_map(text: str) -> tuple[str, list[int]]:
    """Testo con spazi collassati e, per ogni carattere normalizzato, l'indice nel testo originale."""
    out, idx, prev_ws = [], [], True
    for i, ch in enumerate(text):
        if ch.isspace():
            if not prev_ws:
                out.append(" ")
                idx.append(i)
            prev_ws = True
        else:
            out.append(ch)
            idx.append(i)
            prev_ws = False
    return "".join(out).rstrip(), idx[:len("".join(out).rstrip())]


def _windows(item_text: str, chunk_texts: list[str]) -> list[tuple[int, int]]:
    """Partizione contigua del testo di un item suddiviso fra piu chunk: [(inizio, fine)] nel testo originale, senza buchi ne sovrapposizioni."""
    nt, idx = _norm_map(item_text)
    total = sum(max(1, len(_WS.sub(" ", c).strip())) for c in chunk_texts)
    bounds, cur = [], 0
    for k, ct in enumerate(chunk_texts):
        nc = _WS.sub(" ", ct).strip()
        if k == len(chunk_texts) - 1:
            end = len(nt)
        else:
            pos = nt.find(nc, cur) if nc else -1
            end = pos + len(nc) if pos >= 0 else min(len(nt), cur + round(len(nt) * max(1, len(nc)) / total))
        bounds.append(end)
        cur = end
    out, prev = [], 0
    for k, end_n in enumerate(bounds):
        end = len(item_text) if k == len(bounds) - 1 else (idx[end_n] if end_n < len(idx) else len(item_text))
        out.append((prev, max(prev, end)))
        prev = max(prev, end)
    return out


def build_chunks(parsed: Parsed, max_tokens: int | None = None) -> list[dict]:
    max_tokens = max_tokens or CONFIG["chunker"]["maxTokens"]
    chunker, tok = _chunker(max_tokens)
    raw = list(chunker.chunk(dl_doc=parsed.doc))
    blocks = {b["ref"]: b for b in parsed.blocks}
    tokenizer_id = _tokenizer_id()

    occ: dict[str, list[int]] = {}
    for ci, ch in enumerate(raw):
        for it in ch.meta.doc_items:
            occ.setdefault(it.self_ref, []).append(ci)
    item_text = {}
    for ch in raw:
        for it in ch.meta.doc_items:
            item_text.setdefault(it.self_ref, getattr(it, "text", "") or "")
    windows: dict[tuple[str, int], tuple[int, int]] = {}
    for ref, cis in occ.items():
        segs = parsed.ref_to_block.get(ref)
        if len(cis) > 1 and segs and blocks[segs[0]["blockRef"]]["type"] != "table":
            for ci, w in zip(cis, _windows(item_text[ref], [raw[c].text for c in cis])):
                windows[(ref, ci)] = w
    table_state: dict[str, dict] = {}

    out = []
    for ci, ch in enumerate(raw):
        prim: list[tuple[str, int, int]] = []
        ctx: list[tuple[str, int, int]] = []
        for it in ch.meta.doc_items:
            segs = parsed.ref_to_block.get(it.self_ref) or []
            if not segs:
                continue
            b0 = blocks[segs[0]["blockRef"]]
            if b0["type"] == "table" and len(occ[it.self_ref]) > 1:
                _table_ranges(b0, ch.text, ci == occ[it.self_ref][-1], table_state.setdefault(b0["ref"], {"next": 0, "header": False}), prim, ctx)
                continue
            for seg in segs:
                blk = blocks[seg["blockRef"]]
                if not blk["text"]:
                    continue
                if (it.self_ref, ci) in windows:
                    a, b = windows[(it.self_ref, ci)]
                    lo, hi = max(a, seg["itemStart"]), min(b, seg["itemEnd"])
                    if lo >= hi:
                        continue
                    base, span = seg["itemStart"], max(1, seg["itemEnd"] - seg["itemStart"])
                    if seg["identity"]:
                        s, e = lo - base, hi - base
                    else:
                        scale = len(blk["text"]) / span
                        s, e = round((lo - base) * scale), round((hi - base) * scale)
                    s, e = max(0, s), min(len(blk["text"]), e)
                    if s < e:
                        prim.append((blk["ref"], s, e))
                else:
                    prim.append((blk["ref"], 0, len(blk["text"])))
        prim = list(dict.fromkeys(prim))
        if not prim:
            continue
        first = blocks[prim[0][0]]
        in_prim = {p[0] for p in prim}
        for href in first.get("headingRefs", []):
            if href not in in_prim and blocks[href]["text"]:
                ctx.append((href, 0, len(blocks[href]["text"])))
        key = f"c{len(out) + 1}"
        ranges = []
        for owner, items in (("Primary", prim), ("Context", list(dict.fromkeys(ctx)))):
            for blk_ref, s, e in items:
                if owner == "Context" and (blk_ref, s, e) in prim:
                    continue
                ranges.append({"rangeKey": f"{key}:{len(ranges) + 1}", "blockRef": blk_ref, "start": s, "end": e, "ownership": owner,
                               "sourceTextSha256": blocks[blk_ref]["textSha256"]})
        out.append({"chunkKey": key, "text": ch.text, "contextualizedText": chunker.contextualize(ch), "headingPath": first["headingPath"],
                    "ranges": ranges, "tokenCount": tok.count_tokens(ch.text), "tokenizerId": tokenizer_id, "tokenSource": "estimated"})
    _own_headings(out, parsed.blocks, blocks, tokenizer_id, tok)
    return out


def _own_headings(chunks: list[dict], block_list: list[dict], blocks: dict, tokenizer_id: str, tok) -> None:
    """HybridChunker non emette i titoli come contenuto (solo come contesto): un titolo senza range proprietario diventa Primary
    nel primo chunk che lo usa come contesto (il suo testo resta citabile una volta sola, altrove e Context)."""
    owned = {r["blockRef"] for c in chunks for r in c["ranges"] if r["ownership"] == "Primary"}
    for h in block_list:
        if h["type"] != "heading" or not h["text"].strip() or h["ref"] in owned:
            continue
        flipped = False
        for c in chunks:
            for r in c["ranges"]:
                if r["blockRef"] == h["ref"] and r["ownership"] == "Context":
                    r["ownership"] = "Primary"
                    flipped = True
                    break
            if flipped:
                break
        if flipped:
            continue
        # titolo mai usato come contesto (es. nessun contenuto sotto di esso): agganciato al chunk successivo, o all'ultimo
        target = next((c for c in chunks if min(blocks[r["blockRef"]]["order"] for r in c["ranges"]) > h["order"]), chunks[-1] if chunks else None)
        rng = {"blockRef": h["ref"], "start": 0, "end": len(h["text"]), "ownership": "Primary", "sourceTextSha256": h["textSha256"]}
        if target is None:
            chunks.append({"chunkKey": f"c{len(chunks) + 1}", "text": h["text"], "contextualizedText": h["text"], "headingPath": h["headingPath"],
                           "ranges": [{**rng, "rangeKey": f"c{len(chunks) + 1}:1"}], "tokenCount": tok.count_tokens(h["text"]),
                           "tokenizerId": tokenizer_id, "tokenSource": "estimated"})
        else:
            target["ranges"].append({**rng, "rangeKey": f"{target['chunkKey']}:{len(target['ranges']) + 1}"})


def _table_ranges(block: dict, chunk_text: str, is_last: bool, state: dict, prim: list, ctx: list) -> None:
    """Righe di una tabella suddivisa fra piu chunk: righe dati in ordine, header sempre presente (Primary la prima volta, poi Context)."""
    rr = block["table"]["rowRanges"]
    headers = [r for r in rr if r["header"]]
    data = [r for r in rr if not r["header"]]
    target = prim if not state["header"] else ctx
    for r in headers:
        target.append((block["ref"], r["start"], r["end"]))
    state["header"] = True
    cells_by_row = {}
    for c in block["table"]["cells"]:
        cells_by_row.setdefault(c["row"], []).append(c["text"])
    taken = 0
    while state["next"] < len(data):
        r = data[state["next"]]
        texts = [t for t in cells_by_row.get(r["row"], []) if t]
        if taken >= 1 and not is_last and not all(t in chunk_text for t in texts):
            break  # la prima riga e sempre assegnata (nessun chunk senza range proprietario); l'ultimo chunk assorbe il resto
        prim.append((block["ref"], r["start"], r["end"]))
        state["next"] += 1
        taken += 1


def assemble_manifest(parsed: Parsed, chunks: list[dict], *, fmt: str, original: bytes, original_sha256: str) -> dict:
    # ogni blocco con testo deve avere un range proprietario, altrimenti e un elemento non trattato (issue bloccante)
    covered: dict[str, int] = {}
    for c in chunks:
        for r in c["ranges"]:
            if r["ownership"] == "Primary":
                covered[r["blockRef"]] = covered.get(r["blockRef"], 0) + (r["end"] - r["start"])
    for b in parsed.blocks:
        if b["text"].strip() and covered.get(b["ref"], 0) < len(b["text"].strip()):
            iss = new_issue(parsed.issues, "block_not_chunked", block_ref=b["ref"], reason="Il testo del blocco non e interamente coperto dai chunk.")
            for inv in parsed.inventory:
                if b["ref"] in inv["blockRefs"]:
                    inv["issueRefs"].append(iss)
                    inv["state"] = "unprocessed"
    incomplete = any(i["affectsCompleteness"] for i in parsed.issues) or any(i["state"] == "unprocessed" for i in parsed.inventory)
    return {
        "schemaVersion": SCHEMA_VERSION, "originalSha256": original_sha256, "parserVersion": PARSER_VERSION,
        "dependencyLockHash": dependency_lock_hash(), "modelArtifactsHash": model_artifacts_hash(), "configHash": config_hash(),
        "status": "Incomplete" if incomplete else "Complete", "originFormat": fmt, "originalByteLength": len(original),
        "pageCount": parsed.page_count, "offsetConvention": OFFSET_CONVENTION, "document": parsed.document,
        "blocks": parsed.blocks, "inventory": parsed.inventory, "chunks": chunks, "issues": parsed.issues,
    }


__all__ = ["build_chunks", "assemble_manifest", "sha256_bytes"]
