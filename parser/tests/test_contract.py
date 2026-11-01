"""T020 - contratto document-parse-1: campi obbligatori, status, inventory/chunks/issues, hash di versione/config/modelli.

Le prime classi provano il validatore su manifest costruiti a mano (nessuna conversione, nessun LLM) e scoprono campi/range
invalidi. Le restanti richiedono il servizio reale (T022) e falliscono finche conversione e servizio non esistono.
"""
import copy

import pytest

from conftest import CORPUS, FILES, fixture_bytes, post_convert
from manifest_checks import sha256_text, validate_manifest

H = "a" * 64


def _minimal() -> dict:
    text = "Titolo\nCorpo"
    return {
        "schemaVersion": "document-parse-1", "originalSha256": H, "parserVersion": "1", "dependencyLockHash": H,
        "modelArtifactsHash": H, "configHash": H, "status": "Complete", "originFormat": "txt", "originalByteLength": 12,
        "pageCount": None, "offsetConvention": "unicode-codepoint",
        "document": {"serialization": "text-lossless-1", "content": text},
        "blocks": [{"ref": "b1", "order": 0, "parentRef": None, "type": "paragraph", "text": text, "headingPath": [],
                    "textSha256": sha256_text(text),
                    "locator": {"page": None, "bbox": None, "coordinateSystem": None, "path": None, "lineStart": 1,
                                "lineEnd": 2, "sourceStart": 0, "sourceEnd": 12}}],
        "inventory": [{"itemRef": "i1", "kind": "block", "state": "represented", "blockRefs": ["b1"], "issueRefs": []}],
        "chunks": [{"chunkKey": "c1", "text": text, "contextualizedText": text, "headingPath": [], "tokenCount": 3,
                    "tokenizerId": "t", "tokenSource": "estimated",
                    "ranges": [{"rangeKey": "r1", "blockRef": "b1", "start": 0, "end": 12, "ownership": "Primary",
                                "sourceTextSha256": sha256_text(text)}]}],
        "issues": [],
    }


def test_validator_accepts_minimal_manifest():
    assert validate_manifest(_minimal()) == []


@pytest.mark.parametrize("mutate,expected", [
    (lambda m: m.pop("configHash"), "campo mancante"),
    (lambda m: m.update(status="Ready"), "status"),
    (lambda m: m.update(modelArtifactsHash="xyz"), "modelArtifactsHash"),
    (lambda m: m["chunks"][0]["ranges"][0].update(end=99), "fuori testo"),
    (lambda m: m["chunks"][0]["ranges"][0].update(start=5, end=5), "fuori testo"),
    (lambda m: m["chunks"][0]["ranges"][0].update(ownership="Owner"), "ownership"),
    (lambda m: m["chunks"][0]["ranges"][0].update(sourceTextSha256=H), "hash sorgente"),
    (lambda m: m["chunks"][0]["ranges"][0].update(blockRef="zz"), "inesistente"),
    (lambda m: m["blocks"].append(copy.deepcopy(m["blocks"][0])), "non univoci"),
    (lambda m: m["blocks"][0].update(parentRef="b1"), "ciclo"),
    (lambda m: m["blocks"][0]["locator"].update(bbox={"l": 0, "t": 0, "r": 1, "b": 1}), "coordinateSystem"),
    (lambda m: m["inventory"][0].update(blockRefs=[]), "senza blocchi"),
    (lambda m: m["inventory"][0].update(state="unprocessed", blockRefs=[]), "senza issue"),
    (lambda m: m["issues"].append({"issueRef": "x", "code": "c", "severity": "blocking", "page": None, "blockRef": None,
                                   "reason": "r", "affectsCompleteness": True}), "Complete con elementi non trattati"),
    (lambda m: m["chunks"][0].update(tokenSource="measured"), "estimated"),
])
def test_validator_discovers_invalid_manifests(mutate, expected):
    m = _minimal()
    mutate(m)
    errs = validate_manifest(m)
    assert any(expected in e for e in errs), errs


def test_validator_discovers_overlapping_primary_ranges():
    m = _minimal()
    c2 = copy.deepcopy(m["chunks"][0])
    c2["chunkKey"] = "c2"
    c2["ranges"][0].update(rangeKey="r2", start=3, end=8)
    m["chunks"].append(c2)
    assert any("sovrapposti" in e for e in validate_manifest(m))


def test_validator_discovers_original_hash_mismatch():
    assert any("originalSha256" in e for e in validate_manifest(_minimal(), b"altro"))


# ---- servizio e conversione reali (falliscono finche T022-T025 non esistono) ----

ACCEPTED = [f["id"] for f in CORPUS["files"] if f["expected"]["parse"] in ("Complete", "Incomplete")]
REJECTED = [f["id"] for f in CORPUS["files"] if f["expected"]["parse"] == "Rejected"]


def test_health_and_version_expose_hashes(client, version_info):
    assert client.get("/health").status_code == 200
    v = version_info
    assert v["schema"] == "document-parse-1"
    for k in ("doclingVersion", "doclingCoreVersion", "parserVersion", "configHash", "dependencyLockHash", "modelArtifactsHash"):
        assert v[k]
    assert v["doclingVersion"] == "2.129.0"
    assert set(v["formats"]) == {"pdf", "docx", "md", "txt"}
    assert "secret" not in str(v).lower()


@pytest.mark.parametrize("file_id", ACCEPTED)
def test_conversion_manifest_is_valid_and_complete_or_incomplete_as_expected(converted, file_id):
    m = converted(file_id)
    assert validate_manifest(m, fixture_bytes(file_id)) == []
    exp = FILES[file_id]["expected"]
    assert m["status"] == exp["parse"]
    assert m["originFormat"] == FILES[file_id]["format"]
    assert m["pageCount"] == exp.get("pages")
    for issue in exp.get("issues", []):
        got = [i for i in m["issues"] if i["code"] == issue["code"]]
        assert got, f"issue attesa {issue['code']}"
        assert got[0]["affectsCompleteness"] is issue["affectsCompleteness"]
        if "page" in issue:
            assert got[0]["page"] == issue["page"]


@pytest.mark.parametrize("file_id", ACCEPTED)
def test_manifest_hashes_match_version_and_every_item_is_covered(converted, version_info, file_id):
    m = converted(file_id)
    for k in ("configHash", "dependencyLockHash", "modelArtifactsHash", "parserVersion"):
        assert m[k] == version_info[k]
    assert m["offsetConvention"] == "unicode-codepoint"
    covered = {br for it in m["inventory"] for br in it["blockRefs"]}
    # ogni blocco con testo e inventariato; ogni item e rappresentato o ha una issue
    assert {b["ref"] for b in m["blocks"] if b["text"].strip()} <= covered
    assert all(it["blockRefs"] or it["issueRefs"] for it in m["inventory"])


def test_incomplete_sources_never_become_complete(converted):
    assert converted("pdf-mixed")["status"] == "Incomplete"
    assert converted("docx-image")["status"] == "Incomplete"
    assert any(i["severity"] == "blocking" for i in converted("pdf-mixed")["issues"])


@pytest.mark.parametrize("file_id,code,status", [
    ("pdf-corrupt", "corrupt", 422), ("pdf-protected", "protected", 422),
    ("docx-corrupt", "corrupt", 422), ("txt-invalid", "invalid_encoding", 415)])
def test_rejected_inputs_return_redacted_contract_errors(client, version_info, file_id, code, status):
    assert file_id in REJECTED
    r = post_convert(client, file_id, version_info=version_info)
    assert r.status_code == status
    body = r.json()
    assert body["code"] == code
    assert "Traceback" not in r.text and "/tmp" not in r.text


def test_secret_is_required(client, version_info):
    assert post_convert(client, "txt-conflict-a", version_info=version_info, secret=None).status_code == 401
    assert post_convert(client, "txt-conflict-a", version_info=version_info, secret="sbagliato").status_code == 401


def test_config_mismatch_returns_409(client, version_info):
    r = post_convert(client, "txt-conflict-a", version_info=version_info, configHash="0" * 64)
    assert r.status_code == 409 and r.json()["code"] == "parser_config_mismatch"


def test_declared_format_mismatch_returns_415(client, version_info):
    r = post_convert(client, "pdf-text", version_info=version_info, fmt="docx")
    assert r.status_code == 415
    r = post_convert(client, "txt-conflict-a", version_info=version_info, fmt="exe")
    assert r.status_code == 415


def test_original_hash_mismatch_is_rejected(client, version_info):
    r = post_convert(client, "txt-conflict-a", version_info=version_info, originalSha256="1" * 64)
    assert r.status_code == 415 or r.status_code == 422


def test_busy_instance_returns_429_with_wait(client, version_info):
    import service
    assert service.CONVERSION_SLOT.acquire(blocking=False)
    try:
        r = post_convert(client, "txt-conflict-a", version_info=version_info)
    finally:
        service.CONVERSION_SLOT.release()
    assert r.status_code == 429 and int(r.headers["Retry-After"]) >= 1


def test_response_never_marks_ready_or_schedules_retry(converted):
    m = converted("txt-conflict-a")
    assert "Ready" not in str(m["status"])
    assert not {"retry", "jobId", "lease", "ready"} & set(m)
