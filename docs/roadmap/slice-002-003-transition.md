# Transizione 002 → 003: consolidamento architetturale e confine fra le slice

Data: 2026-11-09. Base: `feature/002-implementation` @ `a3bbe8a`; ricerca `research/slice-003-lab` @ `f005c8d`. **Documento di proposta: nessuna specifica, contratto, task, codice o stato ufficiale è stato modificato.** Nessun benchmark, nessuna inferenza DGX, `/speckit.tasks` non lanciato. Le decisioni D1–D8 (§C) attendono approvazione del committente.

Convenzioni: «verificato» = letto nel codice/documenti in questa fase; «da verificare» = dichiarato da un'analisi ma non ricontrollato riga per riga. Le evidenze della ricerca 003 sono **evidenze sperimentali su corpus sintetici, non requisiti**.

---

## A. Stato effettivo

### A.1 Baseline dei task (verificato con grep su `tasks.md`)

143 task (T001–T143, nessun buco), 130 `[x]`, 13 `[ ]`: T126, T067, T142, T082, T083, T138, T090, T104, T107, T109, T110, T111, T114. Stato ufficiale della 002: **NOT ACCEPTED**.

Dipendenze dichiarate ancora aperte (tutti gli altri predecessori sono `[x]`):

| Task | Predecessori aperti |
|---|---|
| T067 | T126, T142 |
| T090 | T082, T083 |
| T104 | T067 |
| T109 | T090 |
| T110 | T067, T090 |
| T111 | T067, T090, T104, T107, T109, T110 |
| T114 | T109, T110, T111, T142 |
| T126, T142, T082, T138, T107 | nessuno (bloccati da decisioni, autorizzazioni o persone) |
| T083 | nessuno tecnico; solo ordine DGX seriale dopo T082 |

T138 **non ha successori dichiarati** (`tasks.md:525`).

### A.2 I 13 task, uno per uno

Legenda: **tec** = serve davvero l'artefatto del predecessore; **ord** = solo serializzazione di gate/DGX.

**T126 — readiness runtime/modelli scaricati**
- Presente: codice `2b4239b`, 18 test dedicati (49 con T124/T119) verdi; prova reale parziale in sola lettura (router `sleeping/unloaded`, zero inferenze, deadline rispettata).
- Manca: prova «scaricato → disponibile»; `loading`, `unreachable`, modello assente, finestra diversa non provati su DGX.
- Verifica: DGX, richiede la decisione D6. Non umana.
- Dipendenze: nessun predecessore aperto. Predecessore di T067 (tec parziale).
- T138/T142: nessuna relazione. Condivisibile con 003: lo schema di readiness servirà anche ai servizi embedding/reranker, ma non è un requisito 002.
- Accettazione: FR-020/SC-012 solo «funzionalmente».

**T067 — US1 piccolo reale (≥5 nominali, 4 formati, browser)**
- Presente: harness `DocumentSmallFlowMafRealTests` e `document-small-flow-real.spec.ts`; N1–N3 PASS, N4 FAIL ×3 (36/5, 17/1, 60/5).
- Manca: un N4 conforme, N5, browser reale, verifica integrata finale.
- Dipendenze: T126 (ord/tec parziale), T142 (tec).
- Accettazione: SC-003/005, parte nominale di SC-001. A valle: T104, T110, T111.

**T142 — N4 reale single-call v5 (≥3 conformi)**
- Presente: harness, `extraction-v3`, `document-analysis-v4/v5/v6` (v6 non citato da plan/tasks), retry informato, coverage-3, UI.
- Manca: N4 conforme. Terza prova 60 PASS/5 FAIL: (1) E2 valutato `Open/Ambiguous` con supporto E3+E1 invece di `AnsweredInSnapshot` col solo E3; (2) duplicazione 30/45 fra `requirements` e alternative del conflitto; il modello non riceve nome documento/titolo.
- Verifica: offline per le correzioni deterministiche; poi DGX seriale (≥3 N4 + avversi).
- Dipendenze: nessuna aperta. Blocca T067 e T114.
- Relazione: banco di prova di D2/D3/D4; **indipendente da T138** (percorso single-call).
- Condivisibile con 003: il formato messaggio con provenienza (D2) servirà anche ai passaggi recuperati.

**T082 — documento singolo oltre contesto**
- Presente: preparazione FUNCTIONALLY_VERIFIED (ottava: 250 chiamate, 297 evidenze, 37,8 min, nessun blocco perso, 16 fatti gold nel testo estratto); policy `single-call-output-estimate-1` (`8eb1c2c`) implementata e verificata solo offline.
- Manca: analisi completa con la nuova policy (mai eseguita; nona esecuzione **non autorizzata** dal committente per questa campagna): copertura, ≥12 fatti, citazioni, conflitto lontano, tempi entro cap E.
- Dipendenze: nessuna aperta. **T138 non è nelle sue Dip.**
- Relazione con T138: **dipendenza d'ordine/semantica, non tecnica sul corpus**. Verificato: `LargeDocumentRealFlow.cs:239-242` invia solo una *dichiarazione* PM e nessuna domanda; `extraction-v3` crea Gap solo da domande esplicite del PM; quindi lo snapshot non contiene Gap e il fail-fast `hierarchical_gap_assessment_unsupported` non scatta. Percorso atteso: gerarchia senza Gap (equivalente v4).
- Rischio: la gerarchia su 297 evidenze non è provata sul DGX.
- Accettazione: SC-004 (documento singolo). Stato: FAILED/aperto.

**T083 — analisi aggregata ≥3 documenti**
- Presente: `DocumentMultiLargeMafRealTests`, generatore condiviso con T082 (TXT/MD/DOCX + dichiarazione PM, nessuna domanda).
- Manca: mai eseguito. Dipendenza da T082: ord (serializzazione DGX), tec-pratica solo per il generatore condiviso.
- Relazione T138: come T082. Accettazione: SC-004 (aggregato).

**T138 — Gap oltre contesto**
- Presente: gate anticipato eseguito e **non superato** (`T130-N4-revision.md`); soluzione temporanea C+D (T143): oltre contesto con Gap → `Failed document_limit_exceeded / hierarchical_gap_assessment_unsupported`, zero chiamate (`DocumentAnalysisWorkflow.cs:632-675`, verificato). Scatta anche quando l'input entra ma l'output atteso supera O.
- Manca: decisione architetturale (D1). Il testo del task descrive ancora l'opzione B («nessuna fase/chiamata LLM dedicata»); la roadmap raccomanda A.
- Verifica: DET/PG/MAF-PG offline + DGX sui gate.
- Dipendenze: nessuna aperta; nessun successore dichiarato.
- Accettazione: FR-013/SC-004 sul perimetro «Gap oltre contesto» e criterio `spec.md:162-164` (esplicitamente non soddisfatto).

**T090 — SC-004 end-to-end**
- Presente: controparti con LLM finto. **Mancano** `DocumentLongContextMafRealTests`, `document-long-context-real.spec.ts`, `I6-real.md`.
- Dipendenze: T082, T083 (tec: usa i loro dataset).
- Relazione T138: **da verificare**: le «3 domande chat su zone lontane» sono turni di chat; se l'analisi parte dopo di esse, `extraction-v3` può trasformarle in Gap e far scattare il fail-fast. Dipende dall'ordine delle fasi nel test, non dal prodotto.
- Accettazione: gate di SC-004.

**T104 — US3 reale (riavvii, versioni, promozione)**
- Presente: controparti fake/stack; T100/T103 chiusi. Mancano `document-version-recovery-real.spec.ts` e `I7-real.md`; contraddizione documentale sulle candidate fallite (`verification.md:81`) da chiarire.
- Dipendenze: T067 → **ord/gate** (tec solo se riusa fixture/stack di T067).
- Accettazione: SC-002/007/008 in modalità reale.

**T107 — accessibilità**
- Presente: Vitest 429/429, axe senza violazioni, fake 12/12 e stack 2/2; checklist umana (d) predisposta, nessuna casella spuntata.
- Manca: righe 1–11 della checklist (lettore di schermo, reflow 320 px/zoom, contrasto, touch).
- Verifica: **solo umana**. Nessuna dipendenza. Predecessore di T111. Accettazione: SC-011/FR-022.

**T109 — coda e W2 con DGX occupato; T110 — profili N≤180 s / E≤3600 s**
- Presenti: logica deterministica di coda/deadline chiusa. **Mancano** `DocumentQueueMafRealTests`, `queue-real.md` (T109); `DocumentPerformanceMafRealTests`, `performance-real.md` (T110).
- Dipendenze: T109←T090 (tec: dataset oltre contesto/owner Extended); T110←T067 (tec: flusso N), T090 (tec: dataset E).
- Accettazione: SC-011/012, W2, B1.

**T111 — E2E e revisione umana SC-001–SC-012**
- Mancano `document-acceptance-real.spec.ts`, `pm-results.md`; due PM indipendenti + un tecnico. Dipendenze: tutte tec/gate. Non automatizzabile.

**T114 — gate finale**
- `acceptance-evidence.md` mancante. Dipende da T109/T110/T111/T142. Nessun gate mancante va classificato PASS.

### A.3 Riferimenti obsoleti o contraddittori

| Dove | Problema |
|---|---|
| `tasks.md:7`, `AGENTS.md` | branch `feature/002-project-documents`; il reale è `feature/002-implementation` |
| `tasks.md:9,14,34-49` | conteggi storici (123/132/133); `:14` dice «T137–T143 aperti» ma è aperto solo T142 |
| `tasks.md` T138 (318) | descrive l'opzione B; closure §14 raccomanda A |
| `ai-workflow.md:3,469` | banner «NON implementata… crea run v4» e «implementazione mancante (T143, T141)»: T135–T141 e T143 sono `[x]` |
| `ai-workflow.md` §13.1 vs §13.6 | §13.1 tratta la gerarchia nominale come prerequisito, annullata da §13.6 |
| plan/tasks/data-model | assenti `document-analysis-v6`, `single-call-output-estimate-1` / `analysisOutputPolicy` (compaiono solo in `ai-workflow.md:510` e I5) |
| `slice-002-technical-closure.md` §3, §8, §13.3 | N4 «×2 FAIL» (superato da §15: 3ª prova 60/5); «T082/T083/T090 dipendono da T138» e catena d'ordine presentate come grafo normativo |
| `CLAUDE.md:13`, `operations.md` | elenco degli aperti omette T126, T138, T142 |
| `HANDOFF.md` | è della 001 (nessuna sezione 002) |
| `validation/resume-prompt.md` | obsoleto (chiede T131/T132/T126 già fatti) |
| `validation/sprint2-handoff.md` | stratificato, blocchi duplicati (`:171/:200`, `:188/:217`) |
| Co-Authored-By | `CLAUDE.md` lo vieta; il reminder di sessione lo richiede: per i commit del repo vale `CLAUDE.md` (D8) |

---

## B. Decisioni già assunte (approvate e/o implementate)

Non sono decisioni le raccomandazioni di laboratorio (§F).

1. Monolite modulare, MAF 1.26.0 pin esatto, LLM solo interno, PostgreSQL 18, **nessun RAG/indice di ricerca/pgvector nella 002** (`plan.md:11,19,24`).
2. Originali/versioni immutabili, parsing Docling isolato, provenienza nel Evidence ledger esistente (FR-007).
3. Snapshot congelato per run, `PromptVersions` di 12 chiavi, `ContractOf` a insiemi chiusi; v2/v3/v4 storici invariati.
4. Analisi single-call v5: `gapAssessments` (`gap-assessment-1`), coverage-3, retry informato unico (T137), `document-analysis-v4/v5/v6` coesistenti (v4 e v5 pinnati per hash).
5. **C + D (2026-11-07, approvata, temporanea)**: Gap valutati solo single-call; oltre contesto con Gap → fail-fast tipizzato a zero chiamate (T143); senza Gap → gerarchia equivalente v4.
6. Gate T138 anticipato: eseguito, non superato (nessuna nuova fase introdotta).
7. `extraction-v3` (domande esplicite del PM → Gap), `extraction-v2` invariato.
8. T082: policy `single-call-output-estimate-1` congelata in `analysisOutputPolicy` (verificata offline); preparazione estesa verificata sul DGX; **nessuna nona esecuzione in questa campagna**.
9. Nessun deduplicatore semantico; duplicazione *esatta* fra item e alternativa rifiutata (`AnalysisAliasComposer.Coherence`, `:355-365`, verificato: testo con newline normalizzati + classificazione + insieme EvidenceId, solo alias-2).

---

## C. Decisioni ancora necessarie

### D1 — Valutazione dei Gap oltre contesto (T138): stadio dedicato su ledger completo

**Problema.** Con Gap nello snapshot e `!measure.Fits || outputExceeded`, il run fallisce (`hierarchical_gap_assessment_unsupported`). SC-004/FR-013 nel perimetro Gap restano aperti; il fail-fast ora scatta anche per output atteso > O.

**Evidenza chiave (derivata da codice e ottava esecuzione).** Il collo di bottiglia non è l'input: *le 297 evidenze entrano nell'input* (`I5-single-real.md:77`); a saturare è l'**output** dell'analisi completa. Il ledger (enunciato + citazione) è un corpus compatto e completo per costruzione (estrazione esaustiva, copertura tracciata). Un Gap può quindi essere valutato contro **l'intero ledger**, senza ranking/top-k, e il record `gap-assessment-1` ha output piccolo. Il problema «Gap e riscontro in porzioni diverse» del gate T138 nasce solo valutando i Gap *dentro* i batch gerarchici; fuori da essi sparisce.

**Proposta («A-ledger», minima).**
1. L'analisi gerarchica resta quella v4 (nessuna modifica a Pack, hierarchy, `reconcile-alias-1`).
2. Dopo l'assembly gerarchico, uno **stadio Gap** (nuovo valore di `Stage` nei batch del piano, non un nuovo executor; `DocumentBatchWorkflow.cs:29-60` invariato) riceve: tutti i Gap + tutte le evidenze non-Gap (enunciato, origine, nome/versione/sezione del documento, citazione) + elenco compatto dei conflitti/domande aperti canonici. Output: soli `gapAssessments` nel formato esistente.
3. Pre-condizione deterministica: payload dello stadio ≤ budget di input. Altrimenti fail-fast tipizzato nuovo (`gap_ledger_exceeds_input`), zero invii, nessun troncamento (FR-014). **Questo è il confine con la 003**: sopra il budget serve retrieval.
4. Riferimenti `openItem`/`openConflict`: il record `Open` può referenziare un conflitto/domanda canonico esistente, oppure portare una `openQuestion` proposta *dal modello* che l'assembly aggiunge come elemento AI (mai creata dal server). È il punto di progetto più delicato: da chiudere in plan.
5. Riuso integrale di `GapAssessmentRules`, `ReconciliationCompletion` (`gap_not_assessed`), coverage-3 (selezione per algoritmo, oggi single-call-only), UI T140.
6. Nuova definizione `project-analysis-documents-v6` con chiavi di freeze aggiuntive (stadio Gap, prompt `document-gap-v1`, DTO, policy al posto di `reconcileGapPolicy=hierarchical-gap-incomplete-1`). Run storici v2–v5 invariati, compreso il fail-fast per i v5.

**Alternative.** (A-retrieval, closure §14) riscontri scelti per ranking: vietato in 002, duplicherebbe la 003. (B) Gap come obbligazioni di riconciliazione: complessità quadratica, non garantisce co-locazione, rompe test T143/gate. (Y) **Trasferire** il criterio «Gap oltre contesto» alla 003 e accettare la 002 con la limitazione documentata: richiede approvazione esplicita e riduce SC-004 per il perimetro Gap.

**Raccomandazione:** D1-X (A-ledger) in 002; il retrieval resta alla 003 come ramo «ledger > budget». Se il committente preferisce Y, T138 diventa «trasferito» e T082/T083/T090 restano indipendenti (D5).

**Conseguenze.** +1 chiamata (o poche) per run con Gap, entro `MaxAiCallsPerRun`; nessuna tabella/migrazione (JSONB) — **da verificare** se `Stage`/`SchemaVersion` hanno CHECK nelle migrazioni; coverage da estendere; dimensione del payload dello stadio su un ledger da 297 evidenze **da misurare offline** (non fatto).

**Misura offline del payload (2026-11-09, eseguita; test `DocumentGapStagePayloadMeasureTests`, 2/2 verdi, nessun DGX).** Profilo effettivo come I5: W=131072, O=16384, B(input)=113664. Ledger 297 evidenze non-Gap + N Gap con metadati D2, prompt v6 (16 KB) come proxy, 12 conflitti/8 domande/30 relazioni assunti.

| Scenario (enunciato/citazione) | Input N=1→20, 4,48 c/t (osservato I5) | 3,5 c/t | 2,5 c/t (prudente) | Max evidenze/chiamata (N=10; 4,48/3,5/2,5) |
|---|---|---|---|---|
| corto (90/120) | 30–31% di B | 39–40% | 54–56% | 1099 / 846 / 588 |
| centrale (180/300) | 46–47% | 59–60% | 82–84% | 681 / 524 / 364 |
| verboso (300/2×400) | 85–86% | **109–110% (non entra)** | **152–154% (non entra)** | 350 / 269 / 187 |

Output (soli `gapAssessments`): 7% di O con 1 Gap, 23% con 10, 40% con 20, 93% con 50; l'`Estimate` esistente raggiunge O a 53 Gap (i record reali sono 2–3× più piccoli). Il vincolo che morde per primo è l'**input**, non l'output: l'ipotesi centrale di D1 è confermata per ledger centrali/corti e per N ≤ ~50; per ledger verbosi vale solo col rapporto car/token osservato. Limiti: nessun tokenizer reale; lunghezze medie di enunciato/citazione non disponibili (DB di I5 eliminato) ⇒ gli scenari sono ipotesi dichiarate; latenza di una chiamata da 50–100k token non misurata; ragionamento del modello e `openQuestion` proposta dal modello esclusi.

**Oltre la soglia (registri che non entrano in una chiamata).** Il partizionamento del ledger (ogni Gap contro tutte le partizioni, composizione deterministica) *non* è top-k, ma: `Answered` si compone in modo monotono; `Partial/Ambiguous` dipendono dalla somma dei riscontri e partizioni discordi richiederebbero un arbitrato (vietato: FR-014, T138 «no union supporti/arbitrato», esito `gap_assessment_unresolved`); `NoAnswer` parziale non prova l'assenza; il conflitto distante documento/PM si perde se i due lati stanno in partizioni diverse. Riproduce il problema del gate T138. **Raccomandazione: non partizionare in 002.** Oltre il budget: fail-fast tipizzato `gap_ledger_exceeds_input` a zero invii, con **perimetro non coperto dichiarato**: «Gap su ledger oltre il budget di input di una chiamata», che *non* è un PASS di FR-013/SC-004 su quel perimetro e viene coperto dalla 003 (retrieval + `retrievalComplete`) solo per decisione formale (D5/Y parziale). Un ledger da 297 evidenze entra; la soglia reale (~360–680 evidenze centrali) va ricalibrata con lunghezze reali al primo run autorizzato.

**Stato D1 (2026-11-09): APPROVATA, opzione A (stadio Gap su ledger completo), con le condizioni del committente recepite in `specs/002-project-documents/contracts/ai-workflow.md` §13.7; task T144–T150 aggiunti in `tasks.md`; implementazione non iniziata. La misura offline autorizza l’implementazione ma non è validazione funzionale: T138, FR-013 e SC-004 (parte Gap) restano aperti. Il «Limite residuo» (ledger oltre il budget di input/output di una chiamata) non è coperto né trasferito automaticamente alla 003. D2, D3, D4, D5, D6, D7, D8 restano pendenti. Numerazione effettiva dei task: T144–T150 (non T144–T151 come nel §G provvisorio; il §G resta storico).**

**Clausole da sottoporre ad approvazione (esatte).**
- `tasks.md` T138 (318): «Nessuna fase/chiamata LLM aggiuntiva dedicata, retry gerarchico o aumento limiti»; (320) «non implementare oltre il gate».
- `tasks.md` T143 (264): «nessuna nuova chiamata, fase, retry gerarchico, ranking/retrieval».
- `ai-workflow.md` §13.4 (451-453): «senza nuova fase o chiamata dedicata ai Gap», «mai chiusa dal server sul solo descriptor»; §13.5 (463) «Nessuna tabella nuova»; §13.6 (469, 481): «Nessuna nuova fase o chiamata LLM, nessun ranking/top-k/retrieval… `EvaluateHierarchical` invariato», «Vietati… `reconcile-alias-2`/`global-reconcile-v2`»; righe 84 e 232 (no ranking/top-k/prossimità).
- `plan.md` 11, 24 («Nessun RAG»), 155, 157; `spec.md:130`; `research.md:59`.
- `spec.md:162-164` (stato temporaneo): da riscrivere secondo l'esito.
- Test da riscrivere (non indebolire) se si tocca il percorso v5: T143 `tripwire`, `V5_above_context_without_a_gap…`, `V5_hierarchy_manifest_differs…`, tre `Characterization_LIMIT_*` (`DocumentReconcileGapGateTests.cs:325,345,369`). Con la definizione v6 nuova, i test v5 restano e la v6 ha test propri.

### Strategia di completezza dei Gap (parte di D1)

Cinque stati distinti con **metadati calcolati dal server**, senza cambiare l'enum del modello (`Answered|NoAnswer|Partial|Ambiguous`):

| Stato richiesto | Rappresentazione | Regola deterministica |
|---|---|---|
| Evidenza **non recuperata** | `searchBasis=retrieval`, candidato non incluso | Solo 003. Mai `NoAnswer`: `Open` con `retrievalComplete=false`; candidati/ranghi/cutoff persistiti |
| **Assente dal corpus completamente verificato** | `NoAnswer` con `searchBasis=ledger_exhaustive` **e** estrazione completa (tutte le porzioni `Completed`, nessuna `coverage_incomplete`) | Il server calcola `absenceProven`; se manca una condizione, `NoAnswer` è etichettato «assenza non provata» |
| **Insufficiente/ambigua** | `Partial` / `Ambiguous` | invariato |
| **Documentata** | `AnsweredInSnapshot/Answered` | supporto non-Gap, nello snapshot, come oggi |
| **Conflitto aperto** | `Open` + `openConflict` | competenza dell'analisi, non dello stadio Gap |

Il top-k non è mai prova di completezza: in 002 non c'è top-k; in 003 `retrievalComplete` impedisce di convertire «non recuperato» in «assente».

### Conflitto documento/PM non perso

1. Vista completa: nello stadio D1 entrambe le fonti sono nel ledger (nel replay SKRAG il messaggio PM di A09 si perdeva per TopK/sommari).
2. Separazione dei ruoli: la risposta documentale usa solo evidenze rilevanti al *contenuto* richiesto; il conflitto lo fa emergere l'analisi, con il controllo esistente §13.4 (`Contradicts/AlternativeTo` Open ⇒ conflitto Open con entrambi gli endpoint).
3. Guardia deterministica **non bloccante** in coverage: se le evidenze di supporto di un record `Answered` partecipano a un conflitto Open o a una relazione nota con evidenza di origine diversa (Message vs Document), la coverage segnala «risposta documentale con conflitto aperto». Nessun matching lessicale, nessun arbitrato.
4. Correzione del prompt in D4.

### D2 — Provenienza nel contesto LLM (T142), modifica minima

**Dove sono i metadati (verificato).** `ProjectDocument.DisplayName` (≤256, non modificabile dopo l'upload, `DocumentUploadService.cs:307`); `DocumentVersion.Version/OriginalName`; `DocumentBlock.HeadingPathJson` (`DocumentParseWorker.cs:329`); `PersistedSource.BlockId/DocumentId/DocumentVersionId` (quindi `AliasSourceEntry`); `Certainty` nel catalogo (`AnalysisAliasCatalog.cs:24`).

**Dove si perdono.** `DocumentAnalysisAliasPlan.cs:133-138` (e `ReconcileAliasMessages.cs:40,43`, `DocumentReconcilePlan.cs:668`) stampano solo `origine/tipo/polarita` + enunciato + citazione; `DocumentAnalysisSources.LoadAsync` (`DocumentAnalysisWorkflow.cs:45-111`) non legge né `ProjectDocuments` né i blocchi; il prompt v6 dichiara «Il nome di un documento NON compare nelle evidenze» (verificato). Il percorso storico v2 stampava `certezza=` (`:745`), quello alias no. Anche l'estrazione (`DocumentEvidenceAgent.cs:206-223`) non passa nome/titoli: il titolo arriva solo come range `Context` se il parser lo emette.

**Modifica minima.** `message-format-2`: nell'intestazione VOCE aggiungere, come testo-dato con escape e senza istruzioni, `documento="…" versione=N sezione="A > B" certezza=…` (sezione solo se `HeadingPath` non vuoto, mai inventata). Dati caricati in `LoadAsync` in una mappa laterale di `DocumentAnalysisInput`, **non** in `AliasSourceEntry` (altrimenti cambiano `CatalogHash`/`RunIndependentHash`). Opzionale: delimitatore per documento.

**Compatibilità.** `BuildMessages` non è versionato e serve v3/v4/v5/v2: cambiare il testo cambia `PayloadHash`, `inputHash` e `batchId` ⇒ lookup miss/`checkpoint_incompatible` per i run in volo. Perciò: nuova chiave di freeze (`analysisMessageFormat`) scelta dalla definizione congelata come `GapPromptOf`; i run storici mantengono il formato 1. Va emendato il §7.3 («elenco chiuso», «mai UUID/blocchi»).

**Limite onesto.** Per il TXT di N4 `headingPath` è vuoto («Scheda A» è un paragrafo non citato): D2 porta `documento="txt-conflict-a.txt"`, non «Scheda A». Resta il dato oggettivo per escludere `origine=Message`, ma il collegamento «scheda A» ↔ file è semantico. Alternative per il titolo: estrazione che cita la riga-titolo come `Context` (nuovo prompt di estrazione, escluso), fixture Markdown, `DisplayName` scelto dal PM. **Non** introdurre il fallback «prima riga del documento» (euristica).

**Indipendenza da T138.** D2 tocca solo il single-call; implementabile da solo, ma entra in una definizione v6: conviene farla coincidere con D1 per un solo bump.

### D3 — Deduplicazione: esatta vs strutturale

- **Esatta (in vigore):** `Coherence`. Sopravvive.
- **Strutturale (proposta):** item e alternativa con stesso insieme di evidenze + stessa classificazione + stesso multiinsieme di token numerici. Studiata su due output catturati: rileva i duplicati (2ª e 3ª prova) senza falsi positivi sugli altri 7 accoppiamenti. **Rischi**: inversione di polarità («entro 30» / «non entro 30»), due fatti distinti sulla stessa evidenza (ammesso da v6:12), campione piccolo.
- **Proposta:** solo come **avviso non bloccante in coverage**, con confronto aggiuntivo di polarità/negazioni; **nessuna eliminazione, correzione o rifiuto** finché ≥3 N4 reali e i fixture deterministici non mostrano zero falsi positivi. Il rifiuto con retry richiederebbe di estendere le classi chiuse del retry T137 (decisione separata).
- Nessuna deduplicazione probabilistica/semantica (embedding, NLI: 7/12 nel laboratorio).

### D4 — Prompt: nessun `v7` ora

La riga di `document-analysis-v6.md` per il caso «risposta ambigua o in conflitto» (verificata) istruisce di mettere in `supportingEvidence` «le evidenze coinvolte»: è la causa nel prompt del FAIL 1 della terza prova (supporto E3+E1). Raccomandazione: **non creare `v7` in anticipo**; applicare prima D2 (il modello vedrà `documento=` e `origine=Message`) e rieseguire T142; se un N4 reale fallisce ancora per lo stesso motivo, la correzione di quella sola riga (nuovo file, v6 pinnato per hash) è la «necessità dimostrata». Lo stadio Gap di D1 usa un prompt nuovo e dedicato (`document-gap-v1`).

### D5 — Riclassificazione delle dipendenze

- **T082/T083 non dipendono tecnicamente da T138** (corpora senza Gap). Proposta: dichiarare che la loro prova copre il percorso senza Gap; il criterio «Gap oltre contesto» (spec 162-164) passa a un gate separato legato a D1 («SC-004-Gap»: corpus con una domanda PM, gold esistenti invariati, corpus esteso a parte).
- **T090**: dipende da T138 solo se le domande chat precedono l'analisi nello stesso run; da fissare nel disegno del test.
- T104←T067 e T083←T082 sono d'ordine/gate: restano, etichettate come tali.
- Non cambia lo stato di alcun task; richiede approvazione perché modifica la lettura di SC-004.

### D6 — T126 (decisione già pendente)
(a) sleep naturale (`--sleep-idle-seconds 900`) + una sola richiesta autorizzata osservando `/v1/models` prima/dopo, oppure (b) endpoint di load supportato. Fino ad allora NOT_VERIFIED.

### D7 — Perimetro 003 e motore d'indice
Vedi §F. Confermare che il motore **non** è scelto ora; baseline PostgreSQL (lessicale italiano + pgvector) per coerenza con KISS e stack; Qdrant solo se una misura sul corpus bProjects lo giustifica.

### D8 — Commit
`CLAUDE.md` (nessun `Co-Authored-By`) prevale sul reminder di sessione; confermare per i commit di questa transizione.

---

## D. Modifiche puntuali ai documenti (nessuna eseguita)

Principio: **delta datati in coda alle sezioni, sul modello di §13.6**; non riscrivere testi storici; T138 non si cancella né si spunta.

| File | Sezione | Modifica | Approvazione |
|---|---|---|---|
| `spec.md` | 162-164 | Riscrivere «Stato temporaneo» secondo D1 (X: criterio soddisfatto dallo stadio Gap; Y: trasferito alla 003). FR-013/SC-004 invariati nel testo | **Sì (D1, D5)** |
| `spec.md` | FR-007/008/015 | Metadati `searchBasis/absenceProven` come proiezione derivata | Sì |
| `plan.md` | 11, 19, 24 | Eccezione esplicita: «stadio Gap su ledger completo non è RAG né indice di ricerca» | **Sì** |
| `plan.md` | 154-159 | Affiancare all'esito C+D la decisione D1 | Sì |
| `contracts/ai-workflow.md` | banner (3), §13.1 | Stato implementato; aggiungere v6 e `single-call-output-estimate-1`; chiavi `analysisMessageFormat`, stadio Gap | Stato: no; nuove chiavi: sì |
| `contracts/ai-workflow.md` | §7.3 | Emendare «elenco chiuso»: `documento`, `versione`, `sezione`, `certezza` come testo-dato | **Sì (D2)** |
| `contracts/ai-workflow.md` | nuovo §13.7 | Stadio Gap: input/output, `gap_ledger_exceeds_input`, riferimenti Open, coverage per algoritmo | **Sì (D1)** |
| `contracts/ai-workflow.md` | §13.4–13.6 | «Superato da §13.7 per le definizioni ≥v6»; v5 invariato | **Sì** |
| `data-model.md` | 15-21 | Campi derivati di coverage; nessuna tabella | Sì |
| `tasks.md` | T138 | Nota «realizzato da T144–T151» senza spunta; correzione riferimenti obsoleti `:7,:9,:14,:34-49` | Obsoleti: no; nota T138: sì |
| `tasks.md` | T082/T083/T090 | Una riga: «T138 non condiziona la prova sul corpus senza Gap» (D5) | **Sì** |
| `tasks.md` | in coda | Task provvisori T144+ (§G) in formato esteso | Sì |
| `slice-002-technical-closure.md` | §3, §8, §13.3 | Note di superamento verso §15 e questo documento | No (nota storica) |
| `CLAUDE.md`, `operations.md`, `AGENTS.md` | elenco aperti / branch | Allineare | CLAUDE.md: solo con approvazione esplicita |
| `validation/resume-prompt.md`, `HANDOFF.md` | — | Marcare obsoleti | No |
| `.specify/memory/constitution.md` | — | **Nessuna modifica** | — |

---

## E. Piano di completamento della 002

Nessuna nuova esecuzione lunga di T082 come attività immediata.

**Fase 0 — Decisioni (committente):** D1, D2, D3, D4, D5, D6, D8.

**Fase 1 — implementabile subito (nessuna clausola normativa modificata):**
1. Misura **offline** (nessun DGX) della dimensione del payload dello stadio Gap su un ledger sintetico da ~300 evidenze con `ContextBudget`/`AnalysisOutputEstimate`: valida o smentisce l'ipotesi centrale di D1.
2. Correzione dei riferimenti obsoleti non normativi (`resume-prompt`, `HANDOFF`, note di superamento).
3. T107: checklist umana (persone, non codice).
4. Harness mancanti dei gate (T090, T104, T109, T110, T111/T114): scheletri *ignorati* senza opt-in DGX; non fanno PASS.

**Fase 2 — correzioni architetturali (dopo approvazione):**
5. D2: `message-format-2` (+ test golden dei messaggi, replay storico invariato, run in volo non rotti).
6. D3: avviso strutturale in coverage.
7. D1: stadio Gap (builder di input da ledger, DTO/prompt `document-gap-v1`, batch con `Stage`, assembly+coverage, definizione v6 + `ContractOf` + routing, fail-fast `gap_ledger_exceeds_input`).

**Fase 3 — test offline (modello finto, MAF e PostgreSQL reali):** rosso→verde per ciascun punto; confine di budget; crash/replay/takeover; Gap con riscontri multipli, parziale/ambiguo, conflitto documento/PM, versione esclusa; compatibilità v2–v5; baseline completa invariata.

**Fase 4 — verifiche DGX (su mandato, una alla volta):** T126 (D6) → T142 (≥3 N4 + avversi, con D2) → T067 → T104; T082 (una sola esecuzione, solo su autorizzazione esplicita) → T083 → gate «SC-004-Gap» → T090 → T109/T110.

**Fase 5 — validazioni umane:** T107 (righe 1–11), T111.

**Fase 6 — gate finale:** T114 con tutta l'evidenza; nessun avanzamento automatico a merge/deploy.

---

## F. Perimetro proposto della 003

Già coperto dalla 002 (**non duplicare**): acquisizione Docling con provenienza, persistenza autorevole di originali/versioni/evidenze, snapshot congelato, Evidence ledger, alias deterministici con catalogo, checkpoint MAF.

Capacità **nuove** giustificate dalla ricerca (evidenze sperimentali, non requisiti):

| # | Capacità | Evidenza | Riuso / dipendenze |
|---|---|---|---|
| C1 | Indice derivato e ricostruibile dei **figli originali** (blocchi), BM25 + dense (bge-m3) con RRF; filtri di progetto/versione/esclusioni *prima* del ranking | Hybrid Recall@10 100% su T082/T083 con passaggi brevi (BM25 78,9% su T082); chunk grandi 44,7%/33,3% | Blocchi e range già in DB; servizio embedding (contratto verificato, hash pesi no) |
| C2 | Reranker BGE sui figli | Hit@1 T082 68,4% → 100%; ~1,1–2,2 s/query | Endpoint `/v1/rerank` osservato; punteggi non calibrati |
| C3 | Espansione **selettiva** dei padri con budget esplicito | Utile per titolo/ambito (N4); ~3,7–4,2× caratteri se sempre | `HeadingPath`/range già persistiti |
| C4 | Registro dei candidati (ordine, ranghi, cutoff, errori) congelato nel run | Perdite distinte di ranking e di confezionamento (S06, M03/M05) | Estende WorkOutput; riusa lo stadio Gap di D1 |
| C5 | Ramo «ledger > budget» dello stadio Gap: candidati da C1–C4 al posto del ledger intero, con `retrievalComplete` | A09/A10, gap non recuperati | Sostituisce solo la sorgente dei candidati; contratto invariato |
| C6 | Risoluzione deterministica degli identificativi di citazione | citazioni invalide 12–15 → 1–2 dopo recupero ID | In 002 gli alias sono già risolti dal catalogo: serve per passaggi recuperati |
| C7 | Valutazione continua: gold, query, metriche (Recall@k, falsi `AnsweredInSnapshot`, conflitti persi) | harness del laboratorio | Promuovere gold/script in test, non copiarli nel prodotto |

**Scartati dalle evidenze:** sommari SKRAG (nessun beneficio, punto cieco ai primi 1.200 caratteri), selezione preventiva distruttiva (B2: −5 fatti, −12 regole; B3: 3/16), NLI come arbitro o filtro (7/12), chunk grandi come unico indice, knowledge graph/agenti/microservizi (nessun beneficio documentato).

**Confine unico fra le slice:** un solo contratto di dati, non una classe: *lista ordinata di evidenze candidate + `searchBasis` + `retrievalComplete`*. La 002 la produce dall'intero ledger; la 003 da C1–C5. Nessuna interfaccia con una sola implementazione (YAGNI).

**Dipendenze 003:** D1 approvata/implementata; D7; servizi DGX embedding/reranker e readiness analoga a T126; corpus cliente reali (la ricerca è su dati sintetici, query scritte da un solo autore, nessuna valutazione indipendente, PDF/isolamento multi-progetto non provati).

---

## G. Preparazione Spec Kit

1. **Contesto.** `.specify/feature.json` non esiste; senza `SPECIFY_FEATURE_DIRECTORY=specs/002-project-documents` (o `feature.json`) gli script falliscono. Per sola lettura: `SPECIFY_FEATURE_NO_PERSIST=1`.
2. **`/speckit.tasks` rigenera da T001: non va usato sulla 002.** I nuovi task si aggiungono **manualmente in coda** a `tasks.md` (T144+, formato esteso con **Dip.**/**Verifica**/FR-SC, come T115–T143), senza toccare i `[x]` né rinumerare. `speckit-converge` è un'alternativa (aggiunge solo una fase in coda, formato breve) ma giudica il codice e può riaprire task chiusi: non adatta ora.
3. **Task provvisori (ID e testi indicativi, non aggiunti):**
   - T144 DET — `message-format-2` (D2) + freeze `analysisMessageFormat`.
   - T145 DET — avviso strutturale di ripetizione in coverage (D3).
   - T146 DET+PG — builder dell'input dello stadio Gap dal ledger congelato, misura, `gap_ledger_exceeds_input`.
   - T147 MAF-PG+IMPL — stadio Gap: `Stage`, DTO, prompt `document-gap-v1`, validatore (riuso `GapAssessmentRules`). Dip.: T146.
   - T148 DET+PG — assembly e coverage per gerarchia con Gap, riferimenti Open, metadati `searchBasis/absenceProven`. Dip.: T147.
   - T149 DET+PG — definizione `project-analysis-documents-v6`, `ContractOf`, routing; v2–v5 invariati. Dip.: T144, T148.
   - T150 E2E-OFFLINE — UI coverage per i nuovi metadati (T140 esteso).
   - T151 DGX — gate «SC-004-Gap». Dip.: T149, T082.
   - T138 resta aperto con nota «realizzato da T144–T151»; si chiude solo con evidenza, non per rinvio.
4. **Dopo l'approvazione:** (a) emendare i documenti come in §D; (b) append dei task; (c) eseguire **`/speckit.analyze`** (sola lettura) con `SPECIFY_FEATURE_DIRECTORY=specs/002-project-documents` per verificare la coerenza spec/plan/tasks prima di implementare.
5. **003.** Dopo la 002 consolidata (o in parallelo solo come specifica, senza codice): `create-new-feature.ps1 -DryRun` darà `specs/003-<nome>` (numerazione dalle cartelle in `specs/`; lo script non crea branch Git); poi `/speckit.specify` nel contesto 003 e ripristino del contesto 002. Branch e nome decisi dal committente; la ricerca resta su `research/slice-003-lab`.
6. **Compatibilità storica.** Nessun run, snapshot, WorkOutput, prompt, gold o checkpoint storico viene riscritto: le novità stanno dietro una nuova definizione congelata (v6) e nuove chiavi di freeze; le v2–v5 conservano algoritmi, messaggi e fail-fast.

---

## H. Non verificato / limiti di questa fase

- Dimensione del payload dello stadio Gap su 297 evidenze: **misurata offline** (§C, D1) con scenari ipotetici di lunghezza; lunghezze reali e latenza restano da misurare.
- Verificato 2026-11-09: nelle migrazioni non esistono CHECK su `Stage`/`SchemaVersion` dei batch (solo `CK_ProcessingRuns_DocumentLimitsSnapshot` e `CK_EvidenceExtractionVersions_Origin`): un nuovo `Stage` non richiede migrazione.
- Percorso corretto: `Evidence/DocumentEvidenceAggregate.cs` (non `AI/`).
- Spec Kit 1.2.0 integrato per Codex (skill in `.agents/skills/speckit-*`, nessun slash command Claude Code, CLI `specify` assente): le skill si eseguono leggendo i `SKILL.md`. Dry-run: prossima feature `003-<nome>`.
- Da verificare ancora: se `ReconciliationCodec.Decode` rifiuta proprietà extra; quanti test pinnano `reconcile-v3`; copertura di `DocumentVersion.Sha256` nello `SourceSnapshot.Hash`.
- Comportamento del parser TXT sui range `Context`.
- Il laboratorio 003 non ha eseguito analisi/bozza bProjects, non garantisce completezza e non verifica gli hash dei pesi dei modelli.
- Le analisi di codice sono state prodotte da sottoprocessi di sola lettura; ricontrollati personalmente: conteggi dei task, `LargeDocumentRealFlow.cs:239-242` (solo dichiarazione PM), prompt v6 (regola sul nome documento e riga sui conflitti), fail-fast `DocumentAnalysisWorkflow.cs:632-675`, `Coherence` `:355-365`, messaggi `DocumentAnalysisAliasPlan.cs:133-138`.
