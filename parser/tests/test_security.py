"""T026 - isolamento e sicurezza: password/corruzione, zip bomb/traversal/macro, kill a timeout, limite output, nessun fetch remoto."""
import glob
import io
import os
import socket
import tempfile
import threading
import zipfile

import pytest

from conftest import FILES, fixture_bytes, post_convert


def _docx_variant(extra: dict[str, bytes] | None = None, replace: dict[str, bytes] | None = None) -> bytes:
    src = zipfile.ZipFile(io.BytesIO(fixture_bytes("docx-basic")))
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for i in src.infolist():
            z.writestr(i.filename, (replace or {}).get(i.filename, src.read(i.filename)))
        for name, data in (extra or {}).items():
            z.writestr(name, data)
    return out.getvalue()


def _post_docx(client, version_info, data, **over):
    return post_convert(client, "docx-basic", version_info=version_info, data=data, **over)


@pytest.mark.parametrize("file_id,code", [("pdf-protected", "protected"), ("pdf-corrupt", "corrupt"), ("docx-corrupt", "corrupt")])
def test_protected_and_corrupt_inputs_are_rejected_without_leaking_content(client, version_info, file_id, code):
    r = post_convert(client, file_id, version_info=version_info)
    assert r.status_code == 422 and r.json()["code"] == code
    assert set(r.json()) == {"code", "message"}


def test_zip_bomb_is_stopped_by_expanded_size_limit(client, version_info):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", b"<Types/>")
        z.writestr("word/document.xml", b"<w:document/>")
        with z.open("word/bomb.bin", "w", force_zip64=True) as f:
            chunk = b"\0" * (1 << 20)
            for _ in range(260):  # 260 MiB espansi > 250 MiB di limite iniziale
                f.write(chunk)
    assert buf.tell() < 5 * 1024 * 1024, "l'archivio compresso deve essere piccolo"
    r = _post_docx(client, version_info, buf.getvalue())
    assert r.status_code == 422 and r.json()["code"] == "parse_limit_exceeded"


def test_expanded_limit_requested_by_backend_is_honored(client, version_info):
    r = _post_docx(client, version_info, fixture_bytes("docx-basic"), maxExpandedBytes=100)
    assert r.status_code == 422 and r.json()["code"] == "parse_limit_exceeded"


@pytest.mark.parametrize("name", ["../evil.xml", "/abs/evil.xml", "word/../../evil.xml", "C:/evil.xml"])
def test_path_traversal_entries_are_rejected(client, version_info, name):
    r = _post_docx(client, version_info, _docx_variant(extra={name: b"x"}))
    assert r.status_code == 422 and r.json()["code"] == "unsafe_archive"


def test_macro_enabled_documents_are_rejected(client, version_info):
    r = _post_docx(client, version_info, _docx_variant(extra={"word/vbaProject.bin": b"\0"}))
    assert r.status_code == 415 and r.json()["code"] == "macro_enabled_not_allowed"


def test_too_many_zip_entries_are_rejected(client, version_info):
    extra = {f"word/media/f{i}.bin": b"" for i in range(10_001)}
    r = _post_docx(client, version_info, _docx_variant(extra=extra))
    assert r.status_code == 422 and r.json()["code"] == "parse_limit_exceeded"


def test_timeout_kills_the_whole_process_group_and_cleans_the_workspace(client, version_info):
    import service
    before = set(glob.glob(os.path.join(service.WORK_ROOT, "req-*")))
    r = post_convert(client, "pdf-table-multipage", version_info=version_info, timeoutSeconds=1)
    assert r.status_code == 422 and r.json()["code"] == "parse_timeout"
    with pytest.raises(ProcessLookupError):
        os.killpg(service.last_process_group, 0)  # nessun processo figlio residuo
    assert set(glob.glob(os.path.join(service.WORK_ROOT, "req-*"))) == before
    # nessun lock residuo: la richiesta successiva viene servita
    assert post_convert(client, "txt-conflict-a", version_info=version_info).status_code == 200


def test_output_over_requested_limit_is_an_explicit_error_never_truncated_json(client, version_info):
    r = post_convert(client, "txt-long", version_info=version_info, maxOutputBytes=1000)
    assert r.status_code == 422 and r.json()["code"] == "parse_limit_exceeded"


def test_hard_file_size_limit_kills_the_child_and_maps_to_limit_error(client, version_info):
    big = ("Riga di prova con testo sufficiente per superare il tetto di scrittura.\n" * 80_000).encode()  # ~5.6 MB -> manifest > 4 MB di margine
    r = post_convert(client, "txt-conflict-a", version_info=version_info, data=big, maxOutputBytes=1)
    assert r.status_code == 422 and r.json()["code"] == "parse_limit_exceeded"


def test_server_limits_cap_what_the_backend_requests():
    import common
    import service
    assert service._clamp(10**15, "maxOutputBytes") == common.SERVER_LIMITS["maxOutputBytes"] == 250 * 1024 * 1024
    assert service._clamp(10**15, "timeoutSeconds") == common.SERVER_LIMITS["timeoutSeconds"]


def test_request_without_matching_secret_never_reaches_conversion(client, version_info):
    import service
    r = post_convert(client, "txt-conflict-a", version_info=version_info, secret="x")
    assert r.status_code == 401 and service.CONVERSION_SLOT.acquire(blocking=False)
    service.CONVERSION_SLOT.release()


def test_process_runs_as_non_root_and_cannot_modify_the_application():
    import common
    assert os.geteuid() != 0
    with pytest.raises(OSError):
        (common.APP_DIR / "planted.py").write_text("x")
    for name in ("PARSER_INTERNAL_SECRET",):
        assert name in os.environ  # solo in questo processo di test: il figlio di conversione non lo riceve (vedi test sotto)


def test_conversion_child_does_not_inherit_the_internal_secret(client, version_info, monkeypatch):
    import service
    seen = {}
    real = service.subprocess.Popen

    def spy(*a, **kw):
        seen["env"] = kw["env"]
        return real(*a, **kw)

    monkeypatch.setattr(service.subprocess, "Popen", spy)
    assert post_convert(client, "txt-conflict-a", version_info=version_info).status_code == 200
    assert "PARSER_INTERNAL_SECRET" not in seen["env"]
    assert seen["env"]["HF_HUB_OFFLINE"] == "1"


def test_remote_references_are_never_fetched(client, version_info):
    hits = []
    srv = socket.socket()
    srv.bind(("127.0.0.1", 0))
    srv.listen(5)
    port = srv.getsockname()[1]

    def accept():
        srv.settimeout(40)
        try:
            while True:
                c, _ = srv.accept()
                hits.append(1)
                c.close()
        except OSError:
            pass

    t = threading.Thread(target=accept, daemon=True)
    t.start()
    try:
        md = f"# T\n\n![immagine](http://127.0.0.1:{port}/a.png)\n\n[link](http://127.0.0.1:{port}/b)\n".encode()
        r = post_convert(client, "md-basic", version_info=version_info, data=md)
        assert r.status_code == 200
        rels = (b'<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                b'<Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" '
                + f'Target="http://127.0.0.1:{port}/c.png" TargetMode="External"/></Relationships>'.encode())
        r = _post_docx(client, version_info, _docx_variant(replace={"word/_rels/document.xml.rels": rels}))
        assert r.status_code == 200
        assert any(i["code"] == "external_relationship_not_fetched" for i in r.json()["issues"])
    finally:
        srv.close()
        t.join(timeout=3)
    assert hits == [], "nessuna connessione verso risorse remote"


def test_errors_and_version_do_not_expose_secret_or_paths(client, version_info):
    secret = os.environ["PARSER_INTERNAL_SECRET"]
    r = post_convert(client, "pdf-corrupt", version_info=version_info)
    for text in (r.text, str(version_info), client.get("/health").text):
        assert secret not in text and tempfile.gettempdir() not in text
