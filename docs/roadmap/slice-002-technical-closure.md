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
| Playwright stack reale con LLM finto, 1ª esecuzione, parser preesistente `bprojects-t055-parser` (obsoleto) | 17 | 14 | **1** (+2 non eseguiti dopo il fallimento) | 0 |
| Playwright stack reale con LLM finto, **2ª esecuzione, parser ricostruito** (`bprojects-parser:rc-3819e30`, container `bprojects-rc-parser`) | 17 | **17** | 0 | 0 |

T125: coperto dalla suite backend completa (0 fallimenti); la verifica reale N1–N3 è in `validation/I4-real-v3-N1-N3.md` e non è stata rieseguita.

**Fallimento dello stack reale**: `document-security-stack.spec.ts:295` (egress/immagini remote) dà `Failed` invece di `Incomplete`. Causa ambientale nota, già descritta in `validation/verification.md` (punto 2): il container `bprojects-t055-parser` (creato il 2026-11-03) precede la correzione `31ef50e` (immagine Markdown in un paragrafo). **Rieseguito con parser ricostruito (2026-11-08, nessun codice, test o criterio modificato)**: immagine `bprojects-parser:rc-3819e30` (id `sha256:68c89d2a…e9af`, target `runtime` costruito da `parser/` al commit `3819e30`, che include `31ef50e`), container temporaneo `bprojects-rc-parser` (porta 58301, `--read-only`, tmpfs `/tmp`, 2 CPU, 4 GB, stesso segreto interno del container preesistente, che non è stato modificato). Allineamento verificato: `sha256` di `/app/text_sources.py` nel container = `a0fac34f…d5f8` = quello di `parser/text_sources.py` nel repository (2 occorrenze di `unprocessed_markdown_image`; il container obsoleto ha hash `61966675…15ec` e 1 occorrenza). Selettori `E2E_STACK_PARSER_CONTAINER=bprojects-rc-parser`, `E2E_STACK_PARSER_PORT=58301`. Esiti: `document-security-stack.spec.ts:295` **PASS** da solo; suite completa dello stack reale **17/17 PASS** (10,5 min), nessun DGX. Il fallimento precedente era quindi ambientale. Il container `bprojects-rc-parser` è lasciato attivo per ulteriori prove.

Accessibilità automatica (T107): `document-accessibility` fake (12 casi) e `document-accessibility-stack` (2 casi) superati; axe senza violazioni, nessuno scroll orizzontale, audit del focus.

## 6. Regressioni corrette

Nessuna regressione di codice emersa. Nessun test o aspettativa modificati.

## 7. Test reali ancora necessari

Prova T126 a modello scaricato sul router DGX (una inferenza alla volta, dopo decisione sul load endpoint); T067 (con browser reale), T082, T083, T090, T104, T109, T110; revisione umana T107; E2E PM T111; gate T114.

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

**READY WITH LIMITATIONS** per un pilota tecnico controllato: build pulite, 0 test falliti in backend, Vitest e Playwright offline; funzionalità implementate e tecnicamente verificate. Non è `FUNCTIONALLY_VERIFIED` né `ACCEPTED`: N4 fallisce semanticamente, T138 blocca SC-004 per i Gap oltre contesto, i gate DGX/PM e la revisione umana di accessibilità non sono eseguiti. Lo stack reale con LLM finto passa 17/17 con parser aggiornato (il fallimento iniziale era ambientale). All'accettazione mancano T126 (prova DGX), T107 (umana), T142, T138, T067, T082, T083, T090, T104, T109, T110, T111, T114.

## 13. Campagna di chiusura dei gate — avvio (2026-11-08, base `217eaf8`)

Sezione aggiunta; le sezioni 1–12 restano il riferimento storico. Nessuna inferenza DGX eseguita in questo passo (`BPROJECTS_TEST_DGX_ALLOW` non impostata).

### 13.1 Verifiche di baseline

- Branch `feature/002-implementation`, HEAD `217eaf8`, worktree pulito, 0 commit di scarto rispetto a `origin/feature/002-implementation`.
- Parser corretto attivo: `bprojects-rc-parser` (immagine `bprojects-parser:rc-3819e30`, healthy). `bprojects-t055-parser` (obsoleto) e `bprojects-002-test-pg2` (PostgreSQL 18) sono attivi e non toccati.
- Task: 143 totali, 130 spuntati, 13 aperti (`T126, T067, T142, T082, T083, T138, T090, T104, T107, T109, T110, T111, T114`).

### 13.2 Sonda sola lettura del router DGX (GET `/v1/models`, `/props`; nessuna inferenza)

- Router llama.cpp `b10703`, `role=router`, `max_instances=4`, `models_autoload=true`. `/slots` senza nome modello risponde 400 (il router richiede il modello).
- `Qwen3.6-35B-Smart`: stato `loaded`, `--ctx-size 131072`, `--parallel 1`, `--sleep-idle-seconds 900`; gli altri 22 modelli `unloaded`. Il profilo N4 concordato (131072, 1 slot) è quindi disponibile **ora**.
- Per T126: il router non espone in sola lettura un modo per caricare un modello senza inferenza; il solo meccanismo osservato è l'autoload alla prima richiesta (che è un'inferenza). Nessun endpoint di load è stato provato né inventato → **decisione necessaria** (vedi 13.4).

### 13.3 Matrice dei gate

| Task | Dipendenze (tasks.md) | Prerequisito mancante | Test / ambiente | Criterio PASS | Stato |
| --- | --- | --- | --- | --- | --- |
| T107 | T087, T092, T101, T103 (chiusi) | revisione umana (lettore di schermo, contrasto visivo, reflow, touch) | checklist `accessibility.md` §(d); Playwright offline 1440/390 già verde | tutte le voci umane eseguite e attribuite | TECHNICALLY_VERIFIED; parte umana NOT_VERIFIED |
| T126 | T124, T125 (chiusi) | meccanismo verificato di load senza inferenza | router DGX, modello scaricato → disponibile | readiness bounded dentro la deadline originale, zero inferenze prima della classificazione | IMPLEMENTED, TECHNICALLY_VERIFIED (49 test); prova reale BLOCKED (decisione) |
| T142 | T141, T055 | decisione semantica (13.4) | N4 reale ×3 seriali, `Qwen3.6-35B-Smart` | 3 N4 conformi, Gap E2 valutato Answered, conflitto Open 45/30 senza duplicati | BLOCKED (decisione); 2 prove FAIL già registrate |
| T067 | T055, T066, T125–T129 | T126 (prova reale) e T142 per N4 | stack reale + DGX + browser | ≥5 nominali SC-003 su 4 formati, gold, provenienze, conferma PM | NOT_VERIFIED, bloccato da T126/T142 |
| T082 | T055, T078, T080, T123, T131 | nuova ipotesi per l'estrazione di corpus narrativo (4 FAIL, `I5-single-real.md`) e T138 per i Gap | DGX | analisi completa >2×B, ≥12 fatti, citazioni corrette entro cap E | FAILED in estrazione; BLOCKED (decisione) |
| T083 | come T082, seriale dopo T082 | T082 | DGX | come T082 su ≥3 documenti | NOT_VERIFIED |
| T138 | — | decisione A/B (e rapporto con la 003) | confronto tecnico | decisione esplicita del committente | BLOCKED (decisione) |
| T090 | T082, T083 | T082, T083, T138 | DGX + browser | SC-004 end-to-end | NOT_VERIFIED, BLOCKED |
| T104 | T067, T100, T103 | T067 | stack reale + DGX | fonti congelate, restart, promozione/esclusione | NOT_VERIFIED |
| T109 | T048, T050, T065, T090, T102 | T090 | DGX occupato | coda/deadline/risultati tardivi | NOT_VERIFIED |
| T110 | T048, T067, T090 | T067, T090 | DGX a zero carico | N ≤180 s, E ≤3600 s | NOT_VERIFIED |
| T111 | T067, T090, T104, T107, T109, T110 | persone reali | due PM + un tecnico | giudizi umani attribuiti | NOT_VERIFIED (componente umana) |
| T114 | T108–T113 | tutti i precedenti | consolidamento | nessun gate mancante classificato PASS | NOT_VERIFIED |

Non è stata alterata alcuna dipendenza approvata. Catena effettiva: T126/T142/T138 → T067 → T104; T082 → T083 → T090 → T109/T110 → T111 → T114.

### 13.4 Decisioni aperte

1. **T126**: autorizzare uno dei due modi di ottenere lo stato «scaricato → disponibile»: (a) attendere lo sleep naturale (`--sleep-idle-seconds 900`) del modello e poi far partire **una sola** richiesta autorizzata che lo risvegli, osservando `/v1/models` prima e dopo; (b) indicare un endpoint di load supportato dal router. Fino ad allora la prova reale resta NOT_VERIFIED.
2. **T142**: dopo la seconda prova la scelta è fra (i) altra revisione del prompt di analisi v5 (vietata senza approvazione), (ii) controllo deterministico del duplicato *identico* già presente (nuova validazione), (iii) retry informato esteso al record Gap invalido (nuovo retry). Nessuna è stata applicata.
3. **T082/T083**: nessuna delle tre correzioni proposte in `I5-single-real.md` (prompt di estrazione, `OutputExpansion`, retry guidato) è autorizzata; rieseguire senza di esse riprodurrebbe il FAIL.
4. **T138**: opzioni A/B, confronto in preparazione; nessuna implementazione.

## 14. T138 — confronto tecnico A/B (2026-11-08, senza implementazione)

Problema (evidenza: `validation/T130-N4-revision.md`, gate anticipato T138): nella gerarchia attuale una domanda (Gap) e il suo riscontro possono stare in batch diversi; solo i percorsi single-call valutano i Gap (T136/T143), quindi oltre contesto con Gap il sistema fa fail-fast (`document_limit_exceeded`) e SC-004 resta non soddisfatto per quei casi.

| Criterio | A — fase dedicata di valutazione Gap | B — Gap come obbligazioni nella riconciliazione esistente |
| --- | --- | --- |
| Soddisfa FR-013/SC-004 | Sì, se il recupero ricompone domanda + riscontri pertinenti entro il budget | Sì, se il partizionamento garantisce co-locazione; non garantita con molte domande e riscontri sparsi |
| Completezza delle evidenze | Dipende dal recupero (selezione = rischio di omissione); i riscontri non recuperati sono invisibili | Completa per costruzione solo se ogni coppia Gap/riscontro viene confrontata: crescita quadratica del lavoro nei casi peggiori |
| Complessità | Nuova fase, nuovo prompt/schema, nuova persistenza dei WorkOutput | Modifica di `DocumentReconcilePlan`, composer, assembly e completamento; nessuna fase nuova ma regole di raggruppamento delicate |
| Chiamate LLM | +1 per Gap (o per gruppo di Gap) | Variabile: aumenta i batch del livello di confronto |
| Latenza / contesto | Prevedibile (una chiamata per gruppo con budget noto) | Cresce con il numero di Gap; rischio di superare il cap E |
| Affidabilità | Più semplice da validare (un record per Gap, contratto già esistente `gapAssessments`) | Più difficile da provare: dipende dal raggruppamento |
| Compatibilità MAF / recovery | Nuovo nodo del grafo: checkpoint e WorkOutput con le regole esistenti | Stesso grafo, ma identità dei batch e checkpoint da rivedere (T138) |
| Riuso | Riusa validazione e formato delle valutazioni Gap v5 | Riusa la riconciliazione, ma ne altera il partizionamento già verificato |
| Rischio rispetto alla 003 | Alto: il recupero è proprio il tema del retrieval della 003, rischio di duplicare e poi sostituire | Medio: meno codice nuovo, ma anche qui parte della logica diventa inutile con il retrieval |

Raccomandazione: **A**, ridotta al minimo e progettata come adattatore sostituibile (input = un Gap + riscontri candidati, output = il record `gapAssessments` già contrattualizzato), così la 003 può sostituire solo il passo «recupero dei candidati» senza toccare il contratto. B è sconsigliata come soluzione definitiva perché lega la correttezza dei Gap a un partizionamento che la 003 dovrà comunque rivedere. Decisione del committente richiesta prima di qualunque implementazione; fino ad allora resta in vigore la soluzione temporanea C+D.

## 15. Consolidamento di T082 e baseline tecnica (2026-11-09, base `8eb1c2c`)

Il committente ha interrotto la campagna T082 dopo otto esecuzioni reali e non autorizza la nona. Nessuna prova DGX eseguita dopo `8eb1c2c`. Dettaglio e rischi in `specs/002-project-documents/validation/I5-single-real.md` (sezione «Consolidamento»). Task: 143 totali, 130 spuntati, 13 aperti; **nessun task chiuso in questa fase, nessun criterio, gold o stato di test modificato**.

| Gate | Stato | Note |
| --- | --- | --- |
| T082 | FAILED / aperto | preparazione FUNCTIONALLY_VERIFIED (ottava); instradamento sull'output IMPLEMENTED + TECHNICALLY_VERIFIED offline; analisi completa NOT_VERIFIED con la nuova policy |
| T083, T090, T104, T109, T110, T067, T114 | NOT_VERIFIED | a valle di T082 / T126 / T142, invariato rispetto a §13.3 |
| T142 | BLOCKED (decisione) | terza prova N4: 60 PASS / 5 FAIL; nessun v7; decisioni su trasmissione di nome documento e percorso titoli e sul controllo duplicati |
| T126 | NOT_VERIFIED | prova reale non eseguita |
| T138 | BLOCKED (decisione) | raccomandazione A (§14); fail-fast C+D in vigore |
| T107 (parte umana), T111 | NOT_VERIFIED | richiedono persone |

### Rischio T138 allargato
Con la policy `single-call-output-estimate-1` un documento con Gap la cui uscita attesa supera O fallisce in fail-fast invece di troncare: comportamento voluto (nessun troncamento non ritentabile), ma amplia il perimetro dei casi non soddisfatti finché T138 non è deciso.

### Baseline
Backend 3613 test, 0 falliti, 84 ignorati (DGX/parser reali e benchmark non eseguiti nella suite ordinaria). Per pianificare: decisioni T138 e T142, autorizzazione di una nona esecuzione T082, prova T126, poi la catena T067 → T104 e T083 → T090 → T109/T110 → T111 → T114.

## 16. Verbale di chiusura tecnica (2026-11-09) — NON è un'accettazione

Branch `feature/002-implementation`. Stato ufficiale: **NOT ACCEPTED**.

**Livelli (mandato di chiusura):**
1. *Implementazione completata*: **sì, per tutto ciò che è implementabile e approvato.** D1 (stadio Gap su ledger completo, `project-analysis-documents-v6`, T144–T148), UI coverage (T149), D2 `message-format-2` (T151), D3 avviso strutturale (T152), emendamenti documentali (T153). T126 è implementato (`2b4239b`) e T138 realizzato da T144–T149; restano aperti per l'evidenza, non per il codice.
2. *Tecnicamente verificata (offline)*: **sì.** Backend `dotnet test --solution bProjects.sln` con PostgreSQL 18 e MAF 1.26.0 reali, modello finto: 3762 test, 3678 PASS, 0 FAIL, 84 SKIP (58 DGX opt-in assente, 21 parser reale assente, 3 traccia V7/review assente, 1 benchmark, 1 symlink). Frontend: build OK (warning budget iniziale 650,39 kB vs 650,00, +386 byte, preesistente), Vitest 438/438, E2E offline T149 14/14, axe 0 violazioni. Gli SKIP non sono PASS. Nessun test storico v2–v5 indebolito; 24+4 asserzioni di freeze aggiornate alla v6 (dettaglio nei commit T148/T151–T153).
3. *Funzionalmente verificata (gate reali)*: **no.** Nessuna inferenza DGX eseguita in questa fase; la nona esecuzione T082 non è autorizzata.
4. *ACCEPTED*: **no.**

**Gate pendenti (14 task aperti su 153, T138 e T150 inclusi):** T126 (prova DGX, decisione D6), T142 (≥3 N4 reali con D2), T067, T104, T082, T083, T090, T109, T110, T150 (gate DGX «SC-004-Gap», criterio PASS in tasks.md), T107 e T111 (umani), T114 (gate finale). T138 resta `[ ]`: chiudibile solo con l'evidenza T150 o con decisione formale del committente. Predecessori: T067←T126,T142; T090←T082,T083; T104←T067; T109←T090; T110←T067,T090; T111←tutti; T114←T109,T110,T111,T142.

**Limiti dichiarati:** ledger oltre il budget di input (o Gap oltre il budget di output) di una chiamata ⇒ fail-fast tipizzato, perimetro NON coperto dalla 002 (non trasferito automaticamente alla 003); costanti di budget dello stadio Gap (1000+150·Gap; ceil((100+260·Gap)·1,25)) da ricalibrare al primo run DGX; il Gap deve avere una domanda canonica nella gerarchia (`gap_reference_missing` altrimenti); `document-analysis-v6.md` afferma ancora che il nome del documento non compare nelle evidenze (tensione con `documento=`, D4: nessun v7 senza fallimento dimostrato).

**Non fatto/da decidere:** aggiornare `CLAUDE.md` riga 13 (T126, T138, T142, T150 tra gli aperti) richiede approvazione esplicita; D6 (modalità di prova T126 su DGX).
