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


# ================================================================ T106 - contenuto ostile (SEC): entita XML, symlink, archivi annidati, campi attivi, PDF attivi, bombe

import re
import time
import zlib


class Honeypot:
    """Listener locale: ogni connessione e un tentativo di fetch/egress non autorizzato."""

    def __init__(self):
        self.hits: list[bytes] = []
        self.srv = socket.socket()
        self.srv.bind(("127.0.0.1", 0))
        self.srv.listen(8)
        self.port = self.srv.getsockname()[1]
        self.thread = threading.Thread(target=self._accept, daemon=True)

    def _accept(self):
        self.srv.settimeout(120)
        try:
            while True:
                c, _ = self.srv.accept()
                try:
                    c.settimeout(0.5)
                    self.hits.append(c.recv(256))
                except OSError:
                    self.hits.append(b"")
                finally:
                    c.close()
        except OSError:
            pass

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.srv.close()
        self.thread.join(timeout=3)

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}"


FORBIDDEN_IN_OUTPUT = ("root:x:0:0", "/bin/bash", "Traceback", "PARSER_INTERNAL_SECRET")


def _assert_clean_outcome(client, r, honeypot=None, *, allow=(200, 422, 415)):
    """Esito pulito di un input ostile: stato del contratto, corpo senza contenuto del sistema ne traceback, nessuna connessione, servizio vivo."""
    print(f"[T106] esito {r.status_code} {r.json().get('code') if r.status_code != 200 else 'ok'}")
    assert r.status_code in allow, f"stato inatteso {r.status_code}: {r.text[:200]}"
    if r.status_code != 200:
        assert set(r.json()) == {"code", "message"}, "errore con campi estranei"
        assert not any(marker in r.text for marker in FORBIDDEN_IN_OUTPUT)
        assert "/app" not in r.text and tempfile.gettempdir() not in r.text
    else:
        assert not any(marker in r.text for marker in FORBIDDEN_IN_OUTPUT), "contenuto di sistema nel manifest"
    if honeypot is not None:
        assert honeypot.hits == [], f"connessioni non autorizzate: {honeypot.hits}"
    assert client.get("/health").status_code == 200, "il servizio non e piu sano dopo l'input ostile"


def _docx_document_xml() -> str:
    return zipfile.ZipFile(io.BytesIO(fixture_bytes("docx-basic"))).read("word/document.xml").decode("utf-8")


def _inject_paragraph(xml: str, paragraph: str) -> str:
    return xml.replace("</w:body>", paragraph + "</w:body>", 1)


def _rels_with(targets: list[tuple[str, str]]) -> bytes:
    rels = "".join(
        f'<Relationship Id="rIdX{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/{t}" Target="{target}" TargetMode="External"/>'
        for i, (t, target) in enumerate(targets))
    return ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' + rels + "</Relationships>").encode()


@pytest.mark.parametrize("kind", ["xxe_file_and_http", "billion_laughs", "parameter_entity"])
def test_docx_xml_entities_are_never_resolved_expanded_or_fetched(client, version_info, kind):
    with Honeypot() as hp:
        xml = _docx_document_xml()
        if kind == "xxe_file_and_http":
            doctype = f'<!DOCTYPE w:document [<!ENTITY xxe SYSTEM "file:///etc/passwd"><!ENTITY remote SYSTEM "{hp.url}/xxe">]>'
            payload = "&xxe;&remote;"
        elif kind == "billion_laughs":
            levels = "".join(f'<!ENTITY l{i} "{("&l%d;" % (i - 1)) * 10}">' for i in range(1, 9))
            doctype = '<!DOCTYPE w:document [<!ENTITY l0 "lol">' + levels + "]>"
            payload = "&l8;"
        else:
            doctype = f'<!DOCTYPE w:document [<!ENTITY % remote SYSTEM "{hp.url}/dtd"> %remote;]>'
            payload = "x"
        declaration = re.match(r"<\?xml[^>]*\?>", xml)
        xml = (declaration.group(0) + doctype + xml[declaration.end():]) if declaration else doctype + xml
        xml = _inject_paragraph(xml, f"<w:p><w:r><w:t>{payload}</w:t></w:r></w:p>")
        started = time.monotonic()
        r = _post_docx(client, version_info, _docx_variant(replace={"word/document.xml": xml.encode("utf-8")}))
        assert time.monotonic() - started < 60, "l'espansione delle entita non deve rallentare la conversione"
        _assert_clean_outcome(client, r, hp)
        if r.status_code == 200:
            text = "\n".join(b["text"] for b in r.json()["blocks"])
            assert "lollollollol" not in text and "root:" not in text


def test_docx_symlink_entries_and_nested_archives_are_inert(client, version_info):
    inner = io.BytesIO()
    with zipfile.ZipFile(inner, "w", zipfile.ZIP_DEFLATED) as z:  # archivio annidato che si espanderebbe a 300 MiB
        with z.open("espanso.bin", "w", force_zip64=True) as f:
            for _ in range(300):
                f.write(b"\0" * (1 << 20))
    assert inner.tell() < 3 * 1024 * 1024
    out = io.BytesIO()
    src = zipfile.ZipFile(io.BytesIO(fixture_bytes("docx-basic")))
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for i in src.infolist():
            z.writestr(i.filename, src.read(i.filename))
        link = zipfile.ZipInfo("word/media/collegamento.xml")
        link.create_system = 3
        link.external_attr = 0o120777 << 16  # symlink verso /etc/passwd
        z.writestr(link, "/etc/passwd")
        z.writestr("word/embeddings/annidato.zip", inner.getvalue())
        z.writestr("word/embeddings/oleObject1.bin", b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1" + b"\0" * 64)
    started = time.monotonic()
    r = _post_docx(client, version_info, out.getvalue())
    assert time.monotonic() - started < 60, "l'archivio annidato non va espanso"
    _assert_clean_outcome(client, r)
    if r.status_code == 200:
        body = r.json()
        assert any(i["code"] == "unprocessed_embedded_object" for i in body["issues"]), "oggetti incorporati dichiarati non elaborati"
        assert "root:" not in "\n".join(b["text"] for b in body["blocks"])


def test_docx_with_declared_sizes_that_lie_is_a_clean_client_error(client, version_info):
    out = io.BytesIO()
    src = zipfile.ZipFile(io.BytesIO(fixture_bytes("docx-basic")))
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for i in src.infolist():
            z.writestr(i.filename, src.read(i.filename))
        z.writestr("word/media/bugiardo.bin", b"\0" * (8 << 20))
        for info in z.infolist():
            if info.filename == "word/media/bugiardo.bin":
                info.file_size = 10  # il direttorio centrale dichiara 10 byte, i dati reali sono 8 MiB
    r = _post_docx(client, version_info, out.getvalue())
    _assert_clean_outcome(client, r)


def test_docx_field_codes_and_external_targets_are_inert_and_never_fetched(client, version_info):
    with Honeypot() as hp:
        codes = ('INCLUDETEXT "file:///etc/passwd"', 'DDEAUTO c:\\\\windows\\\\system32\\\\cmd.exe "/k calc.exe"', f'INCLUDEPICTURE "{hp.url}/pic"',
                 f'HYPERLINK "{hp.url}/link"', f'LINK Excel.Sheet.12 "{hp.url}/x.xlsx" "R1C1"', 'IMPORT "file:///etc/passwd"')
        runs = "".join(f'<w:r><w:instrText xml:space="preserve">{code}</w:instrText></w:r>' for code in codes)
        paragraph = (
            f"<w:p>{runs}<w:r><w:t>Testo visibile del campo</w:t></w:r></w:p>"
            f'<w:p><w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml"><v:imagedata src="{hp.url}/vml" '
            f'o:href="{hp.url}/vml2" xmlns:o="urn:schemas-microsoft-com:office:office"/></v:shape></w:pict></w:r></w:p>'
            '<w:altChunk xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:id="rIdX0"/>')
        xml = _inject_paragraph(_docx_document_xml(), paragraph)
        rels = _rels_with([("hyperlink", f"{hp.url}/h"), ("oleObject", "file:///etc/passwd"), ("afChunk", f"{hp.url}/chunk.html"), ("image", f"{hp.url}/img.png"),
                           ("attachedTemplate", f"{hp.url}/template.dotm"), ("frame", f"{hp.url}/frame"), ("subDocument", "file:///etc/passwd")])
        r = _post_docx(client, version_info, _docx_variant(replace={"word/document.xml": xml.encode(), "word/_rels/document.xml.rels": rels},
                                                           extra={"word/_rels/settings.xml.rels": rels}))
        _assert_clean_outcome(client, r, hp)
        if r.status_code == 200:
            assert any(i["code"] == "external_relationship_not_fetched" for i in r.json()["issues"])
            assert "root:" not in "\n".join(b["text"] for b in r.json()["blocks"])


def _pdf(objects: dict[int, bytes]) -> bytes:
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for num in sorted(objects):
        offsets[num] = len(out)
        out += f"{num} 0 obj\n".encode() + objects[num] + b"\nendobj\n"
    xref = len(out)
    size = max(objects) + 1
    out += f"xref\n0 {size}\n".encode() + b"0000000000 65535 f \n"
    for num in range(1, size):
        out += (f"{offsets[num]:010d} 00000 n \n" if num in offsets else "0000000000 65535 f \n").encode()
    out += f"trailer\n<</Size {size}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


def _text_pdf(port: int | None = None, extra_catalog: str = "") -> bytes:
    stream = b"BT /F1 12 Tf 72 700 Td (Il collaudo avviene entro trenta giorni dalla consegna del software.) Tj ET"
    objects = {
        1: f"<</Type/Catalog/Pages 2 0 R {extra_catalog}>>".encode(),
        2: b"<</Type/Pages/Kids[3 0 R]/Count 1>>",
        3: b"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>" + (b"/Annots[7 0 R 8 0 R 9 0 R]" if port else b"") + b">>",
        4: b"<</Length %d>>\nstream\n" % len(stream) + stream + b"\nendstream",
        5: b"<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
    }
    if port:
        objects[6] = f"<</S/JavaScript/JS (app.launchURL\\('http://127.0.0.1:{port}/js'\\);)>>".encode()
        objects[7] = f"<</Type/Annot/Subtype/Link/Rect[0 0 200 200]/Border[0 0 0]/A<</S/URI/URI(http://127.0.0.1:{port}/uri)>>>>".encode()
        objects[8] = f"<</Type/Annot/Subtype/Link/Rect[0 0 100 100]/A<</S/GoToR/F(http://127.0.0.1:{port}/remote)/D[0 /Fit]>>>>".encode()
        objects[9] = b"<</Type/Annot/Subtype/Link/Rect[0 0 50 50]/A<</S/Launch/F(cmd.exe)/Win<</F(cmd.exe)/P(/c calc.exe)>>>>>>"
        objects[10] = b"<</Type/Filespec/F(programma.exe)/EF<</F 11 0 R>>>>"
        objects[11] = b"<</Type/EmbeddedFile/Length 4>>\nstream\nMZ\x90\x00\nendstream"
    return _pdf(objects)


def test_pdf_javascript_uri_launch_and_embedded_file_actions_are_inert_and_never_fetched(client, version_info):
    with Honeypot() as hp:
        catalog = (f"/OpenAction 6 0 R/AA<</O 6 0 R>>/Names<</EmbeddedFiles<</Names[(programma.exe) 10 0 R]>>>>"
                   f"/URI<</Base(http://127.0.0.1:{hp.port}/)>>")
        r = post_convert(client, "pdf-text", version_info=version_info, data=_text_pdf(hp.port, extra_catalog=catalog))
        _assert_clean_outcome(client, r, hp)
        if r.status_code == 200:
            text = "\n".join(b["text"] for b in r.json()["blocks"])
            assert "app.launchURL" not in text and "calc.exe" not in text


def test_pdf_flate_bomb_is_a_bounded_clean_outcome_and_leaves_no_process(client, version_info):
    import service
    comp = zlib.compressobj(9)
    parts = [comp.compress(b"BT /F1 12 Tf 72 700 Td (Testo prima della bomba che resta leggibile.) Tj ET\n")]
    for _ in range(300):  # 300 MiB di spazi: poche centinaia di KiB compressi
        parts.append(comp.compress(b" " * (1 << 20)))
    parts.append(comp.flush())
    body = b"".join(parts)
    assert len(body) < 3 * 1024 * 1024
    objects = {
        1: b"<</Type/Catalog/Pages 2 0 R>>", 2: b"<</Type/Pages/Kids[3 0 R]/Count 1>>",
        3: b"<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
        4: b"<</Filter/FlateDecode/Length %d>>\nstream\n" % len(body) + body + b"\nendstream", 5: b"<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
    }
    started = time.monotonic()
    r = post_convert(client, "pdf-text", version_info=version_info, data=_pdf(objects), timeoutSeconds=60)
    assert time.monotonic() - started < 90, "la bomba deve fermarsi nei limiti, non consumare il tempo del server"
    _assert_clean_outcome(client, r)
    with pytest.raises(ProcessLookupError):
        os.killpg(service.last_process_group, 0)


@pytest.mark.parametrize("kind", ["html_and_links", "deep_nesting", "one_huge_line", "nul_and_controls"])
def test_markdown_and_text_with_active_or_pathological_content_are_inert_bounded_and_never_fetched(client, version_info, kind):
    with Honeypot() as hp:
        if kind == "html_and_links":
            data = (f"# Titolo\n\n<script>fetch('{hp.url}/s')</script>\n\n<iframe src=\"{hp.url}/f\"></iframe>\n\n"
                    f"![x]({hp.url}/img.png) [js](javascript:alert(1)) [file](file:///etc/passwd) ![f](file:///etc/passwd) "
                    f"[ref][1]\n\n[1]: {hp.url}/ref \"titolo\"\n\n<img src=x onerror=\"fetch('{hp.url}/e')\">\n").encode()
            fmt, fid = "md", "md-basic"
        elif kind == "deep_nesting":
            data = (">" * 20_000 + " annidato\n\n" + "- a\n" + "".join("  " * i + "- n\n" for i in range(1, 2_000))).encode()
            fmt, fid = "md", "md-basic"
        elif kind == "one_huge_line":
            data = ("parola " * 800_000).encode()  # ~5,6 MB senza a capo
            fmt, fid = "txt", "txt-negations"
        else:
            data = b"Riga uno\x00con NUL\x1b[31m e controlli\x07\nRiga due\r\rRiga tre\n"
            fmt, fid = "txt", "txt-negations"
        started = time.monotonic()
        r = post_convert(client, fid, version_info=version_info, data=data, fmt=fmt, timeoutSeconds=90)
        assert time.monotonic() - started < 120
        _assert_clean_outcome(client, r, hp)


def test_markdown_inline_images_are_declared_unprocessed_never_fetched_and_never_a_crash(client, version_info):
    """Regressione T106: un'immagine DENTRO un paragrafo (testo + immagine) mandava in crash il parser (503 parser_crash, poi documento Failed)."""
    from manifest_checks import validate_manifest

    with Honeypot() as hp:
        data = (f"# Titolo\n\nPrima ![logo]({hp.url}/logo.png) dopo il testo, e ancora ![fine]({hp.url}/fine.png)\n\n"
                f"Un altro paragrafo con [un link]({hp.url}/link) e ![img]({hp.url}/x.png) in mezzo e poi altro testo.\n").encode()
        r = post_convert(client, "md-basic", version_info=version_info, data=data)
        assert r.status_code == 200, r.text[:200]
        m = r.json()
        assert hp.hits == [], "nessun fetch delle immagini"
        assert validate_manifest(m, data) == []
        assert m["status"] == "Incomplete", "immagini non elaborate = documento incompleto, come per le immagini isolate"
        images = [i for i in m["issues"] if i["code"] == "unprocessed_markdown_image"]
        assert len(images) == 3 and all(i["blockRef"] for i in images)
        assert sum(1 for it in m["inventory"] if it["kind"] == "picture" and it["state"] == "unprocessed") == 3
        text = "\n".join(b["text"] for b in m["blocks"])
        assert "Prima" in text and "dopo il testo" in text and "altro testo" in text, "il testo attorno alle immagini resta"
