#!/usr/bin/env python3
"""Laboratorio esplorativo retrieval bProjects 003. Solo standard library, nessuna CI."""
import argparse
import collections
import hashlib
import json
import math
import os
import re
import statistics
import time
import unicodedata
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
STOP = set("il lo la i gli le un una uno di del della delle dei da in con per su al alla alle agli a e ed o che cosa dice quale quali quanto e entro sono è non sul sui come si deve dei".split())
WORDS = re.compile(r"[a-z0-9]+")
def words(s):
    normalized = unicodedata.normalize("NFKD", s.lower())
    ascii_text = "".join(c for c in normalized if not unicodedata.combining(c))
    return [w for w in WORDS.findall(ascii_text) if w not in STOP]

def decode_docx(path):
    with zipfile.ZipFile(path) as z:
        xml = ET.fromstring(z.read("word/document.xml"))
    return "\n".join("".join(n.itertext()) for n in xml.iter() if n.tag.endswith("}p"))

def read_chunks(folder):
    chunks, skipped = [], []
    for path in sorted(folder.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in (".txt", ".md", ".docx"):
            if path.suffix.lower() == ".pdf":
                skipped.append(str(path.relative_to(folder)))
            continue
        try:
            raw = decode_docx(path) if path.suffix.lower() == ".docx" else path.read_text(encoding="utf-8-sig")
        except (UnicodeError, OSError, ValueError, KeyError, zipfile.BadZipFile) as e:
            skipped.append(str(path.relative_to(folder)) + ": " + str(e))
            continue
        heading = ""
        file_name = str(path.relative_to(folder)).replace("\\", "/")
        for line in raw.splitlines():
            line = line.strip()
            if not line:
                continue
            if line.startswith("#"):
                heading = line.lstrip("#").strip()
                continue
            if not heading and len(line) < 90 and not line.endswith((".", "!", "?", ";", ":")):
                heading = line
                continue
            chunks.append({"id": len(chunks), "file": file_name, "heading": heading, "text": line,
                           "context": " ".join([heading, line]).strip()})
    return chunks, skipped

def bm25_rank(query, chunks):
    docs = [words(c["context"]) for c in chunks]
    freq = [collections.Counter(d) for d in docs]
    N = len(docs)
    avg = sum(map(len, docs)) / max(1, N)
    df = collections.Counter(k for d in freq for k in d)
    q = set(words(query))
    scores = []
    for tf in freq:
        n = sum(tf.values())
        score = 0
        for term in q:
            f = tf[term]
            if f:
                idf = math.log(1 + (N-df[term]+.5) / (df[term]+.5))
                score += idf * f * 2.2 / (f + 1.2 * (.25 + .75*n/max(1,avg)))
        scores.append(score)
    return sorted(range(N), key=lambda i:(-scores[i], i)), scores

def post(url, payload, token, timeout=90):
    body = json.dumps(payload, ensure_ascii=True).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, body, headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))

def embeddings(url, model, texts, token, batch=24):
    out = []
    for start in range(0,len(texts),batch):
        data = post(url, {"model":model,"input":texts[start:start+batch]}, token)
        rows = sorted(data["data"], key=lambda x:x.get("index", 0))
        if len(rows) != len(texts[start:start+batch]):
            raise ValueError("Embedding: numero di vettori diverso da input")
        for row in rows:
            vec = row["embedding"]
            norm = math.sqrt(sum(v*v for v in vec)) or 1
            out.append([v/norm for v in vec])
    return out

def cos_rank(qvec, vectors):
    scores = [sum(a*b for a,b in zip(qvec, v)) for v in vectors]
    return sorted(range(len(scores)), key=lambda i:(-scores[i], i)), scores

def rrf(a,b, length, k=60):
    scores = [0.0]*length
    for rank,idx in enumerate(a):
        scores[idx] += 1/(k+rank+1)
    for rank,idx in enumerate(b):
        scores[idx] += 1/(k+rank+1)
    return sorted(range(length),key=lambda i:(-scores[i], i)), scores

def in_gold(c, gold):
    return (c["file"] == gold["file"] or c["file"].endswith("/"+gold["file"])) and (
        gold.get("contains", "").lower() in c["text"].lower())

def evaluate(ranking, chunks, gold, k):
    eligible = [i for i,c in enumerate(chunks) if in_gold(c,gold)]
    if not eligible:
        return None
    top = ranking[:k]
    return {"hit": any(i in top for i in eligible),
            "rank": next((pos+1 for pos,i in enumerate(ranking) if i in eligible), None)}

def main():
    ap = argparse.ArgumentParser(description="POC retrieval indipendente dal prodotto")
    ap.add_argument("--docs", type=Path, default=ROOT/"tests/fixtures/documents")
    ap.add_argument("--cases", type=Path, default=HERE/"cases.json")
    ap.add_argument("--out", type=Path, default=HERE/"results"/"latest.json")
    ap.add_argument("--embed-url", default="", help="URL COMPLETO dell'endpoint OpenAI-compatible /v1/embeddings")
    ap.add_argument("--embed-model", default="", help="model ID embedding")
    ap.add_argument("--nli-url", default="", help="URL COMPLETO dell'endpoint /v1/nli")
    ap.add_argument("--token-env", default="BPROJECTS_LAB_TOKEN")
    args = ap.parse_args()
    if bool(args.embed_url) != bool(args.embed_model):
        ap.error("--embed-url e --embed-model vanno passati insieme")
    chunks, skipped = read_chunks(args.docs)
    if not chunks:
        ap.error("Nessun chunk testuale: verifica --docs")
    cases = json.loads(args.cases.read_text(encoding="utf-8"))["cases"]
    token = os.environ.get(args.token_env, "")
    started = time.perf_counter()
    corpus_hash = hashlib.sha256(json.dumps(chunks,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
    vecs = None
    if args.embed_url:
        vecs = embeddings(args.embed_url,args.embed_model,[c["context"] for c in chunks],token)
    rows, nli = [], []
    for case in cases:
        if not any(in_gold(c,case["gold"]) for c in chunks):
            rows.append({"id":case["id"],"status":"NOT_IN_CORPUS"})
            continue
        t0 = time.perf_counter()
        ranks = {"bm25":bm25_rank(case["query"],chunks)[0]}
        if vecs is not None:
            qvec = embeddings(args.embed_url,args.embed_model,[case["query"]],token)[0]
            ranks["dense"] = cos_rank(qvec, vecs)[0]
            ranks["hybrid_rrf"] = rrf(ranks["bm25"],ranks["dense"],len(chunks))[0]
        row={"id":case["id"],"query":case["query"],"gold":case["gold"],"status":"MEASURED","latency_ms":round(1000*(time.perf_counter()-t0),1),"methods":{}}
        for name,ranked in ranks.items():
            row["methods"][name]={"top5":[{"file":chunks[i]["file"],"heading":chunks[i]["heading"],"text":chunks[i]["text"][:250]} for i in ranked[:5]],"hit1":evaluate(ranked,chunks,case["gold"],1),"hit3":evaluate(ranked,chunks,case["gold"],3),"hit5":evaluate(ranked,chunks,case["gold"],5)}
        rows.append(row)
    if args.nli_url:
        for case in json.loads((HERE/"nli-cases.json").read_text(encoding="utf-8"))["cases"]:
            t0 = time.perf_counter()
            response=post(args.nli_url,{"premise":case["premise"],"hypothesis":case["hypothesis"]},token)
            nli.append({**case, "prediction":response.get("prediction"),"scores":response.get("scores"),"latency_ms":round(1000*(time.perf_counter()-t0),1)})
    result={"lab":"slice-003-lab-v1","corpus":str(args.docs),"corpus_hash":corpus_hash,"chunks":len(chunks),
            "skipped_files":skipped,"embed_model":args.embed_model or None,"cases":rows,"nli":nli,
            "duration_s":round(time.perf_counter()-started,2),"note":"Esplorativo: non valida i gate della slice 002."}
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    measured=[r for r in rows if r["status"]=="MEASURED"]
    names=list(measured[0]["methods"]) if measured else []
    lines=["# Risultati laboratorio Slice 003","","Corpus: "+str(args.docs), f"Chunk: {len(chunks)}; query misurate: {len(measured)}; non presenti: {len(rows)-len(measured)}",f"Durata complessiva: {result['duration_s']} s","", "| Metodo | Hit@1 | Hit@3 | Hit@5 |","|---|---:|---:|---:|"]
    for method in names:
        vals=[sum(1 for row in measured if row["methods"][method][f"hit{k}"]["hit"]) for k in (1,3,5)]
        lines.append(f"| {method} | {vals[0]}/{len(measured)} | {vals[1]}/{len(measured)} | {vals[2]}/{len(measured)} |")
    lines += ["", "## Casi non recuperati in top 5"]
    for row in measured:
        for method in names:
            if not row["methods"][method]["hit5"]["hit"]:
                lines.append(f"- {row['id']}: {method}")
    lines += ["","## NLI diagnostico (non criterio di accettazione)"]
    for x in nli:
        lines.append(f"- {x['id']}: atteso={x['expected']}, predetto={x['prediction']} ({x['latency_ms']}ms)")
    lines += ["","## Limiti","- Gold locale, non rappresentativo; nessuna verifica semantica automatica.","- Hit@k misura il recupero, non la correttezza dell'analisi.","- PDF richiedono testo estratto dal parser; file PDF saltati qui.","- I chunk a riga sono una baseline volutamente semplice, non il chunking Docling della produzione."]
    args.out.with_suffix(".md").write_text("\n".join(lines)+"\n",encoding="utf-8")
    print("\n".join(lines[:14]))
    print("Output:", args.out)

if __name__ == "__main__":
    main()
