"""Processo figlio di una conversione (T022): legge il workspace della richiesta e scrive result.json oppure error.json.

Eseguito come `python conversion_process.py <workdir>` in un nuovo gruppo di processi: il servizio lo termina per intero a timeout.
Nessuna rete: modelli e tokenizer sono caricati solo da MODELS_DIR (HF offline). Esito non previsto = exit code != 0 (503 al chiamante).
"""
import errno
import json
import os
import resource
import sys
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("DOCLING_ARTIFACTS_PATH", os.environ.get("PARSER_MODELS_DIR", "/opt/models"))

sys.path.insert(0, str(Path(__file__).resolve().parent))

import common  # noqa: E402
from common import ParseError  # noqa: E402


def run(workdir: Path) -> None:
    req = json.loads((workdir / "request.json").read_text(encoding="utf-8"))
    limits = req["limits"]
    # tetto di scrittura del file di output: il file troncato non puo essere confuso con un JSON valido (il parent controlla comunque)
    resource.setrlimit(resource.RLIMIT_FSIZE, (limits["maxOutputBytes"] + (4 << 20), limits["maxOutputBytes"] + (4 << 20)))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    data = (workdir / "input.bin").read_bytes()
    fmt = req["format"]

    import chunk_manifest  # noqa: PLC0415  (import pesanti solo nel figlio)
    import converters  # noqa: PLC0415
    import text_sources  # noqa: PLC0415

    if fmt in ("pdf", "docx"):
        parsed = converters.convert_binary(fmt, data, workdir, limits)
    elif fmt == "md":
        parsed = text_sources.convert_markdown(data, limits)
    else:
        parsed = text_sources.convert_txt(data, limits)
    if len(parsed.blocks) > limits["maxBlocks"]:
        raise ParseError("parse_limit_exceeded", 422, "Numero di blocchi oltre il limite.")
    chunks = chunk_manifest.build_chunks(parsed)
    manifest = chunk_manifest.assemble_manifest(parsed, chunks, fmt=fmt, original=data, original_sha256=req["originalSha256"])
    tmp = workdir / "result.json.tmp"
    tmp.write_bytes(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    tmp.rename(workdir / "result.json")


def main() -> int:
    workdir = Path(sys.argv[1])
    try:
        run(workdir)
    except OSError as e:
        if e.errno != errno.EFBIG:
            raise
        (workdir / "error.json").write_text(json.dumps({"code": "parse_limit_exceeded", "status": 422, "message": "Output oltre il limite configurato."}), encoding="utf-8")
        return 0
    except ParseError as e:
        (workdir / "error.json").write_text(json.dumps({"code": e.code, "status": e.status, "message": e.message}), encoding="utf-8")
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
