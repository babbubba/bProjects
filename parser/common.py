"""Costanti e funzioni condivise del parser document-parse-1 (nessuno stato, nessun accesso a rete/DB)."""
import hashlib
import json
import os
from pathlib import Path

SCHEMA_VERSION = "document-parse-1"
PARSER_VERSION = "1.0.0"
OFFSET_CONVENTION = "unicode-codepoint"
FORMATS = ("pdf", "docx", "md", "txt")
DOCLING_PIN = "2.129.0"

APP_DIR = Path(__file__).resolve().parent
MODELS_DIR = Path(os.environ.get("PARSER_MODELS_DIR", "/opt/models"))
MODEL_ARTIFACTS_FILE = Path(os.environ.get("PARSER_MODEL_ARTIFACTS", str(APP_DIR / "model-artifacts.json")))
LOCK_FILE = Path(os.environ.get("PARSER_LOCK_FILE", str(APP_DIR / "requirements.lock")))

# Parametri che determinano il risultato della conversione: entrano in configHash.
CONFIG = {
    "schema": SCHEMA_VERSION,
    "offsetConvention": OFFSET_CONVENTION,
    "formats": list(FORMATS),
    "pdf": {"ocr": False, "tableStructure": True, "vlm": False, "pictureDescription": False, "enrichment": False,
            "remoteServices": False, "plugins": False},
    "docx": {"fetchExternal": False, "macroEnabled": False},
    "md": {"fetchImages": False, "fetchLinks": False},
    "txt": {"encodings": ["utf-8", "utf-8-sig", "utf-16-bom"], "replacement": False},
    "chunker": {"type": "HybridChunker", "maxTokens": 1024, "mergePeers": True, "tokenSource": "estimated"},
    "txtMaxBlockCodepoints": 8000,
}

# Limiti server (il backend puo solo restringerli).
SERVER_LIMITS = {
    "maxBytes": 250 * 1024 * 1024,
    "maxExpandedBytes": 250 * 1024 * 1024,
    "maxPages": 2000,
    "maxBlocks": 500_000,
    "maxOutputBytes": 250 * 1024 * 1024,
    "timeoutSeconds": 900,
    "maxZipEntries": 10_000,
}


class ParseError(Exception):
    """Errore del contratto: codice stabile, stato HTTP e messaggio gia redatto (mai contenuti del documento)."""

    def __init__(self, code: str, status: int, message: str):
        super().__init__(message)
        self.code, self.status, self.message = code, status, message


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_text(text: str) -> str:
    return sha256_bytes(text.encode("utf-8"))


def canonical_json(obj) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def config_hash() -> str:
    return sha256_bytes(canonical_json(CONFIG))


def dependency_lock_hash() -> str:
    return sha256_bytes(LOCK_FILE.read_bytes()) if LOCK_FILE.is_file() else "0" * 64


def load_model_artifacts() -> dict:
    return json.loads(MODEL_ARTIFACTS_FILE.read_text(encoding="utf-8"))


def model_artifacts_hash() -> str:
    """Hash dell'elenco (percorso, sha256, size) dei file dei modelli: identifica i pesi senza rileggerli."""
    files = load_model_artifacts().get("files", [])
    return sha256_bytes(canonical_json([[f["path"], f["sha256"], f["size"]] for f in files]))


def verify_model_files() -> list[str]:
    """Confronta i pesi presenti in MODELS_DIR con model-artifacts.json. Ritorna le discrepanze (vuoto = OK)."""
    problems = []
    for f in load_model_artifacts().get("files", []):
        p = MODELS_DIR / f["path"]
        if not p.is_file():
            problems.append(f"mancante: {f['path']}")
            continue
        h = hashlib.sha256()
        with p.open("rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest() != f["sha256"]:
            problems.append(f"hash diverso: {f['path']}")
    return problems


def versions() -> dict:
    from importlib.metadata import version
    return {"doclingVersion": version("docling"), "doclingCoreVersion": version("docling-core")}


from dataclasses import dataclass, field  # noqa: E402


@dataclass
class Parsed:
    """Risultato di una conversione prima dei chunk: DoclingDocument, blocchi canonici, inventario e issue."""
    doc: object                      # DoclingDocument (anche per TXT: costruito in memoria per il chunker)
    document: dict                   # {"serialization": ..., "content": ...} incluso nel manifest
    blocks: list = field(default_factory=list)
    inventory: list = field(default_factory=list)
    issues: list = field(default_factory=list)
    page_count: int | None = None
    ref_to_block: dict = field(default_factory=dict)   # self_ref dell'item Docling -> ref del blocco
    source_text: str | None = None   # solo MD/TXT: testo decodificato


def new_issue(issues: list, code: str, *, severity="blocking", page=None, block_ref=None, reason: str, affects=True) -> str:
    ref = f"i{len(issues) + 1}"
    issues.append({"issueRef": ref, "code": code, "severity": severity, "page": page, "blockRef": block_ref,
                   "reason": reason, "affectsCompleteness": affects})
    return ref


def make_block(ref: str, order: int, btype: str, text: str, *, parent=None, heading_path=None, page=None, bbox=None,
               coord=None, path=None, line_start=None, line_end=None, src_start=None, src_end=None, table=None,
               pages=None) -> dict:
    b = {"ref": ref, "order": order, "parentRef": parent, "type": btype, "text": text, "textSha256": sha256_text(text),
         "headingPath": heading_path or [],
         "locator": {"page": page, "pages": pages or ([page] if page else []), "bbox": bbox, "coordinateSystem": coord,
                     "path": path, "lineStart": line_start, "lineEnd": line_end, "sourceStart": src_start, "sourceEnd": src_end}}
    if table is not None:
        b["table"] = table
    return b
