# I4 reale - prima verifica DGX del contratto v3 con alias (T125), N1-N3

Esecuzione 2026-11-03, serializzata (una inferenza DGX per volta), una ripetizione per test, nessuna modifica a codice, test, gold, asserzioni, prompt, schemi, validatori o compositore (nessun log diagnostico aggiunto). **Esito: gate N1-N3 NON soddisfatto (N1 FAIL, N2 FAIL, N3 FAIL).** T125 non spuntato, T067 aperto. Dati grezzi (prompt/risposte) solo nello scratchpad locale non versionato; qui solo conteggi e classificazioni.

## 1. Configurazione effettiva
Qwen3.6-35B-Smart; DGX verificato prima di ogni test via HTTP (`/props?model=` e `/slots?model=`: `n_ctx 131072`, 1 slot, `is_processing=false`). W=131072, Analysis reasoning OFF (`BPROJECTS_TEST_ANALYSIS_THINKING=0`), output massimo Analysis 16384, margine 1024 (input budget Analysis 113664), timeout richiesta Analysis 900 s; altre fasi come Fase A. Variabili impostate solo nel processo di test: `BPROJECTS_TEST_DGX_MODEL`, `_CTX_WINDOW`, `_ANALYSIS_MAX_OUTPUT`, `_ANALYSIS_THINKING`, `_SAFETY_MARGIN`. PG 15432 (`bprojects-002-test-pg2`), parser Docling reale 58300. Contratto v3 `alias-1` (prompt `document-analysis-v3.md`), HEAD `a58e731`. Filtri verificati con `--list-tests` (1 test ciascuno): `--filter-class "*DocumentSmallFlowMafRealTests"` con `--filter-method "*N1_pdf_and_docx*"` / `"*N2_markdown*"` / `"*N3_document_vs*"` (il nome completo senza wildcard individua 0 test).

## 2. Risultati per test e per stadio
Tutte le fasi precedenti all'analisi (upload, Ready, estrazione, chat con fonti e citazioni esatte, classificazione Ordinary) sono PASS nei tre test. Reasoning OFF: token di output = visibili. `finish_reason=stop` ovunque, nessun `length`.

| Stadio | N1 PDF+DOCX | N2 MD+TXT | N3 conflitto + tabella PDF |
| --- | --- | --- | --- |
| Token analisi input / output (visibili) | 2148 / 1717 | 2197 / 875 | 1964 / 547 |
| Durata analisi / test | 29,2 s / 2 m 11 s | 14,9 s / 2 m 09 s | 9,6 s / 1 m 24 s |
| Finish | stop | stop | stop |
| Validita DTO semantico (codec strict) | OK | OK | OK |
| Ricostruzione server-side (alias -> canonico) | OK (nessun alias sconosciuto) | **KO: chiave locale duplicata** (1) | **KO: chiavi locali duplicate** (2) |
| Validazione canonica (dominio) | **KO: 27 violazioni** | NOT VERIFIED | NOT VERIFIED |
| Copertura | NOT VERIFIED dal validatore (lettura mia: tutte le 9 evidenze in `visited` e citate da almeno un item) | NOT VERIFIED | NOT VERIFIED |
| Correttezza delle citazioni (server-side) | NOT VERIFIED (analisi scartata prima) | NOT VERIFIED | NOT VERIFIED |
| Semantica dei requisiti vs gold del test | NOT VERIFIED (lettura mia, non una verifica: 7 attese su 7 sembrano presenti, negazioni conservate) | NOT VERIFIED (lettura mia: esclusioni, date, consenso, vietato carta, eccezione, 45 non 30 presenti) | NOT VERIFIED (lettura mia: conflitto 60/30 presente, R-05 esclusa) |
| Conflitti e alternative | nessuno atteso, nessuno prodotto | nessuno atteso, nessuno prodotto | 1 conflitto con 2 alternative (60 e 30 giorni) prodotto, ma scartato prima delle verifiche di stato Open/resolution null |
| Esito test | **FAIL** | **FAIL** | **FAIL** |

## 3. Classificazione degli errori
- **N1 - semantico/classificazione (25 violazioni) + output/istruzione (2)**. DTO valido e ricostruzione riuscita; la validazione di dominio rifiuta (`work_output_invalid`, 27 violazioni). L'app ne conserva 5 nel messaggio (2 `noFindingsReason` per `results[0]` e `results[1]`, 3 «Declared richiede una fonte PM»); le altre 22 sono dedotte per conteggio: 25 item `Declared` (3 goals, 7 scopeIncluded, 2 scopeExcluded, 9 requirements, 4 constraints) piu 2 `noFindingsReason` = 27, coerente ma non letto dal validatore. Cause: (a) il modello classifica `Declared` ogni item sostenuto da evidenze con `origine=Document` (N1 non ha messaggi PM), contro la regola del prompt «`Declared` solo se `origine=Message`»; ogni intestazione di voce riporta `certezza=Declared` e il modello sembra averla ricopiata come classificazione, nonostante il prompt dica che non lo e (ipotesi, non provata); (b) `noFindings.A/B = null` con zero finding di vista A/B (i 2 finding sono `Cross`): il validatore richiede il motivo per le viste senza proposte, candidati o patch e il modello non lo ha fornito. Gli errori di copia di id/hash della v2 non compaiono (0 UUID, 0 alias sconosciuti, 0 errori codec).
- **N2 - ricostruzione (chiave locale duplicata)**. L'item `i3` («Consultazione degli ordini ...») e emesso sia in `scopeIncluded` sia in `requirements`; il prompt dice chiavi mai ripetute e ogni elemento una sola volta in una sola sezione. Il compositore rifiuta prima della validazione di dominio: validazione, citazioni e semantica NOT VERIFIED. Lettura mia, non del validatore: tutti gli 11 item sono `Declared` pur con origine Document (stessa causa di N1: avrebbe fallito anche la validazione di dominio).
- **N3 - ricostruzione (chiavi duplicate, 2 violazioni)**. Le due alternative del conflitto (`i7`, `i8`) sono ripetute anche in `items.requirements` con la stessa chiave: il modello tratta le alternative come item da elencare anche nelle sezioni (la regola «alternative = elementi propri» e «una sola volta, una sola sezione» e ambigua per un modello). Semantica apparentemente corretta, ma NOT VERIFIED. Lettura mia: 5 item `Declared` con origine Document (stessa causa di N1).
- Nessun errore di runtime, contesto, output (nessun `length`), preflight, `runtime_unverified`, strutturale DTO, alias sconosciuto/fuori catalogo.
- Limiti: compositore e validatore sono fail-fast per stadio (N2/N3 non raggiungono la validazione di dominio); l'harness `Replay` di `DiagnosticsHarness.cs` e v2-only e non riproduce le violazioni v3 oltre le 5 salvate dal run.

## 4. Confronto con v2 (Fase A/B, una esecuzione per test)
| Test | v2 Fase A (OFF) | v2 Fase B (ON) | v3 (OFF) |
| --- | --- | --- | --- |
| N1 | PASS: 6287 in / 7317 out / 126 s | PASS: 6268 / 13968 / 236 s | **FAIL** (classificazione + noFindings): 2148 / 1717 / 29 s |
| N2 | FAIL (id di fonte al posto di evidenza, 25/25) | FAIL (`overview` null) | **FAIL** (chiave duplicata): 2197 / 875 / 15 s |
| N3 | FAIL (id item non UUID, 14) | FAIL (UUID troncato) | **FAIL** (chiavi duplicate): 1964 / 547 / 10 s |
| Gate | no | no | no |

Lettura: la classe di errori che v3 doveva eliminare (copia di id/hash) non compare in 3/3 (nessun alias errato, nessun UUID). Input circa -66%, output -76/-94% e durata -77/-94% rispetto alla Fase A. Ma N1, che passava in v2, ora fallisce per errori di istruzione/classificazione: 0/3 PASS contro 1/3. Con n=1 per test non e una stima di varianza.

## 5. Cause e correzioni proposte (NON applicate)
1. `certezza=Declared` nell'intestazione di ogni voce coincide con la classificazione `Declared` sbagliata (25/25 in N1, tutti gli item in N2/N3). Proposta: derivare Declared/Documented server-side dall'origine delle evidenze (Message -> Declared, Document -> Documented), lasciando al modello solo Inferred/Assumed, oppure rimuovere `certezza` dal messaggio v3. La derivazione server-side e piu robusta di un'istruzione nel prompt.
2. `noFindings` A/B: il compositore puo valorizzare un motivo standard quando la vista non ha candidati, invece di richiederlo al modello (campo derivato).
3. Chiavi duplicate (N2, N3): chiarire nel prompt, con esempio, che le alternative di un conflitto non vanno ripetute in `items` e che un item compare in una sola sezione; oppure, piu robusto, il server assegna le chiavi locali per posizione (sono solo etichette locali) e decide se rifiutare o deduplicare un item ripetuto.
Le modifiche toccano contratto, prompt o compositore: servono approvazione e una nuova verifica N1-N3 senza allentare test o gold. Gate N1-N3 resta no; T067/T125 non spuntati.

## 6. Note
Nessun commit, push, modifica di `tasks.md`, `docs/acceptance-trace/` o `project-events.service.spec.ts`. Configurazione del server invariata.
