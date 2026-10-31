"""Replay di una richiesta di analisi catturata dal proxy, cambiando SOLO il contenuto del messaggio system.

Legge il corpo originale dal JSONL del proxy (es. docs/acceptance-trace/calls-qwen3.8-flash-next-smart-v7.jsonl),
sostituisce messages[0].content con il prompt indicato e verifica che tutto il resto del payload sia identico
(messaggi PM e assistant, ruoli, ordine, id, cutoff, response_format, model, temperature, max_completion_tokens,
chat_template_kwargs e ogni altra chiave). Senza --send non usa la rete.

    python -I replay_analysis.py --calls <calls.jsonl> --id 233 --system <analysis.md> --out <cartella>
    python -I replay_analysis.py ... --send http://<dgx>:8080      # AI_API_KEY facoltativa (Bearer)

Solo libreria standard. La cartella di uscita non deve stare sotto docs/.
"""

import argparse
import copy
import hashlib
import json
import os
import pathlib
import sys
import urllib.request


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def canonical(obj):
    return json.dumps(obj, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def without_system(body):
    rest = copy.deepcopy(body)
    rest["messages"][0]["content"] = None
    return rest


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--calls", required=True)
    p.add_argument("--id", type=int, required=True)
    p.add_argument("--system", required=True, help="file del prompt; il testo e' usato dopo strip(), come LoadPrompt")
    p.add_argument("--out", required=True)
    p.add_argument("--send", help="URL base del server OpenAI-compatibile; senza, nessuna chiamata")
    a = p.parse_args()

    out = pathlib.Path(a.out).resolve()
    if "docs" in out.parts:
        sys.exit("la cartella di uscita non puo' stare sotto docs/")
    out.mkdir(parents=True, exist_ok=True)

    record = None
    with open(a.calls, encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            if r["id"] == a.id:
                record = r
                break
    if record is None or record["path"] != "/v1/chat/completions":
        sys.exit(f"richiesta {a.id} non trovata o non chat/completions")

    original = record["requestBody"]
    systems = [i for i, m in enumerate(original["messages"]) if m["role"] == "system"]
    if systems != [0]:
        sys.exit(f"atteso un solo messaggio system in posizione 0, trovati {systems}")

    system = pathlib.Path(a.system).read_bytes().decode("utf-8").strip()
    payload = copy.deepcopy(original)
    payload["messages"][0]["content"] = system

    # Unica differenza ammessa: messages[0].content.
    same_rest = canonical(without_system(original)) == canonical(without_system(payload))
    same_key_order = list(original.keys()) == list(payload.keys())
    report = {
        "proxyId": a.id,
        "startedAt": record["startedAt"],
        "originalSystemSha256": sha(original["messages"][0]["content"]),
        "replaySystemSha256": sha(system),
        "restOfPayloadSha256": {"original": sha(canonical(without_system(original))), "replay": sha(canonical(without_system(payload)))},
        "restOfPayloadIdentical": same_rest,
        "topLevelKeyOrderIdentical": same_key_order,
        "messages": [m["role"] for m in payload["messages"]],
        "options": {k: v for k, v in payload.items() if k not in ("messages", "response_format")},
        "responseFormatSha256": sha(canonical(payload["response_format"])),
    }
    if not (same_rest and same_key_order):
        (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        sys.exit("payload diverso dall'originale oltre al system: replay rifiutato")

    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    (out / "payload.json").write_bytes(body)

    original_response = record["responseBody"]
    original_response = json.loads(original_response) if isinstance(original_response, str) else original_response
    report["original"] = {k: original_response.get(k) for k in ("model", "system_fingerprint", "usage")}

    if a.send:
        headers = {"Content-Type": "application/json"}
        if os.environ.get("AI_API_KEY"):
            headers["Authorization"] = "Bearer " + os.environ["AI_API_KEY"]
        request = urllib.request.Request(a.send.rstrip("/") + "/v1/chat/completions", data=body, headers=headers, method="POST")
        with urllib.request.urlopen(request, timeout=900) as response:
            raw = response.read()
        (out / "response.json").write_bytes(raw)
        replay = json.loads(raw)
        (out / "content.json").write_text(replay["choices"][0]["message"]["content"], encoding="utf-8")
        report["replay"] = {k: replay.get(k) for k in ("model", "system_fingerprint", "usage")}
        report["replay"]["finish_reason"] = replay["choices"][0].get("finish_reason")
        report["sameServerBuild"] = replay.get("system_fingerprint") == original_response.get("system_fingerprint")

    (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
