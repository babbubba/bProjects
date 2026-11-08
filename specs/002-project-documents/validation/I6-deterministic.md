# Validazione I6 deterministica (T092)

Due livelli **distinti**, mai sommati ne confusi. Nessun DGX, nessun modello reale in nessuno dei due (`BPROJECTS_TEST_DGX_ALLOW` mai impostata, nessun `Category=MafReal`, nessun `E2E_MODEL_MODE=real`; il backend dello stack reale ha `Ai__*` esplicite e fittizie, l'ambiente non e ereditato).

| Livello | File | Discovered | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- | --- |
| 1. **E2E fake-stack** (browser, API simulate; 1440 e 390 px) | `document-long-context.spec.ts` + `support/fake-long-context.ts` | 12 (6 x 2 viewport) | 12 | 0 | 0 |
| 2. **E2E stack reale + LLM finto** (backend + PostgreSQL + parser REALI; desktop-1440) | `document-long-context-stack.spec.ts` | 4 | 4 | 0 | 0 |

Stato di T092: entrambi i livelli eseguiti, **spuntato** (limiti sotto).

## Livello 1 - fake-stack (solo contratto HTTP letto dal browser)

Nessun backend, PostgreSQL o parser. Comando: `npx playwright test document-long-context.spec.ts` (senza `E2E_BASE_URL`). Rieseguito il 2026-11-08 insieme a `document-parsing`, `document-archive`, `document-small-flow` e `document-version-recovery` dopo le estensioni di `fake-stack.ts`/`fake-parsing.ts` (T103): 44 passed, 18 skipped (le varianti `[E2E reale]`/DGX di archive, parsing e small-flow-real, gated da `E2E_BASE_URL`: SKIP, non PASS), 0 failed.

## Livello 2 - stack reale + LLM finto

Backend reale (`bProjects.App.dll`, nessun hook ne fake mode), database PostgreSQL **isolato** per esecuzione nel container di test, parser Docling reale, Angular compilato servito da un proxy 127.0.0.1; le sole risposte del modello sono simulate da `tests/e2e/support/fake-llm.ts` (stesso harness e stesse regole di `I7-deterministic.md`; qui in piu: scansione/riduzione/finale della chat oltre contesto e riconciliazione gerarchica `reconcile-alias-1`, con le stesse regole dei modelli finti in-process di T084/T078). Configurazione NORMALE del backend: `Ai__ContextWindowTokens=4000`, `Ai__MaxOutputTokens=1500`, `Ai__SafetyMarginTokens=20` (budget di input ~2480, come i test T084/T089/T078). Corpus: 3 documenti TXT (zone ALFA/BETA/GAMMA, 22 paragrafi ciascuno, 66 evidenze) caricati dalla UI e preparati dal parser reale.

Comando: `npx playwright test -c playwright.real.config.ts document-long-context-stack`.

| Prova | Cosa prova (server reale) |
| --- | --- |
| chat oltre contesto | regime Esteso deciso dal server (`InitialRegime == Regime`, mai riclassificato dal tempo); 5 gruppi di scansione, ogni evidenza (66) compare in esattamente un gruppo, riduzione gerarchica delle note (note forzate grandi), UNA risposta finale, `CallCount` = scansioni + riduzioni + finale; tre fonti di zone lontane apribili sul passaggio originale (ALFA, BETA, GAMMA); la chat invia solo il messaggio; il pannello Esecuzione non mostra mai percentuali o «completato al». |
| analisi + copertura + bozza | Analizza oltre contesto: riconciliazione gerarchica (piu batch `doc-reconcile`, nessuna chiamata single-call), run `Completed`/Esteso; anteprima con 66 voci (una per evidenza) e fonti apribili; copertura «66 di 66 evidenze attese collegate a un elemento» con la nota «nessuna copertura, anche al 100%, certifica il significato» e nessuna dicitura di fedelta/completezza; conferma, Genera con bozza per 3 unita editoriali, fonti della bozza apribili, un solo comando Analizza e uno Genera. |
| capacita ridotta | runtime riconfigurato a 700 token DOPO la preparazione: il run fallisce con errore esplicito del server (`document_incomplete`: «Un documento incluso non e completo o non e pronto») e nessuna anteprima, conferma, Genera, bozza, revisione; ripristinata la capacita, una nuova richiesta riesce e il run fallito resta nello storico con lo stesso codice. NOTA: l'errore osservato NON e `context_overflow`/`coverage_incomplete` (questi due restano provati dal livello 1 e dai test backend T089/T078). |
| disconnessione | rete del browser tolta a meta run multipasso mentre il server lo completa: alla riconnessione l'anteprima compare da sola dai GET, un solo comando Analizza, un solo run, nessun altro comando. |

### Difetto di prodotto emerso dal livello reale (corretto)

**Frontend (`analysis-preview.component.ts`)**: se il completamento del run arrivava via SSE ma il GET dell'anteprima falliva (rete caduta), la UI restava su «Nessuna analisi ancora» con Analizza abilitato, senza errore visibile e senza riprovare, finche non si ricaricava la pagina (osservato 60 s, poi la ricarica mostrava l'anteprima). Due interventi: (1) `reconcile()` rilegge l'anteprima di un run Completed senza analisi caricata a ogni (ri)connessione dello stream (da solo NON bastava: lo stream non si riconnette se la connessione resta aperta); (2) `loadAnalysis` ripete la sola lettura a intervalli (al piu 30 volte, solo GET). Due test Vitest nuovi, rossi senza la correzione (45/45 PASS con); il test E2E reale di disconnessione prima falliva e ora passa.

## LIMITI NOTI e NON verificato

- **Progressi multipasso reali**: il contratto reale riporta `completedUnits`/`plannedUnits` solo per le preparazioni documentali (`InferenceMetrics`); per chat e analisi sono `null` e il pannello dice «Unita: indisponibili». Le unita «x di y pianificate finora» sono quindi provate SOLO sul fake-stack (che le simula); sullo stack reale e provato che non compaiono percentuali ne valori inventati. Non e un difetto corretto qui (richiederebbe una nuova telemetria).
- **Riclassificazione N->E dopo latenza**: sul reale il regime e Esteso fin dall'inizio (`InitialRegime == Regime`, nessuna promozione); il caso Ordinario che resta Ordinario con latenza lunga e provato solo sul fake-stack.
- Il modello finto non emette relazioni, conflitti ne alternative: coverage con alternative/frontiera, fonti multi-blocco con gruppi condivisi e copertura parziale restano provate dal livello 1 e dai test backend; sul reale la bozza ha un blocco per unita editoriale.
- Solo desktop-1440 sullo stack reale; tokenizer del modello finto = parole separate da spazi; non eseguite la suite backend completa ne la suite Playwright di default completa (Vitest completa rieseguita a fine lavoro: 23 file, 423/423 PASS).
- Le garanzie di qualita semantica del modello reale restano T090/DGX.

## Dettaglio del livello 1: cosa provano i test fake-stack (solo contratto HTTP letto dal browser)

| Test | Prova |
| --- | --- |
| Chat multipasso | Regime Ordinario con latenza lunga resta Ordinario (nessuna riclassificazione N->E dedotta dal browser); promozione dichiarata solo dal server con ricevuta; unita «x di y pianificate finora» con denominatore che cresce, mai percentuali/«completato»; nessuna risposta finche il server non completa; tre fonti di documenti diversi apribili da tastiera. |
| Limite documentale in chat | `document_limit_exceeded`: nessuna risposta assistant, messaggio d'errore, composer utilizzabile. |
| Analisi + copertura | Progresso multipasso in analisi; report coverage-2 al 100% per ID con nota «nessuna copertura, anche al 100%, certifica il significato», nessuna dicitura di fedelta/completezza, alternative di un conflitto aperto e frontiera mostrate integralmente; report parziale (porzioni mancanti + bloccante => «la verifica non è chiusa»); errore 500 del server => messaggio, nessun nuovo report. |
| Bozza multi-blocco | Due citazioni con la prima fonte condivisa restano distinte (mappa persistita), ciascuna con le proprie parti; fonte di una sola citazione senza etichetta «x di y»; dopo nuova versione del documento e reload le stesse parti originali si aprono, nessun comando emesso. |
| Errori/limiti analisi | `coverage_incomplete` e `context_overflow` (con token misurati): nessuna anteprima, conferma, Genera o bozza; retry con nuova chiave. |
| Disconnessione | Stream caduto con riconnessione rifiutata durante un run multipasso: il risultato perso si riconcilia dai GET, una sola connessione, nessun comando. |

## Limiti del livello 1

- Le garanzie del server (scansione di tutte le unita, riduzione, fail-fast, commit/assembly, ripresa, routing) sono provate dai test backend T084/T089/T091/T078/T123, non da questo livello.
- Il parsing simulato serve blocchi canned per formato; le citazioni multi-blocco usano blocchi del fake.
- `hasSourceChanges`/`sourceMap` sono impostati dal fake come da contratto DraftView.
