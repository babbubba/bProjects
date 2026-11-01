"""T026 - golden offline sui quattro formati e coerenza dei range sull'intero corpus (nessun LLM, nessuna rete)."""
import json
import os
import socket

import pytest

from conftest import CORPUS, FILES, fixture_bytes, post_convert
from manifest_checks import validate_manifest

ACCEPTED = [f["id"] for f in CORPUS["files"] if f["expected"]["parse"] in ("Complete", "Incomplete")]
FORMAT_SAMPLES = {"pdf": "pdf-text", "docx": "docx-basic", "md": "md-basic", "txt": "txt-negations"}


@pytest.mark.parametrize("fmt,file_id", sorted(FORMAT_SAMPLES.items()))
def test_four_formats_convert_with_golden_text(converted, fmt, file_id):
    m = converted(file_id)
    assert m["originFormat"] == fmt and m["status"] == "Complete"
    assert validate_manifest(m, fixture_bytes(file_id)) == []
    text = "\n".join(b["text"] for b in m["blocks"])
    from conftest import FACTS
    for f in (x for x in FACTS if x["fileId"] == file_id and x["locator"]["kind"] != "docx-path"):
        assert " ".join(f["quote"].split()) in " ".join(text.split()), f["id"]


def test_whole_corpus_is_valid_and_deterministic(client, version_info, converted):
    for file_id in ACCEPTED:
        first = converted(file_id)
        assert validate_manifest(first, fixture_bytes(file_id)) == [], file_id
        again = post_convert(client, file_id, version_info=version_info).json()
        assert json.dumps(first, sort_keys=True) == json.dumps(again, sort_keys=True), f"{file_id}: manifest non deterministico"


def test_long_document_keeps_distant_facts_in_separate_ranges(converted):
    m = converted("txt-long")
    src = fixture_bytes("txt-long").decode("utf-8")
    assert len(m["chunks"]) > 1, "documento lungo = piu chunk"
    for f in (x for x in CORPUS["facts"] if x["fileId"] == "txt-long"):
        loc = f["locator"]
        assert src[loc["startCodepoint"]:loc["endCodepoint"]] == f["quote"]
        owners = [b for b in m["blocks"] if b["locator"]["sourceStart"] <= loc["startCodepoint"] and loc["endCodepoint"] <= b["locator"]["sourceEnd"]]
        assert len(owners) == 1


def test_tokencount_is_estimated_and_tokenizer_identified(converted, version_info):
    for c in converted("txt-long")["chunks"]:
        assert c["tokenSource"] == "estimated" and "all-MiniLM-L6-v2@" in c["tokenizerId"] and c["tokenCount"] > 0


@pytest.mark.skipif(os.environ.get("PARSER_EXPECT_OFFLINE") != "1", reason="eseguire con --network none e PARSER_EXPECT_OFFLINE=1")
def test_process_has_no_network_and_conversion_still_works(converted):
    with pytest.raises(OSError):
        socket.create_connection(("1.1.1.1", 443), timeout=3)
    with pytest.raises(OSError):
        socket.getaddrinfo("huggingface.co", 443)
    for file_id in ("pdf-text", "pdf-table-multipage", "docx-basic", "md-basic", "txt-utf16"):
        assert converted(file_id)["status"] == "Complete"
    assert os.environ.get("HF_HUB_OFFLINE") == "1"
