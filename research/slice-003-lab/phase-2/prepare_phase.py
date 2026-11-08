"""Freeze the evaluation cohort, extraction sample and judgments before inference."""
import json
import random
import re
import sys
from pathlib import Path

PHASE = Path(__file__).resolve().parent
LAB = PHASE.parent
sys.path.insert(0, str(LAB))
from experiment import norm
from prepare import dump, sha

def read(path):
    return json.loads(path.read_text(encoding="utf-8"))

def file_hash(path):
    return sha(path.read_bytes())

# Broad signals for applicable prose. These are features, not proof of relevance.
SIGNALS = re.compile(
    r"\d|\b(?:non|solo|soltanto|entro|salvo|tranne|eccett\w*|esclus\w*|viet\w*|"
    r"deve|devono|dovr\w*|obblig\w*|richied\w*|consent\w*|ammess\w*|"
    r"garant\w*|previst\w*|conserv\w*|inviat\w*|eseguit\w*|se)\b", re.I
)

def candidate(text):
    return len(text.split()) >= 6 and bool(SIGNALS.search(text))

def rows(scenario):
    return read(LAB / "indexes" / f"{scenario}--block_parent.json")

def prepare_a():
    base = read(LAB / "retrieval-cases.json")["scenarios"]
    lookups = {s: {r["id"]: r for r in base[s]} for s in ("N4-fixtures", "T083-multi")}
    n, t = "N4-fixtures", "T083-multi"
    # Enriched diagnostic cohort, including non-evidence parents. Indices refer to the
    # immutable Phase 1 manifests and are fixed for every query and configuration.
    selected = {n: list(range(6)), t: [4, 9, 32, 80, 87, 94, 150, 177, 190, 196, 201,
                240, 246, 304, 321, 331, 373, 396, 403, 454, 460, 475]}
    fixture = []
    for scenario, indices in selected.items():
        src = rows(scenario)
        parents = list(src["parents"].items())
        for i in indices:
            key, parent = parents[i]
            children = [c for c in src["children"] if key in c["parent_keys"]]
            heading = next((c["heading"] for c in children if c["heading"]), [])
            fixture.append(dict(id=f"{scenario}-{i}", key=key, file=parent["file"],
                                text=parent["text"], heading=heading,
                                version=children[0]["version_sha256"],
                                type=children[0]["source_type"], children=[
                                    dict(id=f"{scenario}-c{c['id']}", text=c["primary"],
                                         search=c["search"], locator=c.get("locator"))
                                    for c in children]))
    old = "Scheda A\nIl termine di pagamento e' 75 giorni. Il pagamento tramite bonifico è ammesso."
    late = "Per il dispositivo Orione, la taratura deve essere ripetuta ogni 73 ore di funzionamento."
    filler = ("Il laboratorio si affaccia sul cortile. Gli abitanti ricordano le visite di primavera. "
              "Le vecchie fotografie sono conservate nella sala d'ingresso. ") * 9
    for key, file, blocks, eligible in [
        ("excluded", "scheda-a-v0.txt", [old], False),
        ("late", "orione.txt", [filler.strip(), late], True),
        ("early", "vega.txt", [late.replace("Orione", "Vega").replace("73", "91"), filler.strip()], True),
    ]:
        original = "\n".join(blocks)
        fixture.append(dict(id=key, key=key, file=file, text=original, heading=[],
                            version=sha(original.encode()), type="Document", eligible=eligible,
                            children=[dict(id=f"{key}-c{i}", text=b, search=b,
                                           locator={"synthetic_block": i}) for i,b in enumerate(blocks)]))
    judgments = []
    queries = []
    def add(id, query, reason, answer, evidence, conflicts=()):
        queries.append(dict(id=id, query=query))
        judgments.append(dict(id=id, status="AnsweredInSnapshot" if reason is None else "Open",
                              reason=reason, answer=answer, evidence=evidence, conflicts=list(conflicts)))
    def from_old(s, id):
        return lookups[s][id]["gold"]
    def g(id, file, text):
        return dict(id=id, file=file, contains=text)

    add("A01-completa", lookups[t]["M01"]["query"], None, "48 mesi dalla firma", from_old(t,"M01"))
    add("A02-assente", "Quale numero di reperibilità telefonica prevede il verbale di collaudo Zefiro?", "assente", None, [])
    add("A03-parziale", "Quale termine di pagamento indica la Scheda A e da quale evento decorre?",
        "parziale", "30 giorni, decorrenza assente", from_old(n,"N4-doc-title"))
    add("A04-discordante", lookups[t]["all-warranty"]["query"], "ambigua", None,
        from_old(t,"all-warranty"), [["M04","M13"]])
    add("A05-altro-documento", "Qual è l'orario limite per gli ordini urgenti secondo il verbale di collaudo Zefiro?",
        "non pertinente", None, from_old(t,"M12"))
    add("A06-condizione", lookups[t]["M17"]["query"], None, "Seconda rata sospesa se il collaudo fallisce", from_old(t,"M17"))
    add("A07-eccezione", lookups[t]["M12"]["query"], None, "Dopo le 16:00 non accettati salvo clienti Gold", from_old(t,"M12"))
    add("A08-esclusa", "La Scheda A inclusa nello snapshot prevede il pagamento tramite bonifico?",
        "assente", None, [])
    a = from_old(n,"N4-doc-title")
    pm = g("N4-PM","pm-message.txt","Il cliente mi ha confermato per telefono che il termine di pagamento e' di 45 giorni.")
    add("A09-PM", lookups[n]["N4-doc-title"]["query"], None, "30 giorni secondo la Scheda A",
        a + [pm], [[a[0]["id"], pm["id"]]])
    add("A10-multifonte", lookups[t]["penalty-reference"]["query"], None,
        "Allegato C, 1,5% del valore dell'ordine per giorno, tetto del 15%", from_old(t,"penalty-reference"))
    add("C11-oltre-prefisso", "Con quale frequenza si deve ripetere la taratura del dispositivo Orione?",
        None, "73 ore di funzionamento", [g("LATE","orione.txt",late)])
    add("C12-prima-del-prefisso", "Con quale frequenza si deve ripetere la taratura del dispositivo Vega?",
        None, "91 ore di funzionamento", [g("EARLY","vega.txt",late.replace("Orione","Vega").replace("73","91"))])
    assert len(filler.strip()) > 1200
    for j in judgments:
        for gld in j["evidence"]:
            assert any(p["file"] == gld["file"] and norm(gld["contains"]) in norm(p["text"]) for p in fixture), gld
    return dict(parents=fixture, queries=queries, phase1_parent_positions=selected), judgments

def prepare_b():
    src = rows("T082-single")
    parents = list(src["parents"].items())
    original = LAB / "corpus/T082-single/capitolato-aurora.txt"
    text = original.read_bytes().decode("utf-8")
    primary = read(LAB / "corpus/T082-single/gold.json")["Gold"]
    # These three templates are exactly LargeDocumentCorpus.BulkRule. They are
    # used only to reconstruct the scoring reference from the immutable original.
    pattern = re.compile(
        r"Per la linea [A-Z][a-z]{5} il lotto minimo di rifornimento è di \d+ pezzi\.|"
        r"La scheda tecnica della linea [A-Z][a-z]{5} prevede un imballo da \d+ pezzi per scatola\.|"
        r"Nel reparto [A-Z][a-z]{5} la temperatura di conservazione è fissata a \d+ gradi\."
    )
    rules = pattern.findall(text)
    assert len(rules) == len(set(rules)) == 260
    targets = [dict(id=g["Id"], text=g["Sentence"], negative=g["Negative"]) for g in primary]
    targets.extend(dict(id=f"R{i:03}", text=r, negative=False) for i,r in enumerate(rules, 1))
    # Enrich the pilot with all 16 primary clauses across the three zones plus
    # two fixed random noise controls and six fixed secondary-rule passages.
    sample = {17,28,39,51,62,222,233,245,256,268,370,382,394,405,417,428}
    rng = random.Random(20261109)
    secondary = [i for i,(_,p) in enumerate(parents) if pattern.search(p["text"]) and i not in sample]
    sample.update(rng.sample(secondary,6))
    noise = [i for i,(_,p) in enumerate(parents) if not pattern.search(p["text"]) and i not in sample]
    sample.update(rng.sample(noise,2))
    pilot = [dict(id=f"b{c['id']}", text=c["primary"], locator=c.get("locator"),
                  block_ref=c["block_ref"], parents=[i for i in sorted(sample) if parents[i][0] in c["parent_keys"]])
             for c in src["children"] if any(parents[i][0] in c["parent_keys"] for i in sample)]
    assert all(text.count(g["text"]) == 1 for g in targets)
    for g in targets:
        g["pilot"] = any(g["text"] in c["text"] for c in pilot)
        g["source_offset_utf16"] = len(text[:text.index(g["text"])].encode("utf-16-le")) // 2
    assert sum(g["pilot"] for g in targets[:16]) == 16
    audit = dict(total_passages=len(src["children"]),
                 selected_passages=sum(candidate(c["primary"]) for c in src["children"]),
                 gold=[dict(id=g["id"], selected=any(candidate(c["primary"]) and g["text"] in c["primary"]
                            for c in src["children"])) for g in targets])
    return dict(original_sha256=file_hash(original), parents=sorted(sample), blocks=pilot), targets, audit

def main():
    assert not (PHASE / "freeze.json").exists(), "Freeze already exists"
    a, ag = prepare_a()
    b, bg, audit = prepare_b()
    for name, data in (("a-input.json", a),("a-gold.json", ag),("b-input.json", b),
                       ("b-gold.json", bg),("b-selection-audit.json", audit)):
        dump(PHASE / name,data)
    files = ["a-input.json","a-gold.json","b-input.json","b-gold.json","b-selection-audit.json","protocol.json"]
    refs = ["retrieval-cases.json", *[f"indexes/{s}--block_parent.json" for s in
             ("N4-fixtures","T082-single","T083-multi")], "corpus/T082-single/capitolato-aurora.txt",
             "corpus/T082-single/gold.json"]
    refs.extend(f"results/{s}--block_parent.json" for s in
                ("N4-fixtures","T082-single","T083-multi"))
    dump(PHASE / "freeze.json", {
        "phase1": "cd0dae3695e80f62d8abb6a1acf549a952c0902f",
        "skrag": "39b2b19024f32cc22922b80215c2ac5b81eed004",
        "sha256": {n:file_hash(PHASE/n) for n in files} | {"../"+n:file_hash(LAB/n) for n in refs}
    })
    print(f"A/C: {len(a['parents'])} parents, {len(a['queries'])} queries. B: {len(b['blocks'])} blocks,"
          f" {sum(g['pilot'] for g in bg)} pilot targets, {len(bg)} corpus targets.")

if __name__ == "__main__":
    main()
