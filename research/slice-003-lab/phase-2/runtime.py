"""Reuse the Phase 1 embedder and retain Phase 2 calls separately."""
import json
import os
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

from prepare_phase import PHASE, read, file_hash, sha
from experiment import EMBED, RERANK, Http
from prepare import dump

CONFIG = read(PHASE / "protocol.json")
MODEL = CONFIG["model"]
OUT = PHASE / "results"

def verify_freeze():
    for path, expected in read(PHASE / "freeze.json")["sha256"].items():
        assert file_hash(PHASE / path) == expected, path

class Busy(RuntimeError):
    pass

class Runtime(Http):
    def __init__(self):
        super().__init__()
        if os.environ.get("BPROJECTS_TEST_DGX_MODEL") != MODEL:
            raise RuntimeError("Configured test model differs from frozen protocol")
        OUT.mkdir(exist_ok=True)

    def idle(self):
        if os.name == "nt":
            check = subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                "$p=Get-CimInstance Win32_Process -Filter \"Name LIKE 'bProjects.IntegrationTests%' OR Name = 'dotnet.exe'\"; "
                "if ($p | Where-Object CommandLine -Match 'T082|T083|MafReal|Document.*Real') { exit 8 }"],
                capture_output=True, timeout=12, check=False)
            if check.returncode:
                raise Busy("Local real bProjects run active or process check unavailable")
        path = "/slots?" + urllib.parse.urlencode({"model": MODEL})
        slots = self.call(path, check_idle=False)
        if not isinstance(slots,list) or len(slots) != 1 or any(
                s.get("is_processing") is not False or s.get("n_ctx") != 131072 for s in slots):
            raise Busy("The configured generative slot is busy or its state is unverified")

    def call(self, path, payload=None, base=None, *, check_idle=True):
        if payload is not None and check_idle:
            self.idle()
        started = time.perf_counter()
        encoded = json.dumps(payload, ensure_ascii=False).encode() if payload is not None else None
        req = urllib.request.Request((base or self.base)+path, encoded,
            {"Authorization":"Bearer "+self.key, "Content-Type":"application/json"})
        row = dict(stage=self.stage,path=path.split("?")[0],
                   model=payload.get("model") if payload else None,
                   input_sha256=sha(encoded) if encoded else None)
        try:
            with urllib.request.urlopen(req, timeout=300) as response:
                data = json.load(response)
            row.update(status="OK", usage=data.get("usage") if isinstance(data,dict) else None,
                       response_sha256=sha(json.dumps(data,sort_keys=True).encode()))
            if path.startswith("/slots"):
                row["slots"] = [{k:s.get(k) for k in ("id","is_processing","n_ctx")} for s in data]
            return data
        except (OSError, ValueError) as exc:
            row.update(status="ERROR",error=type(exc).__name__,http_status=getattr(exc,"code",None))
            raise RuntimeError(f"{row['path']}: {row['error']} {row['http_status']}") from None
        finally:
            row["milliseconds"] = (time.perf_counter()-started)*1000
            self.calls.append(row)
            with (OUT/"http.jsonl").open("a",encoding="utf-8") as handle:
                handle.write(json.dumps(row)+"\n")

    def preflight(self):
        props, models = self.guard()
        known = {m["id"]:m for m in models}
        assert {MODEL, EMBED, RERANK} <= known.keys()
        assert known[MODEL]["status"]["value"] == "loaded", "Generation model must already be loaded"
        self.idle()
        allowed = ("--ctx-size", "--parallel", "--batch-size", "--ubatch-size", "--pooling")
        configs = {}
        for m in (MODEL,EMBED,RERANK):
            args = known[m]["status"].get("args",[])
            configs[m] = dict(state=known[m]["status"]["value"],
                              parameters={flag:args[args.index(flag)+1] for flag in allowed if flag in args})
        dump(OUT/"runtime.json",dict(build=props.get("build_info"),models=configs))

    def checkpoint(self, key, path, payload):
        dest = OUT / "calls" / (key+".json")
        if dest.exists():
            record = read(dest)
            assert record["request"] == payload, key
            return record["response"]
        self.stage = key
        response = self.call(path,payload)
        temp = dest.with_suffix(".tmp")
        dump(temp,dict(request=payload,response=response,measurement=self.calls[-1]))
        temp.replace(dest)
        return response

    def chat(self, key, system, user, max_tokens, *, summary=False):
        payload = dict(model=MODEL, temperature=CONFIG["summaryTemperature" if summary else "temperature"],
                       seed=CONFIG["seed"], max_tokens=max_tokens, stream=False,
                       chat_template_kwargs={"enable_thinking":False},
                       messages=[dict(role="system",content=system),dict(role="user",content=user)])
        if not summary:
            payload["response_format"] = {"type":"json_object"}
        response = self.checkpoint(key,"/v1/chat/completions",payload)
        choice = response["choices"][0]
        return choice["message"].get("content") or "",choice.get("finish_reason")

def utf16_len(text):
    return len(text.encode("utf-16-le"))//2

def prefix(text, length):
    return text.encode("utf-16-le")[:length*2].decode("utf-16-le",errors="ignore")

def loads(text):
    try:
        return json.loads(text)
    except (ValueError,TypeError):
        return None
