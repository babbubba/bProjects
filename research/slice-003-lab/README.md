# Slice 003 — laboratorio esplorativo indipendente

**Stato:** ricerca/POC; non implementazione della 003 e non test di accettazione della 002.

È un laboratorio volutamente piccolo, senza framework, CI o gate: `lab.py` usa solo la libreria standard Python e le **fixture sintetiche già esistenti** in `tests/fixtures/documents/` della slice 002. I risultati vanno conservati nel branch di ricerca, non nella 002.

## Avvio rapido

Dalla root del repository:

```bash
python3 research/slice-003-lab/lab.py
```

Esegue una baseline lessicale BM25 sui file TXT/Markdown/DOCX esistenti. Produce `results/latest.json` (risposte grezze, top-5, hit@k) e `results/latest.md` (sintesi). **Non contatta il DGX**.

Per provare l'endpoint embedding locale, servono URL completo e model ID reali; non sono indovinati:

```bash
export BPROJECTS_LAB_TOKEN="INSERISCI_TOKEN_NELLA_SHELL"
python3 research/slice-003-lab/lab.py \
  --embed-url "http://HOST:PORT/v1/embeddings" \
  --embed-model "MODEL_ID_ESATTO" \
  --out research/slice-003-lab/results/bge.json
```

L'endpoint deve usare il contratto OpenAI-compatible per embeddings. Questo esperimento confronta la stessa query e gli stessi chunk su `bm25`, `dense` e `hybrid_rrf`. Nessun Qdrant richiesto.

Per un test parallelo NLI diagnostico **sul servizio esistente**, aggiungere:

```bash
  --nli-url "http://HOST:PORT/v1/nli"
```

L'opzione NLI non decide la classifica: scrive separatamente i punteggi ottenuti sui casi numerici già noti. Il token è letto da una variabile d'ambiente e mai salvato.

Puoi passare una cartella con **testi già estratti da T082/T083** (TXT, MD e DOCX) tramite `--docs PATH`. Prima elimina dati cliente, credenziali e diagnostica sensibile; per riusare gli stessi gold le query di `cases.json` devono puntare alle fonti presenti in tale cartella. Se l'output usa altre fonti, crea una copia del dataset con nuovi gold e selezionala con `--cases`.

## Cosa misura concretamente

- `hit@1/3/5`: la query recupera la riga con testo gold del file corretto? Sono misure di **retrieval**, non di risposta LLM.
- Ordine e contenuto dei migliori candidati, utili per capire perché la query N4 recupera o non recupera la «Scheda A».
- Differenza fra ricerca testuale e embedding dense, a **parità di corpus**.
- Casi NLI su limiti quantitativi/condizioni, senza reinterpretarli come validazione dell'analisi.
- Tempo totale del laboratorio e tempo di ogni query; l'esecuzione contiene anche il costo di creazione degli embedding e non rappresenta la latenza di ricerca a indice persistente.

**Limiti deliberati:** nessun test/gate formale, nessuna richiesta al modello generativo, nessuna persistenza di indice, nessun reranker integrato, nessun PDF nel runner standard (per PDF usare testo già estratto dal parser della 002). Il chunking a riga è una baseline semplice, *non* rappresenta la struttura Docling e non può dimostrare copertura completa. I risultati valgono solo per il corpus effettivamente misurato.

## Sviluppi opzionali solo se le prime misure sono promettenti

1. Aggiungere una richiesta HTTP al reranker BGE già disponibile, per confrontare l'ordine dei candidati.
2. Provare i documenti sintetici grandi generati dal codice `LargeDocumentCorpus.cs` (T082/T083), meglio se materializzati una volta in questa cartella e con i gold originali; non copiare dati privati.
3. Confrontare retrieval su chunk strutturati reali del parser anziché righe.
4. Misurare corretta valutazione Gap su evidenze recuperate e risposta verificata, lasciando distinta la ricerca di candidati dall'esistenza di un riscontro.

## Conservazione su Git

Branch di ricerca isolato: `research/slice-003-lab`. Claude continua a lavorare sulla `feature/002-implementation`; evitare merge o cherry-pick incrociati fino al consolidamento della 002.

`results/` è ignorata nel lab solo in assenza di una decisione su quali output salvare. Per conservare esperimenti pubblicabili (solo dati sintetici), fare commit espliciti dei JSON/MD dopo aver verificato assenza di token, endpoint e dati riservati. Non salvare log HTTP completi contenenti header.

Il documento [ricerca preliminare](research-preliminare.md) è conservato accanto ai file del laboratorio. Tutti i risultati successivi possono essere citati nella futura pianificazione Spec Kit.
