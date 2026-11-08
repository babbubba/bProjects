"""Score after inference. No gold enters the retrieval or generation runners."""
import re

from prepare_phase import PHASE, read
from experiment import norm
from prepare import dump
from runtime import OUT, loads, utf16_len, verify_freeze

def hit(item,g):
    return item["file"]==g["file"] and norm(g["contains"]) in norm(item["text"])

def score_ac():
    gold = {g["id"]:g for g in read(PHASE/"a-gold.json")}
    results = []
    for folder in ("ac","ac-full","ac-lucene"):
        for path in sorted((OUT/folder).glob("*.json")):
            raw = read(path)
            q = gold[raw["query"]["id"]]
            expected = q["evidence"]
            for method,ranking in raw["rankings"].items():
                context = raw["contexts"][method]
                lookup = raw["child_items"] if method=="hybrid" else list(raw["parent_items"].values())
                by_id = {item["id"]:item for item in lookup}
                ranks = {g["id"]:next((i for i,id in enumerate(ranking,1) if hit(by_id[id],g)),None)
                         for g in expected}
                packed = [g["id"] for g in expected if any(hit(item,g) for item in context["items"])]
                native = raw["native"].get(method)
                found_native = None
                if native is not None:
                    # The native formatter carries source labels. Do not credit a
                    # statement from an unrelated source even if its text matches.
                    parts = [s.partition("\n") for s in native["text"].split("\n\n---\n\n")]
                    found_native = [g["id"] for g in expected if any(
                        h.startswith("["+g["file"]+" - ") and norm(g["contains"]) in norm(t) for h,_,t in parts)]
                replay = dict(id=q["id"],method=method,rank_positions=ranks,packed=packed,
                              missing=[g["id"] for g in expected if g["id"] not in packed],
                              recall={k:sum(v is not None and v<=k for v in ranks.values())/len(ranks)
                                      if ranks else None for k in (5,10,20)},
                              context_utf16=utf16_len(context["text"]), native_packed=found_native,
                              all_expected_packed=len(packed)==len(expected) if expected else None,
                              excluded_in_context=any(item["id"]=="excluded" or item["file"]=="scheda-a-v0.txt"
                                                      for item in context["items"]))
                result = OUT/"gap"/method/path.name
                if result.exists():
                    generated = read(result)
                    replay["gap"] = score_gap(q,context,generated,method)
                results.append(replay)
    if results:
        dump(OUT/"ac-scores.json",results)
    return results

def objects(value):
    return [row for row in value if isinstance(row,dict)] if isinstance(value,list) else []

def recovered_json(result, call_id):
    if isinstance(result["parsed"],dict):
        return result["parsed"], False
    call = OUT/"calls"/(call_id+".json")
    if call.exists():
        text = read(call)["response"]["choices"][0]["message"].get("content") or ""
        match = re.fullmatch(r"```(?:json)?\s*(.*?)\s*```",text.strip(),re.I|re.S)
        if match:
            return loads(match[1]), True
    return None, False

def score_gap(expected, context, result, method):
    parsed, unwrapped = recovered_json(result,f"gap-{method}-{expected['id']}")
    if not isinstance(parsed,dict):
        return dict(valid_json=False,finish_reason=result["finish_reason"])
    items = {c["id"]:c for c in context["items"]}
    def citation(c):
        raw_id = c.get("id")
        item = items.get(raw_id) if isinstance(raw_id,str) else None
        quote = c.get("quote")
        valid = bool(item and isinstance(quote,str) and quote and quote in item["text"])
        if item is None and isinstance(raw_id,str) and " | " in raw_id:
            key,file = raw_id.split(" | ",1)
            if key in items and file == items[key]["file"]:
                item = items[key]
        with_header = bool(item and isinstance(quote,str) and quote and quote in item["text"])
        support = [g["id"] for g in expected["evidence"] if with_header and hit(item,g)
                   and (norm(quote) in norm(g["contains"]) or norm(g["contains"]) in norm(quote))]
        return dict(c,exact_original=valid,exact_after_header=with_header,
                    gold_ids=support if valid else [],gold_ids_after_header=support)

    citations = [citation(c) for c in objects(parsed.get("citations"))]
    conflicts = [[citation(c) for c in objects(conflict.get("sides"))]
                 for conflict in objects(parsed.get("conflicts"))]
    recognized = [sorted({g for c in sides for g in c["gold_ids"]}) for sides in conflicts]
    recognized_after_header = [sorted({g for c in sides for g in c["gold_ids_after_header"]})
                               for sides in conflicts]
    expected_conflicts = [sorted(c) for c in expected["conflicts"]]
    return dict(valid_json=not unwrapped,unwrapped_json=unwrapped,status=parsed.get("status"),reason=parsed.get("reason"),
                answer=parsed.get("answer"),expected_answer=expected["answer"],
                status_correct=parsed.get("status")==expected["status"],
                reason_correct=parsed.get("reason")==expected["reason"],
                false_answered=parsed.get("status")=="AnsweredInSnapshot" and expected["status"]=="Open",
                citations=citations,conflicts=conflicts,
                invalid_citations=sum(not c["exact_original"] for c in citations+[c for sides in conflicts for c in sides]),
                conflicts_lost=[x for x in expected_conflicts if x not in recognized],
                conflicts_lost_after_header=[x for x in expected_conflicts if x not in recognized_after_header],
                invalid_citations_after_header=sum(not c["exact_after_header"]
                                                   for c in citations+[c for sides in conflicts for c in sides]),
                unmatched_reported_conflicts=[i for i,x in enumerate(recognized) if x not in expected_conflicts])

def score_b():
    inputs = {b["id"]:b for b in read(PHASE/"b-input.json")["blocks"]}
    targets = [g for g in read(PHASE/"b-gold.json") if g["pilot"]]
    review_path = PHASE/"extraction-review.json"
    review = read(review_path) if review_path.exists() else {}
    results = {}
    for method in ("B1","B2","B3"):
        folder = OUT/"extraction"/method
        plan_path = OUT/f"{method}-plan.json"
        if not plan_path.exists():
            continue
        plan = read(plan_path)
        seen, suggestions = {}, []
        completed, invalid, unwrapped_count = 0,0,0
        for path in sorted(folder.glob("*.json")):
            completed += 1
            result = read(path)
            response, unwrapped = recovered_json(result,f"extract-{method}-{path.stem}")
            unwrapped_count += int(unwrapped)
            if unwrapped or result["finish_reason"]!="stop" or not isinstance(response,dict) or not isinstance(response.get("facts"),list):
                invalid += 1
                if not isinstance(response,dict) or not isinstance(response.get("facts"),list):
                    continue
            for i,f in enumerate(response["facts"]):
                if not isinstance(f,dict):
                    continue
                key = f"{method}/{path.stem}/{i}"
                raw_id = f.get("id")
                wrapped_id = isinstance(raw_id,str) and re.fullmatch(r"\[b[0-9]+\]",raw_id)
                locator = raw_id[1:-1] if wrapped_id else raw_id
                c = inputs.get(locator) if locator in result["blocks"] else None
                q = f.get("quote")
                valid = bool(c and isinstance(q,str) and q and q in c["text"])
                possible = [g["id"] for g in targets if valid and g["text"] in c["text"]
                            and (norm(q) in norm(g["text"]) or norm(g["text"]) in norm(q))]
                statement = f.get("statement") if isinstance(f.get("statement"),str) else ""
                exact = [g["id"] for g in targets if g["id"] in possible and g["text"] in q
                         and norm(statement).rstrip(".") == norm(g["text"]).rstrip(".")]
                decision = review.get(key,{})
                graded = exact or decision.get("credit",[])
                for id in graded:
                    assert valid and id in possible, key
                    seen[id] = key
                suggestions.append(dict(key=key,entry=f,exact_source=valid,unwrapped_id=bool(wrapped_id),candidates=possible,
                                        exact_meaning=exact,decision=decision))
        selected_ids = {id for group in plan["groups"] for id in group}
        skipped = [g["id"] for g in targets if not any(
            g["text"] in c["text"] for id,c in inputs.items() if id in selected_ids)]
        results[method] = dict(completed_calls=completed,planned_calls=len(plan["groups"]),
                               invalid_or_truncated_calls=invalid,fenced_json_calls=unwrapped_count,exact_or_adjudicated=seen,
                               missing=[g["id"] for g in targets if g["id"] not in seen],
                               missing_from_selection=skipped,
                               invalid_sources=sum(not s["exact_source"] for s in suggestions),
                               wrapped_ids=sum(s["unwrapped_id"] for s in suggestions),
                               irrelevant=sum(s["decision"].get("category")=="irrelevant" for s in suggestions),
                               pending_semantic_review=sum(not s["exact_meaning"] and not s["decision"]
                                                           for s in suggestions),rows=suggestions)
    if results:
        dump(OUT/"b-scores.json",results)
    return results

def main():
    verify_freeze()
    score_ac()
    score_b()

if __name__=="__main__":
    main()
