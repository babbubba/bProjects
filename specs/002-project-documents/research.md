# Research — 002-project-documents

## Decisione di revisione T130/N4 (2026-11-07)

**Decisione**: A + C minima approvata dal committente. Retry unico informato dalla proposta respinta come dato; valutazione Gap derivata per snapshot e consultabile nella coverage. Disegno esatto/versioni [AI §13](contracts/ai-workflow.md#13-revisione-approvata-t130n4--a--c-minima-2026-11-07). Nessuna nuova dipendenza, primitiva MAF o scelta infrastrutturale da ricercare; riuso dei batch e JSONB esistenti.

**Motivazione**: 005 individua il conflitto 45/30 ma duplica le alternative nelle sezioni ordinarie e non rendiconta E2; non è integralmente corretta. 006 rende E2 mappata ma perde il conflitto strutturato. Una domanda storica riscontrata non è un fatto né una lacuna sempre aperta. Copertura tecnica non certifica fedeltà. I report e le catture storiche restano immutati; N4 è FAIL, non risolto da questa pianificazione.

**Alternative considerate**: patch puntuale congela errori non validati e richiede merge/precondizioni; sola istruzione di preservare conflitti conserva anche quelli falsi; ignorare Gap nasconde omissioni. Respinte. Valutazione nel WorkOutput e proiezione in coverage evitano un lifecycle globale e una migrazione. Risposte parziali/ambigue restano Open con motivo, come da mandato.

**Impatto I5**: necessario adeguamento di contratto, metadati e assembly nei batch esistenti per non mantenere due semantiche. Nessun nuovo stadio/chiamata dedicata; limiti e frontiera restano espliciti. Gate con riscontri fra porzioni diverse; se la capacità nominale non riesce, FAIL, non chiusura artificiale. Versioni storiche congelate e nuove versioni AI §13.1. Nessuna ambiguità funzionale residua individuata da Clarify; fattibilità eseguibile e qualità LLM da provare successivamente.

Data: 2026-11-01. Ricerca statica e documentazione ufficiale; nessuna installazione, PoC, inferenza o test reale eseguito. Le decisioni sono di progetto; la compatibilità eseguibile dei nuovi percorsi è un gate dell'implementazione.

## R1 — Riuso invece di una seconda piattaforma

**Decisione**: estendere Documents/Evidence nel monolite e il dispatcher già presente. Solo parser Python separato, come D07/§3 del piano MVP; niente broker, motore custom o indice.
**Motivo**: `ExecutionLeaseStore` serializza claim via advisory lock e limite globale uno; `AiDispatchService.RunFormalAsync` riusa heartbeat/retry; `EvidenceLedgerStore` committa versioni e risultati sotto fence. Non sono astrazioni solo pianificate: sono implementate nei file elencati in plan.
**Alternative**: queue Python, Celery, servizi documentali esterni e nuovo scheduler respinti perché duplicano persistenza e recovery. Parser in-process .NET respinto perché Docling è una dipendenza Python già approvata e il file non fidato richiede isolamento risorse.

## R2 — Parser e dipendenze

**Decisione**: Python 3.12, baseline Docling 2.129.0, lock completo di transitive e immagini/modelli tramite hash in implementazione. PDF testuali/DOCX/MD via DocumentConverter; TXT via lettura lossless locale con codifica validata (UTF-8 con/senza BOM e UTF-16 con BOM; altre codifiche → errore esplicito). Conservare bytes e mapping righe MD/TXT; per MD nessun rendering che perda code fences o link.
**Motivo**: release pubblicata e versione Python compatibile nella [release ufficiale](https://pypi.org/project/docling/2.129.0/). Non si afferma che sia latest o già provata sul server target. Il codice Docling è MIT; i pesi hanno licenze proprie da registrare prima di distribuirli.
**Alternative**: usare latest non riproducibile respinto; solo conversione Markdown respinta perché insufficiente per locator e tabelle. Un parser di fallback diverso non va introdotto silenziosamente se Docling fallisce: manifest incompleto e gate da riesaminare.
**Verifica richiesta**: build Linux CPU x64 dell'app server, ARM64 soltanto se effettivamente target; convertire fixture golden senza rete. Nessuna dipendenza installata nel DGX per parsing.

## R3 — Struttura, provenance e chunking

**Decisione**: persistere rappresentazione DoclingDocument completa, ordine/gerarchia e riferimenti agli item; manifest normalizzato con pagine, bbox ove disponibili e intervalli di testo immutabili. HybridChunker per gruppi strutturali, guard tokenizer effettivo .NET per i passi AI.
**Motivo**: [DoclingDocument](https://docling-project.github.io/docling/concepts/docling_document/) espone testo, tabelle e struttura con provenance; [chunking](https://docling-project.github.io/docling/concepts/chunking/) descrive raggruppamento strutturale e affinamento token-aware. Il [sorgente ufficiale HybridChunker](https://github.com/docling-project/docling-core/blob/main/docling_core/transforms/chunker/hybrid_chunker.py) e l'[esempio tokenizer](https://docling-project.github.io/docling/_generated/examples/hybrid_chunking/) guidano l'adattamento, ma i default non sono un tokenizer valido per qualunque LLM.
**Alternative**: split per parole o taglio dei primi N caratteri respinti; un unico riassunto respinto perché non rende verificabili omissioni, polarità e citazioni. Il chunking è preparazione esaustiva, non retrieval o RAG.
**Verifica richiesta**: riferimenti da chunk a item e mapping di ciascun intervallo; tabelle spezzate per righe con header/condizioni replicati come contesto, non come nuova fonte. Offset Python convertiti/verificati in unità UTF-16 dal backend. Un elemento non mappabile rende il manifest incompleto, non pronto.

## R4 — Esecuzione offline e confine parser

**Decisione**: parser stateless sincrono interno, OCR disabilitato, nessun VLM/enrichment remoto, plugin esterni o download runtime; modelli locali prefetchati. Risorse e timeout imposti dal container e dal subprocess, non soltanto dal parser.
**Motivo**: le [opzioni ufficiali](https://docling-project.github.io/docling/usage/advanced_options/) documentano offline, configurazione e limiti; il [riferimento pipeline](https://docling-project.github.io/docling/reference/pipeline_options/) espone i controlli su OCR e servizi remoti. Questi controlli software non sostituiscono rete senza egress.
**Alternative**: Docling Serve con propria gestione job non necessario; un sottile endpoint sincrono evita due code autorevoli. Nessun Docker socket nell'app per avviare container per ogni file.

## R5 — MAF .NET e checkpoint

**Decisione**: riusare WorkflowBuilder, Executor<TInput>, QueueStateUpdateAsync, CheckpointManager.CreateJson e InProcessExecution Run/ResumeStreamingAsync della versione .NET 1.26.0. Nel nuovo ciclo InvokeBatch crea una AgentSession nativa per invio e controlla WorkOutput prima di chiamare AIAgent; BindAsExecutor/ChatProtocolExecutor rimangono nei grafi 001. Topologia statica con feedback e dati derivati (AI §5), output idempotenti; nessun loop applicativo di dispatch executor dal DB. Riscontro puntuale delle firme e limiti in R10.
**Motivo**: `MafApiCompatibilityTests` contiene prove eseguibili delle primitive, `AnalysisConsolidation` già costruisce passi e valida gli alias, `PostgresCheckpointStore` implementa lo store fenced. Questa sessione ne ha letto il codice, non rieseguito le prove. [Documentazione Microsoft dell'agent executor](https://learn.microsoft.com/agent-framework/workflows/advanced/agent-executor) è un riferimento concettuale aggiornato, non attestazione della release fissata: il codice locale/lockfile prevale per le firme.
**Alternative**: Durable Functions, Temporal o workflow Python respinti; non servono nuove primitive native ipotetiche. Estendere owner dello store è responsabilità applicativa, non sostituzione del checkpoint nativo.

## R6 — Contratti e identità fonti

**Decisione**: AnalysisContent 1.1.0 per documenti, decoder/version dispatch separato. Fonti DocumentPassage e classificazione Documented; schema 1.0.0 invariato. Stesso ledger esteso con sorgenti discriminate e vincoli XOR, no messaggi sintetici.
**Motivo**: `AnalysisContent.cs` usa converter const, enum rigidi e Disallow sulle proprietà extra; l'aggiornamento sotto lo stesso numero romperebbe lo schema storico. EvidenceItem/Version/Job richiedono MessageId e il coverage assume messaggi; serve una migrazione esplicita, non semplici nuove stringhe di prompt.
**Alternative**: rimpiazzare 1.0 in-place o creare ConversationMessage per ogni chunk respinti; side-ledger documentale respinto. La compatibilità è lettura/esecuzione storica sui nuovi binari, non promessa che i vecchi binari comprendano dati 1.1.

## R7 — Oltre contesto end-to-end

**Decisione**: inventario completo e estrazione per range, consolidamento per pacchetti persistiti, riconciliazione gerarchica globale con copertura obbligatoria e generazione per unità editoriali integrali con assemblaggio deterministico. Chat visita tutte le unità ammesse rispetto alla domanda senza retrieval.
**Motivo**: l'implementazione attuale reinvia l'intera analisi incrementale e DocumentAgent riceve l'intero snapshot; entrambe possono diventare il collo di bottiglia anche dopo aver diviso l'input. Nessun aumento ctx-size né riassunto generico risolve questo limite. Coverage degli ID resta distinto dalla fedeltà semantica, come già EvidenceCoverage.
**Alternative**: top-k, FTS/vector ranking, embeddings e retrieval esplicitamente esclusi; merge crescente fino a un'unica chiamata respinto. Il confronto all-pairs obbligatorio della prima bozza è stato respinto dal committente (D2): due viste globali e raccordo delle relazioni mantengono tutte le evidenze senza imporre O(n²). Limiti chiamate/deadline non autorizzano pruning silenzioso.

## R8 — Budget e performance realistici

**Decisione**: mantenere Ai:Phases e monitor; risolvere probe/tokenizer per modello effettivo, non usare il globale come se valesse per tutti. Template/schema/framing nel conto; margine calibrato, reasoning incluso nella riserva totale di output. Runtime non verificato blocca il nuovo percorso documentale.
**Motivo**: AiPhaseOptions dichiara oggi che gli override devono condividere tokenizer/finestra col globale. ModelRuntimeMonitor legge /props o minimo n_ctx degli slot; ContextBudget usa attualmente la finestra configurata. Sono limiti reali da correggere prima delle nuove chiamate, non funzionalità già disponibili.
**Alternative**: valori teorici del modello, char/4 o finestra hardcoded respinti. Configurazioni per modelli differenti senza probe identificabile sono rifiutate; nessun nuovo registry/provider generalista.
**Rischio**: uno slot DGX e qualità del modello possono impedire il target ordinario 180 s. D1 approva classificazione automatica e regime esteso massimo iniziale 3600 s dal t0, senza reset; un nominale N che si promuove rimane FAIL del target N. Limiti e prove in Plan/Quickstart, nessuna modifica dei criteri 001.

## Esito della ricerca

Decisioni di disegno risolte, nessun NEEDS CLARIFICATION residuo. Build, prove parser/API MAF dei nuovi grafi, conteggio runtime, recovery e nominali DGX da eseguire in implementazione. Se una baseline dipendenza non supera tali prove, registrare FAIL e aggiornare motivatamente il piano prima del relativo lavoro; non inventare compatibilità o PASS. D3 formalizza il confine strutturale già approvato con [ADR-0001](../../docs/adr/0001-parser-docling-stateless.md). Nessuna nuova approvazione Docling richiesta; nessun ulteriore servizio.


## R9 — Consolidamento F1–F8 dopo le decisioni del committente

**Decisioni approvate**: D1 regime assegnato automaticamente, 180 s per riferimento ordinario e massimo esteso iniziale configurabile 60 min, stato asincrono visibile e possibile promozione controllata senza reset; D2 riconciliazione gerarchica globale e Evidence comune, copertura distinta dalla fedeltà; D3 parser senza stato autonomo, backend responsabile di persistenza/recupero e ADR circoscritto.

**Scelte implementative conseguenti**: timestamp e cap sul root; deadline figlio subordinata; un solo EvidenceExtractionJob aggregatore; schema Evidence 1.1 chiuso con source union e passaggi multipli; current bundle per versione file; topologia MAF fissa con batch derivati append-only e checkpoint nativi. Servono per rendere riproducibili le operazioni senza esporre scelte tecniche al PM. Non introducono nuovi comportamenti di approvazione o nuove infrastrutture.

**Alternative escluse**: reset della deadline al cambio regime, confronto obbligatorio di ogni coppia, reinferenza di un output già persistito per ricalcolare figli, doppio Finalize/aggregatore con autorità concorrenti, citazione multi-pagina appiattita e candidata Ready usata senza promozione.

**Verifica delle primitive**: codice e XML dei pacchetti locali documentano AddEdge<T> condizionale, executor e stato/checkpoint; non è stato eseguito un test del nuovo ciclo. [Documentazione Microsoft checkpoint .NET](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints) descrive superstep e stessa topologia al resume; non certifica da sola la release fissata. Gate implementativo per ciclo, restore e unicità degli effetti, non exactly-once delle inferenze. Nessun secondo workflow engine ammesso.

La precedente affermazione di piano completamente pronto era antecedente alla checklist: ora il consolidamento è pronto per revisione documentale indipendente, non dichiarazione che i 18 controlli siano superati. Nessuna prova reale svolta qui.

## R10 — Risoluzione tecnica B1–B3 e disponibilità MAF fissata

**B1**: policy document-regime-v2 in Plan §Classificazione. Ammissione Ordinary richiede piano di una chiamata, payload completo misurabile entro budget e minimo strutturale d'output entro riserva visibile; unknown valido va Extended, runtime non verificabile fallisce. Non si predice durata. A 120 s ogni Ordinary non terminale viene promosso senza discrezionalità, queued o running; a 180 s senza promozione già valida scade. Extended resta massimo iniziale 3600 s da t0. La proiezione DeadlineAt legacy cambia nella transazione della promozione; claim/timer vecchi non possono chiudere il root promosso. Alternativa di throughput stimato respinta perché non rende certa la conclusione né risolve il dato mancante.

**B2 — evidenza statica riproducibile**: csproj e packages.lock.json fissano Microsoft.Agents.AI, OpenAI e Workflows a [1.26.0]. Letti i file NuGet locali `microsoft.agents.ai.abstractions/1.26.0/lib/net10.0/Microsoft.Agents.AI.Abstractions.xml`, `microsoft.agents.ai/1.26.0/lib/net10.0/Microsoft.Agents.AI.xml` e `microsoft.agents.ai.workflows/1.26.0/lib/net10.0/Microsoft.Agents.AI.Workflows.xml`; nuspec Workflows conferma versione/target net10.0. Provenienza ufficiale: [release dotnet-1.26.0](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.26.0), [pacchetto Microsoft.Agents.AI 1.26.0](https://www.nuget.org/packages/Microsoft.Agents.AI/1.26.0), [Workflows 1.26.0](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.26.0). Non dedotte firme da API Python o latest.

| Necessità | Riscontro nella release .NET adottata | Limite |
| --- | --- | --- |
| Sessione isolata | AIAgent.CreateSessionAsync(CancellationToken); RunAsync(IEnumerable<ChatMessage>,AgentSession,AgentRunOptions,CancellationToken), RunStreamingAsync equivalente | Nuova sessione per invio; nessun ResetSession richiesto |
| Serializzazione sessione | SerializeSessionAsync(AgentSession,JsonSerializerOptions,CancellationToken), DeserializeSessionAsync(JsonElement,JsonSerializerOptions,CancellationToken) | Non è ripresa intra-inferenza; non usata per accumulare batch |
| Executor di raccordo | Executor<TInput> con HandleAsync e IWorkflowContext | È nodo MAF, non scheduler applicativo |
| Hit/miss e feedback esclusivi | WorkflowBuilder.AddEdge<T>(ExecutorBinding,ExecutorBinding,Func<T,bool>) | Predicati disgiunti nel codice del grafo; nuova composizione non eseguita |
| Stato/checkpoint | QueueStateUpdateAsync<T>(string,T,string,CancellationToken), CheckpointManager.CreateJson(ICheckpointStore<JsonElement>,JsonSerializerOptions) | Visibilità agli altri executor dal superstep successivo; commit dominio distinto |
| Resume processo nuovo | InProcessExecution.ResumeStreamingAsync(Workflow,CheckpointInfo,CheckpointManager,CancellationToken) | Stessa topologia/ID; nessuna API di resume token LLM assunta |

`MafApiCompatibilityTests` contiene test di SDK reale con modello finto per sessione/serialize e workflow/checkpoint/resume. `AnalysisWorkflow` costruisce agenti separati per i passi, quindi non dimostra isolamento di un host ciclico. `PostgresCheckpointStore` è fenced su ProcessingRun ma richiede estensione di owner e deadline root: il codice attuale non prova B2. Questa sessione non ha compilato/eseguito nuove prove, non ha creato codice applicativo o test.

MAF non fornisce nell'interfaccia adottata un commit transazionale comune fra output applicativo PostgreSQL, checkpoint e inferenza remota. Non si presume una primitiva nativa di cache Evidence/resume LLM. Raccordo minimo: LookupOutput più guard interno InvokeBatch (serve anche se il checkpoint contiene Miss), sessione nuova su miss, transazione WorkOutput/id/mappa/figli o ricevuta, poi checkpoint nativo. È responsabilità di dominio/durabilità della Constitution IV. Routing, feedback e resume rimangono nativi: nessuna tabella NextExecutor, nessun interprete o retry di nodo parallelo. Crash prima del commit può reinferire; dopo commit compatibile deve riusare, fence vecchio non pubblica. Alternative sessione cumulativa e solo lookup in ValidateBatch respinte dai controesempi della revisione.

**B3**: reconcile-1/global-reconcile-v1 in AI §4. Directory letterale paginata unita esattamente, due viste dell'intero insieme, livelli di descriptor con cardinalità strettamente decrescente, candidati con membership riaperta integralmente, confronti diretti di endpoint e un round finale bounded. Patch validate simultaneamente sulla stessa base; unioni compatibili, incompatibilità conservate con alternative per PM, stale/invalid non applicate. Cataloghi non sostituiscono enunciati/qualificatori né costituiscono ricerca o Evidence parallelo. Alternative catalogo unico fuori budget, last-write-wins e cicli fino a convergenza respinte. Limiti espliciti producono incompleto, non un risultato completo ridotto.

**Limiti residui**: prove runtime del ciclo/guard/recovery e dei timer promossi, DB reale e tokenizer/DGX, qualità dei descriptor/candidati e contraddizioni gold ancora da dimostrare. Warning W1 resa citazioni, W2 attesa con owner formale esteso, W3 fedeltà parser/correlazione, W4 fixture legacy restano verifiche future del Quickstart; nessuna dichiarazione di risoluzione artificiale. Nessuna rivalidazione delle checklist eseguita in questa correzione.

## R11 — Alias brevi e composizione server-side dell'analisi documentale (2026-11-03)

**Decisione**: il modello interpreta e cita evidenze con alias brevi (E/S/R) e chiavi locali; il server tiene catalogo immutabile, id canonici deterministici, hash, provenienza e integrità referenziale e compone il canonico (AI §7, definition v3). Il formato v2 (il modello riproduce UUID, hash, `Source`, `evidenceMap`) è storico/frozen per i run già richiesti.
**Motivo**: la campagna DGX reale (`validation/I4-real.md` e fasi 131K A/B/C) ha dato 1/25 analisi valide a W=32768 e 1/3 per cella a W=131072, con errori di copia di identificativi che cambiano a ogni run e non dipendono da finestra, reasoning o prompt; il prompt v2 stesso imponeva `candidates`/`patches` vuoti, incompatibile con le relazioni note di §4.5. **Alternative**: più prompt o reasoning (non riducono la classe di errore), grammatica sull'intero schema (non elimina UUID/hash e non sposta la responsabilità), validatori più permissivi (violerebbero le invarianti). **Non provato**: qualità semantica con il nuovo formato, effetto di `presence-penalty` del profilo Smart, esistenza dell'endpoint di load del router per la readiness (T126).
