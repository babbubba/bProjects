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

## Terza esecuzione (2026-11-07 07:23–07:31, dopo T134) — FAILED per un motivo DIVERSO: troncamento dell'output di estrazione
Condizioni: stesso modello/finestra (W=131072, O=16384, M=1024, B=113664), modello `sleeping`→risvegliato, nessun altro modello caricato, slot inattivo; stesso corpus (sha256 a926fb1d…30f6, 295725 token = 2,60×B).
Esito: strutturale PASS; `[COMPLETAMENTO]` preparazione Failed, durata 7m59s.
- Il difetto `Other` senza `kindRationale` NON si è ripresentato in questa esecuzione (non provato che il retry T134 sia intervenuto: non c'è evidenza di marker nei log).
- Nuova causa: job `ExtractUnit` `work_output_invalid` «Output del modello troncato dal limite di lunghezza» — chiamata `finish=length`, prompt 3645, completion 16384 = tetto di output, 275 s. Le altre chiamate osservate: finish=stop (prompt 3512/completion 1723/29 s; prompt 2783/completion 6805/112 s). Le altre unità `parent_failed`.
- Per contratto il troncamento non è ritentato (§2.3, e T134 lo esclude espressamente). Analisi, gerarchia, copertura, semantica e citazioni NOT VERIFIED.
- Ipotesi (non verificata): su un'unità con molto testo narrativo o ripetitivo il modello genera un'estrazione molto lunga fino al tetto di 16384 token. Non si può distinguere fra contenuto pertinente numeroso, ripetizioni o degenerazione senza il contenuto della chiamata (DB di test eliminato).
Per mandato dell'autore («se fallisce nuovamente per un motivo diverso, fermarsi e riportare le evidenze») nessun ulteriore tentativo. T083 non eseguito.
