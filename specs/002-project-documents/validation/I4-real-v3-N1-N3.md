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

---

# Seconda prova (dopo correzioni)

Esecuzione 2026-11-03 sul commit `8971fb4` (prompt v3 con tabella origine-classificazione, motivo `noFindings` obbligatorio, chiavi uniche; messaggio senza `certezza=`). Stesso metodo, ambiente e filtri della prima prova (sezione 1): Qwen3.6-35B-Smart, W=131072 e slot idle verificati via `/slots?model=` prima di ogni test, reasoning OFF, output massimo Analysis 16384, margine 1024, una inferenza alla volta, UNA ripetizione per test, PG 15432, parser 58300. Nessuna modifica a codice, prompt, compositore, validatori, gold o asserzioni; nessun log diagnostico aggiunto. Report grezzi solo locali (`%TEMP%\bprojects-t067`, `%TEMP%\bprojects-diag`, non versionati).

**Esito: N1 PASS, N2 PASS, N3 FAIL (analisi PASS, falliscono 2 verifiche a valle). Gate N1-N3 NON soddisfatto.** T125/T067 non spuntati. N4/N5 non eseguiti.

## Risultati per test e per stadio
| Stadio | N1 PDF+DOCX | N2 MD+TXT | N3 conflitto + tabella PDF |
| --- | --- | --- | --- |
| Token analisi input / output | 2732 / 852 | 2781 / 723 | 2556 / 577 |
| Durata analisi (chiamata) / test | 15,6 s / 2 m 09,9 s | 12,3 s / 2 m 07,2 s | 10,0 s / 2 m 02,9 s |
| Finish (tutte le chiamate) | stop, 0 `length` | stop, 0 `length` | stop, 0 `length` |
| DTO semantico (codec strict) | OK | OK | OK |
| Ricostruzione alias -> canonico | OK (0 chiavi duplicate) | OK (0 duplicate) | OK (0 duplicate) |
| Validazione di dominio | OK (run Completed, revisione 1.1.0; 0 violazioni: l'app le conserva solo in caso di rifiuto) | OK (idem) | OK (idem) |
| Copertura | PASS: ogni evidenza (9) rappresentata | PASS (9) | PASS (7) |
| Citazioni analisi (server-side) | PASS 100% (9 fonti) | PASS 100% | PASS 100% (7 fonti) |
| Semantica vs gold del test | PASS 7/7 attese (24 mesi, anonimo negato, SSO, rapporto, annullati esclusi, quinto giorno, Operatore sola lettura), 0 conflitti spuri | PASS (esclusioni, date, consenso, vietato carta, eccezione anagrafici, 45 non 30, injection ignorata, 0 voci Declared spurie) | PASS 7/7 attese (30, 60, R-01..R-05 con R-05 escluso) |
| Conflitti / alternative | 0 / 0 (nessuno atteso) | 0 / 0 (nessuno atteso) | 1 conflitto Open, 2 alternative (60 e 30 giorni), resolution null: PASS |
| Classificazioni (Declared/Documented) | tutte Documented (nessun messaggio PM sul contenuto) | tutte Documented | tutte Documented |
| Correzione PM + conferma | PASS | PASS | correzione accettata (PUT 200, PmEdit 1.1.0), conferma OK; **FAIL verifica «attribuzione»** (vedi sotto) |
| Bozza | PASS (6 call, marcatori 10/10 validi) | PASS | **FAIL: `work_output_invalid`, 2 violazioni** (vedi sotto) |
| Esito test | **PASS** (53/0) | **PASS** (53/0) | **FAIL** (49/2) |

Le tre cause della prima prova risultano eliminate in 3/3: nessuna classificazione `Declared` su evidenze Document, `noFindings` A/B valorizzato (validazione di dominio superata), nessuna chiave locale duplicata. Il fail-fast della prima prova nascondeva gli stadi successivi: in questa prova N3 li raggiunge e si ferma piu a valle.

## Errori di N3 (post-analisi, non toccano il contratto v3 dell'analisi)
1. **Verifica «attribuzione: la voce corretta cita una fonte PmRevision aggiunta dal server» - classe: test/harness (aspettativa), non modello ne server.** La correzione di N3 risolve un conflitto (`ResolveConflictInFavourOf60`): il server ha aggiunto correttamente la fonte `PmRevision` (`c4378fdf...`) e l'ha collegata a `conflicts[0].resolution.evidenceRefs`, come da db-final (1 PmRevision source, conflitto Resolved con alternativa 60 giorni selezionata, revisione AI intatta). L'asserzione pero cerca la fonte PmRevision solo in `Items` (voci e alternative), che non include `resolution.evidenceRefs`; in N1 la correzione modifica il testo di una voce e passa. Questa verifica non era mai stata raggiunta prima (fail-fast). Non corretto (asserzioni intoccabili): serve decisione.
2. **Bozza: `work_output_invalid` (2 violazioni: `itemIds` blocks[2] cita un id non appartenente alla revisione confermata; `fonti` blocks[2] cita la fonte PmRevision che non sostiene nessuno dei suoi item) - classe: semantico/istruzione del modello nella fase Document (contratto bozza v2, non toccato da T125).** Il blocco 3 della bozza mette tra gli `itemIds` l'id del conflitto (`8729b802...`, non e un item: sono item le alternative) e tra le `sourceIds` la fonte PmRevision della risoluzione. Il messaggio della bozza espone `conflicts[].id` accanto agli item e la fonte della risoluzione, che il validatore non ammette come sostegno di item. Una sola esecuzione: non e una stima di varianza. Il validatore ha rifiutato correttamente (nessuna bozza con riferimenti inventati). Non verificato: se il fallimento si ripete (n=1), ne' se con corretta attribuzione 1 il resto della bozza sarebbe passato (la bozza N3 non e stata valutata semanticamente: DraftTokens «60», «tema scuro» NOT VERIFIED).

## Confronto
| Test | Prima prova (v3 iniziale) | Seconda prova |
| --- | --- | --- |
| N1 | FAIL (Declared su Document + noFindings, 27 viol.): 2148 / 1717 / 29 s | **PASS**: 2732 / 852 / 16 s |
| N2 | FAIL (chiave duplicata) | **PASS**: 2781 / 723 / 12 s |
| N3 | FAIL (chiavi duplicate) | analisi **PASS** (2556 / 577 / 10 s), test FAIL a valle (1 aspettativa di test + 1 errore di bozza) |

## Gate e correzioni proposte (NON applicate, nessuna terza prova)
Gate N1-N3: **no** (N3 FAIL). Proposte, da approvare: (a) aspettativa di N3: verificare la fonte PmRevision anche in `conflicts[].resolution.evidenceRefs` (nessun allentamento: la fonte deve comunque essere PmRevision e aggiunta dal server) oppure usare per N3 una correzione su una voce; (b) bozza: nel messaggio della fase Document distinguere in modo esplicito gli id citabili (item e alternative) dall'id del conflitto e dalle fonti di risoluzione (non citabili come sostegno), oppure esporre il conflitto senza `id` proprio; poi rieseguire solo N3 (e, a gate chiuso, decidere su N4/N5). Nessun segnale che l'analisi v3 richieda altre modifiche.

---

# Terza esecuzione N3 (dopo fix bozza/attribuzione)

Esecuzione 2026-11-03 sul commit `68b47d8` (messaggio di bozza senza id del conflitto, attribuzione PM che ammette `conflicts[].resolution.evidenceRefs`). Solo N3, UNA ripetizione, stessa configurazione della sezione 1 (Qwen3.6-35B-Smart, W=131072 e slot idle verificati con `/props?model=` e `/slots?model=`, reasoning OFF, output Analysis 16384, margine 1024, PG 15432 con `Database=postgres` come DB amministrativo, parser 58300). Filtro verificato con `--list-tests` (1 test). Nessuna modifica a codice, prompt, test, gold. Un tentativo precedente e fallito in 19 s nel warm-up `Require()` (`HttpIOException: response ended prematurely`, router DGX che risvegliava il modello): il test non era iniziato, nessuna fase eseguita, quindi non conta come ripetizione e il run e stato rilanciato una sola volta con modello gia `loaded`.

**Esito: N3 FAIL (50 PASS / 3 FAIL). Analisi, correzione PM, attribuzione e conferma PASS; falliscono 2 verifiche semantiche di analisi (violazioni del test) e la bozza (`work_output_invalid`). Gate N1-N3 NON soddisfatto.** Nessuna terza ripetizione. T125/T067 non spuntati; N4/N5 non eseguiti.

## Per stadio
| Stadio | Esito |
| --- | --- |
| Analisi: token in/out, durata, finish | 2556 / 845, 14,4 s, stop (0 `length`; 7 chiamate totali) |
| DTO, ricostruzione alias, dominio | OK (revisione 1.1.0), 0 chiavi duplicate |
| Copertura / citazioni | PASS (7/7 evidenze rappresentate; 100% citazioni corrette) |
| Semantica attese annotate | PASS 7/7 (30, 60, R-01..R-04, R-05 ESCLUSO) |
| Conflitto | PASS: 1 conflitto Open, 2 alternative (30 e 60 giorni), resolution null |
| Classificazioni | tutte Documented/Inferred coerenti (1 Inferred su domanda aperta) |
| «nessuna inversione/errore» | **FAIL 2 violazioni** (vedi sotto) |
| Correzione PM (risolve per 60) | PASS (PUT 200, PmEdit 1.1.0, revisione AI intatta) |
| Attribuzione PmRevision | **PASS** (il fix dell'asserzione funziona) |
| Conferma | PASS (PmConfirmed, canGenerate) |
| Bozza | **FAIL `work_output_invalid`** (2 violazioni); call Document 5908 / 3167, 55,4 s, stop |
| Citazioni bozza solo item/alternative, semantica (60 vs 30, R-05 esclusa), Markdown | NOT VERIFIED (bozza scartata dal validatore) |

## Classificazione degli errori
1. **Bozza: UUID di item corrotto nel blocco 1 (classe: errore di copia di id del modello, fase Document, contratto bozza v2 che usa UUID reali, non alias).** Il modello ha emesso `358a2730-2dfc-8941-828d-e02f67856759` al posto di `358a2730-2dfc-8941-828d-230af10cf0a5` (item «Storico fatture in PDF»): ha mescolato il prefisso del vero id con l'ultimo gruppo del vicino `f9acc383-32b8-8df4-842d-e02f67856759`. Le due violazioni sono la stessa causa (id inesistente citato; id reale non rappresentato). Il problema precedente (id del conflitto / fonte della risoluzione nei blocchi) NON si ripresenta: il fix del messaggio e efficace, ma resta la classe «copia di UUID» gia eliminata nell'analisi dagli alias e ancora presente nella fase Document.
2. **«termine di pagamento affermato fuori dal conflitto» (2 voci) - classe: strettezza della regola del test / ridondanza del modello, non errore di valore.** Le voci sono la domanda aperta «Qual e il termine di pagamento effettivo (30 o 60 giorni)?» e la nota «Esistono discrepanze ... (30 vs 60 giorni)»: nessuna afferma un termine, ma citano entrambi i numeri fuori dal conflitto, e la regola `Violations` del test le conta (testo con «60 giorni» + «pagamento», non `InConflict`). Il modello ha anche duplicato lo stesso contenuto in piu sezioni (19 voci per 7 evidenze: goal, scope e requisiti riformulati e ripetuti), il che e rumore ma non viola il contratto. Non corretto (test e gold intoccabili): serve decisione se una domanda aperta/discrepanza che nomina entrambi i valori sia ammessa.
- Nessun errore di runtime, contesto, output, preflight, parser, DTO, ricostruzione, dominio, citazioni.

## Correzioni proposte (NON applicate)
(a) Bozza: rendere gli id citabili non copiati dal modello, con alias locali anche nella fase Document (come per l'analisi) e mappatura server-side a UUID, oppure riparare/rifiutare con un nuovo tentativo guidato dal solo elenco di id validi; e la correzione robusta, perche il modello non copia affidabilmente gli UUID. (b) Regola del test: restringere `Violations` alle sole voci che affermano un singolo termine (es. escludere voci che nominano entrambi i valori 30 e 60 / `Questions`), oppure istruire il prompt a non generare voci duplicate sul termine di pagamento: decisione dell'autore. Poi rieseguire solo N3.

---

# Quarta esecuzione N3 (dopo T127)

Esecuzione 2026-11-03 sul commit `7b09f02` (alias brevi nella fase bozza con ricostruzione server-side, `document-draft-v3`). Solo N3, UNA ripetizione, configurazione identica alla sezione 1: Qwen3.6-35B-Smart (`BPROJECTS_TEST_DGX_MODEL` solo nel processo di test), W=131072 e slot idle verificati con `/props?model=` e `/slots?model=` (`n_ctx 131072`, `is_processing=false`), reasoning OFF, output Analysis 16384, margine 1024, PG 15432 (`Database=postgres`), parser 58300. Filtro verificato con `--list-tests` (1 test). Warm-up riuscito al primo tentativo (nessun `HttpIOException`). Nessuna modifica a codice, prompt, test, gold. Report grezzo solo locale (`%TEMP%\bprojects-t067\N3-110057.txt`).

**Esito: N3 FAIL (22 PASS / 1 FAIL), durata 1 m 33 s. Il fallimento e nello stadio ANALISI (run `Failed work_output_invalid`), quindi la bozza T127 NON e stata raggiunta ne valutata. Gate N1-N3 NON soddisfatto.** Nessuna ripetizione. T127/T125/T067 non spuntati; N4/N5 non eseguiti.

## Per stadio
| Stadio | Esito |
| --- | --- |
| Fasi precedenti (upload, Ready, estrazione, chat con fonti e citazioni esatte, classificazione Ordinary 6,8 s) | PASS (7 evidenze) |
| Analisi: token in/out, durata, finish | 2556 / 691, 12,6 s, stop (6 chiamate totali, 0 `length`) |
| DTO, ricostruzione alias | **FAIL: `work_output_invalid`, 1 violazione: `knownRelationChecks[0].relation: alias R1 sconosciuto o fuori snapshot`** |
| Validazione di dominio, copertura, citazioni, semantica, conflitto 60/30, R-05 | NOT VERIFIED (analisi scartata dalla ricostruzione) |
| Correzione PM e attribuzione, conferma | NOT VERIFIED (non raggiunti) |
| Bozza (DTO con alias, ricostruzione, validazione, citazioni item/alternative, semantica 60 vs 30, R-05 esclusa), Markdown con citazioni | NOT VERIFIED (nessuna chiamata Document eseguita) |
| «nessuna inversione/errore» | NOT VERIFIED (nessuna regola violata: non valutata) |

## Classificazione dell'errore
1. **Analisi: `knownRelationChecks` con alias `R1` non presente nello snapshot - classe: errore del modello (istruzione/allucinazione di alias), non runtime, contesto, output, parser ne T127.** Il modello ha emesso un verdetto per una relazione `R1` che il messaggio non conteneva; il ricostruttore ha rifiutato correttamente (nessun dato inventato accettato). Ipotesi NON verificata (n=1, prompt/risposta non ispezionati): lo snapshot N3 non ha relazioni note e il modello ha copiato l'esempio `R1` del prompt `document-analysis-v3.md` (riga sui `knownRelationChecks`) invece di `[]`. Regressione rispetto a Seconda/Terza prova, dove la stessa analisi passava (3/3 e 1/1 con 0 violazioni): varianza del modello a reasoning OFF su un contratto invariato da T125, non effetto di T127 (la fase analisi non e toccata da T127).
- Nessun errore di runtime, contesto, output, preflight, parser, DTO strict.

## Conclusione e proposta (NON applicata)
T127 resta non verificato su DGX: serve un run in cui l'analisi passi per arrivare alla bozza. Proposta: rendere la ricostruzione tollerante ai `knownRelationChecks` su alias inesistente quando lo snapshot non ha relazioni note (scartare il verdetto, mai inventare), oppure togliere l'esempio `R1` dal prompt quando non ci sono relazioni note; poi rieseguire solo N3. Decisione dell'autore.

---

# Quinta esecuzione N1–N3 (dopo fix relazioni note e T127)

Esecuzione 2026-11-03 sul commit `e932ee1` (prompt v3 senza alias R concreti e dichiarazione esplicita di assenza di relazioni note) con T127 (bozza con alias, `document-draft-v3`). Ordine N1, N2, N3, UNA inferenza alla volta, UNA ripetizione ciascuno, configurazione della sezione 1: Qwen3.6-35B-Smart (`BPROJECTS_TEST_DGX_MODEL` solo nel processo di test), W=131072 e slot idle verificati con `/props?model=` e `/slots?model=` prima di ogni test, reasoning OFF, output Analysis 16384, margine 1024, PG 15432 (`Database=postgres`), parser 58300. Filtri verificati con `--list-tests` (1 test ciascuno). Nessun tentativo caduto nel warm-up. Nessuna modifica a codice, prompt, test, gold, asserzioni. Report grezzi solo locali (`%TEMP%\bprojects-t067`, `%TEMP%\bprojects-diag`).

**Esito: N1 FAIL (43/2), N2 FAIL (18/1), N3 FAIL (7/1). Gate N1-N3 NON soddisfatto.** T125/T127/T067 non spuntati; N4/N5 non eseguiti; nessuna ripetizione.

## Per test e per stadio
| Stadio | N1 PDF+DOCX | N2 MD+TXT | N3 conflitto + tabella |
| --- | --- | --- | --- |
| Fasi precedenti (upload, Ready, estrazione) | PASS (9 evidenze) | PASS (11) | PASS (7) |
| Chat con fonti/citazioni | PASS (citazioni esatte) | PASS | **FAIL** (`work_output_invalid`: marcatore di fonte malformato) |
| Analisi: token in/out, durata, finish | 2903 / 1209, 21,1 s, stop | 3060 / 935, 16,3 s, stop | NOT VERIFIED (non raggiunta) |
| DTO / ricostruzione alias | OK (`knownRelationChecks: []`, 0 alias sconosciuti) | OK (`knownRelationChecks: []`) | NOT VERIFIED |
| Dominio | OK (rev. 1.1.0, 28 voci, 10 fonti, 0 conflitti) | **KO: 1 violazione** (`assumptions[0].classification`: gli elementi di assumptions sono Assumed) | NOT VERIFIED |
| Copertura / citazioni analisi | PASS (tutte le evidenze, citazioni 100%) | NOT VERIFIED | NOT VERIFIED |
| Semantica vs gold annotati | PASS 7/7 (24 mesi, anonimo negato, SSO, rapporto, annullati esclusi, quinto giorno, Operatore sola lettura); 0 conflitti spuri; **FAIL regola «anonimo non negato»** (vedi sotto) | NOT VERIFIED (lettura mia, non del test: esclusioni, date, 45 giorni, divieti presenti) | NOT VERIFIED |
| Correzione PM, attribuzione, conferma | PASS (PmEdit 1.1.0, fonte PmRevision aggiunta dal server, PmConfirmed, canGenerate) | NOT VERIFIED | NOT VERIFIED |
| Bozza (T127): DTO alias, ricostruzione | OK (6 call totali; Document 2717 / 1097, 18,9 s, stop; alias I/F ricostruiti) | NOT VERIFIED | NOT VERIFIED |
| Bozza: validazione | **KO `work_output_invalid`, 1 violazione**: item `049f7ed4...` non rappresentato in nessun blocco | NOT VERIFIED | NOT VERIFIED |
| Bozza: citazioni solo item/alternative, semantica, Markdown con citazioni | NOT VERIFIED (bozza scartata) | NOT VERIFIED | NOT VERIFIED |
| Chiamate / `length` | 6 / 0 | 5 / 0 | 4 / 0 |
| Durata test | 2 m 07,8 s | 1 m 48,3 s | 1 m 12,2 s |

## Classificazione degli errori
1. **N1 bozza: omissione di un item (classe: errore del modello, fase Document T127).** Dai dati locali: l'item omesso e `I24` («Non e ammesso l'accesso anonimo ai dati degli ordini», constraint Documented, fonte F3); il modello ha riassunto il divieto in altri blocchi ma non ha elencato `I24` in alcun `items`. Il validatore ha rifiutato correttamente. Il problema UUID della terza prova non compare piu: gli alias I/F funzionano (0 id sconosciuti). Resta un'analisi molto ridondante (28 voci per 9 evidenze, stessi contenuti riformulati in piu sezioni), che moltiplica gli item da rappresentare.
2. **N1 analisi: regola del test «accesso anonimo non negato» (classe: test, regola troppo stretta; non errore del modello).** Viola la voce «Divieto di accesso anonimo ai dati degli ordini.» (non `InConflict`, contiene «anonim»): la regola `NegativeMarker` riconosce `non|nessun*|senza|vietat*|vieta*|esclus*|escluder*|esclud*|mai|tranne` ma non «divieto», quindi una negazione corretta e contata come non negata. La voce gemella «Non e ammesso l'accesso anonimo...» passa. Non corretto (asserzioni intoccabili): serve decisione dell'autore.
3. **N2 analisi: classificazione incoerente con la sezione (classe: errore del modello, semantico/istruzione).** `i21` in `assumptions`: «Esiste un riferimento alle Linee guida, ma il collegamento fornito non e attivo o valido», `classification=Documented` (evidenza E11). La regola impone Assumed nella sezione assumptions; e anche un fatto del documento messo nella sezione sbagliata. Il resto dell'analisi (20 voci, 0 conflitti, `knownRelationChecks: []`, `noFindings` valorizzato) appare coerente (lettura mia, NOT VERIFIED dal test per fail-fast).
4. **N3 chat: UUID di fonte malformato (classe: errore di copia di id del modello, fase Chat, UUID reali nel contratto chat, non toccata da T125/T127).** Il modello ha scritto `[fonte:b9a595a8-89c1-9f29-bfc5c8b6fd80]` al posto di `b9a595a8-89c1-5908-9f29-bfc5c8b6fd80` (gruppo `5908` perso, UUID a 4 gruppi); il resto della risposta (60 vs 30 giorni, R-05 escluso) e semanticamente corretto. Il test si ferma prima di analisi, conflitto, correzione, conferma, bozza: tutto NOT VERIFIED. Nessuna HttpIOException; non e un caso di warm-up. Non rilanciato (regola: nessuna ripetizione).
- **Regola «termine di pagamento affermato fuori dal conflitto» (N3):** NON valutata in questa esecuzione (analisi non raggiunta). Testo invariato: viola ogni voce non `InConflict` che contiene «30 giorni» o «60 giorni» e «pagamento» (riga `Violations` di N3 nel test); nella terza prova colpiva una domanda aperta e una nota di discrepanza che nominano entrambi i valori senza affermarne uno. Decisione dell'autore ancora aperta.
- Nessun errore di runtime, contesto, output (0 `length`), preflight, parser, DTO strict, ricostruzione. Il fix «alias R concreti» e verificato per i casi raggiunti: `knownRelationChecks: []` in N1 e N2 (l'errore `R1` della quarta esecuzione non si ripresenta).

## Confronto e proposte (NON applicate)
| Test | Seconda | Terza/Quarta | Quinta |
| --- | --- | --- | --- |
| N1 | PASS | n/a | FAIL (regola test «Divieto» + bozza omette I24) |
| N2 | PASS | n/a | FAIL (assumption classificata Documented) |
| N3 | FAIL (bozza/attribuzione) | FAIL (UUID bozza; poi R1 in analisi) | FAIL (UUID di fonte nella chat) |

Con n=1 per test lo stesso contratto passa e fallisce tra esecuzioni (N1/N2 PASS nella seconda, FAIL nella quinta): la varianza a reasoning OFF e alta e l'esito dipende da errori diversi ogni volta. Proposte: (a) regola N1: aggiungere «divieto» al `NegativeMarker`; (b) validatore/ricostruttore: correggere o derivare lato server la classificazione di una voce in `assumptions` (Assumed); (c) bozza: riparazione server-side degli item omessi (aggiungerli a un blocco con le loro fonti) o un solo tentativo guidato con l'elenco degli item mancanti; (d) chat: alias brevi per le fonti anche nella chat (come analisi e bozza) o riparazione dei marcatori quasi corretti con corrispondenza univoca. Servono approvazione e nuova verifica senza allentare test o gold.

## Sesta esecuzione (2026-11-03, altro PC, HEAD b88a43f con T129 verificato in isolamento)
Qwen3.6-35B-Smart W=131072 reasoning OFF out 16384, margine 1024, parser Docling reale (58300), PostgreSQL reale. Gold, asserzioni e validatori invariati.
- **N1 PASS** (1/1, 2m18s). **N2 PASS** (1/1).
- **N3 FAIL 2/2** (22 PASS / 1 FAIL ciascuna): analisi scartata per chiavi locali duplicate fra sezioni (`i8` in scopeIncluded e requirements; nel secondo run `i7` in constraints e in `conflicts[].alternatives`). Gli altri 22 controlli passano.
- **Browser reale** (`document-small-flow-real.spec.ts`, stack Compose): 1° run analisi scartata per chiavi duplicate (`i1` in goals e requirements, 16 violazioni); 2° run analisi OK, FAIL sull'asserzione testuale «ordini annullati ESCLUSI» (l'esclusione è una voce di ambito escluso senza marcatore negativo nel testo: falso negativo probabile della regola E2E, NON modificata, decisione dell'autore). Conferma e Genera completate da browser con spec temporanea: 1ª generazione `work_output_invalid` (4 item omessi, retry NON partito: residuo 146 s < timeout per chiamata 150 s); con `AI_DOCUMENT_REQUEST_TIMEOUT_SECONDS=120` il **retry guidato T129 è partito** (batch `draft_retry`) e la bozza è stata prodotta (Proposta/non approvata, 52 citazioni).
- Esito gate N1–N3: **NON soddisfatto** (N3 FAIL). Difetto residuo dominante: chiave locale ripetuta fra sezioni (4 analisi su 7 viste oggi). Il prompt v3 lo vieta già; serve decisione dell'autore (rafforzare prompt, retry guidato dell'analisi con contratto nuovo, ecc.). T125/T127/T128/T129 NON spuntati; T126 e T067 non avviati.
