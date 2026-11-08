# Slice 002 — chiusura tecnica e release candidate

Data: 2026-11-08. Branch `feature/002-implementation`. Baseline di codice verificata: **`03514b0`** (HEAD al momento delle prove; dopo `b4404c7` cambia solo la documentazione di accessibilità). Questo documento non accetta la 002: separa implementazione, verifica tecnica, verifica funzionale/semantica e accettazione PM.

Stati usati: `IMPLEMENTED`, `TECHNICALLY_VERIFIED`, `FUNCTIONALLY_VERIFIED`, `ACCEPTED`, `BLOCKED`, `NOT_VERIFIED`. Non si escludono.

## 1. Baseline

- Verificato su `specs/002-project-documents/tasks.md`: **130 task spuntati, 13 aperti**, esattamente `T126, T067, T142, T082, T083, T138, T090, T104, T107, T109, T110, T111, T114`.
- Nessun prompt, regola di validazione, gold o specifica storica modificati in questa sessione. Unica modifica: checklist di revisione umana in `validation/accessibility.md` (commit `03514b0`).

## 2. Task implementativi completati

130 task spuntati (T001–T143 esclusi gli aperti del §3). T143 (fail-fast oltre contesto con Gap, decisione C+D) è completo e coperto da test.

## 3. Task aperti e motivi

| Task | Stato | Motivo |
| --- | --- | --- |
| T126 | IMPLEMENTED, TECHNICALLY_VERIFIED; prova DGX a modello scaricato **NOT_VERIFIED** | Codice `2b4239b`, test `8cf92db` (18 test dedicati, 49 con T124/T119, tutti superati). Resta la prova sul router reale (endpoint di load non provato). Non spuntato: nessuna inferenza DGX è stata eseguita solo per spuntare la casella. |
| T107 | IMPLEMENTED, TECHNICALLY_VERIFIED (automatico); revisione umana **NOT_VERIFIED** | Checklist eseguibile in `validation/accessibility.md` §(d); nessuna casella spuntata. |
| T142 | BLOCKED (semantico) | N4 reale ×2 FAIL (vedi §8). Nessuna terza revisione dei prompt. |
| T138 | BLOCKED (architetturale) | Vedi §9. |
| T067, T082, T083, T090, T104, T109, T110, T111, T114 | NOT_VERIFIED | Richiedono DGX/PM; nessuna campagna eseguita (richiesta di non farlo). |

Harness dei gate (esistenza dei file, non esecuzione):

- Presenti: `DocumentSmallFlowMafRealTests` (T067), `DocumentSingleLargeMafRealTests` (T082), `DocumentMultiLargeMafRealTests` (T083), `document-small-flow-real.spec.ts`.
- **Mancanti** (la loro creazione fa parte dei rispettivi task DGX/PM; non creati ora per non far passare per pronto ciò che non si può eseguire): `DocumentLongContextMafRealTests` e `document-long-context-real.spec.ts` (T090), `document-version-recovery-real.spec.ts` (T104), `DocumentQueueMafRealTests` e `document-queue-real.spec.ts` (T109), `DocumentPerformanceMafRealTests` (T110), `document-acceptance-real.spec.ts` (T111). Esistono le controparti con LLM finto (`document-long-context-stack`, `document-version-recovery`).
- T082, T083, T090 e ogni criterio SC-004 con Gap distribuiti dipendono da T138: una prova eseguibile non implica che possa superare tutti i criteri.
- I 83 test backend ignorati lo sono per mancanza di `BPROJECTS_TEST_DGX_ALLOW` o del parser reale: non sono PASS.

## 4. Funzionalità disponibili

IMPLEMENTED e TECHNICALLY_VERIFIED, non FUNCTIONALLY_VERIFIED né ACCEPTED: upload e versioni di PDF testuali/DOCX/Markdown/TXT; parsing offline con provenienza; Evidence documentali con riferimenti; chat con fonti; analisi single-call con valutazione Gap; documenti oltre contesto (riduzione gerarchica) con fail-fast esplicito se contengono Gap; conferma PM, bozza, snapshot e configurazioni congelate; promozione/esclusione di versioni; readiness bounded del runtime (T126); leasing, retry, recovery e checkpoint.

## 5. Test eseguiti

Ambiente: Windows 11, .NET 10.0.403, Node 26.13, PostgreSQL 18 in Docker locale, variabili `BPROJECTS_TEST_DGX_*` rimosse dal processo, nessuna inferenza reale. Commit `03514b0`.

| Suite | Scoperti | Superati | Falliti | Ignorati |
| --- | --- | --- | --- | --- |
| Build backend | 0 errori, 0 avvisi | | | |
| Backend completo (unit, deterministici, PostgreSQL, MAF con modello finto, parser) | 3567 | 3484 | 0 | 83 |
| T126 + T124 + T119 (`DocumentRuntimeReadinessTests`, `DocumentRootClassificationTests`, `DocumentDeadlineDispatchTests`) | 49 | 49 | 0 | 0 |
| Frontend Vitest | 429 (23 file) | 429 | 0 | 0 |
| Build frontend Angular | OK; avviso: budget iniziale 650 kB superato di 386 byte (650,39 kB) | | | |
| Playwright offline (1440 e 390 px) | 314 | 242 | 0 | 72 (stack reale/DGX) |
| Playwright stack reale con LLM finto (backend, PostgreSQL, parser reali) | 15 | 14 | **1** | 0 |

T125: coperto dalla suite backend completa (0 fallimenti); la verifica reale N1–N3 è in `validation/I4-real-v3-N1-N3.md` e non è stata rieseguita.

**Fallimento dello stack reale**: `document-security-stack.spec.ts:295` (egress/immagini remote) dà `Failed` invece di `Incomplete`. Causa ambientale nota, già descritta in `validation/verification.md` (punto 2): il container `bprojects-t055-parser` (creato il 2026-11-03) precede la correzione `31ef50e` (immagine Markdown in un paragrafo). **Non rieseguito con un parser ricostruito in questa sessione**: nessuna regressione di codice dimostrata, ma il test è **non superato** in questa esecuzione e va ripetuto con `E2E_STACK_PARSER_CONTAINER`/`E2E_STACK_PARSER_PORT` su un container ricostruito.

Accessibilità automatica (T107): `document-accessibility` fake (12 casi) e `document-accessibility-stack` (2 casi) superati; axe senza violazioni, nessuno scroll orizzontale, audit del focus.

## 6. Regressioni corrette

Nessuna regressione di codice emersa. Nessun test o aspettativa modificati.

## 7. Test reali ancora necessari

Prova T126 a modello scaricato sul router DGX (una inferenza alla volta, dopo decisione sul load endpoint); T067 (con browser reale), T082, T083, T090, T104, T109, T110; revisione umana T107; E2E PM T111; gate T114; ripetizione dello stack reale con parser aggiornato.

## 8. Limiti semantici (T142/N4)

Due prove DGX reali. Seconda: E2 estratta come Gap (riuscito, `extraction-v3`); valutazione E2 rispetto a E3 non valida; valore 30 ancora duplicato; analisi `document-analysis-v5` scartata come `work_output_invalid`. Lo stato resta coerente: il run non pubblica revisioni non valide (coperto dai test deterministici su `work_output_invalid` della suite backend). Prompt storici preservati; nessuna deduplicazione euristica, correzione automatica dei Gap o retry aggiunti. Evidenze: `validation/T130-N4-revision.md`.

## 9. Limiti architetturali (T138)

La riconciliazione gerarchica non garantisce la valutazione completa dei Gap quando domanda e riscontro stanno in porzioni diverse. Soluzione temporanea C+D attiva (T143): Gap valutati solo single-call; oltre contesto con Gap, fail-fast `document_limit_exceeded`, zero invii, nessun risultato parziale. Le alternative A/B non sono implementate e richiedono una decisione architetturale e la pianificazione Spec Kit. Task dipendenti: T082, T083, T090, T110, T111, T114.

## 10. Requisiti non soddisfatti

- **SC-004**: non soddisfatto per i casi con Gap oltre contesto (T138).
- SC-001–SC-012 nel complesso: non accettati (T111/T114 aperti). FR-020/SC-012 (readiness): tecnicamente verificati, funzionalmente no.
- Accessibilità: criteri umani (lettore di schermo, reflow 320 px/zoom, touch) NOT_VERIFIED.

## 11. Rischi e vincoli per la slice 003

Debito T138 e qualità N4; KB condivise/RAG restano fuori; il livello reale dipende da un container parser allineato; bundle frontend al limite del budget (650 kB); nessun deduplicatore semantico; il load endpoint del router non è provato. Nessuna specifica della 003 è stata creata.

## 12. Valutazione della release candidate

**READY WITH LIMITATIONS** per un pilota tecnico controllato: build pulite, 0 test falliti in backend, Vitest e Playwright offline; funzionalità implementate e tecnicamente verificate. Non è `FUNCTIONALLY_VERIFIED` né `ACCEPTED`: N4 fallisce semanticamente, T138 blocca SC-004 per i Gap oltre contesto, i gate DGX/PM e la revisione umana di accessibilità non sono eseguiti, e un test dello stack reale è fallito per parser non allineato. All'accettazione mancano T126 (prova DGX), T107 (umana), T142, T138, T067, T082, T083, T090, T104, T109, T110, T111, T114.
