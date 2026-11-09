"""Fixture condivise: corpus sintetico T002 (tests/fixtures/documents), servizio in-process, cache delle conversioni."""
import hashlib
import json
import os
from pathlib import Path

import pytest

os.environ.setdefault("PARSER_INTERNAL_SECRET", "test-secret-non-reale")

SECRET = os.environ["PARSER_INTERNAL_SECRET"]


def _fixtures_dir() -> Path:
    candidates = [os.environ.get("FIXTURES_DIR"), "/fixtures/documents",
                  str(Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "documents")]
    for c in candidates:
        if c and (Path(c) / "manifest.json").is_file():
            return Path(c)
    raise RuntimeError("corpus fixture non trovato: impostare FIXTURES_DIR")


FIXTURES = _fixtures_dir()
CORPUS = json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
FILES = {f["id"]: f for f in CORPUS["files"]}
FACTS = CORPUS["facts"]


def fixture_bytes(file_id: str) -> bytes:
    data = (FIXTURES / FILES[file_id]["path"]).read_bytes()
    assert hashlib.sha256(data).hexdigest() == FILES[file_id]["sha256"], "fixture alterata rispetto al manifest"
    return data


@pytest.fixture(scope="session")
def client():
    from fastapi.testclient import TestClient
    import service  # noqa: PLC0415  (fallisce finche il servizio non esiste: T020 prima di T022)
    with TestClient(service.app) as c:
        yield c


@pytest.fixture(scope="session")
def version_info(client):
    r = client.get("/version")
    assert r.status_code == 200
    return r.json()


def post_convert(client, file_id, *, version_info, secret=SECRET, data=None, fmt=None, **over):
    meta = FILES[file_id]
    data = fixture_bytes(file_id) if data is None else data
    form = {
        "declaredFormat": fmt or meta["format"],
        "documentVersionId": "00000000-0000-0000-0000-00000000d0c1",
        "originalSha256": hashlib.sha256(data).hexdigest(),
        "configHash": version_info["configHash"],
        "maxExpandedBytes": str(250 * 1024 * 1024),
        "maxPages": "500",
        "maxBlocks": "100000",
        "maxOutputBytes": str(250 * 1024 * 1024),
        "timeoutSeconds": "280",
    }
    form.update({k: str(v) for k, v in over.items()})
    headers = {"X-Internal-Secret": secret} if secret is not None else {}
    return client.post("/convert", data=form, files={"file": (meta["path"], data, meta["mediaType"])}, headers=headers)


_cache: dict = {}


@pytest.fixture(scope="session")
def converted(client, version_info):
    """Manifest 200 per fixture, calcolati una sola volta per sessione."""
    def get(file_id):
        if file_id not in _cache:
            r = post_convert(client, file_id, version_info=version_info)
            assert r.status_code == 200, f"{file_id}: {r.status_code} {r.text[:200]}"
            _cache[file_id] = r.json()
        return _cache[file_id]
    return get


def cp_to_utf16(text: str, cp: int) -> int:
    return len(text[:cp].encode("utf-16-le")) // 2
