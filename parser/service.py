"""Servizio interno del parser (T022): /health, /version e /convert sincrono, una conversione per istanza.

Nessun DB, coda durevole, LLM, mount di originali o retry autonomo: il backend e l'unico proprietario di stato, lease e
retry. Ogni conversione gira in un subprocess con gruppo di processi proprio, terminato per intero a timeout.
"""
import hashlib
import hmac
import json
import logging
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, Form, Header, UploadFile
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, Response

import common
from common import FORMATS, SERVER_LIMITS, ParseError

log = logging.getLogger("parser")

WORK_ROOT = Path(os.environ.get("PARSER_WORK_DIR", tempfile.gettempdir()))
CONVERSION_SLOT = threading.Semaphore(1)  # una conversione per istanza
BUSY_RETRY_AFTER_SECONDS = 5
KILL_GRACE_SECONDS = 5
state = {"model_problems": None}
last_process_group: int | None = None  # osservabile dai test: dopo la risposta il gruppo non deve esistere piu


@asynccontextmanager
async def lifespan(_app):
    state["model_problems"] = common.verify_model_files()
    if state["model_problems"]:
        log.error("artefatti dei modelli non conformi: %d", len(state["model_problems"]))
    yield


app = FastAPI(title="bProjects parser", docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)


def _error(code: str, status: int, message: str, headers: dict | None = None) -> JSONResponse:
    return JSONResponse({"code": code, "message": message}, status_code=status, headers=headers)


@app.exception_handler(ParseError)
async def _parse_error(_req, exc: ParseError):
    return _error(exc.code, exc.status, exc.message)


@app.exception_handler(RequestValidationError)
async def _validation_error(_req, _exc):  # nessun eco dei valori ricevuti
    return _error("invalid_request", 400, "Richiesta non valida.")


@app.get("/health")
def health():
    problems = state["model_problems"]
    if problems is None or problems:
        return _error("models_unavailable", 503, "Artefatti dei modelli non verificati.")
    return {"status": "ok"}


@app.get("/version")
def version():
    return {
        "schema": common.SCHEMA_VERSION, "parserVersion": common.PARSER_VERSION, **common.versions(),
        "configHash": common.config_hash(), "dependencyLockHash": common.dependency_lock_hash(),
        "modelArtifactsHash": common.model_artifacts_hash(), "formats": list(FORMATS),
        "offsetConvention": common.OFFSET_CONVENTION,
    }


def _clamp(value: int, key: str) -> int:
    if value < 1:
        raise ParseError("invalid_request", 400, "Limite non valido.")
    return min(value, SERVER_LIMITS[key])


def _kill_group(pgid: int) -> None:
    try:
        os.killpg(pgid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        pass


@app.post("/convert")
def convert(
    file: UploadFile = File(...),
    declaredFormat: str = Form(...),
    documentVersionId: str = Form(...),
    originalSha256: str = Form(...),
    configHash: str = Form(...),
    maxExpandedBytes: int = Form(...),
    maxPages: int = Form(...),
    maxBlocks: int = Form(...),
    maxOutputBytes: int = Form(...),
    timeoutSeconds: int = Form(...),
    x_internal_secret: str | None = Header(default=None),
):
    global last_process_group
    secret = os.environ.get("PARSER_INTERNAL_SECRET", "")
    if not secret or x_internal_secret is None or not hmac.compare_digest(x_internal_secret.encode(), secret.encode()):
        raise ParseError("unauthorized", 401, "Non autorizzato.")
    if declaredFormat not in FORMATS:
        raise ParseError("unsupported_format", 415, "Formato non ammesso.")
    if configHash != common.config_hash():
        raise ParseError("parser_config_mismatch", 409, "Configurazione del parser non compatibile.")
    if not CONVERSION_SLOT.acquire(blocking=False):
        return _error("parser_busy", 429, "Parser occupato.", {"Retry-After": str(BUSY_RETRY_AFTER_SECONDS)})
    workdir: Path | None = None
    pgid: int | None = None
    try:
        limits = {
            "maxExpandedBytes": _clamp(maxExpandedBytes, "maxExpandedBytes"), "maxPages": _clamp(maxPages, "maxPages"),
            "maxBlocks": _clamp(maxBlocks, "maxBlocks"), "maxOutputBytes": _clamp(maxOutputBytes, "maxOutputBytes"),
            "maxZipEntries": SERVER_LIMITS["maxZipEntries"],
        }
        timeout = _clamp(timeoutSeconds, "timeoutSeconds")
        workdir = Path(tempfile.mkdtemp(prefix="req-", dir=WORK_ROOT))
        size, digest = 0, hashlib.sha256()
        with (workdir / "input.bin").open("wb") as out:
            while chunk := file.file.read(1 << 20):
                size += len(chunk)
                if size > SERVER_LIMITS["maxBytes"]:
                    raise ParseError("parse_limit_exceeded", 422, "Dimensione oltre il limite del parser.")
                digest.update(chunk)
                out.write(chunk)
        if digest.hexdigest() != originalSha256.lower():
            raise ParseError("original_hash_mismatch", 415, "Hash dell'originale non coerente con i byte ricevuti.")
        (workdir / "request.json").write_text(json.dumps({
            "format": declaredFormat, "documentVersionId": documentVersionId, "originalSha256": originalSha256.lower(),
            "limits": limits}), encoding="utf-8")

        env = {k: v for k, v in os.environ.items() if k not in ("PARSER_INTERNAL_SECRET",)}
        env.update({"HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1", "HF_HOME": str(workdir / "hf"), "HOME": str(workdir),
                    "TMPDIR": str(workdir), "PYTHONDONTWRITEBYTECODE": "1", "OMP_NUM_THREADS": "2", "MPLCONFIGDIR": str(workdir / "mpl")})
        proc = subprocess.Popen(
            [sys.executable, str(common.APP_DIR / "conversion_process.py"), str(workdir)],
            cwd=str(common.APP_DIR), env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            start_new_session=True)
        pgid = last_process_group = proc.pid
        try:
            proc.wait(timeout=timeout)  # il timeout decorre dall'avvio effettivo del subprocess
        except subprocess.TimeoutExpired:
            _kill_group(pgid)
            try:
                proc.wait(timeout=KILL_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                log.error("subprocess non terminato dopo SIGKILL")
            raise ParseError("parse_timeout", 422, "Timeout di conversione.") from None
        err = workdir / "error.json"
        if err.is_file():
            e = json.loads(err.read_text(encoding="utf-8"))
            raise ParseError(e["code"], e["status"], e["message"])
        result = workdir / "result.json"
        if proc.returncode == -signal.SIGXFSZ:  # RLIMIT_FSIZE: output oltre il limite, mai JSON troncato presentato come valido
            raise ParseError("parse_limit_exceeded", 422, "Output oltre il limite configurato.")
        if proc.returncode != 0 or not result.is_file():
            log.error("conversione terminata in modo anomalo (rc=%s)", proc.returncode)
            raise ParseError("parser_crash", 503, "Conversione terminata in modo anomalo.")
        if result.stat().st_size > limits["maxOutputBytes"]:
            raise ParseError("parse_limit_exceeded", 422, "Output oltre il limite configurato.")
        return Response(content=result.read_bytes(), media_type="application/json")
    except ParseError:
        raise
    except Exception:  # noqa: BLE001 - errore non previsto: 503 redatto, dettaglio solo nel log senza contenuti
        log.exception("errore interno di conversione")
        raise ParseError("parser_crash", 503, "Errore interno del parser.") from None
    finally:
        if pgid is not None:
            _kill_group(pgid)  # nessun processo figlio residuo dopo la risposta
        if workdir is not None:
            shutil.rmtree(workdir, ignore_errors=True)  # solo il workspace della propria richiesta
        CONVERSION_SLOT.release()
