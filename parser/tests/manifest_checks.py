"""Validatore del manifest document-parse-1 (lato test): gli stessi invarianti che il backend ricontrolla (parser.md)."""
import hashlib
import re

SHA = re.compile(r"^[0-9a-f]{64}$")
STATUS = {"Complete", "Incomplete"}
BLOCK_TYPES = {"heading", "paragraph", "list", "table", "code", "picture", "other"}
OWNERSHIP = {"Primary", "Context"}
SEVERITY = {"blocking", "warning"}
INV_STATE = {"represented", "unprocessed", "empty"}
TOP = ["schemaVersion", "originalSha256", "parserVersion", "dependencyLockHash", "modelArtifactsHash", "configHash",
       "status", "originFormat", "originalByteLength", "pageCount", "offsetConvention", "document", "blocks",
       "inventory", "chunks", "issues"]


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def validate_manifest(m: dict, original: bytes | None = None) -> list[str]:
    errs: list[str] = []
    for k in TOP:
        if k not in m:
            errs.append(f"campo mancante: {k}")
    if errs:
        return errs
    if m["schemaVersion"] != "document-parse-1":
        errs.append("schemaVersion")
    for k in ("originalSha256", "dependencyLockHash", "modelArtifactsHash", "configHash"):
        if not SHA.match(str(m[k])):
            errs.append(f"{k} non e sha256")
    if original is not None:
        if m["originalSha256"] != hashlib.sha256(original).hexdigest():
            errs.append("originalSha256 non corrisponde ai byte")
        if m["originalByteLength"] != len(original):
            errs.append("originalByteLength")
    if m["status"] not in STATUS:
        errs.append("status")
    if m["offsetConvention"] != "unicode-codepoint":
        errs.append("offsetConvention")
    if m["pageCount"] is not None and (not isinstance(m["pageCount"], int) or m["pageCount"] < 0):
        errs.append("pageCount")

    blocks = m["blocks"]
    refs = [b.get("ref") for b in blocks]
    if len(set(refs)) != len(refs):
        errs.append("ref dei blocchi non univoci")
    by_ref = {b["ref"]: b for b in blocks if "ref" in b}
    orders = [b.get("order") for b in blocks]
    if orders != sorted(orders) or len(set(orders)) != len(orders):
        errs.append("order dei blocchi non strettamente crescente")
    for b in blocks:
        missing = [k for k in ("ref", "order", "parentRef", "type", "text", "headingPath", "locator", "textSha256") if k not in b]
        if missing:
            errs.append(f"blocco {b.get('ref')}: manca {missing}")
            continue
        if b["type"] not in BLOCK_TYPES:
            errs.append(f"blocco {b['ref']}: type")
        if b["textSha256"] != sha256_text(b["text"]):
            errs.append(f"blocco {b['ref']}: textSha256")
        if b["parentRef"] is not None and b["parentRef"] not in by_ref:
            errs.append(f"blocco {b['ref']}: parentRef inesistente")
        loc = b["locator"]
        for k in ("page", "bbox", "coordinateSystem", "path", "lineStart", "lineEnd", "sourceStart", "sourceEnd"):
            if k not in loc:
                errs.append(f"blocco {b['ref']}: locator.{k}")
        if loc.get("bbox") is not None and loc.get("coordinateSystem") is None:
            errs.append(f"blocco {b['ref']}: bbox senza coordinateSystem")
        if loc.get("page") is not None and (not isinstance(loc["page"], int) or loc["page"] < 1):
            errs.append(f"blocco {b['ref']}: page")
        if b["type"] == "table":
            t = b.get("table")
            if not t:
                errs.append(f"blocco {b['ref']}: tabella senza celle")
            else:
                for c in t["cells"]:
                    if not (0 <= c["row"] < t["rows"] and 0 <= c["col"] < t["cols"]):
                        errs.append(f"blocco {b['ref']}: cella fuori griglia")
                    if c["rowSpan"] < 1 or c["colSpan"] < 1:
                        errs.append(f"blocco {b['ref']}: span")
    for b in blocks:  # parent senza cicli
        seen, cur = set(), b.get("parentRef")
        while cur is not None:
            if cur in seen or cur == b.get("ref"):
                errs.append(f"ciclo di parent da {b['ref']}")
                break
            seen.add(cur)
            cur = by_ref.get(cur, {}).get("parentRef")

    for ch in m["chunks"]:
        missing = [k for k in ("chunkKey", "text", "contextualizedText", "headingPath", "ranges", "tokenCount", "tokenizerId", "tokenSource") if k not in ch]
        if missing:
            errs.append(f"chunk: manca {missing}")
            continue
        if ch["tokenSource"] != "estimated":
            errs.append("tokenSource deve essere estimated")
        if not ch["ranges"] or not any(r.get("ownership") == "Primary" for r in ch["ranges"]):
            errs.append(f"chunk {ch['chunkKey']}: nessun range Primary")
        for r in ch["ranges"]:
            if any(k not in r for k in ("rangeKey", "blockRef", "start", "end", "ownership", "sourceTextSha256")):
                errs.append("range: campi mancanti")
                continue
            b = by_ref.get(r["blockRef"])
            if b is None:
                errs.append(f"range verso blocco inesistente {r['blockRef']}")
                continue
            if r["ownership"] not in OWNERSHIP:
                errs.append("ownership")
            if not (0 <= r["start"] < r["end"] <= len(b["text"])):
                errs.append(f"range fuori testo in {r['blockRef']}")
            if r["sourceTextSha256"] != b["textSha256"]:
                errs.append(f"range {r['rangeKey']}: hash sorgente")
    prim: dict[str, list[tuple[int, int]]] = {}  # nessuna duplicazione proprietaria
    for ch in m["chunks"]:
        for r in ch.get("ranges", []):
            if r.get("ownership") == "Primary" and r.get("blockRef") in by_ref:
                prim.setdefault(r["blockRef"], []).append((r["start"], r["end"]))
    for ref, rs in prim.items():
        rs.sort()
        for (_s1, e1), (s2, _e2) in zip(rs, rs[1:]):
            if s2 < e1:
                errs.append(f"range Primary sovrapposti in {ref}")
    keys = [r["rangeKey"] for ch in m["chunks"] for r in ch.get("ranges", []) if "rangeKey" in r]
    if len(set(keys)) != len(keys):
        errs.append("rangeKey non univoche")

    issue_refs = set()
    for i in m["issues"]:
        missing = [k for k in ("issueRef", "code", "severity", "page", "blockRef", "reason", "affectsCompleteness") if k not in i]
        if missing:
            errs.append(f"issue: manca {missing}")
        if i.get("severity") not in SEVERITY:
            errs.append("issue severity")
        issue_refs.add(i.get("issueRef"))
    for it in m["inventory"]:
        missing = [k for k in ("itemRef", "kind", "state", "blockRefs", "issueRefs") if k not in it]
        if missing:
            errs.append(f"inventory: manca {missing}")
            continue
        if it["state"] not in INV_STATE:
            errs.append("inventory state")
        for br in it["blockRefs"]:
            if br not in by_ref:
                errs.append(f"inventory verso blocco inesistente {br}")
        for ir in it["issueRefs"]:
            if ir not in issue_refs:
                errs.append(f"inventory verso issue inesistente {ir}")
        if it["state"] == "represented" and not it["blockRefs"]:
            errs.append(f"inventory {it['itemRef']} represented senza blocchi")
        if it["state"] == "unprocessed" and not it["issueRefs"]:
            errs.append(f"inventory {it['itemRef']} unprocessed senza issue")
    incomplete = any(i.get("affectsCompleteness") for i in m["issues"]) or any(it.get("state") == "unprocessed" for it in m["inventory"])
    if incomplete and m["status"] != "Incomplete":
        errs.append("status Complete con elementi non trattati")
    if not incomplete and m["status"] != "Complete":
        errs.append("status Incomplete senza motivo")
    return errs
