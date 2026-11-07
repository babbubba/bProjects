# I5 — T082 (documento singolo oltre contesto): FAILED all'estrazione (2 esecuzioni), analisi NON raggiunta

**Autorizzazione**: autoload del router consentito dall'autore (2026-11-07); `BPROJECTS_TEST_DGX_ALLOW=1` solo nel processo del test; una inferenza alla volta; nessun altro modello caricato né slot attivo prima di ciascuna prova.
**Runtime**: `Qwen3.6-35B-Smart` (router llama.cpp, ctx 131072, parallel 1), `BPROJECTS_TEST_CTX_WINDOW=131072`, PostgreSQL e parser Docling reali, endpoint `Analizza` di produzione.
**Dataset**: `capitolato-aurora.txt`, generatore deterministico seed 20261106, 3116 blocchi; **295725 token** (tokenizer reale) contro 2×B = 227328 ⇒ requisito >2×budget SODDISFATTO (B = 113664).
**Esito**: FAILED, `[COMPLETAMENTO]` preparazione non Ready. Esecuzioni: 9m38s e 2m41s, stesso esito.
- Strutturale: PASS (dimensione >2×B, hash originale, upload 201).
- Causa osservata (job `ExtractUnit`): `work_output_invalid` — «L'output del modello non è un'estrazione valida (24 violazioni)… evidenza eN: kind Other richiede kindRationale». La chiamata del modello è terminata normalmente (`finish=stop`, prompt 3581, completion 7422 token, 124 s). Le altre unità sono andate `parent_failed`.
- Copertura / semantica / provenienza / analisi v4 / riconciliazione gerarchica: **NOT VERIFIED** (mai raggiunte).
**Classificazione**: non infrastrutturale; è il validatore di produzione dell'estrazione che rifiuta l'intera unità (e la preparazione) quando il modello etichetta evidenze come `Other` senza `kindRationale`. Il riempitivo non applicabile del corpus induce evidenze `Other`; l'estrazione non ha retry guidato. Non risolvibile senza una decisione (contratto estrazione / corpus). Nessuna correzione tentata, nessun gold o dimensione modificati. Aggiunta al test solo diagnostica (errori job e chiamate) per leggere la causa prima che il DB di test venga eliminato.
**Chiamate**: T082 non ha raggiunto l'analisi; chiamate di estrazione osservate prima dell'errore non contate integralmente (DB temporaneo eliminato; solo l'ultima chiamata riportata).
