# Riconciliazione gerarchica della 002 — piano di intervento

Diagnosi: [assessment.md](assessment.md). Task operativi: [tasks.md](tasks.md). Il piano sta nel perimetro della slice 002 e non apre nuove slice. **Nessuna parte è autorizzata all'implementazione finché non sono approvate le decisioni del §11.**

Il piano è stato rivisto dopo una revisione indipendente (assessment, «Revisione indipendente»). Le modifiche principali:
- il retry, il prompt per stadio e le misure P-2/P-3 sono diventati **opzioni differite** (§12) con un criterio d'ingresso misurabile;
- l'attivazione come default avviene solo **dopo** le verifiche reali.

## 1. Obiettivo e perimetro

**Obiettivo.** Rendere la riconciliazione gerarchica della 002, cioè il percorso predefinito senza recupero (oggi v6), affidabile sui difetti dimostrati. Concretamente:
- nessuna risposta semanticamente legittima rifiutata per un campo informativo;
- nessuna precondizione dello stadio Gap affidata a un passo generativo;
- nessun item ricostruito dal modello quando è derivabile dall'evidenza;
- nessun candidato del confronto risolto per cache senza verifica.

Tutto senza riscrivere motore, griglia, port, checkpoint o modello dati.

**Vincolo di intento (D2, D2b, M2).** Lo scopo è eliminare la rigenerazione strutturale superflua da parte dell'LLM, **non** rendere verdi i test di copertura. Il prodotto deve continuare a distinguere fatti documentati, requisiti, decisioni, ipotesi, alternative, conflitti e domande aperte. La proiezione deterministica è una **rappresentazione intermedia**: la sua completezza (`evidence_not_mapped` assente, `MissingGaps` vuoto) è condizione necessaria, non sufficiente, per la qualità della documentazione finale, che si valuta solo ai livelli 7–8 (§7). La tabella `leaf-projection-1` (§4) non si modifica senza una decisione esplicita e una nuova versione. Nessuna regola di assembly può trasformare un fatto contestato in un fatto acquisito né cancellare una domanda aperta.

**Dentro il perimetro:**
- nuova definizione `project-analysis-documents-v11` (senza recupero);
- profilo di schema `reconcile-alias-schema-by-stage-1`;
- validazione `reconcile-validation-v2`;
- composizione deterministica degli item delle foglie della vista A, con un'unica regola di assembly per gli estremi dei conflitti;
- prompt unico `document-reconcile-v6`, organizzato per stadio;
- memo dei token degli extra (P-1, risultato identico per tutte le definizioni);
- violazioni tipizzate (diagnostica);
- verifiche offline e fixture rappresentativa;
- emendamenti dei contratti della 002;
- proposta di verifiche reali e, dopo, attivazione.

**Fuori dal perimetro:**
- definizione con recupero (v12) e ogni contratto 003;
- stadio Gap (E5);
- retry, prompt per stadio, P-2/P-3, algoritmo di espansione: §12, solo con evidenza;
- riuso del lavoro fra run diversi;
- cambio di modello, finestra o budget;
- ripetizione di T040;
- M4/M5 della 003.

**Rapporto con il `tasks.md` della 002.** Il `CLAUDE.md` del progetto stabilisce che `specs/<slice>/tasks.md` è l'unica baseline operativa, senza pianificazione parallela. Il `tasks.md` di questa cartella è quindi l'**allegato tecnico** di un mandato. Dopo l'approvazione, i task approvati vanno riportati in `specs/002-project-documents/tasks.md` come nuovi `Txxx` (decisione D11). Questa sessione non lo ha modificato.

## 2. Architettura attuale e proposta

| Stadio | Oggi (v6/v10): il modello produce | v11: il modello produce | v11: il server compone |
|---|---|---|---|
| Foglia A | descriptor, candidati E–E, **un item per ogni evidenza** (testo, sezione, classificazione), `noFindings` condizionale | descriptor, candidati E–E | **item di ogni voce** (tabella `leaf-projection-1` per tipo, origine e certezza), domanda canonica di ogni Gap |
| Foglia B, riduzione, cross | descriptor (non nel cross), candidati, `noFindings` condizionale | invariato; `noFindings` informativo | — |
| Espansione | candidati E–E (≤ 16), `Unresolved` | invariato; `noFindings` informativo | invariato (griglia, saturazione, precheck) |
| Confronto | esito Compared/Open, candidati, patch, `noFindings` condizionale | **esito, con motivo obbligatorio se `Open`**, patch; **nessun candidato** | disposizione (invariato) |
| Assembly | item del modello più alternative dei conflitti | — | item proiettati; un estremo **non Gap** di un conflitto `Open` compare **solo** come alternativa (D2b). Un **Gap** conserva sempre la propria domanda canonica in `openQuestions`, anche se estremo di un conflitto `Open` (regola candidata R-D2b-1, §8) |

Restano invariati:
- motore dei candidati, griglia `expansion-grid-1`, round finale, completamento, stadio Gap;
- port e protocollo di retry esistenti (non usati dalla riconciliazione);
- WorkOutput e checkpoint;
- DTO `reconcile-alias-1` e canonico `reconcile-1`;
- provenienza.

## 3. Delta esatto per componente

Ogni modifica vale solo con la definizione v11, salvo P-1.

| Componente | Modifica |
|---|---|
| `AI/ReconcileStageContracts.cs` (nuovo) | Tabella unica per stadio: variante di schema, campi ammessi, estremi, tetto, regola del focus, patch, item del server. Fonte dei test di equivalenza fra schema e compositore |
| `AI/ReconcileAliasContracts.cs` | Profilo `reconcile-alias-schema-by-stage-1`, in un dizionario separato come il `-3`:<br>• LeafA = copia esatta di LeafB del `-2` (`items` vuoto);<br>• Compare = Compare del `-2` con `candidates.maxItems = 0` e `focus` = `anyOf` [{`outcome`: `enum` [Compared], `reason` nullable}, {`outcome`: `enum` [Open], `reason`: stringa con `minLength` 1}];<br>• Reduce, Cross e LeafB come il `-2`, Expand come il `-3`.<br>Nessun `const` (fuori dal sottoinsieme validato, `ReconcileSchemaSubset.cs:15-18`). Nuove costanti: `PromptVersionV6`, `SchemaProfileByStage`, `StageContract`. Profili `-1/-2/-3` invariati |
| `AI/Prompts/document-reconcile-v6.md` (nuovo) | Prompt **unico** con una sezione per stadio:<br>• niente istruzioni sugli item (li compone il server);<br>• la frase del tetto di 16 solo nella sezione dell'espansione;<br>• confronto senza candidati;<br>• `noFindings` facoltativo.<br>v3/v4/v5 invariati byte per byte |
| `AI/ReconcileAliasMessages.cs` | Solo per la v11, le righe «Item già proposti» diventano `[i3] (E3)`, senza testo (`items-ref-1`: il testo coincide con l'enunciato già presente nella voce). Per v4–v10 il messaggio resta byte per byte invariato |
| `AI/ReconcileAliasComposer.cs` | Violazioni **tipizzate** (`ReconcileViolationKind`), aggiunte ai messaggi storici, che restano invariati. Ramo v11:<br>• candidati nel confronto rifiutati;<br>• controlli sui testi delle patch duplicati dal validatore (difesa in profondità);<br>• item delle foglie della vista A presi da `ReconcileLeafProjection` |
| `AI/ReconcileLeafProjection.cs` (nuovo) | Funzione pura: voce → `ItemProposal` (testo = `Statement`, evidenza = la voce stessa, fonti della voce, classificazione e sezione dalla tabella §4) |
| `AI/ReconciliationContracts.cs` | `CheckResult(r, sourceHash, rules)`: `V1` predefinito (comportamento attuale); `V2` senza alcuna regola su `noFindingsReason`. `CheckSingleCall` invariato |
| `AI/ReconciliationHierarchy.cs`, `AI/ReconciliationCandidates.cs` | Passano le regole da `ReconciliationContext` a `CheckResult` |
| `AI/DocumentReconcileAssembly.cs` | Ramo v11: un item proiettato la cui unica evidenza è estremo di almeno un conflitto `Open`, e non bersaglio di patch accettate, non entra nelle sezioni. L'evidenza resta mappata tramite l'alternativa (`:98-113`). **Eccezione R-D2b-1:** l'item proiettato di un evidenza di tipo **Gap** non è mai escluso: la sua domanda canonica resta in `openQuestions` (la rende visibile a `GapStageReferences.From`, che non legge le alternative) |
| `AI/DocumentReconcilePlan.cs` | Ramo v11: controllo di `:133`, `configHash` e piano con le nuove chiavi, attesa `reconcile-validation-v2`, opzioni del compositore, formato dei messaggi. **P-1 per tutte le definizioni** |
| `AI/ContextBudget.cs` | `DocumentPreflight.MeasureAsync(..., ExtraTokenMemo? memo = null)` (P-1) |
| `AI/DocumentAnalysisWorkflow.cs` | Riconoscimento della v11:<br>• `ContractOf` → 11;<br>• `ReconcilePromptOf`, `ReconcileSchemaProfileOf`, `ReconcileExpansionOf`;<br>• elenco di `:707`;<br>• `contract switch` di `:839` e `:874`;<br>• **condizioni `contract is 6 or 7 or 8 or 9 or 10` di `:678` (formato di messaggio della single-call) e `:835` (instradamento verso `RunStagedAsync`)**;<br>• `:901` senza recupero |
| `Documents/SourceSnapshotStore.cs` | `AnalysisDefinitionV11` e `AnalysisPromptVersionsV11()`. `DocumentaryAnalysisFreeze` cambia **solo** con RH-060 |
| `AnalysisWorkflow.cs:393`, `Evidence/EvidenceCoverage.cs:1066-1068`, `AI/DocumentAnalysisAliasPlan.cs:46` | v11 aggiunta dove compare la v6, con la stessa semantica |
| `specs/002-project-documents/contracts/ai-workflow.md` | Emendamento v11 in §4.1, §4.3, §11, §13.7; righe esistenti invariate |

## 4. Contratti e versioni

| Elemento | v6 (oggi, default senza recupero) | v11 |
|---|---|---|
| Definizione | `project-analysis-documents-v6` | `project-analysis-documents-v11` |
| Chiavi congelate | v6 | chiavi della v6 con `reconcile` = `document-reconcile-v6`, più `reconcileSchemaProfile` = `reconcile-alias-schema-by-stage-1`, `reconcileExpansion` = `expansion-grid-1`, `reconcileContract` = `reconcile-stage-contract-1`. Nessuna chiave `retrieval*` |
| Bundle `reconcile-stage-contract-1` | — | validazione `reconcile-validation-v2`; item `leaf-projection-1`; estremi dei conflitti `open-endpoints-as-alternatives-1` (D2b); confronto `no-candidates`; messaggi `items-ref-1` |
| DTO AI / canonico | `reconcile-alias-1` / `reconcile-1` | **invariati** |
| `ContractOf` | 6 | 11 solo con l'insieme esatto delle chiavi; ogni combinazione mista ⇒ `workflow_incompatible`. Il ramo della v10 non si tocca |
| `configHash` della gerarchia | forma v4–v7 | ramo proprio con `prompt`, `instructions` (SHA), `schemaProfile`, `schemaProfileHashes`, `expansion` (come la v10), `stageContract` (contenuto del bundle). Gli hash v8/v9/v10 restano byte per byte |
| Attesa del lookup | `reconcile-1` / `reconcile-validation-v1` | `reconcile-1` / `reconcile-validation-v2` |
| Single-call, stadio Gap, estrazione, bozza | — | **identici alla v6**: formato di messaggio 2, `RunStagedAsync`; verificati da RH-010 |

**Tabella `leaf-projection-1`** (decisione D2). Testo = `Statement` letterale; evidenze = la voce. Classificazione per origine: Message ⇒ `Declared`, Document ⇒ `Documented`. La **certezza** dell'evidenza (`EvidenceCertainty`, `Evidence/EvidenceEntities.cs:52-58`) prevale sul tipo:

| Condizione (in ordine) | Sezione | Classificazione |
|---|---|---|
| tipo Gap | openQuestions | per origine |
| certezza `ToClarify` | openQuestions | per origine |
| certezza `Hypothesized`, oppure tipo Assumption | assumptions | `Assumed` |
| tipo Alternative | openQuestions | per origine (mai sezione strutturale: `EvidenceCoverage.cs:145-156`) |
| certezza `Inferred` | sezione del tipo (righe sotto) | `Inferred` |
| tipo Goal / Context / Requirement / Action / Constraint / Exclusion / Dependency / Decision / Other (certezza `Declared`) | goals / scopeOverview / requirements / flows / constraints / scopeExcluded / dependencies / requirements / scopeOverview | per origine |

Chiave locale dell'item proiettato: `ReconcileAliasComposer.LocalKey(request, "p" + n)`, con *n* = posizione 1-based della voce in `request.Entries`.

Nota per D2: gli item di `openQuestions` (quindi anche Alternative e `ToClarify`) sono offerti allo stadio Gap come riferimenti `i<n>` (`GapStageInput.cs:92-93`). La domanda canonica di ciascun Gap resta quella che cita il Gap.

## 5. Compatibilità delle definizioni storiche

- v2–v10:
  - prompt, profili, DTO, `configHash`, piani, attese e messaggi invariati byte per byte (golden esistenti in `RetrievalGapFlowTests`, `ReconcileSchemaProfileV9Tests`/`V10Tests`, `ReconcileDefinitionV8Tests`/`V9Tests`/`V10Tests`);
  - `CheckResult` con regole `V1`;
  - assembly invariata;
  - nessun output Committed reinterpretato.
- P-1 si applica a tutte le definizioni perché produce **gli stessi numeri**. È verificato dal golden della sequenza `Tokens`/`PayloadHash` di ogni misura su v6 e v10 (RH-030); nessun `batchId`/`inputHash` cambia.
- Run v6 già congelate restano v6: nessuna migrazione e nessun import incrociato; manifesti T040 e WorkOutput congelati non toccati.
- La v11 non sostituisce la v6: coesistono.

## 6. Coesistenza, verifiche reali e attivazione

1. Fino a RH-060 la v11 esiste solo nel codice e nei test. Nessuna richiesta nuova la congela.
2. Prima di RH-060 servono le verifiche reali RH-050 e RH-051 (costituzione VIII), autorizzate a parte (D8) e superate.
3. RH-060 (approvazione D1): `SourceSnapshotStore.DocumentaryAnalysisFreeze(useRetrieval: false)` restituisce la v11. Con recupero resta la v10: 003 e manifesto T040 v6 non sono toccati.
4. Rollback: ripristinare in RH-060 la v6 come default (una riga, solo nuove richieste). Le analisi v11 già congelate restano eseguibili.
5. Branch (D9): il codice di `expansion-grid-1` esiste solo su `feature/003-retrieval-evidence-search`, quindi il branch di lavoro deriva da `c76c6c4`. Il merge verso `feature/002-implementation` resta decisione dell'autore. Nessun merge su `main`, nessun push senza autorizzazione.

## 7. Strategia di verifica (progressione economica)

| Livello | Contenuto | Task | Costo |
|---|---|---|---|
| 1. Contratti e invarianti | tabella di stadio ⇔ schema ⇔ compositore; regole V1/V2; proiezione; identità della v11; single-call e instradamento v11 = v6; golden v2–v10 | RH-002, RH-010…RH-015, RH-020 | offline |
| 2. Proprietà | generatore di istanze dello schema per variante: ogni istanza ammessa dalla grammatica v11 è accettata oppure rifiutata solo con una **classe residua dichiarata** (RH-016). L'elenco e la frequenza delle classi residue sono il criterio d'ingresso dell'opzione O1 (§12) | RH-003, RH-016 | offline |
| 3. Replay di output reali | per batch, con il costruttore di fixture: esiti v10 di caratterizzazione (030 rifiutata; 024 senza domanda per E4; 022 del Run 4 con E1 non citata); esiti v11 sugli output reali già validi per la grammatica v11 (030 accettata) | RH-001, RH-006, RH-017, RH-022 | offline |
| 4. Workflow con modello finto | PostgreSQL e MAF 1.26.0 reali, gerarchia v11 completa fino allo stadio Gap. **Prova strutturale**, non di conservazione semantica: il modello finto è scritto dal gold. Include inflazione dei candidati (≥ 30 per foglia): esito completo oppure limite tipizzato, mai un successo parziale | RH-041 | offline |
| 5. Fault injection | crash a ogni confine, takeover, replay senza inferenza, deadline, limite di chiamate | RH-042 | offline |
| 6. Misure prestazionali | contatore di richieste del tokenizer finto: meccanismo e baseline v10 e v6; P-1 con token identici | RH-004, RH-030, RH-031 | offline |
| 7. Verifiche reali piccole | sonda della grammatica v11 (≤ 4 richieste); ≤ 10 batch reali ricostruiti dalle fixture; esame del PM degli item proiettati | RH-050 | **autorizzazione separata (D8)** |
| 8. Prova integrata | una run v11 reale sulla fixture ERP/EDI | RH-051 | **autorizzazione separata (D8)** |

Il piano non presuppone la ripetizione di T040: riguarda il percorso con recupero ed è sospeso.

**Distinzione obbligatoria.**
- I livelli 1–6 dimostrano affidabilità **strutturale** e assenza di regressioni, **non** la correttezza semantica dell'analisi.
- Conservazione dei conflitti, qualità di descriptor e candidati, adeguatezza della tabella di proiezione e assenza di conflitti spuri si misurano solo ai livelli 7–8, con gold annotato prima.

## 8. Rischi e rollback

| Rischio | Mitigazione | Rollback |
|---|---|---|
| Testo verbatim degli item nelle analisi gerarchiche, diverso dalla single-call | D2; esame del PM in RH-050/RH-051 prima dell'attivazione | default a v6 (RH-060) |
| Tabella di proiezione inadatta (Decision, Other, Alternative in `openQuestions`) | D2; test esaustivo su tipo × origine × certezza; `EvidenceCoverage` sulla fixture | nuova tabella = nuova versione di `leaf-projection` |
| Esclusione degli estremi dei conflitti `Open` dalle sezioni (D2b) | regola deterministica limitata (unica evidenza, nessuna patch accettata); asserzione D3 = 0 in RH-041 | D2b respinta ⇒ comportamento di v6–v10 (avviso D3 deterministico) |
| **R-D2b** (registrato il 2026-11-13, prima di M1): la regola D2b toglie dalle sezioni l'item proiettato la cui unica evidenza è estremo di un conflitto `Open`. Se quell'evidenza è un **Gap**, sparisce anche la sua domanda canonica da `openQuestions`, e `GapStageReferences.From` (`GapStageInput.cs:92-110`: cerca il Gap solo nella mappa degli item di `openQuestions`, mai nelle alternative dei conflitti) lo dichiara mancante ⇒ `coverage_incomplete gap_reference_missing`, prima dello stadio Gap. I candidati Gap↔evidenza nella gerarchia sono osservati (Run 4 029; Run 5 027, E255–E4); se la disposizione sia `Open` non è noto | **Regola R-D2b-1 ADOTTATA in M2 (2026-11-14, condizioni soddisfatte: test (b)1–(b)12 scritti per primi con il negativo (b)7 osservato; D2b-1/2/3 dell'autore; vedi «Evidenze M2»)** — testo della regola come formulato a M1: «Un'evidenza di tipo Gap deve conservare la propria domanda canonica in `openQuestions`, anche quando partecipa a un candidato con disposizione `Open`.» **Verifica a codice** (2026-11-14, solo lettura): (1) il difetto è reale: `GapStageReferences.From` cerca il Gap solo nella mappa degli item di `openQuestions` (`GapStageInput.cs:92-110`) e il piano rifiuta con `gap_reference_missing` (`DocumentReconcilePlan.cs:864`); (2) con l'eccezione, per ogni Gap estremo di un candidato `Open` esistono due item con testo (`Statement`), classificazione (per origine, in entrambe le regole) e insieme di evidenze **identici**: la `Coherence` esatta di alias-2 (`AnalysisAliasComposer.cs:355-364`, «le alternative di un conflitto vanno solo in `conflicts[].alternatives`») li rifiuterebbe, ma vale solo per la single-call: `DocumentReconcileAssembly` e `DocumentAnalysisContent.Check` (`:294-336`) non la applicano; (3) l'avviso D3 `item_repeats_conflict_alternative` (`EvidenceCoverage.cs:1075-1104`) scatta quindi **sempre** per quel Gap, una volta per ciascuna alternativa identica (due conflitti ⇒ due avvisi), ma è `SemanticRisk` e mai bloccante (`Blocking` solo per `DeterministicError`, `:827-830`). Nessun invariante **bloccante** è violato: è una **deviazione consapevole** dal principio di §13.4, limitata ai Gap nella gerarchia v11, con avviso informativo. **Condizioni per renderla definitiva (prima di RH-021):** (a) i test (b)1–(b)12 di RH-021 scritti per primi, col negativo (b)7 osservato come dichiarato; (b) decisioni D2b-1, D2b-2 e D2b-3 dell'autore (§11). Alternative scartate: far leggere a `GapStageReferences` anche le alternative (contratto 003, D7/O4); sopprimere l'avviso D3 per i Gap (modifica di `EvidenceCoverage`: solo con decisione esplicita, mai in silenzio). Test: RH-021(b), RH-022(c), RH-041 scenario A, in [tasks.md](tasks.md) | rinuncia alla D2b per i Gap = comportamento v6–v10 |
| `anyOf` con `enum` non applicato da llama.cpp b10703 nel confronto | elenco chiuso delle parole chiave; sonda RH-050; difesa in profondità nel compositore | profilo senza `anyOf` (solo validatore), I-2 resta residuo |
| Contaminazione fra stadi con il prompt unico ripulito | sezioni per stadio; misura in RH-050 (candidati delle foglie B confrontati con i 16+16 della v5) | opzione O2 (§12) |
| Violazioni residue fatali (alias fuori batch, duplicati, testi di soli spazi) | tipizzate e misurate (RH-016, RH-050) | opzione O1 (§12) |
| Costo delle misure (≈ 11 min per run e per ogni ripresa) solo ridotto del 13% da P-1 | RH-004 misura il meccanismo | opzione O3 (§12) |
| Costo delle espansioni ripetute e saturazione | misurati da RH-005 | opzione O5 (§12) |
| Candidati Gap↔evidenza nella gerarchia (conflitti «domanda contro risposta») | registrato | opzione O4 (§12) |

## 9. Criteri di accettazione dell'intervento

1. Livelli 1–6 verdi. Suite backend completa (PostgreSQL di test e parser reale): 0 falliti; ignorati invariati salvo i nuovi test reali opt-in.
2. Golden v2–v10 invariati; nessun test storico indebolito.
3. Single-call v11 byte per byte uguale alla v6; instradamento oltre budget v11 uguale alla v6 (RH-010).
4. Replay: la 030 del Run 5 è accettata dalla v11; le fixture 024 (Run 5) e 022 (Run 4) mostrano i due blocchi latenti in v10 e nessun blocco con la proiezione v11.
5. Proprietà: le istanze ammesse dalla grammatica v11 sono accettate o rifiutate solo con classi residue dichiarate; la tabella delle classi è registrata.
6. Workflow (RH-041): run `Completed`; ogni evidenza produce un item proiettato oppure un'alternativa; `MissingGaps` vuoto; nessuna `evidence_not_mapped`; avvisi D3 = 0 **per le evidenze non Gap** con D2b (per ogni Gap estremo di un conflitto `Open` l'avviso D3 è atteso e non bloccante, R-D2b-1); nessun WorkOutput duplicato.
7. P-1: token identici su v6 e v10; `/tokenize` ridotte esattamente di (misure con schema − schemi distinti).
8. Verifiche reali RH-050 e RH-051 superate **prima** di RH-060.
9. Documenti: emendamento v11 in `ai-workflow.md`; task riportati nel `tasks.md` ufficiale dopo il mandato (D11); nessuna voce T040/T027/T150 dichiarata superata.

## 10. Dipendenze della slice 003

- La 003 usa l'analisi assemblata dalla gerarchia (domande canoniche, conflitti) prima di recupero e stadio Gap. I difetti I-1…I-6 valgono anche per la v10. Il percorso con recupero ne beneficerà solo con una definizione **v12** = v10 + `reconcile-stage-contract-1`, che richiede l'emendamento di `contracts/candidate-source.md` §4 e un'**autorizzazione separata** (D7).
- P-1 riduce anche le richieste della misura dei costi del recupero (`DocumentReconcilePlan.ExecuteRetrievalAsync`/`MeasureStageAsync`) senza cambiare numeri né contratti 003.
- Manifesto T040 v6 (`871e884c…0599`), harness isolato e test 003: invariati. RH-060 cambia solo il default **senza** recupero.

## 11. Decisioni che richiedono approvazione

| ID | Decisione | Raccomandazione |
|---|---|---|
| D1 | Introdurre `project-analysis-documents-v11` e renderla default delle nuove analisi **senza recupero** (RH-060), **dopo** RH-050/RH-051. Non riapre T040: la chiusura del ciclo T040 vietava una v11 *per T040* | approvare |
| D2 | Confine di responsabilità: item delle foglie della vista A composti dal server (testo verbatim, tabella §4 con la certezza). Costituzione IX: il committente può chiedere un ADR; la motivazione è nell'assessment | approvare, tabella inclusa |
| D2b | Un estremo **non Gap** di un conflitto `Open` compare solo come alternativa (regola §3, Assembly). **Condizionata al rischio R-D2b** (§8): la regola come scritta farebbe sparire la domanda canonica di un Gap. Regola candidata R-D2b-1: un Gap conserva sempre la domanda canonica in `openQuestions`. **DECISA dall'autore il 2026-11-14 (gate M2 chiuso) e implementata in M2 (RH-021).** **D2b-1 approvata** (un Gap conserva la propria domanda canonica in `openQuestions`, provenienza integra); **D2b-2 approvata e resa DEFINITIVA lo stesso giorno**: `Gap` conserva sempre la domanda canonica in `openQuestions`; `ToClarify` conserva la rappresentazione dell'elemento ancora da chiarire in `openQuestions` (mai eliminato, né requisito acquisito, né risolto perché alternativa); `Alternative` estremo di un conflitto `Open` è rappresentata soltanto come alternativa del conflitto, salvo sia anche `ToClarify` o `Gap`; `Alternative` `Hypothesized`, proiettata in `assumptions`, è esclusa dalla sezione ordinaria se estremo contestato (una lettura intermedia, «mai escluso un item di `openQuestions`», è stata sostituita da questa); **D2b-3 approvata solo come comportamento transitorio**, per `Gap` e per `ToClarify` conservato (nessuna disattivazione generale dell'avviso D3, nessun indebolimento di `EvidenceCoverage`, nessuna deduplicazione silenziosa, nessuna presentazione della domanda come decisione approvata; l'avviso è un finding atteso e non bloccante nel caso specifico, distinguibile da una ripetizione indesiderata perché riguarda sempre una domanda di `openQuestions` di un Gap o ToClarify, e la duplicazione resta un rischio semantico da verificare con documenti realistici prima della promozione della v11 a default). **Classificazione delle alternative (v11-only, M2):** l'alternativa di un candidato `Open` porta la classificazione della tabella `leaf-projection-1` (un'ipotesi resta `Assumed`, una deduzione `Inferred`), non più la sola origine; v4–v10 invariate. Vincolo: un candidato che collega una Gap a un'evidenza potenzialmente risolutiva non dimostra una contraddizione fra requisiti; la risposta al Gap appartiene allo stadio Gap. Testo originario delle sotto-decisioni, preso prima di RH-021 con i test (b)1–(b)12 scritti: **D2b-1** eccezione per i Gap (R-D2b-1); **D2b-2** item non Gap con sezione `openQuestions` (certezza `ToClarify` o tipo Alternative), estremi di un conflitto `Open`: esclusi come negli altri item (comportamento di RH-021 (b)6, raccomandato: l'evidenza resta visibile come alternativa di un conflitto non risolto) oppure conservati (proposta originaria di R-D2b); **D2b-3** l'avviso D3 per i Gap estremo è accettato come informativo (raccomandato) oppure va soppresso con una modifica esplicita di `EvidenceCoverage` | approvare solo con l'eccezione per i Gap (D2b-1), D2b-2 esclusi, D2b-3 accettato; da confermare dopo C0 e prima di RH-021 |
| D3 | `noFindingsReason` informativo nella v11 (emenda «zero findings richiede motivo», §4.1, per la v11) | approvare |
| D4 | Confronto senza candidati, con esito ed eventuale `Open` motivato | approvare |
| D5 | (ritirata dopo la revisione: il retry è l'opzione differita O1) | — |
| D6 | P-1 (memo degli extra, risultato identico) per tutte le definizioni | approvare |
| D7 | Estensione al percorso con recupero (v12) | rinviare a una decisione 003 separata |
| D8 | Verifiche reali RH-050 e RH-051 | autorizzare separatamente dopo M4 |
| D9 | Branch di lavoro derivato da `feature/003-retrieval-evidence-search@c76c6c4` | approvare |
| D10 | Opzioni differite O1–O5 (§12) | nessuna adozione senza il criterio d'ingresso soddisfatto |
| D11 | Riportare i task approvati nel `tasks.md` ufficiale della 002 come `Txxx` (regola «unica baseline operativa») | approvare insieme al mandato |

Se D2 è respinta si applica A+ (assessment §9): RH-023 sostituisce RH-020 e RH-021. Avverte prima, ma non recupera: la violazione resta fatale.

## 12. Opzioni differite, con criterio d'ingresso

| ID | Opzione | Criterio d'ingresso | Vincoli di progetto già noti (dalla revisione) |
|---|---|---|---|
| O1 | Retry informato unico per le classi residue | ≥ 1 occorrenza reale di una classe residua in RH-050/RH-051, oppure classi residue frequenti in RH-016 sotto un modello di errore realistico | (a) solo per confronto, riduzione e cross: foglie ed espansioni sono impacchettate al massimo prefisso con 256 token di riserva e il payload del retry, con la prima risposta, non entra (`DocumentBatchPort.cs:391-406`); (b) marcatore con ordinale fuori dal cursore, come `DocumentEvidenceAgent.cs:454-467` (1.000.000 + ordinale): `GetByOrdinalAsync` sceglie per ordinale; (c) semantica T137: una risposta valida del retry persa con un crash fa fallire il run (`DocumentAnalysisGuidedRetryTests.cs:1084-1110`); (d) registrare la variante dello schema prima della misura del retry (`AgentFor`, `DocumentReconcilePlan.cs:268-283`); (e) prima risposta codificata come in `DocumentAnalysisAliasPlan.cs:308-317`; (f) margine per i retry nel precheck dell'espansione (`ReconciliationCandidates.cs:361-369`); (g) `ValidateFinalAsync` nel port; (h) `ReconcileViolationKind.IntegrityMismatch` (solo replay, fail-closed) non è mai una classe correggibile dal modello e non entra mai nell'insieme dei motivi del retry |
| O2 | Istruzioni di sistema per stadio (agenti per variante) | contaminazione osservata con il prompt unico v6 in RH-050 | misuratori consapevoli dello stadio; le istruzioni restano nel sistema e non nel messaggio con il materiale non fidato |
| O3 | Pagine del catalogo per numero di voci e Pack con ricerca del confine (solo una nuova definizione) | RH-004 conferma che le misure delle pagine sono ≥ 30% delle richieste e la scomposizione è riconciliata con la prova isolata | cambia gli hash delle pagine e i confini delle foglie: solo in una definizione nuova |
| O4 | Riferimenti `openItem` dello stadio Gap composti dal server; esclusione dei Gap dai candidati della gerarchia | decisione su `gap-stage-alias-1` (003) | contratti 003 |
| O5 | Copertura delle coppie condivisa fra espansioni | RH-005 mostra il precheck violato con ≤ 2 figli per blocco | nuova versione dell'algoritmo |
