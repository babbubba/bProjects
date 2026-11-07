# Quickstart di validazione — 002-project-documents

## Revisione T130/N4 — guida futura, non eseguita (2026-11-07)

Baseline codice 65a9204: v4, non implementa ancora [AI §13](contracts/ai-workflow.md#13-revisione-approvata-t130n4--a--c-minima-2026-11-07). Prima completare i nuovi task della baseline tasks.md solo con mandato di sviluppo. Nessun test/inferenza autorizzato dalla revisione documentale.

Gate anticipato T138, prima della implementazione completa del raccordo gerarchico v5: prova offline mirata sul percorso reale degli attuali batch leaf/reduce/Cross con SDK MAF 1.26.0 e PostgreSQL reali, client modello simulato e output controllati. Collocare Gap e riscontro in porzioni diverse e verificare nei payload effettivi che il batch valutatore riceva simultaneamente entrambi gli originali integrali con provenienze risolvibili, WorkOutput/checkpoint persistiti e ripresa coerente; registrare percorso, batch, input/hash e numero/tipi di chiamate in `validation/T130-N4-revision.md`. Due riferimenti nei DTO non bastano. Se il grafo esistente non garantisce il nominale senza nuove chiamate o fasi, fermarsi prima della implementazione completa e segnalare il limite per decisione architetturale, senza introdurre autonomamente stage o chiamate LLM. SC-004, limiti e gold restano invariati.

T142 verifica specificamente retry informato, valutazioni Gap, casi avversi e stabilità v5 con almeno tre N4 seriali conformi; T067 mantiene la verifica finale integrata I4 (N1–N5, browser reale, provenienze, conferma PM e bozza). Riutilizzare evidenze verificabili comuni con riferimenti puntuali a run/output/hash/configurazione ed esiti quando soddisfano integralmente entrambi i task, senza duplicare inferenze DGX né ridurre criteri, numero di prove N4 o livello di verifica. T142 resta obbligatorio; T082/T083 restano gate I5 separati e non condizionano la chiusura I4.

1. Dopo implementazione, scoprire i test con il runner MTP già documentato in CLAUDE.md; eseguire DET e integrazioni PostgreSQL+SDK MAF reali con client HTTP modello finto per codec, completezza Gap, retry al limite/oltre input, errori misti, budget/deadline, fencing/crash/replay e hash. Classi esistenti da estendere: DocumentAnalysisGuidedRetryTests, DocumentAnalysisAliasWorkflowTests, DocumentReconcileAliasWorkflowTests, DocumentWorkflowRoutingTests, DocumentCoverageTests; verificare i nomi effettivi nella discovery, non dichiarare PASS per filtro vuoto.
2. Con DGX solo su mandato separato, configurazione registrata e slot seriale: N4 originale tre volte, tutte con E2 riscontrata da E3 e conflitto Open E1/E3 45/Declared contro 30/Documented, senza duplicazioni ordinarie o scelta automatica; poi avversi con prima proposta errata per T142; regressioni N1/N2/N3/N5 nel gate integrato T067, riusando le evidenze comuni come sopra. Non cambiare corpus/gold/asserzioni; errore gestito resta FAIL nominale. Conservare 180 s e giudizi di qualità separati dalla durata.
3. Gerarchia: Gap e riscontro in porzioni diverse, confronto con originali, Open NoAnswer/Partial/Ambiguous, conflitto distante, restart/checkpoint/tetti e coverage-3; nessuna nuova chiamata di riparazione. Prove oltre contesto T082/T083 e relativi corpus restano invariati e richiedono propria autorizzazione; non sostituirle con piccolo fixture.
4. Browser reale: coverage-3 consultabile con riferimenti apribili e motivi, tastiera/1440/390, distinzione revisione AI/edit PM; conferma e bozza mantengono conflitto e fonti. Report v1/coverage-2 storici leggibili senza retrocompilazione. Nuove evidenze in `validation/T130-N4-revision.md` solo dopo prove; non sovrascrivere I4-real/sprint2-handoff o catture originali. T067 e gate umani restano aperti fino alle prove richieste.

Guida per la futura implementazione, non istruzioni già eseguibili integralmente sul codice 001. Nessuna prova eseguita durante plan; nessun PASS dichiarato. Non eseguire adesso migrazioni, suite AI o campagne 001. I test/servizio parser indicati devono essere realizzati nei successivi task autorizzati.

## Prerequisiti e ambiente

.NET 10, Node compatibile col frontend esistente, Docker Compose, PostgreSQL 18 isolato e DGX interno autorizzato. Account PM e Admin predisposti con bootstrap esistente, segreti in `infra/.env` ignorato. Volumi DB e documenti separati dal pilota e mai `down -v` sui dati del pilota. Build parser da lock completo e pesi verificati/licenze disponibili, rete runtime offline. Comandi dalla root repository salvo indicazione. Leggere i [contratti](contracts/http-api.md) e il [modello dati](data-model.md) prima delle verifiche.

Comandi di base già presenti:

```powershell
dotnet restore backend/bProjects.sln --locked-mode
dotnet build backend/bProjects.sln --no-restore
dotnet test --solution backend/bProjects.sln
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix frontend run test:ci
docker compose -f infra/compose.yaml config -q
```

Dopo l'implementazione del servizio `parser` e delle migrazioni:

```powershell
docker compose -f infra/compose.yaml build parser
docker compose -f infra/compose.yaml up -d --wait db parser
docker compose -f infra/compose.yaml run --rm app migrate
docker compose -f infra/compose.yaml up -d --build --wait app web
```

I nomi db/app/web sono quelli dello stack corrente; parser è nuovo. Il deployment reale ferma app e prende backup coerente DB+storage prima della migrazione; questa sequenza è destinata a uno stack di test isolato, non autorizza modifiche al pilota.

## Matrice dei controlli

| Livello | Prove da realizzare | Evidenza richiesta |
| --- | --- | --- |
| Deterministico .NET/fake LLM | Quote e receipt, idempotenza, XOR/FK e schema dispatch 1.0/1.1, span UTF-16 con caratteri non BMP, divisione tabelle/range, no perdita alias/item, payload completo entro budget, schema/quote inventati rifiutati | Output runner, fixture/gold e hash; fake non è qualità del modello |
| Parser reale senza LLM | PDF/DOCX/MD/TXT golden, tabelle multipagina, header/condizioni, negazioni/numeri, code fences, righe/locator, corruzione/password, PDF misti/scansioni, zip bomb/traversal, rete bloccata, kill per timeout | Versioni e hash Docling/pesi, manifest e confronto passaggi; nessun testo integrale nei log ordinari |
| PostgreSQL reale | Upgrade con dati 001, cutoff zero document-only, nuova versione durante run, selezioni concorrenti, quota upload concorrente, crash fra rename/DB, fence obsoleto, claim singolo slot, checkpoint/output replay | DB isolato, hash prima/dopo, nessun duplicato, nessuna riscrittura storica |
| MAF .NET reale/fake modello | Costruzione grafi nuovi con binding 1.26.0, serialize/checkpoint/resume processo nuovo, owner ChatTurn/DocumentJob, topologia/hash incompatibili | Distinguere SDK reale da LLM finto, nessuna API supposta |
| DGX reale seriale | Tokenizer/finestra per modello fase, output/reasoning, oltre contesto, conflitti cross-pacchetto, latenza con preparazione concorrente | Endpoint interno, modello/config runtime reali, token provenance, tempi e tutti i FAIL/SKIP |
| Browser PM | Upload/lista/download, scelta versione/esclusione, chat citata, analisi/edit/conferma/Markdown, stato errori/retry, coverage, refresh/restart, tastiera 1440/390 | Playwright offline separato da reale, revisione due PM e tecnico |

Comandi suite esistenti da estendere con i nuovi test, non nuove categorie ipotetiche:

```powershell
# BPROJECTS_TEST_POSTGRES deve puntare al DB test isolato
dotnet test --solution backend/bProjects.sln --filter-trait "Category=Integration"
# BPROJECTS_TEST_DGX_ENDPOINT e configurazione interna documentata nel harness esistente
dotnet test --solution backend/bProjects.sln --filter-trait "Category=MafReal"
npm --prefix frontend run test:e2e
# Nuovo comando previsto nel servizio parser dopo l'implementazione dei test
docker compose -f infra/compose.yaml run --rm parser python -m pytest
```

Variabili mancanti/test ignorati = SKIP, mai PASS. Il quickstart 001 rimane riferimento per nomi completi delle variabili di autenticazione DGX e Playwright/stack; non stamparne i valori. Verificare raccolta/discovery dei test nuovi: una categoria senza casi non vale superamento.

## Scenari nominali e guasti

1. Quattro formati, almeno cinque casi come SC-003: preparare attese con requisiti, numeri/unità, negazioni, esclusioni, condizioni/eccezioni, riferimenti e conflitti. Caricare, consultare, scaricare e confrontare SHA-256. Verificare pagina/sezione reale, nessuna pagina inventata per DOCX/MD/TXT. Tutte le evidenze sono nel medesimo ledger.
2. Caso oltre contesto singolo: testo estratto >2× budget input effettivo del modello Analysis, entro quote; almeno 12 fatti applicabili distribuiti fra inizio/metà/fine. Includere una contraddizione fra punti distanti e condizioni che qualificano una regola lontana. Domande chat su tre zone, poi Analizza, correzione/conferma e Markdown. Nessuna omissione sostanziale rispetto alle attese; verificare anche che analisi ampia e generazione multipasso non collassino in una sola finestra.
3. Caso oltre contesto aggregato: almeno tre documenti, totale >2× budget; fatti complementari e conflittuali in file diversi più dichiarazione PM successiva. Nessuna scelta automatica della fonte più recente. Mantenere runtime/finestra invariati, nessuna riduzione documenti per ottenere PASS.
4. Versioni storiche: confermare analisi su v1, generare bozza, caricare v2 e cambiare esclusioni mentre un'altra elaborazione è attiva. V1 e relativi parse/estrazioni/citazioni restano identici e consultabili; run già accettato usa il proprio snapshot. Nuova analisi usa v2 quando Ready salvo pin v1; vecchia bozza non cambia. Un'esclusione persiste su v3.
5. Parziale: PDF misto con porzione informativa non leggibile → Incomplete localizzato, Analizza impedita se incluso. Escludere esplicitamente consente lavorare sulle altre fonti con manifest esclusioni, ma non supera il nominale originario. Chat segnala l'indisponibilità senza inventare risposte documentate.
6. Guasti: AI offline dopo upload, parser kill, backend kill prima/dopo file rename e commit, perdita lease prima di pubblicare unità/bozza, retry con stessa chiave e payload diverso. Nessuna falsa acquisizione, nessuna scrittura stale, zero duplicati, ultimi risultati conservati e deadline non azzerata.
7. Sicurezza: anonimo/ruolo errato, ID altro progetto, path traversal, macro/HTML/link esterni, prompt injection, archivi espansi e quote concorrenti. Nessun fetch/runtime download o contenuto completo nei log; analisi progetto A non usa fonti di B pur visibili allo stesso PM.
8. Percorso senza documenti: regressioni pertinenti 001 (FIFO U1/U2/A1, conferma/generazione, edit attribuiti, recovery/streaming) e decodifica/revisione/generazione da 1.0.0. Non attribuire ai nuovi test la chiusura T052/T053 o una nuova accettazione generale della 001.

## Registrazione degli esiti e gate

Risultati negli artefatti di verifica della 002 durante implementazione; non scrivere in `docs/acceptance-trace/` e non creare ora un registro parallelo o task. Ogni esecuzione riporta dataset/hash, build, parser/pesi, modello/finestra/tokenizer, configurazione di fase, concorrenza, tempi upload/preparazione/coda/chat/analisi/documento, tentativi, coverage e giudizio PM/tecnico.

Rilevare total input, istruzioni/schema/framing e output totale con reasoning: confronto conteggio preflight vs usage runtime, valori non misurabili marcati estimated/unavailable. Una copertura di ID 100% non dimostra SC-003/004/005: verificare semantica sulle attese e citazioni. Un limite raggiunto deve produrre esito esplicito, ma non vale successo del caso nominale.

Accettazione 002 solo dopo prove reali oltre contesto, percorso browser e revisione umana SC-001–SC-012. Profili N/E fissati prima della prova e distinti dal regime operativo: N richiede risultato completo valido e tutti i criteri qualitativi entro 180 s dal comando, coda/preflight/retry inclusi; E entro hard cap iniziale 3600 s dal medesimo t0, preparazione separata. Un N promosso tecnicamente resta N: a 150 s con tutti i criteri soddisfatti PASS, oltre 180 s FAIL temporale anche se funzionalmente Completed. Nessuna sottrazione ex post o riclassificazione del dataset per nascondere la latenza, nessuna attestazione 001 o accettazione per soli fake/error handling.


## Prove mirate del consolidamento F1–F8 (da realizzare)

- F1/F7: clock controllato per Ordinary t0+180, Extended t0+3600 e singolo cambio tecnico prima della scadenza. Una promozione a t0+110 motivata da ricevuta additional_work valida lascia hard deadline t0+3600, mai t0+3710. A 120 s queued/running senza tale ricevuta non promuove. Figlio nato a t0+3500 non ottiene altri 300 s oltre il padre; timeout, tentativi, coda e heartbeat distinti. Nessuna promozione dopo terminale/scadenza, reset al restart o estensione da modifica Admin. Retry tecnico mantiene root/deadline; nuovo comando PM dopo terminale è richiesta distinta collegata, senza alterare esito e limite originari.
- F2: nuovi grafi MAF 1.26.0 con feedback condizionale esclusivo e più passaggi nello stesso executor InvokeBatch, sessione nuova per invio secondo AI §5; output intermedi di lunghezze diverse producono DerivedBatch bounded senza nuovi nodi/archi. Crash dopo WorkOutput prima del checkpoint → medesimi batch/result hash, nessuna reinferenza di output committato e nessun doppio effetto. Restore dei cursori/passate da processo nuovo. Se la release fissata non supporta la composizione, FAIL gate prima dell'implementazione del resto, non nuovo engine.
- F3: due worker provano a pubblicare l'aggregato; solo owner aggregatore corrente con root vivo può inserire EvidenceExtractionVersion+Ready+eventuale current bundle. Worker scaduto anche con output valido rifiutato. Job unità non può promuovere; unità fallita/issue nota impedisce aggregato completo. Replay dopo commit e prima della risposta recupera stesso risultato.
- F4/F5: golden di payload evidence-1.1.0 e righe legacy 1.0, schemi chiusi, nullabilità e source/alias distinti; messaggio assistant non Primary. Passaggio di tabella fra pagine con header ripetuto e frase multi-blocco → più sourceId nello stesso gruppo, quote verificabili, ordinal/locator originali e offset UTF-16 non BMP. Versioni storiche riaperte dopo promozione mantengono le citazioni esatte.
- F6: prima preparazione v1 corrente, rielaborazione g2 Ready candidata non applicata, promozione CAS un solo vincitore, fallimento g3 lascia g2 corrente. Nuova versione FILE v2 prima Ready applicabile in LatestReady; Pinned v1 e Included=false restano. Snapshot già accettati immutabili prima/dopo promozioni; legacy frontend su viewVersion 1.0 non riceve documenti deformati.
- D2: conflitti gold tra documenti distinti, sezioni lontane, espressioni parafrasate, condizioni/eccezioni e numeri con polarità diverse; verificare riconciliazione trasversale anche quando il primo raggruppamento non li mette vicini. Coverage range/ID 100% con conflitto gold mancato resta FAIL semantico, non PASS. Nessun obbligo all-pairs, nessun indice RAG introdotto.
- D1/UX: PM esegue un solo comando; notifica iniziale/cambio regime e stato persistito, nessun selettore breve/lungo, nessuna conferma tecnica, nessuna attesa dichiarata pari a un'ora. Cambio pagina/browser chiuso non perde esito; perdita evento riconciliata via GET. PlannedUnits mostra soltanto piani realmente derivati; niente percentuali simulate.
- F8: parser senza DB/storage originali/job autorevoli, interruzione ripetibile e risultato tardivo non pubblicabile dal backend scaduto; dati acquisiti e stato rimangono responsabilità backend come ADR.

La telemetria distingue initial/final regime, ricevuta/motivo tecnico della promozione, profilo N/E originario, esito funzionale, target temporale e qualità, coda ed elaborazione effettiva, durata totale dal t0, limiti locali/cap globale, passate/batch e chiamate. Non dedurre tempo attivo dal solo Running o dal lease; intervalli non osservabili dopo crash sono unavailable. Una promozione non determina da sola PASS/FAIL N. Ogni conflitto atteso perso, incompletezza nota nascosta o fonte inventata è FAIL anche entro deadline. Guida futura di validazione, non checklist eseguita o risultato runtime.

## Fixture e confini B1–B3 da verificare nell'implementazione

Queste prove completano gli scenari precedenti; non sono tasks.md, una checklist rivalidata o test già eseguiti. Clock DB controllabile, input/prompt/policy/runtime hash fissati, risposte del fake registrate per batch. Distinguere determinismo della partizione e degli effetti dalla variabilità dell'LLM reale.

### B1 — Classificazione, promozione tecnica ed esito nominale

Fixture tabellare document-regime-v2 con PolicyHash della correzione finale B1, uno scenario per riga e priorità fra condizioni simultanee. Con W=10000, O=2000, M=1000 si ha B=7000: T=6999/7000/7001 e shapeFloorTokens=V−1/V/V+1. T=B e floor=V ammettono Ordinary solo con una chiamata senza dipendenze. Floor ignoto, reasoning senza tetto visibile, conteggio solo stimato o dipendenze tecniche ignote → Extended con unknownReasons; finestra/tokenizer non verificati, B≤0 e input invalido → errore. Variare solo carico/slot o coda non cambia il regime tecnico. Stessi input/hash → stesso outcome/reason. Pending non invia AI; piano Ordinary classificato a 130 s ha 50 s residui, a 190 s fallisce senza diventare Extended per sola attesa. Piano strutturalmente multipasso valido, anche oltre contesto → Extended automatico con prova piano/budget, senza scelta PM.

Clock a 119.999/120.000/179.999/180.000 s; nessuna soglia temporale è un motivo di promozione. Fixture di ricevuta additional_work: trigger WorkOutput Committed del medesimo root, obbligazioni residue reali, batch distinto necessario senza cache hit, input/genitori/hash e preflight validi. Ricevuta assente, solo booleano AI, riferimenti stale/fuori snapshot, batch già Committed o retry della stessa chiamata non autorizzano promozione. Non inventare errore per far emergere il bisogno di lavoro: JSON/quote invalidi restano permanenti.

| Controesempio | Esito atteso da verificare |
| --- | --- |
| Nominale N senza coda, una chiamata valida, commit a 150 s | Ordinary senza promozione temporale; funzionalmente Completed, PASS N se tutti gli altri criteri soddisfatti |
| N con promozione tecnica valida prima della scadenza, commit a 150 s | Extended come regime; profilo N preservato e PASS se tutti gli altri criteri soddisfatti |
| Nominale N senza coda, risultato disponibile solo a 190 s | Senza motivo tecnico di promozione: Failed a 180 s e nessun commit tardivo. Se già promosso validamente per lavoro aggiuntivo: Completed funzionale ammesso entro hard cap, FAIL temporale N; mai riclassificare E |
| Job interamente in coda a 120 s, stesso piano singola chiamata | Nessuna promozione; queueMs=120000, processingMs=0 se intervalli interamente osservati. Eventuale inferenza a 130 s e commit a 140 s non impongono cambio di regime; l'esito della prova usa il profilo originario |
| Piano iniziale multipasso con batch bounded e dipendenze verificabili | AdmitExtended multipass_or_capacity prima di inviare AI, senza scelta/conferma PM e senza attesa del timer |
| Promozione concorrente con commit finale | Lock/CAS serializzano: commit terminale precedente impedisce promozione; promozione valida precedente aggiorna la deadline letta dal commit. Un solo risultato/riga transizione, nessun lavoro o invio duplicato |
| Promozione concorrente con scadenza | now≥EffectiveDeadlineAt fa fallire; nessuna promozione tardiva. Se CAS valido committato prima della scadenza Ordinary, sweep legge deadline aggiornata senza chiusura erronea a 180 s |
| Retry automatico/reclaim dopo promozione a 110 s | Stesso t0, root, ricevuta, contatori e HardDeadlineAt=t0+3600; nessun nuovo cap dal retry. Cache B2 riusata, chiamata attiva non allungata |
| Copertura incompleta al limite di chiamate/byte o a 3600 s | Failed/Incomplete con frontier/porzioni mancanti, nessun Completed/revisione/bozza parziale; eventuale output tardivo rifiutato |
| Tutti gli output aggiuntivi sono già Committed compatibili | Lookup/guard riusano gli output senza nuova inferenza o promozione per reinferirli; stessi ID/hash e copertura |

Nelle gare usare clock DB letto sotto lock, fence obsoleto e vecchio claim; verificare CHECK DeadlineAt=EffectiveDeadlineAt e atomicità ricevuta/transizione/batch. Restart a 179/180 s non promuove per recuperare tempo; cap esatto a 3600 s impedisce commit. Profili, regimi e giudizi temporali distinti anche con evento perso, browser chiuso e dettagli temporali unavailable. Queste sono attese future, nessun test eseguito.

### B2 — Sessioni, cache e confini di crash

Usare SDK **reale 1.26.0**, fake IChatClient che cattura lista effettiva dei messaggi e numero invii, PostgreSQL reale per commit/claim/checkpoint. Tre batch ciascuno al 60% di B, cumulativamente oltre finestra: sessioni distinte, input effettivo privo dei batch precedenti, token preflight includono istruzioni/schema/framing; niente compattazione/history implicita. Stessa identità workflow/ID executor dopo riavvio e un solo ramo hit/miss attivato.

| Fault injection | Attesa verificabile |
| --- | --- |
| Dopo checkpoint Miss, LLM terminato, prima commit WorkOutput | Reinferenza ammessa con sessione nuova, precedente invio contato, nessun output pubblicato due volte |
| Dopo commit WorkOutput prima checkpoint ValidatePersistBatch | Riavvio dal checkpoint con Miss: guard **interno InvokeBatch** evita ogni invio ulteriore e restituisce output/ID/hash già committati |
| Dopo lookup hit o Advance, prima checkpoint seguente | Ripetizione idempotente, stessa membership/DerivedBatch, nessuna chiamata |
| Dopo primo genitore prima del secondo in un join | Nessun figlio prematuro; dopo tutti i genitori un solo manifest derivato deterministico |
| Lease perso durante inferenza o prima commit/checkpoint | Output/checkpoint/finale/evento stale rifiutati; owner nuovo legge output validi preesistenti, non quelli tardivi |
| Risposta Length, JSON incompleto, source fuori snapshot | Nessun Committed/cache hit/noEvidence, fallimento permanente e copertura residua |
| Checkpoint presente ma output/payload/hash referenziato mancante | Errore esplicito, nessun avanzamento o sovrascrittura tramite reinferenza nascosta |
| Nessun checkpoint ma output committato; finale Completed senza checkpoint finale | Primo caso riparte dall'ingresso nativo riusando cache; secondo legge stesso risultato finale senza avviare workflow |
| Retry PM stesso/differente input semantico o config/runtime | Nuovo root/RetryOfId; import solo compatibile con DerivedFromOutputHash e mappe verificate, altrimenti nessun riuso; vecchio immutato |

Provare anche fallimento dello store checkpoint dopo commit dominio e crash fra riserva invio/rete: la riserva resta consumata, nessuna promessa exactly-once inferenze. Il numero di invii deve restare invariato **nei casi con output Committed compatibile**, non nel caso prima del commit. Prove DGX separate devono poi verificare cancellazione/timeout reale e rifiuto di risultati tardivi; fake+SDK non dimostra il runtime remoto.

### B3 — Cataloghi, livelli e patch

Fixture golden con identità e attese note: tre documenti e note PM, ≥12 fatti, negazione e quantità contraddittorie in prima/ultima sezione di file diversi, eccezione in un terzo file, parafrasi con chiavi primarie diverse, relazione nota fra endpoint lontani, due evidenze senza alcuna chiave. Entrambe le viste producono directory e descriptor complessivi oltre B con almeno tre livelli. Chiavi non primarie condivise devono produrre obbligazioni cross-foglia; correlazioni non letterali richiedono il raccordo gerarchico/Cross. Nessuna dipendenza dall'ordine di completamento dei batch o dal file più recente.

Con fake deterministico: confrontare hash CatalogEntry/Page/Set/Node, union esatta dei membri a ogni livello, pagine senza buchi e visite di tutte le evidenze in A/B; nodo singleton finale riportato senza inferenza, due descriptor irriducibili → errore, livello non decrescente → stallo esplicito. Catalogo non entra in un prompt: paginazione e merge streaming devono conservare tutte le chiavi/relazioni; niente taglio e niente invio del solo primo catalogo. Espansione di candidati Node/Set visita tutti i membri originali; una relazione nota/candidato Evidence confronta entrambi gli originali integrali, senza qualificatori omessi. Candidato con endpoint non identificati resta NotProcessed; un gold non trovato resta FAIL semantico anche se visite al 100%.

Patch fixture sulla **medesima base**: duplicati identici, ID uguale/payload diverso, AddSupport compatibili, SameRule contro Contradicts, Supersedes opposti, alternative incompatibili sul medesimo item, base/target obsoleti, evidenza o source inesistente/fuori progetto. Permutare ordine arrivo/applicazione: stessi hash di risultato o medesimo errore/conflitto, mai ultimo writer vincitore. Incompatibilità preserva base, entrambe le proposte, fonti, polarità, condizioni/eccezioni e decisione PM aperta; invalid/stale necessario impedisce Completed. Retry/replay non duplica proposte o relazioni. Prove DB reali delle unique/FK/fence e nessuna modifica degli EvidenceItem originali.

Esaurimento di livelli (32), candidati (100000), chiamate (512), spazio derivati, hard deadline o nuovo lavoro richiesto dopo unico round finale → coverage_incomplete con frontier mancante, nessuna nuova revisione completa. Distinguere tale fallimento dall'Open semanticamente confrontato e integralmente rappresentato per PM. Con DGX reale ripetere i conflitti gold distanti/inter-documento senza cambiare fonti o finestra; verificare separatamente copertura e fedeltà.

### Warning residuali conservati

W1: resa `[fonte:UUID]` e riapertura di due CitationGroup che condividono header/sourceId, anche dopo edit PM, da verificare end-to-end. W2: misurare coda con owner formale Extended e slot unico (anche chat di altro progetto); nessuna equità/preemption o risposta simultanea promessa, l'app deve restare navigabile. W3: golden parser e qualità dei descriptor/candidati non certificabili dal solo manifest. W4: liste miste 1.0/1.1, coverage-v1 e resume storico con topologia originale, hash dei JSON storici invariati. Queste restano verifiche da pianificare nei task successivi; nessun nuovo filone di progettazione e nessun warning dichiarato superato qui.
