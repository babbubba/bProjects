# Revisione indipendente Checklist: piano consolidato 002

**Purpose**: Valutare criticamente completezza, determinazione e coerenza dei requisiti tecnici prima di generare task; non assumere risolte F1–F8 sulla base delle dichiarazioni del precedente consolidamento.
**Created**: 2026-11-01
**Feature**: [spec.md](../spec.md), [plan.md](../plan.md)
**Baseline**: [consistency.md](consistency.md), preservata senza modifiche.
**Depth**: approfondita, revisione documentale pre-tasks; lettura degli artefatti e confronto con contratti/codice 001 pertinenti. Non è un secondo autore umano né un collaudo runtime: indipendenza del giudizio rispetto alle conclusioni precedenti.
**Review Ownership**: checklist generata con item `[ ]` di proprietà del revisore. La valutazione assistita è esplicitamente richiesta dal committente ed è riportata nelle tabelle; non vengono spuntate automaticamente le caselle.
**Marker Semantics**: `[x]` significherebbe approvazione della qualità documentale, non implementazione o test superati. `$speckit-implement` non modifica i marker. Nessuna modifica alla checklist generica `requirements.md`.

## Criteri originali: completezza e precisione

Gli ID CHK001–CHK018 mantengono corrispondenza uno-a-uno con i controlli originali, anche quando il testo è abbreviato. Riferimenti ai documenti e sezioni nella matrice successiva.

- [ ] CHK001 È documentata la catena inventario→porzioni→Evidence→analisi→bozza, distinguendo copertura e fedeltà semantica? [Completeness, Spec FR-013–FR-015, AI §3–4/6]
- [ ] CHK002 Sono misurabili i nominali oltre contesto senza riduzione delle fonti, aumento della finestra o errore spacciato per successo? [Measurability, Spec SC-004, Quickstart scenari 2–3]
- [ ] CHK003 Sono determinati stati, limiti e azioni PM in caso di elaborazione incompleta, senza falsi completamenti? [Coverage, Spec FR-006/011/014/016, HTTP §Selezione]
- [ ] CHK004 È completa la rappresentazione end-to-end delle citazioni multi-blocco e degli header replicati, sino al documento e alla vista storica? [Clarity, AI §1–3/4, HTTP §Bundle corrente, Parser §Passaggi compositi]
- [ ] CHK005 Sono preservate lettura, modifica e generazione di AnalysisContent 1.0.0 senza conversioni o riscritture implicite? [Consistency, AI §1, Data model §Migrazioni]
- [ ] CHK006 Sono definite conservazione di evidenze/citazioni/checkpoint e compatibilità storica, distinguendole dal rollback ai vecchi binari? [Coverage, Spec FR-018, Data model §Migrazioni]
- [ ] CHK007 Il contratto Evidence 1.1 determina campi, discriminanti, nullabilità, identità e alias senza ricorrere a messaggi fittizi? [Completeness, AI §2, HTTP §Bundle e payload Evidence]
- [ ] CHK008 Sono coerenti congelamento delle fonti, cutoff, retry/resume e aggiornamenti concorrenti? [Consistency, Spec FR-008, Data model §Selezioni/Lifecycle, HTTP §Selezione]
- [ ] CHK009 È univoco il criterio di applicabilità di preparazioni correnti/candidate e relative promozioni nella UI? [Clarity, Data model §Lifecycle F6, HTTP §Bundle corrente]
- [ ] CHK010 Sono distinti e raccordati tutti i livelli temporali, compresi figli, tentativi, lease e deadline root senza reset? [Clarity, Plan §Regimi, Data model §Campi temporali, Parser §Durata]
- [ ] CHK011 È riproducibile la classificazione ordinaria/estesa e il relativo cambio, oltre alla separazione dei profili di accettazione? [Measurability, Plan §Classificazione/§Misure, Spec SC-012]
- [ ] CHK012 È determinata la ripresa dei passaggi dinamici senza modifica della topologia, accumulo incontrollato di contesto o reinferenze vietate dal contratto? [Consistency, AI §5, Data model §Manifesti derivati]
- [ ] CHK013 È identificato un unico proprietario del commit aggregato Evidence con transazione, fencing e idempotenza? [Clarity, Data model §Commit aggregato F3, AI §2.3]
- [ ] CHK014 Sono esplicite acquisizione durevole, riconciliazione DB/filesystem e idempotenza senza atomicità fittizia? [Completeness, Data model §Documenti/upload, HTTP §Documenti]
- [ ] CHK015 Sono definiti isolamento di progetto, sicurezza file, egress e accessi senza nuove ACL o KB fuori scope? [Coverage, Spec FR-021, Parser §Operatività, HTTP §Sicurezza]
- [ ] CHK016 Sono distinti test deterministici/fake, integrazioni reali, E2E e accettazione umana senza PASS dedotti da SKIP? [Acceptance criteria, Quickstart §Matrice/§Registrazione]
- [ ] CHK017 È formalizzata la decisione sul confine parser ai sensi della Constitution IX, senza riaprire l'adozione Docling? [Consistency, ADR-0001, Plan §Constitution Check IX]
- [ ] CHK018 È coerente la separazione chat/Analizza, incluse fonti non pronte ed esclusioni dal contesto derivato? [Consistency, AI §6, HTTP §Selezione, Spec FR-019]

## Controlli aggiuntivi del consolidamento

- [ ] CHK019 Sono determinati input/output e confini dei livelli globali di correlazione, anche con cataloghi oltre contesto e patch sovrapposte? [Completeness, Gap, AI §4–5, D2]
- [ ] CHK020 SC-012 rappresenta le decisioni D1 approvate senza alterare i criteri e le riserve della 001? [Consistency, Spec SC-012, Plan §Regimi, baseline 001]
- [ ] CHK021 Il disegno resta nel perimetro approvato, senza RAG, embedding, infrastrutture ulteriori o dipendenze circolari dimostrate? [Scope, Spec §Perimetro, Plan, ADR-0001]
- [ ] CHK022 Sono disponibili evidenze eseguibili dei nuovi percorsi MAF/Docling/PostgreSQL/DGX, distinte dalla sola qualità documentale? [Dependency, Quickstart §Matrice, Research R5/R9]

## Esito dei 18 controlli originali

Legenda: PASS = criterio documentale verificato; FAIL/BLOCKER = specificazione insufficiente per task affidabili; WARNING = rischio/raccordo gestibile nell'implementazione senza nuova scelta del committente; NOT VERIFIED = evidenza non disponibile. Tutti gli esiti sono del presente riesame, non ereditati dalle caselle o dai conteggi precedenti.

Abbreviazioni: **AI** = [contracts/ai-workflow.md](../contracts/ai-workflow.md), **HTTP** = [contracts/http-api.md](../contracts/http-api.md), **Parser** = [contracts/parser.md](../contracts/parser.md), **DM** = [data-model.md](../data-model.md), **Plan** = [plan.md](../plan.md), **QS** = [quickstart.md](../quickstart.md), **Spec** = [spec.md](../spec.md).

| Controllo | Esito | Evidenza e giudizio specifico |
| --- | --- | --- |
| CHK001 | PASS | Plan §Documenti oltre contesto distingue inventario, estrazione e risultati; AI §3 assegna range proprietari/issue, §4 conserva EvidenceId e §6 separa errori deterministici, rischi stimati e PM. La correttezza semantica non è dedotta dal 100% di ID. La lacuna operativa della correlazione è trattata separatamente in B3, non annulla questa distinzione. |
| CHK002 | PASS | Spec SC-004 richiede singolo documento e almeno tre documenti oltre 2× budget, 12 fatti distribuiti, domande su zone lontane, nessun aumento finestra/riduzione input. QS scenari 2–3 e D2 richiedono conflitti distanti/inter-documento; errore o conflitto atteso mancato non supera il nominale. |
| CHK003 | PASS | Spec FR-006/011/014, HTTP §Selezione e DM lifecycle distinguono Incomplete/Failed/Ready, bloccano Analizza con fonte inclusa non pronta e conservano esclusione esplicita. Parser §Manifest elenca issue localizzate; AI §6 vieta risposte documentate inventate. Nessuna pubblicazione parziale riuscita prevista. |
| CHK004 | WARNING | AI §2.2/3 definisce più sourceId ordinati in citationGroupId, range UTF-16 e esempio b10 p2/b25 p3; Parser §Passaggi compositi mappa copie header all'originale. Raccordo al Markdown e ai gruppi di AnalysisContent non del tutto esplicito: W1. Il requisito di provenienza è soddisfacibile senza cambiare modello di prodotto. |
| CHK005 | PASS | AI §1 impone decoder separati 1.0/1.1 e edit 1.0→1.0; HTTP §Contratti esistenti richiede nuova Analizza per aggiungere documenti; DM §Migrazioni conserva JSON/ID/conferme. Codice 001 AnalysisContent.cs usa realmente converter const 1.0 e JSON chiuso: il dispatch è una necessità correttamente riconosciuta. |
| CHK006 | PASS | DM §Migrazioni e §Lifecycle impongono FK Restrict, bundle storici, checkpoint con definizione propria e backup DB+volume. Il rollback al binario 001 dopo dati 1.1 richiede restore coerente/release compatibile, non viene confuso con la lettura degli storici sul software nuovo. |
| CHK007 | PASS | AI §2.1 definisce output, enum ereditati, CandidateSource discriminata e ruoli; §2.2 determina sourceId, citationGroupId/part, XOR e Content 1.1; HTTP §Bundle/payload definisce viewVersion, envelope misto e 406 per formato legacy incompatibile. Sono specificati old decoder e assenza di messaggi sintetici. W1 riguarda il raccordo editoriale, non la forma primaria Evidence. |
| CHK008 | PASS | Plan §Fonti congela documenti alla richiesta e distingue Evidence PM al primo claim entro cutoff come 001. DM §Lifecycle congela versione file+parse+estratto+revisioni; HTTP impone replay della richiesta prima di ricalcolare selezione. Fonti diventate pronte dopo richiesta non entrano nel retry; upload/promozioni usano lock comune. |
| CHK009 | PASS | DM §Lifecycle distingue ProcessingStatus e ApplicationStatus; state keyed DocumentVersion con due puntatori atomici e Revision; prima Ready auto-corrente per versione file, rielaborazione Ready candidata fino a promozione CAS. HTTP espone currentPreparationId/currentEvidenceVersionId/bundleRevision/candidatePreparations; pin/esclusione restano invariati. |
| CHK010 | PASS | Plan §Autorità temporale definisce t0, effective/hard cap, min timeout figlio/padre, 300 s parser e cleanup entro root, 3 tentativi e nessun reset heartbeat/restart. DM §Campi temporali subordina claim/commit al padre e QS prova t0+120→t0+3600. Il timer della chiamata già avviata non viene allungato. La scelta di quando promuovere resta lacunosa in CHK011/B1, distinta dalle formule di scadenza. |
| CHK011 | FAIL/BLOCKER | Plan §Classificazione usa «output riservato sufficiente», «non può concludersi ... nel tempo rimasto», «capacità ... verificabile» senza regola quantitativa o criterio conservativo vincolante. A t0+120 «può promuovere» permette esiti diversi dagli stessi input. N/E e soglie sono definiti, ma il classificatore richiesto come riproducibile non è ancora implementabile senza scegliere la policy: B1. |
| CHK012 | FAIL/BLOCKER | AI §5 risolve topologia fissa vs dati derivati ma non determina il ciclo di vita della sessione del medesimo agente bound né il percorso di cache hit prima dell'agente nel replay. Il grafo documentato manda PrepareBatch all'agente e legge WorkOutput dopo l'output; «non richiama AI» al replay resta privo di raccordo. B2; B3 per i dati globali derivati. |
| CHK013 | PASS | DM §Commit aggregato elimina il doppio Finalize, assegna unico EvidenceExtractionJob per Preparation, claim solo dopo unità valide, controlli owner/fence/lease/deadline sotto lock e transazione versione/item/Ready/current. Unique(JobId), replay Completed, vecchio owner senza autorità ed evento solo post-commit sono espliciti. |
| CHK014 | PASS | DM §Documenti descrive receipt+quota, stream temporaneo, rename prima della transazione e riconciliazione dopo crash; nessuna conferma prima di acquisizione durevole, nessuna pulizia di file referenziati. HTTP distingue replay, upload_in_progress e conflitto input; assenza di resume byte-level è dichiarata. |
| CHK015 | PASS | Parser §Operatività e ADR limitano rete, filesystem, subprocess, formati, byte espansi e risorse; HTTP isola ogni ID sotto progetto e download privato; Spec FR-021 mantiene tutti i PM senza nuove ACL. Nessuna promozione KB o fetch di link nel parsing. |
| CHK016 | PASS | QS §Matrice separa .NET/fake, parser reale, PostgreSQL reale, MAF/fake e DGX; §Gate richiede nominali reali, E2E e revisione PM/tecnico, SKIP≠PASS. Le prove nuove sono previste e non dichiarate eseguite. Esecuzione effettiva NV1. |
| CHK017 | PASS | [ADR-0001](../../../docs/adr/0001-parser-docling-stateless.md) riporta D3 approvata, responsabilità, alternativa worker diretto DB, conseguenze e ripetizione conversione. Plan Constitution IX lo richiama: risolto il precedente «nessun ADR» per il confine significativo, senza riaprire Docling. |
| CHK018 | PASS | AI §6 visita fonti per domande documentali ma non scrive revisioni formali, conserva stato indisponibile dello snapshot e invalida sessioni/sintesi su cambio fonti. HTTP mantiene comandi distinti; Spec FR-019 vieta analisi/generazione automatiche. La separazione è coerente; isolamento operativo delle sessioni nel ciclo è B2. |

Totale originale: **15 PASS, 2 FAIL/BLOCKER, 1 WARNING, 0 NOT VERIFIED** sul piano documentale. L'assenza di runtime non è stata usata per far fallire criteri che chiedono soltanto di documentare prove future.

## Rivalidazione individuale F1–F8

| Carenza originale | Esito | Verifica effettiva |
| --- | --- | --- |
| F1 — gerarchia temporale | PASS | Root autorevole e figli subordinati, tentativi/lease distinti, cambio prima della scadenza e nessun nuovo t0 automatico: Plan §Regimi, DM §Campi temporali, Parser §Durata, QS prova F1. Resta B1 sulla decisione di cambio, non sui limiti temporali. |
| F2 — topologia/riproducibilità | FAIL/BLOCKER | Topologia fissa e DerivedBatch sono definiti; sessioni/contesto dell'agente e replay prima dell'inferenza non lo sono (B2). Pacchetti di correlazione globali richiedono inoltre B3. |
| F3 — proprietario aggregato | PASS | Unico aggregatore Evidence, niente DocumentJob Finalize concorrente; transazione e fence root/owner espliciti in DM §Commit aggregato e AI §2.3. |
| F4 — evidence-1.1.0 | PASS | Unioni, enum, campi persistiti, ruoli, alias, version dispatch e viste documentati in AI §1–2 e HTTP §Bundle. Non è semplice aggiunta di campi sotto 1.0. |
| F5 — citazioni multi-blocco | WARNING | Rappresentazione atomica multipla, ordine, offset e header originali definiti. Resta W1 sul raccordo finale fra gruppi e markup citato, risolvibile senza decisione di prodotto. |
| F6 — candidati/correnti | PASS | DM §Lifecycle e HTTP §Bundle distinguono versione file da generazione parse/evidence e promozione CAS del bundle; nuovo file Ready vs nuova preparazione candidata non sono più confusi. |
| F7 — regimi/profili | FAIL/BLOCKER | 180/3600, automaticità e dati di misura sono chiari; i predicati del classificatore e della promozione restano indeterminati (B1). |
| F8 — parser/ADR | PASS | ADR-0001 coerente con Parser, Plan e D3; backend unica autorità durevole, niente DB/coda nel parser. |

Totale F: **5 PASS, 2 FAIL/BLOCKER, 1 WARNING**. Nessuna pretesa di chiusura automatica delle caselle di consistency.md.

## Blocker effettivi: controesempi e correzioni necessarie

### B1 — Predicati del classificatore e del cambio di regime non determinati

**Evidenze**: Plan §Classificazione automatica riproducibile (in particolare rivalutazione t0+120); §Misure definisce invece chiaramente i profili; HTTP §Regimi espone classificationVersion/reason senza completare il predicato.

**Controesempio**: richiesta pronta e in una chiamata; a 120 s l'inferenza è ancora attiva, o il job è in coda. Nessuna misura documentata stabilisce se l'output è «sufficiente» o se la capacità di completamento residua è verificata. Un'implementazione promuove sempre, un'altra attende 180 s: entrambe possono invocare il testo attuale, con esiti UX e consumo diversi. Un contatore ClassificationVersion non rende deterministico un predicato non specificato.

**Correzione tecnica richiesta**: tabella decisionale completa per ammissione/classificazione e promozione (inclusi misura mancante, queued/running, chiamata attiva, output shape e timer). O una regola conservativa deterministica senza predizione di durata, o una formula calibrata con fallback esplicito; non lasciare «può» come scelta libera. Specificare anche rapporto fra vecchio DeadlineAt del dispatcher e EffectiveDeadlineAt nel nuovo ramo, per non prolungare il root mentre il guard legacy lo chiude.

Nessuna nuova decisione funzionale necessaria: D1 autorizza già classificazione tecnica e passaggio controllato. Non cambiare 180/60 min né chiedere al PM.

### B2 — Sessione del ciclo MAF e replay prima della chiamata

**Evidenze**: AI §5, grafo Load→PrepareBatch→agente bound→ValidateBatch→feedback, e promessa «non richiama AI» se output dominio già committato; DM §Manifesti derivati. Il codice 001 `AI/AnalysisWorkflow.cs` costruisce agenti distinti per i passi; non è evidenza che un singolo host ciclico isoli automaticamente i turni.

**Controesempi**:

1. Tre batch ognuno entro budget passano nello stesso agente con sessione cumulativa: il payload effettivo potrebbe includere batch precedenti. Il contratto misura i batch ma non definisce sessione nuova, reset o stato incluso nel preflight. Non si afferma che il framework abbia necessariamente questo difetto: manca la scelta contrattuale per impedirlo.
2. WorkOutput è persistito in ValidateBatch, il checkpoint precedente precede l'agente, poi crash. Il grafo ripristinato può raggiungere nuovamente l'agente prima che ValidateBatch rilegga WorkOutput. Non è definita una cache/guard prima dell'inferenza o una rotta di riuso che soddisfi la promessa di non reinferire output committati.

**Correzione tecnica richiesta**: lifecycle di AgentSession per batch/fase e contesto completo conteggiato; punto esplicito di lookup del WorkOutput e routing nativo di cache hit/miss prima dell'agente; stato MAF e output di dominio riconciliati al resume. Aggiornare le prove QS con i due confini di crash e più batch cumulativamente oltre contesto. Non basta il richiamo a primitive native o il futuro test MAF.

Le API del feedback su 1.26.0 sono inoltre NOT VERIFIED runtime, distinto da questa mancanza documentale. Non richiede automaticamente un motore nuovo o una decisione del committente.

### B3 — Dati della correlazione globale e dei livelli superiori non abbastanza determinati

**Evidenze**: AI §4 descrive due viste, bucket, «catalogo di ID, chiavi/alias e relazioni aperte», candidati a livello superiore e patch; §5 rende immutabile una partizione una volta derivata, ma non specifica il contratto che la produce. CHK019 aggiuntivo = FAIL/BLOCKER.

**Controesempio**: entrambe le viste producono cataloghi che non entrano in una chiamata e propongono patch sovrapposte su un medesimo item/relazione. Manca un formato chiuso e una regola per costruire i livelli superiori, assegnare coppie/gruppi cross-bucket da riaprire, terminare le ricorsioni e comporre patch contraddittorie senza perdere qualificatori. Il solo ordine degli ID non determina quali evidenze siano confrontate globalmente; «passaggio di raccordo fra confini» non definisce il suo input verificabile.

**Correzione tecnica richiesta**: contratti minimi di catalogo/candidato/patch con riferimenti ammessi e condizioni di validazione; regole bounded di formazione dei livelli, copertura dei gruppi, trattamento degli orfani e disposizione delle relazioni note; ordinamento/composizione o rifiuto esplicito di patch incompatibili. Il ripartizionamento deve riaprire originali pertinenti senza trasformare cataloghi in sostituti dei fatti. Non si richiede all-pairs, recall semantica assoluta o RAG: tali scelte rimangono escluse.

Nessuna dipendenza circolare certa è dimostrata, ma la produzione dei batch globali resta un raccordo non definito: non va affidato all'invenzione durante i task. È un completamento della progettazione tecnica delegata da D2; interpellare il committente soltanto se la soluzione richiede di indebolire copertura/fedeltà o cambiare perimetro/risorse approvate.

## Warning e verifiche non disponibili

**W1 — CitationGroup e resa finale**. AI §2.2 definisce citationGroupId per evidenza, §1 Source 1.1 non lo include e §3 affida l'ordine a evidenceRefs; §4 restituisce markdown/sourceIds ma non esplicita la convenzione di markup e ricostruzione del gruppo dopo edit PM. La 001 usa marcatori `[fonte:<UUID>]` (`Analysis/DocumentDraftValidator.cs` e contratto AI §6). È ragionevole riusarli, ma va dichiarato il raccordo e aggiunto il caso di due gruppi che condividono un header/sourceId. Warning, non motivo per rinegoziare la rappresentazione atomica o invalidare tutte le citazioni: preservare la policy già scelta.

**W2 — Attesa sotto un owner esteso**. AI §5 mantiene un owner per workflow formale e un solo slot; codice 001 ExecutionLeaseStore limita i run Running globali a uno. Una lavorazione Extended può ritardare altre chat per molti minuti; plan rende esplicito il rilascio per ExtractUnit ma non promette equità/preemption fra superstep formali. D1 richiede app navigabile, non risposte simultanee garantite: non è contraddizione funzionale né autorizzazione a nuova concorrenza. Misurare/documentare coda e carico nel QS prima di attribuire qualità prestazionale.

**W3 — Confidenza del parsing**. Inventario completo del parser è verificabile rispetto al manifest, ma il manifest stesso può omettere un elemento non riconosciuto. Parser §Fedeltà e QS golden/revisore ammettono il limite: non presentare Ready come prova di fedeltà semantica assoluta. Stesso rischio per chiavi/alias di correlazione e coppie di fatti non riconosciute dal modello. Non si sostituisce B3 con un requisito impossibile di perfezione.

**W4 — Dettagli operativi di compatibilità**. Rami 1.0/1.1 sono definiti e il payload legacy non viene riscritto. Durante implementazione concretizzare fixture di liste miste, report coverage-v1 e resume storico; verificare formati effettivi nelle risposte, non soltanto dichiarazioni DTO. Nessuna nuova decisione di compatibilità richiesta se si conserva il contratto approvato.

**NV1 — NOT VERIFIED**: esecuzione dei nuovi cicli MAF 1.26.0, ripresa con owner nuovi, build Docling offline e licenze/pesi nel target, migrazioni reali DB+storage, nominali DGX e percorsi browser. QS prevede questi gate; non ci sono artefatti di esecuzione nuovi che li attestino e questa sessione non li esegue. Questo esito non è un FAIL runtime né, da solo, un blocker per descrivere i relativi test nei task.

## SC-012, perimetro e conclusione

CHK020 = **PASS documentale**: SC-012 002 recepisce automaticità, un solo comando, regime esteso massimo iniziale configurato 60 min, notifica senza consenso tecnico e nessun reset. Plan §Misure conserva FAIL N se promuove e separa E/Preparazione, senza promettere qualsiasi file in 180 s. Contratti/SC della 001 e sua accettazione con riserve restano riferimenti distinti; nessuna attestazione su T052/T053. B1 riguarda la determinazione operativa della policy, non la legittimità della modifica approvata a SC-012.

CHK021 = **PASS documentale**: nessun RAG, embedding, Qdrant, pgvector, KB condivisa o servizio ulteriore non approvato individuato. Le viste di correlazione descrivono lavoro sull'intero snapshot, non ricerca top-k. Il parser isolato è coperto da D3/ADR. Nessuna nuova dipendenza circolare dimostrata fra acquisizione, estrazione, promozione e analisi; B3 lascia da precisare i dati fra i passaggi gerarchici.

CHK022 = **NOT VERIFIED**, come NV1. Conteggio totale dei 22 criteri: **17 PASS, 3 FAIL/BLOCKER, 1 WARNING, 1 NOT VERIFIED**; le caselle restano tutte `[ ]`.

**Giudizio conclusivo: NOT READY per `$speckit-tasks`.** Risolvere B1–B3 negli artefatti tecnici prima della decomposizione affidabile; W1–W4 sono raccordi/rischi gestibili senza nuove decisioni funzionali. Nessuna nuova domanda al committente necessaria sulla base dei documenti attuali: D1–D3 non vanno riaperte. Una modifica delle garanzie di copertura, della UX approvata o delle infrastrutture richiederebbe invece un nuovo mandato.

È stato creato soltanto questo file di revisione. Nessun altro artefatto aggiornato, nessuna prova runtime, task, codice, commit, push o merge; nessun altro skill avviato.

---

## Riesame indipendente finale B1–B3 — 2026-11-01

Questa sezione aggiorna il verdetto corrente; tutte le valutazioni precedenti sopra rimangono storiche e inalterate. **Esito: NOT READY per `$speckit-tasks`.** Un blocker progettuale residuo in B1; B2 e B3 superano il riesame documentale. Nessun PASS runtime.

Ambito: branch verificato `feature/002-project-documents`; letti AGENTS, Constitution, piano MVP, roadmap, tutti gli artefatti 002, ADR-0001 e contratti/codice pertinenti della 001, inclusi backlog, decisione di accettazione e HANDOFF. File 002 e ADR già non tracciati all'inizio; tracce locali preservate. Lo skill Tasks allegato è stato letto come riferimento, senza setup, hook o generazione: prevale il mandato esplicito di sola revisione.

### 1. Esito dei blocker

| Controllo | Esito corrente | Motivazione |
| --- | --- | --- |
| B1 — classificazione/regimi | **FAIL — BLOCKER progettuale** | Ammissione, soglie, precedenze e CAS ora sono determinate. La combinazione promozione incondizionata a 120 s + FAIL N per qualsiasi promozione restringe però il nominale approvato di 180 s e confonde attesa con necessità di lavorazione estesa. Controesempi sotto. |
| B2 — lifecycle MAF | **PASS documentale** | AI §5 determina identità, sessione nuova per invio, input congelato, doppio lookup prima dell'AI, commit prima di Advance, checkpoint nativo e recovery fenced. Primitive riscontrate staticamente su 1.26.0; nuovo ciclo e integrazioni NOT VERIFIED runtime. |
| B3 — riconciliazione globale | **PASS documentale** | AI §4.1–4.5 determina contratti, membership, paginazione, espansione, composizione delle patch e arresto. I controesempi originari hanno ora un esito implementabile; qualità semantica e capacità dei nominali restano NOT VERIFIED runtime. |

**B1: controesempio che impedisce la chiusura.** [Plan](../plan.md), righe 211, 217 e 221; [Quickstart](../quickstart.md), riga 98. Fonti Ready, servizio disponibile senza altro carico, input misurato al 40% di B, skeleton entro V, una sola chiamata senza dipendenze: ammissione Ordinary e dataset N fissato prima della prova. L'inferenza termina validamente e il root committa a t0+150 s, con copertura completa. A t0+120 s il monitor deve comunque promuovere. Plan impone quindi FAIL N, benché durata totale 150 < 180 s e tutti gli altri criteri siano rispettati. Anche un output già validato in attesa del commit subisce la stessa regola. Di fatto il target nominale riuscito è ristretto a prima della promozione, circa 120 s: questa restrizione non deriva da D1/SC-012. Registrare onestamente il regime finale non giustifica alterare il criterio temporale.

**Coda e DGX occupato.** Un secondo root, classificato Ordinary con piano di una chiamata e carico noto, può attendere lo slot fino a t0+130 s e richiedere solo 10 s di inferenza. A 120 s viene promosso pur non avendo ancora elaborato nulla; completa a 140 s senza alcun lavoro aggiuntivo. Le metriche separano queueMs e durata attiva, ma il predicato di promozione le ignora esplicitamente. Questo secondo caso dimostra la promozione non necessaria; il caso senza carico sopra dimostra la contraddizione sul nominale N.

**Hard cap e atomicità.** 3600 s decorrono dal comando, coda e retry inclusi: dopo 3590 s in coda restano 10 s, e un lavoro da 20 s deve fallire con copertura residua; dopo 3600 s non può nemmeno iniziare. È un limite implementabile e coerente col massimo approvato, non una garanzia di 3600 s di calcolo dopo il claim. Resta rischio di capacità W2, senza autorizzazione a spostare t0. Lock root, precedenza terminale/scadenza, CAS unico, aggiornamento atomico DeadlineAt/EffectiveDeadlineAt e guard su claim/renew/commit risolvono documentalmente la gara con completamento, sweep e retry. Chiamata attiva conserva il proprio timeout; retry manuale crea nuovo root collegato. Copertura incompleta impedisce Completed; nessuna scelta tecnica viene richiesta al PM.

**B2: attraversamento dei crash.** [AI §5.1–5.4](../contracts/ai-workflow.md), [DM — manifesti derivati](../data-model.md), [Quickstart — B2](../quickstart.md).

| Confine | Esito contrattuale verificato |
| --- | --- |
| LLM terminato, crash prima del commit WorkOutput | Nessun output valido durevole: nuova sessione e reinferenza ammesse; invio/contatore già riservati restano consumati. Vecchio fence non pubblica al ritorno. |
| WorkOutput Committed, crash prima del checkpoint | Nuovo lease; anche se il checkpoint ripresenta Miss, il guard interno InvokeBatch trova l'output compatibile prima di creare la sessione. Riusa payload, ID, hash e ricevuta/figli senza nuova AI; Advance idempotente. |
| Risultato finale committato, checkpoint finale fallito | Completed e risultato applicativo prevalgono; nessun replay generativo. |

Il riuso cross-root è subordinato a fonti/cutoff/revisione, prompt/schema/algoritmo, runtime e budget compatibili, con DerivedFromOutputHash; un payload non rimappabile non viene importato. Tre batch al 60% di B hanno tre sessioni distinte, senza history o compattazione implicita. Lo store esistente è davvero ProcessingRun-only e richiede il delta dichiarato: non è prova dei nuovi owner o delle nuove guard di deadline.

Riscontro statico indipendente: csproj/packages.lock.json e XML NuGet locali `1.26.0/lib/net10.0` confermano CreateSessionAsync, RunAsync/RunStreamingAsync, Serialize/DeserializeSessionAsync, Executor<TInput>, AddEdge<T> condizionale, QueueStateUpdateAsync, CheckpointManager.CreateJson e ResumeStreamingAsync con CheckpointInfo. La visibilità dello stato dal superstep successivo è documentata nell'XML. Letto MafApiCompatibilityTests: SDK reale/modello finto, non rieseguito e non prova del nuovo feedback. Verificata anche la provenienza pubblica della [release Microsoft dotnet-1.26.0](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.26.0) e del [pacchetto Workflows 1.26.0](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.26.0); questi link non certificano l'esecuzione del disegno. Lookup e persistenza sono raccordi di dominio; archi, feedback e resume restano MAF, senza NextExecutor o secondo engine.

**B3: controesempi riesaminati.** [AI §4.1–4.5](../contracts/ai-workflow.md) e [Quickstart — B3](../quickstart.md):

- Cataloghi A/B oltre contesto: pagine con ordinal/hash concatenati, merge streaming esatto delle chiavi, membership completa e bucket unclassified. Pack misura il payload completo; elemento irriducibile produce errore, senza taglio. NodeId deriva da input/membership, non dalle proprie pagine: nessun ciclo hash dimostrato.
- Evidenze in documenti diversi o sezioni distanti: chiavi anche non primarie generano obbligazioni cross-foglia; descriptor gerarchici e Cross propongono ulteriori candidati. Espansione Node/Set riapre tutti gli originali; relazioni note e candidati Evidence richiedono entrambi gli endpoint integrali. Endpoint non risolvibili restano NotProcessed. La scansione senza candidati può terminare con motivo e hash di tutte le espansioni: non certifica assenza semantica di conflitti.
- SameRule contro Contradicts, Supersedes opposti o alternative incompatibili: patch sulla stessa base/expectedHash, niente precedenza per ordine d'arrivo. Conflitto preservato con alternative, qualificatori e fonti; nessuna sostituzione o risoluzione AI. Invalid/Stale necessario impedisce Completed.
- Arresto: livelli strettamente decrescenti, singleton inoltrato, massimo 32 livelli/100000 candidati/512 chiamate e un round finale; nuovo lavoro residuo o stallo produce coverage_incomplete. Copertura calcolata dal server e commit finale atomico impediscono pubblicazione parziale. Fixture con visite 100% ma conflitto gold mancato deve fallire la qualità semantica; fake e hash non la provano.

### 2. CHK004, CHK011, CHK012 e regressioni

| Controllo | Stato corrente |
| --- | --- |
| CHK004 | **WARNING implementativo W1**, correttamente circoscritto. AI §4.5 ora prescrive marcatori atomici e mappa storica dei gruppi anche con header condiviso/edit PM; resa end-to-end ancora da verificare. |
| CHK011 | **FAIL/BLOCKER B1** nel criterio ampliato della revisione indipendente. Riproducibilità e separazione dei profili risolte; resta l'incoerenza fra timer 120 e riferimento nominale 180. |
| CHK012 | **PASS documentale**: topologia fissa, batch derivati persistiti, sessioni e cache pre-inferenza definiti; B2 chiuso, runtime non attestato. |

I **15 PASS originali restano confermati**, ciascuno sul proprio ambito: CHK001–003 (catena/nominali/stati); CHK005–007 (decoder storici, conservazione e union Evidence); CHK008–010 (snapshot, current/candidate, gerarchia temporale); CHK013–018 (aggregatore unico, storage, sicurezza, livelli di prova, ADR, separazione chat/Analizza). Nessuna regressione documentale rilevata in questi 15 controlli; nessuna deduzione di compatibilità runtime dai soli contratti.

Controlli aggiuntivi: CHK019 passa a **PASS documentale** con B3; **CHK020 regredisce da PASS a FAIL/BLOCKER** per il nuovo restringimento implicito del nominale tramite timer 120; CHK021 resta **PASS documentale** (nessun RAG/KB, nuova infrastruttura o dipendenza circolare dimostrata); CHK022 resta **NOT VERIFIED runtime**. La nuova restrizione di B1 è la decisione comportamentale non giustificata da correggere; non sono emerse altre nuove decisioni funzionali.

Conteggi correnti: 18 originali = **16 PASS, 1 FAIL/BLOCKER, 1 WARNING**; 22 complessivi = **18 PASS, 2 FAIL/BLOCKER, 1 WARNING, 1 NOT VERIFIED**. CHK011/020 rinviano allo stesso blocker B1, non a due problemi indipendenti. Marker e conteggi storici sopra e nelle altre checklist invariati.

F1–F8: **F1 PASS**, **F2 PASS**, **F3 PASS**, **F4 PASS**, **F5 WARNING W1**, **F6 PASS**, **F7 FAIL/BLOCKER B1**, **F8 PASS**, tutti documentali. T052/T053 della 001 restano aperti, accettazione solo pilota con riserve e FAIL storici invariati.

### 3. Correzione necessaria e warning per i futuri task

**Unico blocker residuo B1**: correggere la combinazione timer/promozione/criterio N in Plan §Classificazione e §Misure e nei raccordi HTTP, Research R10 e Quickstart. La regola deve distinguere attesa da necessità di regime esteso, conservare il riferimento nominale approvato di 180 s e produrre motivi verificabili. Non basta sostituire una soglia con un'altra o contare qualsiasi promozione come insuccesso temporale. Il caso N completo a 150 s e quello in coda con inferenza breve devono avere esiti coerenti con le decisioni approvate; conservare hard cap dal comando, automaticità, copertura e atomicità già definite. Definire questa correzione negli artefatti tecnici prima dei task; nessuna modifica eseguita qui.

Warning traducibili in verifiche nei futuri task:

| Warning | Evidenza di chiusura richiesta |
| --- | --- |
| W1 — citazioni | Resa, apertura storica ed edit di due gruppi con header/sourceId condiviso e passaggi multipagina; marcatori, ordine e locator corrispondenti. |
| W2 — carico/coda | Misurare chat di altro progetto dietro owner Extended e attesa prossima a 3600 s; UI navigabile, coda distinta da calcolo, residuo corretto e nessun Completed tardivo. Nessuna nuova equità/preemption presunta. |
| W3 — fedeltà | Golden parser e DGX con conflitti distanti/inter-documento, parafrasi, quantità e negazioni; rilevare separatamente copertura strutturale e attese semantiche. |
| W4 — compatibilità | Upgrade PostgreSQL con dati 001, listing misti 1.0/1.1, coverage-v1, citazioni e resume storico; hash/payload e conferme preservati. |

**NOT VERIFIED runtime**: compilazione/esecuzione del nuovo ciclo e dei due guard MAF 1.26.0, crash/fencing/checkpoint con DB reale, timer/deadline del dispatcher aggiornato, build Docling offline e pesi/licenze, migrazioni DB+volume, conteggio tokenizer/DGX, nominali oltre contesto, browser e accettazione umana. Le prove sono previste; non eseguite in questo riesame. L'assenza di runtime non è usata come blocker aggiuntivo pre-tasks.

### 4. Verdetto finale corrente

**NOT READY per `$speckit-tasks` finché B1 non è corretto e riesaminato.** B2 e B3 hanno PASS documentale; W1–W4 restano verifiche implementative. Aggiornato esclusivamente questo rapporto mediante append, senza spuntare checklist, modificare artefatti tecnici/storici o codice, generare tasks.md, eseguire test runtime, commit, push o merge. STOP dopo il rapporto.

---

## Verifica indipendente finale mirata B1 — 2026-11-01

Questo aggiornamento sostituisce il solo verdetto corrente su B1, CHK011/CHK020 e readiness pre-tasks; conserva integralmente lo storico sopra. **B1 PASS documentale; CHK011 PASS documentale; CHK020 PASS documentale. Verdetto: READY per `$speckit-tasks`.** Non attesta implementazione, prove runtime o accettazione della 002 e non avvia la generazione dei task.

Ambito limitato alla correzione finale B1 e ai suoi raccordi/regressioni: letti AGENTS, Constitution, piano MVP, roadmap, Spec/Plan, contratti HTTP/AI, Quickstart, Research, Data model e questo rapporto. Consultati i raccordi temporali del parser e la baseline 001 pertinente (criteri temporali e contratti, backlog, decisione di accettazione, HANDOFF §§10.5–10.6). Branch verificato `feature/002-project-documents`; `specs/002-project-documents/`, `docs/adr/` e `docs/acceptance-trace/` già non tracciati all'avvio. Nessuna baseline Git della precedente versione 002 disponibile per un confronto puntuale delle ultime modifiche: il controllo di regressione confronta gli invarianti correnti con le evidenze del precedente riesame, senza attribuirgli valore di test runtime.

### B1, CHK011 e CHK020

| Controllo | Esito | Evidenza e motivazione |
| --- | --- | --- |
| B1 — classificazione e promozione | **PASS documentale** | Plan §§Classificazione/Promozione, righe 187–238: tabella ordinata a prima corrispondenza, input/hash congelati, budget e skeleton riproducibili, unknown espliciti, nessuna previsione di durata/carico. La promozione richiede ricevuta server immutabile con almeno un WorkOutput Committed, obbligazioni residue e un batch logico necessario senza output compatibile già committato, con manifesti/preflight/hash del piano derivato. Coda, inferenza lenta, retry della stessa chiamata e attesa di commit non bastano. Lock/CAS, precedenza terminale/scadenza/completamento e aggiornamento atomico della deadline determinano le gare. |
| CHK011 — riproducibilità e profili | **PASS documentale** | Plan righe 191–212 distingue prima classificazione da guard successivi; Pending non invia AI e non guadagna tempo. Plan §§Misure, HTTP §Regimi, AI §6 e QS §B1 separano InitialRegime/Regime da profilo N/E fissato prima della prova. N richiede risultato completo, qualità conforme e durata entro 180 s dal comando: nessun FAIL o PASS deriva dalla sola promozione. |
| CHK020 — D1 e continuità 001 | **PASS documentale** | Spec SC-012 e Plan mantengono comando unico, assegnazione automatica, notifica senza scelta tecnica PM, limite esteso iniziale 3600 s dal t0 e preparazione distinta. Il nominale a 150 s non viene più penalizzato dal timer 120. Il riferimento 180 s e i FAIL/riserve della 001 non cambiano; il nuovo regime non è applicato retroattivamente ai run storici o al ramo senza documenti. |

Il PM non sceglie regime, timeout o strategia (HTTP righe 59–69). HardDeadlineAt resta assoluto; Ordinary parte da t0+180, Extended dal cap congelato originario. Una promozione valida cambia la deadline effettiva, non RequestedAt/HardDeadlineAt; lease, heartbeat e retry automatici non la rinnovano. Dopo terminale, il retry PM è un nuovo comando collegato, con esito precedente immutato (Plan righe 176–185, 232–236; AI §5.3). Nessun risultato incompleto ottiene Completed.

### Controesempi obbligatori A–G

Tutti gli esiti della tabella sono verifiche **documentali**, non esecuzioni con clock, DB o modello.

| Caso | Esito della verifica | Regola determinante |
| --- | --- | --- |
| A — nominale completo a 150 s | **PASS**: criterio prestazionale soddisfatto; PASS N se anche qualità e criteri funzionali sono rispettati, indipendentemente da una promozione tecnica valida | Plan riga 246; HTTP riga 69; QS righe 102–103. Ordinary non viene promosso a 120 s per il solo tempo trascorso. |
| B — nominale oltre 180 s | **PASS della regola di controllo**: il caso è FAIL prestazionale N, mai falso PASS | Senza promozione tecnica valida scade a 180 s e rifiuta output tardivo; se già promosso può completare funzionalmente entro hard cap ma resta FAIL temporale N, senza diventare E ex post (Plan righe 230/246; QS riga 104). |
| C — interamente in coda a 120 s | **PASS**: nessuna promozione per attesa | Plan righe 218/230/248; QS riga 105. Con osservazione completa queueMs=120000 e processingMs=0; un commit valido a 140 s resta possibile con la deadline originaria. |
| D — lavoro strutturalmente multipasso | **PASS**: Extended automatico e motivato prima dell'inferenza | Plan tabella, regole 5–8: input valido e piano multipasso trattabile → multipass_or_capacity con piano/hash; impossibilità irriducibile → errore esplicito, non Extended inutile. QS righe 96/106. |
| E — promozione contro completamento/scadenza | **PASS**: transizioni serializzate, senza due esiti vincenti | Plan righe 220–236; QS righe 107–108/113. Terminale già committato prevale; sotto lock scadenza esatta precede completamento/promozione; finale già valido e completo precede promozione. CAS valido precedente aggiorna DeadlineAt/EffectiveDeadlineAt insieme; sweep e vecchi claim devono leggere il DB attuale. |
| F — retry dopo promozione | **PASS**: stesso root/t0, ricevuta, contatori e hard deadline originari | Promozione a 110 s e successivo retry conservano t0+3600, non aggiungono 3600 s dal retry; timer della chiamata già inviata invariato. Riuso dei WorkOutput compatibili preservato (Plan righe 234–236; QS riga 109; AI §5). |
| G — copertura incompleta alla scadenza | **PASS**: nessun Completed | Plan righe 225/238/246; AI §4.5; QS riga 110: errore con frontier/porzioni residue, nessuna revisione/bozza completa e commit tardivo vietato. Un errore gestito non supera il nominale. |

### Coerenza e residui testuali temporali

Plan, HTTP §Regimi, AI §6, Quickstart §§Gate/B1 e Spec SC-012 concordano sulle regole nuove. Sono tuttavia ancora presenti **tre enunciati obsoleti incompatibili se letti isolatamente**:

| Residuo | Precedenza verificata e conseguenza |
| --- | --- |
| Research R8, riga 54: qualsiasi N promosso sarebbe FAIL N | Plan riga 189 nomina espressamente R8/R10 e sostituisce il FAIL N da sola promozione; §Misure riga 246 fornisce la formula applicabile. Il residuo non governa la valutazione. |
| Research R10, riga 75: ogni Ordinary non terminale sarebbe promosso a 120 s, queued o running | Lo stesso override puntuale di Plan riga 189, seguito dal predicato additional_work e dal divieto esplicito di timer/coda alle righe 216–230, esclude questa regola precedente. R10 rinvia inoltre alla policy di Plan. |
| Data model §Campi temporali, riga 83: prima classificazione subordinata a PromotionDueAt | Plan riga 189 nomina il raccordo temporale del Data model, impone PromotionDueAt sempre null e rimuove espressamente tale subordinazione; riga 191 determina il preflight tardivo. Data model righe 81/83/85 rinvia ai campi/policy di Plan; HTTP riga 61 conferma null e prima classificazione dal t0. Nessun timer di promozione va derivato dal campo residuo. |

La precedenza è **univoca e specifica**, non una generica nota «superato»: identifica i passaggi sostituiti, rimuove PromotionAfterSeconds e definisce integralmente la policy sostitutiva. Non è stato trovato un altro artefatto che attribuisca a quei passaggi una precedenza concorrente su Plan. I tre residui sono segnalati come difetti editoriali non bloccanti; **nessuna contraddizione normativa irrisolta dimostrata**. Non sono stati corretti in questa revisione, secondo il mandato.

### Regressioni mirate e limiti della verifica

- **B2: nessuna regressione documentale concreta individuata.** AI §§5.1–5.3 conserva sessione nuova per invio, doppio lookup prima dell'AI, identità/hash congelati, WorkOutput prima di Advance/checkpoint e recovery idempotente sotto nuovo fence. Plan riga 238 e AI riga 198 subordinano la promozione a tali invarianti; la cache non autorizza reinferenza per giustificare additional_work. Le gare checkpoint/deadline sono previste, non provate runtime. Il precedente PASS documentale resta valido.
- **B3: nessuna regressione documentale concreta individuata.** AI §§4.1–4.5 e Data model §Cataloghi mantengono directory/membership, riapertura integrale, patch sulla stessa base, conflitti preservati, copertura server e arresto bounded. Un piano aggiuntivo non scavalca limiti, round finale o obbligazioni; residui necessari impediscono Completed. Il precedente PASS documentale resta valido.
- **15 PASS originali: nessuna invalidazione documentale rilevata nel raccordo B1.** CHK001–003: copertura/nominali/incompletezza preservati; CHK005–007: decoder, storici e fonti invariati; CHK008–010: snapshot/current-candidate e tempi assoluti preservati, con precedenza temporale chiarita sopra; CHK013–018: aggregatore, storage, sicurezza, livelli di prova, confine parser e separazione chat/Analizza preservati. Non è una nuova revisione generale né un'attestazione di regressione runtime superata.
- **001: criteri storici preservati.** Spec 001 SC-002/008/016 e contratto AI mantengono riferimento 180 s; decisione di accettazione, backlog e HANDOFF conservano N2/N4/N5 FAIL e pilota con riserve. `specs/001-project-draft/tasks.md` T052/T053 restano aperti, non PASS. Le incoerenze storiche già censite dalla roadmap non sono sanate o riclassificate.

**NOT VERIFIED runtime**: implementazione del classificatore/ricevuta, clock DB e gare CAS/sweep, timer/deadline dopo promozione e retry, conteggi tokenizer, nuovi grafi MAF e crash/checkpoint/cache con DB reale, parser offline, migrazioni, nominali DGX, UI e accettazione umana. Nessuna suite eseguita; nessun nuovo PASS/FAIL runtime o SKIP spacciato per prova. CHK022 resta NOT VERIFIED; W1–W4 restano i warning/verifiche già registrati.

**Verdetto finale: READY per `$speckit-tasks` sul piano documentale.** B1 risolto; CHK011/CHK020 PASS documentale; nessuna regressione effettivamente dimostrata. Aggiunto solo questo aggiornamento al rapporto, senza modificare storico o checkbox, artefatti progettuali, codice o file della 001; nessun tasks.md, skill Spec Kit, commit, push o merge. STOP.
