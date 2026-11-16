# RH-051 / M5 — Diagnosi offline della crescita della riconciliazione v11

Data: 2026-11-16. Branch `feature/003-retrieval-evidence-search`. Slice 002, T179 (non completato). **Zero chiamate al DGX, nessuna nuova run, nessun codice di produzione, gold e default invariati, M5 non è PASS.**

## 0. Metodo, fonti e limiti

| Cosa | Fonte |
|---|---|
| Output reali committati (73) | `%USERPROFILE%\bprojects-rh051-evidence\run4-resume-real\final\work-outputs.jsonl` + `batch-ledger.jsonl` (ordine verificato batch per batch) |
| Richiesta/risposta grezza del batch 73 | `…\run\raw\0043-expand-request.json` / `0043-expand-response.json` (fuori da Git) |
| Regole del motore | `ReconciliationCandidates.cs`, `ReconciliationFinalPass.cs`, `ReconciliationCompletion.cs`, `DocumentReconcilePlan.cs` (lette, non modificate) |
| Ledger diagnostici deterministici | cartella [`rh051-frontier/`](rh051-frontier/) (prodotti da [`build-frontier-ledgers.ps1`](rh051-frontier/build-frontier-ledgers.ps1) sugli output archiviati) |

**Limiti dichiarati.** (1) La ricostruzione è *statica*: legge gli output persistiti e applica le regole del motore lette nel codice; non esegue il motore (servirebbe il tokenizer del DGX, escluso). (2) I candidati *originati dal server* (known relations, shared-key) non sono negli output: per la fase A sono noti solo quelli già confrontati (6); per B e Cross non sono noti. I conteggi della frontiera sono quindi **limiti inferiori**. (3) Il batch 73 non ha payload persistito (rifiutato): non è conteggiato come valido e i suoi figli sono sconosciuti. (4) Correzione a un mio rapporto precedente: la richiesta del batch 73 contiene **41** voci (15 in N1, 26 in N2), non 42; avevo contato l'esempio `[E3]` del prompt di sistema.

## 1. Saturazione sistematica

**Fatti (dati).**
- 23 espansioni committate, **23 su 23 con esattamente 16 candidati** (minimo = massimo = 16). Il tetto è `ExpansionCandidateCap = 16` (`ReconcileAliasContracts.cs:224`); lo schema ha `maxItems 16` e il composer rifiuta più di 16 (`ReconcileAliasComposer.cs:327`). Anche la risposta rifiutata del batch 73 ne aveva 16.
- Le 23 espansioni non sono 23 candidati: sono **blocchi della griglia** di **5 candidati-gruppo** della fase A (blocchi 4, 4, 8, 4, 3 committati; il batch 73 è, per ordine, il quarto blocco del quinto gruppo — inferenza dall'ordinale, non dal payload). Il rapporto precedente «23 espansioni» va letto come «23 blocchi».
- Per blocco: 16 candidati → 16 coppie distinte (una sola eccezione, 15). Su 368 candidati: **283 coppie distinte** (85 duplicati, 84 fra espansioni e 1 nello stesso blocco), 18 già confrontate prima dell'arresto, **265 da confrontare**. Dettaglio per blocco in `expansions-ledger.jsonl`.
- Mix dei tipi (368): `Qualifies`/`DependsOn` dominanti; **`Contradicts` 5**, `Corrects` 1, `Confirms` 1, `Alias` 5. Solo 6 su 368 portano una relazione di conflitto/correzione.

**Regola del codice (verificata).**
1. `ExpandGridAsync` conta `saturated += outcome.Result.Candidates.Count >= policy.CandidateCap` (`ReconciliationCandidates.cs:397`). Con tetto = 16 e massimo schema = 16, **un blocco che usa il massimo è sempre saturo**.
2. Se `saturated > 0` il padre diventa `NotProcessed` con motivo `expansion_saturated` e i figli validi sono conservati come `Pending` (`:405-410`). Il padre non è mai `Expanded` né `Compared`.
3. `NotProcessed` non è terminale (`Terminal`, `:34-39`; `IsTerminal`, `ReconciliationCompletion.cs:302-307`). Il round finale elabora solo i `Pending` (`ReconciliationFinalPass.cs:38-44`) e non riapre i `NotProcessed`.
4. Quindi `run.Unresolved` contiene il padre → round finale `CoverageIncomplete` (`:76-78`) → gap `candidate_not_terminal` (`ReconciliationCompletion.cs:206-208`) → `AssembleAsync` lancia `coverage_incomplete` (`DocumentReconcilePlan.cs:1120-1125`): **nessuna revisione**, neppure se tutti i figli fossero confrontati.

**Conclusione 1 — causa verificata.** La saturazione sistematica *non* dipende dal budget: nel contratto v11 un blocco al tetto rende il padre non terminale per costruzione e non esiste un percorso di riapertura. Finché i blocchi restano al tetto, `Completed` è irraggiungibile.

**Ipotesi (non verificata, richiede inferenza):** la distribuzione a massa puntuale sul tetto (23/23) suggerisce che il modello riempia l'array fino a `maxItems` (anche con il prompt che dice «è una protezione, non un obiettivo»), con coda di candidati deboli; l'alternativa è che i blocchi densi (fino a 15×26 coppie fra i due gruppi nel batch 73) abbiano davvero più di 16 collegamenti plausibili. I due casi non si distinguono con i dati attuali.

## 2. Frontiera residua effettiva (solo batch validati e committati)

Stato dell'ingresso nel batch 73 (ledger `candidates-ledger.jsonl`, `frontier-summary.json`):

| Categoria | Fase A | Fase B | Cross | Note |
|---|---|---|---|---|
| Candidati-gruppo | 5 (tutti con espansione iniziata) | **5 non iniziati** | **1 non iniziato** | A: 5 padri `NotProcessed:expansion_saturated` (derivato); il quinto ha 3 blocchi su 4 |
| Evidenza–evidenza proposti dai modelli | 57: 39 confrontati (29 `Compared`, 10 `Open`), **18 non elaborati** (4 hanno la coppia già confrontata → cache hit senza chiamata) | **43 non elaborati** (7 hanno la coppia già confrontata in A) | 0 | |
| Candidati figli delle espansioni | 308 candidateId (368 prodotti), tutti `Pending` | — | — | 273 coppie distinte registrate come figli + 10 coincidenti con candidati delle foglie |
| Server (known relations) | 6 già confrontati | ignoti | ignoti | non derivabili dagli output |

Non esistono, tra i committati, candidati `Expanded` né `Saturated` oltre ai 5 padri; i `Pending` sono i figli e i non elaborati sopra. Le dipendenze: ogni figlio dipende dal padre-gruppo (`parentCandidateId`); i padri non possono diventare terminali (§1).

**Sequenza dei batch ancora necessari (regole del motore).** (a) rifare il blocco 73; (b) fase A, ciclo 1: 14 confronti E-E non in cache; (c) fase A, ciclo 2: confronto dei figli `Pending` (coppia già confrontata → nessuna chiamata); (d) fase B: 43 E-E, poi 5 gruppi con ≥ 1 blocco ciascuno e i confronti dei loro figli; (e) fase Cross: 1 gruppo e i figli; (f) round finale per i `Pending` residui; (g) stadio Gap (1 chiamata riservata).

**Confronti distinti ancora dovuti, senza stime «368 − 39»:** deduplicando per coppia non ordinata (la cache logica del motore, `ReconciliationCandidates.cs:242-248`) e togliendo le coppie già confrontate: **274 per la sola fase A** (265 dai figli + 14 E-E, meno 5 sovrapposizioni), **303 includendo i 43 E-E della fase B** (`owed-compare-pairs.txt`).

**Conclusione 2.** Frontiera minima dimostrata: 303 confronti distinti + 1 (blocco 73) + ≥ 6 espansioni (5 gruppi B + 1 Cross, ≥ 1 blocco ciascuno) + 1 Gap = **≥ 311 chiamate**, con tutti i figli futuri posti a zero. Il carico reale sarà più alto (§4).

## 3. Errore E109 (batch 73)

**Fatti.**
- Richiesta: 41 voci, alias da E4 a E194 (N1: 15, N2: 26). **`E109` compare zero volte nella richiesta** (né voce, né item, né relazione); compare solo nella risposta.
- `E109` è un alias valido del run: è un membro (mappatura unità di misura, famiglia `UM-xx` con E108, E113, E114) delle richieste 0040 e 0041, due espansioni diverse. Quindi non è inventato nel catalogo del run: è **fuori dal batch**.
- Il candidato `f14` è `E59 → E109`, `DependsOn`, `Inferred`. La motivazione parla di `numeroOrdineCliente` (E59) e dice: «E109 (non presente nel batch ma riferimento implicito al contesto N1/N2) o meglio, confrontando con E5 (scambio ordini)…». Il modello riconosce nel campo di testo che E109 non è nel batch, si corregge a metà frase su E5 (che *è* nel batch, gruppo N1), ma lascia `right = E109`.
- Il prompt dice esplicitamente che un'etichetta non presente nel batch rende la risposta «non valida e non riparabile». Lo schema JSON ammette qualunque `^E[1-9][0-9]*$`: **il vincolo di appartenenza non è espresso nello schema**, solo nel validatore.
- Nessuna evidenza disponibile *nel batch* sostiene un legame con E109 (che è una voce UM, estranea a `numeroOrdineCliente`): il collegamento intenzionale, se esiste, è E59–E5, validabile.
- Altre anomalie nella stessa risposta: `f10–f13` etichettano come `Alias` coppie di facce dello stesso campo (tipo/lunghezza vs significato), cioè un uso largo del tipo `Alias`; `f14` è l'unico slot con motivazione che si smentisce.

**Ipotesi graduate (nessuna confermabile senza altre inferenze).**
1. **Errore di generazione** (più probabile): scivolamento di token nel campo `right`, associato alla pressione del tetto (slot 14 su 16, motivazione già incerta).
2. Interpolazione posizionale: nel batch compaiono E108 ed E113/E114 della stessa tabella UM e non E109–E112; l'alias «mancante» fra E108 e E113 potrebbe attirare il modello.
3. Difetto di formulazione del prompt: poco probabile come causa unica (l'istruzione è chiara e ripetuta), ma lo **schema debole** (nessun `enum` per batch) lascia la barriera al solo validatore.
4. Problema del catalogo: **non supportato** (E109 esiste, il batch lo esclude correttamente).

**Probabilità.** Osservato: 1 risposta con alias fuori batch su 24 risposte di espansione (7 pre-ripresa, il blocco 30 recuperato, 16 della ripresa; il blocco 30 aveva un difetto diverso, la chiave locale ripetuta, già corretto). Stima puntuale 4,2 % per risposta; intervallo esatto al 95 % ≈ 0,1–21 %. Con ≥ 6 espansioni restanti (minimo) P(almeno un errore analogo) ≈ 22 %; con ~34 (scenario centrale §4) ≈ 77 %. In v11 un singolo `expand` non valido termina la run: la politica è incompatibile con questo tasso.

Il controllo fail-closed non è stato toccato: nessuna correzione automatica, nessun candidato eliminato, nessun output o hash alterato.

## 4. Sostenibilità del budget di 200 chiamate

| Voce | Chiamate | Natura |
|---|---|---|
| Già consumate (46 + 43) | 89 | fatto, da `InferenceCalls` |
| Residuo del tetto sperimentale | **111** | fatto (200 − 89) |
| Rifare il blocco 73 | 1 | minimo dimostrato |
| Confronti distinti fasi A + E-E di B | 303 | **minimo dimostrato** (da output committati) |
| Espansioni gruppi B (5) e Cross (1), 1 blocco ciascuno | 6 | minimo dimostrato (≥ 1 blocco per gruppo) |
| Gap | 1 | riserva `ExpansionReservedCalls = 1` |
| **Minimo dimostrato** | **311** | contro 111 residue |

**Limite inferiore dimostrato:** servono ≥ 311 chiamate ulteriori, cioè **≥ 200 in più delle 111 rimaste**: concludere entro il tetto di 200 è **impossibile**, anche con tutti i figli futuri a zero e senza ritentare l'errore di E109.

**Scenario centrale (ipotesi esplicite).** Blocchi per gruppo B come in A (media 4,8 → 24 blocchi), 16 candidati per blocco, ~11,5 nuovi confronti per blocco (media osservata in A: 265/23), Cross con 8 blocchi (il massimo osservato in A): +24 espansioni, ~276 confronti B, +8 espansioni e ~92 confronti Cross → ≈ **700 chiamate** residue. Scenario basso (B a 5 blocchi, Cross a 1): ≈ 380. Il tempo non è il vincolo (≈ 1 h con 32 s per espansione e 2,9 s per confronto).

**Limite congelato di 512.** `CallCount` persistito = 74; riserva 1. Lo scenario centrale (74 + ~700) lo supera, e il precheck del motore (`ReconciliationCandidates.cs:361-376`, `required = blocchi × 17`) marcherebbe prima i gruppi successivi `NotProcessed` con `expansion_budget_exceeded`, comunque fatale per la copertura (ipotesi sul momento in cui interviene, non misurata). Anche nello scenario basso (380 < 438 residue consentite) la saturazione dei blocchi (§1) impedirebbe `Completed`.

## 5. Impedimenti strutturali al PASS M5

1. **Saturazione = non terminale** (§1): con blocchi al tetto la v11 non può raggiungere `Completed`. Indipendente dal budget.
2. **Politica di fallimento rigida**: un solo `expand` non valido termina la run (tasso stimato 4 %, ≥ 22 % di abortire con il solo minimo restante).
3. **Carico**: ≥ 311 chiamate ulteriori a fronte di 111 (200 sperimentali) e probabile sforamento del 512 congelato.
4. **Vincolo non nello schema**: l'appartenenza al batch è solo nel validatore.
5. **Costo di ripresa non attribuito** (≈ 257 s senza chiamate): non blocca, ma ostacola ripetizioni frequenti.
6. Mancano ancora Gap, revisione finale e confronto col gold: nessun giudizio semantico possibile oggi.

## 6. Soluzioni esaminate (nessuna implementata)

Ogni modifica semantica richiede **v12** (`project-analysis-documents-v12`) con nuovo prompt/contratto/profilo schema e nuovo piano di validazione; la v11 resta storica e immutata, le run esistenti non cambiano contratto.

### B. Espansioni sature: completezza e tracciabilità
| Opzione | Impatto funzionale | Rischio | Compatibilità | Costo | Test |
|---|---|---|---|---|---|
| **B1 Riparto adattivo**: un blocco saturo è bisecato (griglia) e rieseguito fino a blocchi non saturi o coppia irriducibile; il padre diventa `Expanded` solo se tutti i blocchi-foglia sono completi | completezza garantita, nessuna perdita silenziosa; ogni blocco saturo sostituito da sottoblocchi tracciati | costo che può raddoppiare per livello (con 23/23 saturi potenzialmente ×2–×4 chiamate): **da solo peggiora il budget** | richiede v12 + `expansion-grid-2`; i hash dei blocchi v11 restano | alto (2–4 gg) | proprietà: copertura delle coppie, determinismo del replay, terminazione |
| **B2 Continuazione paginata**: dopo un blocco a 16 si richiede «gli altri collegamenti», passando le chiavi già date; si ferma quando una risposta ne ha < 16 | nessuna perdita, ordine deterministico, costo ≈ ⌈n/16⌉ chiamate per blocco | rischio di ripetizioni/loop del modello; serve limite alle pagine | v12 | medio-alto | continuità, assenza di duplicati, stop |
| **B3 Frontiera dichiarata**: un padre saturo è terminale `PartiallyScanned` con motivo e conteggio dei figli, e la revisione riporta la limitazione (Gap/domanda aperta) invece di bloccare `Completed` | sblocca `Completed` senza nuove chiamate; la completezza non è più «garantita» ma esplicita | **cambia il significato di Completed**: decisione di prodotto/costituzione, non tecnica | richiede amendement del contratto 4.3 e v12 | basso-medio | regole di completamento, rendering in revisione |

### A. Candidati meno numerosi e più significativi
Prompt più selettivo (priorità ai tipi con effetto: `Contradicts`, `Corrects`, `Supersedes`, `Confirms` con esito discordante; escludere `Qualifies` generici e gli `Alias` fra facce dello stesso campo) e tetto che non spinga al riempimento. **Impatto:** meno confronti (nei dati solo 6 su 368 candidati sono di conflitto/correzione). **Rischio:** perdere legami che il gold si aspetta; mitigato perché la «copertura» del motore è definita dalle celle presentate al modello, non dai candidati restituiti. **Compatibilità:** v12 (prompt v7). **Costo:** 1–2 gg + una validazione contro il gold. **Test:** esperimento controllato su ≤ 3 blocchi già osservati (richiede autorizzazione e un piccolo budget DGX), non fattibile offline.

### C. Recupero di output non valido
- **C1 Retry guidato (max 1 per batch)**: in caso di violazione di appartenenza al batch si invia di nuovo lo stesso batch con l'elenco degli alias ammessi e il messaggio di errore; il secondo output passa lo *stesso* validatore, nessun alias fuori catalogo accettato, entrambi i tentativi tracciati (`InferenceCalls`, payload originale conservato). **Impatto:** +1 chiamata nel ~4 % dei casi; elimina l'abort per questa classe. **Rischio:** mascherare derive sistematiche (mitigato da una metrica/soglia). **Compatibilità:** v12 (la v11 conserva «nessun retry»). **Costo:** medio. **Test:** matrice dei contratti, replay deterministico, un test non generativo con risposta finta invalida seguita da valida.
- **C2 Appartenenza nello schema**: `enum` per batch sui campi `left/right` (e su `key`/`evidence`/`target`), generato in modo deterministico dal catalogo del batch, con hash per batch nel ledger. **Impatto:** rende non generabile l'alias fuori batch (anche con grammar llama.cpp); riduce i token inutili. **Rischio:** schema dinamico per batch contraddice il profilo «congelato per stadio» (`by-stage-1`): serve profilo `by-stage-2` come *template*; compatibilità con la cache del prefisso da verificare. **Costo:** medio-alto. **Test:** unit su generazione/hash, contratto, replay, smoke DGX da una chiamata.
- C1 e C2 si combinano: C2 previene, C1 copre le violazioni rimaste.

### D. Costi di replay
Il costo di 257 s è concentrato nella *prima* `port.persist` (le altre 43 costano in media 0,9 s): è un avviamento a freddo, non cresce con n. La scomposizione non è misurata (ipotesi: ricostruzione di directory/gerarchia e misure di token via tokenizer). **D1** span/contatori per tokenizer, directory, gerarchia (solo telemetria, nessuna versione): costo ½ giornata. **D2** cache di misura dei token indicizzata da hash del payload + fingerprint del tokenizer (semantica invariata se la chiave è esatta): ~1 gg. **D3** persistenza di uno snapshot del motore nel checkpoint (grande, richiede v12 e un piano di compatibilità): più giorni. Test: confronto di hash dei batch prima/dopo (devono essere identici).

## 7. Ordine proposto (efficacia sul PASS M5, poi rischio)

| # | Proposta | Efficacia su M5 | Rischio | Nota |
|---|---|---|---|---|
| 1 | **B3** (o B2) — politica di saturazione | massima: toglie l'impedimento n. 1 | decisione di prodotto (B3) / loop (B2) | senza questo nessuna run v11/v12 può fare `Completed` |
| 2 | **C2 + C1** — appartenenza nello schema e retry guidato | alta: elimina la classe E109 e l'abort (≥ 22 %, ~77 % nello scenario centrale) | medio | v12 |
| 3 | **A** — selettività dei candidati | media-alta sul carico (potenzialmente < 311 confronti) | medio: copertura rispetto al gold | richiede esperimento DGX bounded |
| 4 | **D1** poi D2 | bassa su M5, utile alla diagnosi | molto basso (D1 solo telemetria) | D1 può partire anche senza decisioni |
| — | B1 | alta sulla completezza ma aumenta il carico | alto | solo se combinata con A |

## 8. Conclusioni richieste

1. **Causa verificata della saturazione:** con tetto 16 = massimo dello schema, un blocco pieno è saturo per costruzione; un padre saturo è `NotProcessed` (non terminale), non viene riaperto dal round finale e `AssembleAsync` lancia `coverage_incomplete`. 23/23 blocchi sono a 16. La causa *a monte* (cap-fill del modello o densità reale dei blocchi) è un'ipotesi non verificata.
2. **Frontiera residua effettiva:** ≥ 303 confronti distinti + blocco 73 + ≥ 6 espansioni + Gap = **≥ 311 chiamate** (limite inferiore dimostrato; ≈ 700 nello scenario centrale); fasi B e Cross non ancora iniziate.
3. **E109:** fatto = assente dalla richiesta, presente come membro in altre due; motivazione che lo smentisce da sola; ipotesi = errore di generazione sotto pressione del tetto, con schema debole. Tasso osservato 1/24 (≈ 4 %, 0,1–21 %); probabilità di un secondo abort ≈ 22–77 % a seconda del carico restante.
4. **Budget di 200:** concludere in 111 residue è **dimostrabilmente impossibile** (≥ 311 necessarie); anche il 512 congelato è a rischio nello scenario centrale.
5. **Impedimenti strutturali:** saturazione non terminale, abort al primo `expand` non valido, carico, vincolo di appartenenza fuori dallo schema, costo di ripresa non attribuito.
6. **Proposte:** vedi §7 (B3/B2 → C2+C1 → A → D1/D2).
7. **Raccomandazione: NO-GO** a un nuovo test reale con la v11 attuale. Riaprire solo dopo: (a) decisione sulla politica di saturazione (B), (b) piano v12 con C, (c) un calcolo preventivo deterministico della frontiera minima che blocchi la run *prima* se supera il budget, (d) un piccolo esperimento autorizzato (≤ 6 chiamate) sul punto A, (e) nuova autorizzazione esplicita. M5 resta aperto, T179 non completato.
