"""Summarize observed calls separately from offline plans and cache hits."""
import collections
import json
import statistics

from prepare_phase import PHASE
from prepare import dump
from runtime import OUT, verify_freeze

def bucket(row):
    stage = row["stage"]
    if row["path"]=="/slots" or row["input_sha256"] is None:
        return None
    if stage.startswith("extract-"):
        return stage.rsplit("-",1)[0]
    if stage.startswith("gap-"):
        return "-".join(stage.split("-")[:2])
    for name in ("ac-summary","b3-summary","ac-hybrid","ac-sk_full","ac-sk_no_summary","ac-sk_lucene_full","ac-sk_lucene_no_summary","ac-query"):
        if stage.startswith(name+"-") and stage!=name+"-embedding":
            return name
    return stage

def main():
    verify_freeze()
    path = PHASE/"results"/"http.jsonl"
    if not path.exists():
        return
    groups = collections.defaultdict(list)
    for line in path.read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        name = bucket(row)
        if name is not None:
            groups[name].append(row)
    result = {}
    for name, rows in groups.items():
        used = [r["usage"] for r in rows if r.get("usage") is not None]
        result[name] = dict(
            calls=len(rows), errors=sum(r["status"]!="OK" for r in rows),
            total_http_s=round(sum(r["milliseconds"] for r in rows)/1000,3),
            p50_http_ms=round(statistics.median(r["milliseconds"] for r in rows),1),
            usage_rows=len(used),
            input_tokens=sum(u.get("prompt_tokens",0) for u in used) if used else None,
            output_tokens=sum(u.get("completion_tokens",0) for u in used)
                          if any("completion_tokens" in u for u in used) else None)
    if result:
        dump(OUT/"call-totals.json",result)

if __name__=="__main__":
    main()
