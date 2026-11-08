# Laboratorio Slice 003 — evidenze sperimentali sul DGX

Campagna autorizzata l'8 novembre 2026. Ricerca separata dalla slice 002: nessuna implementazione della 003, nessun avvio Spec Kit, nessuna chiusura o accettazione di T082/T083/T138/T142. Le misure riguardano il recupero di passaggi, non il percorso applicativo Analizza → conferma → bozza.

## Disegno e input

Baseline del branch di ricerca: `a312c0651930440ff6164cf765d2f15583f3bf1c`. Il worktree applicativo era su `feature/002-implementation`, commit `acf35a6e1129f8ff15f89331fc3be68b78b1b621`, con `benchmarks/` preesistente e non tracciato. Non sono stati modificati i suoi file, cambiato il suo branch o eseguiti test applicativi. Worktree separato, commit e push sul solo branch di ricerca sono autorizzati dal mandato specifico.

| Corpus | Origine | Attese |
|---|---|---|
| N4 e fixture collegate | `txt-conflict-a.txt`, `txt-conflict-b.txt`, `txt-negations.txt`, `md-basic.md` originali della 002; dichiarazione PM di N4 dal test | 12 query/gold preesistenti; 30 giorni della Scheda A, 60 della B e dichiarazione PM a 45 giorni restano distinti |
| T082 | Originale sintetico già materializzato nel run 4 della 002, SHA-256 `a926fb1da97b8b01edfa61e67e87967a8249f6bd37e22d239fd642eb491530f6` | 16 fatti gold originali, 260 regole secondarie; 19 query incluse domande con più riscontri |
| T083 | `LargeDocumentCorpus.Multi(1300000, 90)` dal generatore C# esistente invariato, seed `20261106` | 3 documenti TXT/MD/DOCX e dichiarazione PM; 17 fatti documentali + P1/P2; 25 query, inclusi conflitti, equivalenza e riscontri complementari |

Nessun corpus T083 già materializzato è stato trovato nelle directory diagnostiche consultate: è stato usato il generatore autorizzato. Il parametro T083 è dichiarato, non presentato come replica di un run storico. Non è stata rieseguita la misura del budget Analysis della 002: questo esperimento non attesta il suo requisito oltre 2× contesto. T082 coincide invece byte per byte con l'originale diagnosticato nella 002. I corpus sono sintetici, realmente usati o generati dai test bProjects; non sono documenti cliente.

I gold di `LargeDocumentCorpus.cs` sono copiati letteralmente, senza adattamento ai risultati. Le query aggiunte sono state scritte prima delle inferenze di retrieval. La dichiarazione PM è conservata come fonte `Message`, non come documento e non come conferma dei contenuti contrattuali. Le query multi-riscontro richiedono tutte le evidenze annotate: un solo lato di un conflitto non vale Recall=1.

Il confronto principale comprende BM25, embedding densi, fusione Hybrid RRF e Hybrid con reranker, ripetuti su quattro rappresentazioni degli stessi originali:

1. `line_text`: righe del chunker storico, senza il titolo euristico nel testo indicizzato.
2. `line_context`: chunker storico invariato, incluso il titolo che deduce dal testo.
3. `docling_text`: range Primary dei chunk realmente restituiti dal parser.
4. `docling_context`: `contextualizedText` realmente restituito dallo stesso parser.

All'interno di ogni rappresentazione A/B/C/D condividono chunk, query e gold. Il passaggio tra granularità cambia intenzionalmente i chunk, non il corpus o le attese. L'esperimento aggiuntivo `block_parent` usa i blocchi originali come unità di ricerca, i soli heading già esistenti e collegamenti deterministici ai chunk padre tramite range Primary. Misura separatamente il recupero nei figli e dopo espansione. Non costruisce un grafo semantico, non inferisce relazioni e non introduce database.

## Contratti reali e riproducibilità

Sono state eseguite richieste reali a `/v1/models`, `/props`, `/v1/embeddings`, `/v1/rerank`, `/health`, `/openapi.json`, `/v1/nli`, una richiesta minima a `/v1/classify`, e otto conversioni `/convert` del parser esistente. Nessuna richiesta a un modello generativo è necessaria a questo confronto. Nessun servizio duplicato, download di modelli, unload/reload forzato o modifica al runtime.

- Router llama.cpp: build osservata `b10703-0b5be7e4a`; capacità dichiarata 4 istanze. Il guard verifica spazio per i due modelli senza richiedere eviction. Inferenze del laboratorio seriali, su modelli embedding/reranker separati dal generativo della 002. La contemporanea influenza GPU su altri processi non è stata misurata e non viene dichiarata nulla.
- Embedding: `bge-m3-Q5`, artefatto `bge-m3-Q5_K_M.gguf`, vettori di 1.024 dimensioni verificati, normalizzati L2, similarità coseno. Batch HTTP di 4 input; nessun troncamento lato client.
- Reranker: `bge-reranker-v2-m3`, artefatto `bge-reranker-v2-m3-Q5_K_M.gguf`, pooling `rank`; tutti i primi 50 candidati RRF, oppure tutto il corpus se più piccolo. Risposta Jina `results[index,relevance_score]` verificata. I punteggi reali osservati possono essere negativi: **non sono trattati come probabilità calibrate**, nonostante descrizioni generiche di API più recenti.
- NLI/classificatore: il servizio dichiara `MoritzLaurer/mDeBERTa-v3-base-mnli-xnli`, device CUDA; contratto NLI `{premise,hypothesis}` → `prediction` e `scores`. Token non esposti. Il classificatore è stato soltanto sottoposto a una prova minima di contratto, non a una nuova valutazione a sei classi.
- Parser: Docling `2.129.0`, docling-core `2.105.0`; chunker 1.024 token stimati con tokenizer MiniLM fissato, OCR disabilitato. Immagine, hash del codice, configurazione e artefatti sono in `results/parser-code.json` e `results/parser-version.json`.

Le API non espongono l'hash dei pesi GGUF né il commit Hugging Face effettivamente installato per NLI. **Queste revisioni esatte restano non verificate**; nomi di artefatto, quantizzazione e parametri osservati non le sostituiscono. I limiti di riproducibilità sono registrati in `results/models.json`; non è stata attribuita al DGX una revisione remota per supposizione.

BM25 riusa tokenizer e formula di `lab.py` (k1=1,2, b=0,75), con costruzione indice separata dalla ricerca. La stoplist rimuove anche «a» e «non», senza stemming italiano. È una baseline semplice, non PostgreSQL FTS italiano né un BM25 ottimizzato. RRF riusa la funzione esistente, k=60 sulle due classifiche complete, incluso il tie-break per ID. Risultati a punteggio lessicale zero possono quindi contribuire alla fusione. Non sono state cercate soglie o iperparametri sui gold.

Ricerca statica: nessuna implementazione RAG/RRF riutilizzabile nel prodotto è stata trovata; sono riusati BM25, coseno e RRF del laboratorio e i manifest del parser. Il piano prodotto cita PostgreSQL/pgvector, mentre la ricerca preliminare considera anche Qdrant: **queste prove in memoria non scelgono né confrontano motori di persistenza**.

## Metriche e lettura corretta

Tabelle complete: [confronto A–D](results/comparison.md), [blocchi/padri](results/parent-comparison.md). JSON con classifiche, punteggi, prime posizioni corrette, gold, provenienze, risposte reranker e tempi sono in `results/`; gli indici esatti, inclusi range originali, in `indexes/`. [Omissioni per query](results/missing-evidence.json) distingue gold fuori top 10 e assenti dai candidati rerank. Hit@1/5/10 e Recall@1/5/10/20/50 sono conservati; Recall conta evidenze distinte, non frammenti duplicati.

nDCG@10 non riportato: non sono disponibili giudizi graduati di rilevanza indipendenti; non sono stati inventati a posteriori. Le latenze per query sono misurate a indice pronto, separando embedding query, ricerca locale, fusione e reranking. Hybrid è la somma delle fasi seriali, non una promessa di prestazione applicativa. Il coseno è Python standard library, senza BLAS o database; i tempi non rappresentano un'implementazione .NET/pgvector ottimizzata. p50/p95 sono riassunti di query diverse, una misura per query, non intervalli di confidenza.

I tempi di indicizzazione distinguono cache, nuovi input e chiamate: `embedding_build` misura lavoro incrementale effettivo. `embedding-cold-work.json` ricostruisce, quando completo, il lavoro HTTP a freddo tramite corrispondenza esatta degli hash delle richieste. Gli input condivisi non vanno conteggiati due volte sommando varianti. `http-totals.json` include pilota e preparazione interrotta; non è il solo costo della campagna finale. Le cache vettoriali sono locali, derivate e ignorate da Git.

La baseline originale è stata eseguita senza modifiche su tutte le fixture supportate: 186 chunk, Hit@1 11/12, Hit@3 e Hit@5 12/12 (`original-baseline.json`). Il corpus N4 del confronto esteso contiene solo le quattro fixture pertinenti e il messaggio PM: 19 righe o 6 chunk Docling. **Hit@10 sul corpus di 6 chunk è saturo e non discrimina gli algoritmi**; per N4 conta soprattutto posizione, attribuzione alla fonte e costo del contesto.

Durante la preparazione è stato rilevato un errore nel nuovo harness: lettura di JSON UTF-8 con la codifica Windows predefinita, che alterava nove stringhe gold T082. La campagna coinvolta è stata interrotta, la lettura resa esplicitamente UTF-8 e la corrispondenza letterale dei 16+19 gold verificata prima delle misure finali. Originali e gold autorevoli non sono stati cambiati. I vettori del corpus, non coinvolti dall'errore, sono stati riusati; i tempi a freddo sono ricostruibili dal ledger HTTP. Il pilota N4 precedente alla separazione corretta della latenza embedding query è conservato in `results/pilot/` ed escluso dalle tabelle principali.

## Titoli, struttura e limiti della ricerca preliminare

Il problema non è semplicemente «parser privo di struttura». Il parser corrente produce blocchi, range, heading dove presenti e collegamenti ai chunk; il payload di evidenze che giunge al modello bProjects può però non conservare quel contesto. La diagnosi N4 della 002 documenta proprio l'assenza del titolo nel messaggio di analisi. Il laboratorio non modifica questo passaggio applicativo.

Nel TXT N4 `headingPath` è vuoto: «Scheda A» è un paragrafo, non un heading riconosciuto. **Il chunk reale conserva comunque titolo testuale e frase dei 30 giorni insieme**, con due range Primary separati. Il laboratorio storico invece deduce il titolo dalla prima riga. I due meccanismi non vanno confusi. T082 è TXT e non ha heading strutturali. Il DOCX sintetico T083 è generato con semplici paragrafi, non stili di titolo: l'assenza di una gerarchia ricca in quel formato è anche una caratteristica del corpus. Il Markdown T083 espone invece titoli e sezioni riconosciuti.

La ricerca preliminare è un'ipotesi, non una dimostrazione: un chunk più grande o più metadati non migliorano automaticamente il ranking. Ripetere un titolo generico su ogni riga può ridurre il potere discriminante lessicale; aggregare una frase importante a molto riempitivo può diluire l'embedding. La qualità di un collegamento strutturale deve essere misurata insieme alla quantità di testo che porta nel contesto.

Il generatore T082/T083 esclude esplicitamente dal riempitivo molte radici dei fatti gold e genera rumore narrativo con un vocabolario limitato. È utile per verificare dispersione e scala, ma rende il retrieval più facile rispetto a contratti reali con molte clausole quasi equivalenti. Le query sono state scritte da un solo autore sulla base dei gold: manca una valutazione indipendente su domande spontanee dei PM. Nessuna prova qui garantisce completezza generale o assenza di altri fatti applicabili.

## NLI e italiano

L'obiezione sulla lingua è pertinente. La [scheda del modello NLI](https://huggingface.co/MoritzLaurer/mDeBERTa-v3-base-mnli-xnli) dichiara pretraining multilingue e fine-tuning su MNLI inglese più XNLI; [XNLI](https://huggingface.co/datasets/facebook/xnli) non include l'italiano fra le sue 15 lingue. Non significa che il modello non abbia mai visto italiano: la [base Microsoft](https://huggingface.co/microsoft/mdeberta-v3-base) è multilingue. Significa che il fine-tuning NLI dichiarato non documenta supervisione italiana; la capacità dipende dal trasferimento tra lingue. La revisione locale dei pesi rimane non verificata.

Per separare parzialmente le cause è stato autorizzato un confronto IT/EN sugli stessi casi, con traduzioni e attese esplicite congelate prima delle richieste. È un diagnostico piccolo, con traduzioni dell'agente non revisionate indipendentemente: eventuali differenze non stimano un divario generale fra lingue.

Già sui cinque casi iniziali, italiano e inglese danno gli stessi esiti: conflitto esatto, parafrasi e ambiti diversi corretti; «entro 30» → «entro 60» e «entro 45» → «entro 30» classificati entrambi come contraddizione. Il risultato **non sostiene una spiegazione esclusivamente linguistica** di questi due errori. Le ulteriori coppie usano evidenze originali T082/T083 effettivamente recuperate, e verificano garanzia, equivalenza, neutralità, condizioni ed eccezioni. Risultati completi in [nli.json](results/nli.json) e [nli-language.json](results/nli-language.json).

Il gold NLI è un giudizio diagnostico esplicito, distinto dal gold applicativo: termini contrattuali divergenti devono restare un conflitto aperto anche quando una lettura astratta come insieme di vincoli è compatibile. Per esempio, 30 giorni dalla ricezione e 60 giorni data fattura hanno anche decorrenze differenti. L'NLI non elimina candidati, non risolve conflitti e non promuove dichiarazioni a fatti confermati.

Il confronto completo comprende **12 coppie × 2 lingue**: 7/12 corrispondenti alle attese in entrambe le lingue, senza alcun cambio di etichetta fra IT ed EN. Sono errati i due casi numerici iniziali, la rimozione della condizione sul collaudo, la rimozione dell'eccezione per i clienti Gold e il vincolo recuperato «entro 40 minuti» rispetto a «entro 60 minuti». Quest'ultimo caso conferma il problema anche usando una vera evidenza T082 recuperata. Le sette coppie collegate ai documenti risultano recuperate nella top 10 prima della valutazione NLI. Non è corretto chiamare questi punteggi una validazione generale dell'NLI italiano.

## Risposte alle domande del mandato

**1. Hybrid recupera più evidenze di BM25?** Su T082 con passaggi brevi sì: Recall@10 medio per query passa da **78,9% a 100%**. Esempi: eccezioni stagionali della fatturazione, autorizzazione agli acquisti urgenti, backup e assistenza notturna. Con la sola ricerca lessicale i gold delle ultime tre domande sono rispettivamente alle posizioni 1595, 1751 e 2787; dense li porta tutti al primo posto. Su T083 con passaggi brevi BM25 è già al 100% di Recall@10: il beneficio è soprattutto nell'ordine. Non emerge una superiorità universale: sui chunk Docling contestualizzati T083 la fusione scende al 74%, mentre BM25 resta al 100%.

**2. Il reranker migliora la posizione?** Sì, in queste configurazioni: su T082 `line_text` Hit@1 passa da 68,4% con RRF a **100%** con reranking; su T083 `line_context` da 96% a **100%**. Sui chunk Docling contestualizzati T083 recupera nella top 10 tutti i gold che RRF aveva ancora entro i 50 candidati. Non risolve invece i gold fuori dai candidati: su T082 Docling restano assenti backup S08 e reperibilità S11. Il confronto usa sempre i medesimi candidati, senza oracle basato sui gold.

**3. Quanto contano i metadati?** Su T083 il contesto euristico delle righe migliora Hit@1 RRF da 64% a 96%; sul caso N4 è utile poter collegare il titolo al passaggio originale. Tuttavia lo stesso titolo ripetuto ovunque peggiora BM25 su T082 (Recall@10 78,9% → 73,7%). I metadati non sostituiscono una buona granularità: l'embedding dei chunk grandi diluiti nel riempitivo ha Recall@10 44,7% su T082 e 33,3% su T083, contro 100% dei passaggi brevi nei confronti misurati.

**4. N4 trova la risposta della Scheda A?** Sì: il chunk Docling reale della Scheda A arriva al primo posto con tutti e quattro i metodi e conserva il titolo testuale e il valore 30 nei range originali. Anche il collegamento blocco → padre funziona: con dense il blocco di risposta della A è al secondo posto, ma il primo blocco recuperato porta al padre corretto. La Scheda B è un controllo utile: il blocco di risposta può essere al terzo/quarto posto, mentre il padre della riga-titolo porta il riscontro al primo posto. Il successo non chiude il Gap applicativo T142: non è stato chiamato il valutatore bProjects, e il conflitto con il PM a 45 giorni rimane distinto.

**5. Quanti gold si recuperano?** La tabella riporta l'unione dei gold trovati nella top 10 di almeno una query pertinente. Le omissioni per singola domanda restano visibili nella tabella di Recall e nel JSON dedicato: l'unione non le nasconde.

| Corpus / rappresentazione | BM25 | Dense | Hybrid RRF | Hybrid + reranker |
|---|---:|---:|---:|---:|
| T082, righe senza titolo | 13/16 | 16/16 | 16/16 | 16/16 |
| T082, chunk Docling con contesto | 13/16 | 7/16 | 13/16 | 14/16 |
| T083, righe con titolo euristico | 19/19 | 19/19 | 19/19 | 19/19 |
| T083, chunk Docling con contesto | 19/19 | 6/19 | 16/19 | 19/19 |

T082 contiene 5 gold iniziali, 5 centrali e 6 finali; nei passaggi brevi dense e Hybrid li recuperano tutti, inclusi i due conflitti distanti. Con Docling + reranker restano persi S08 (centro: backup) e S11 (fine: reperibilità). T083 comprende 17 evidenze documentali più 2 fatti del messaggio PM; il 19/19 include entrambe le origini e tutte le alternative annotate. Il corpus non contiene una prova completa di versioni escluse, autorizzazioni o cutoff: non sono dichiarate verificate.

**6. Quanto tempo serve?** Mediane per query, in millisecondi, a indice pronto e con embedding della query realmente richiesto ogni volta:

| Corpus / rappresentazione | BM25 | Dense | Hybrid RRF | Hybrid + reranker |
|---|---:|---:|---:|---:|
| T082, righe senza titolo | 26 | 285 | 306 | 1138 |
| T082, chunk Docling con contesto | 5 | 133 | 139 | 2179 |
| T083, righe con titolo euristico | 28 | 275 | 306 | 1133 |
| T083, chunk Docling con contesto | 5 | 138 | 142 | 2036 |

Il lavoro HTTP embedding T082 a freddo, ricostruito integralmente dagli hash delle richieste, è **122,5 s / 311.964 token** sulle righe e **35,6 s / 306.657 token** sui chunk Docling; le righe con titolo richiedono 132,4 s / 333.769 token. Sono tempi di indicizzazione separati dalla query, non la durata di estrazione di tutte le Evidence. T083 e gli altri costi incrementali sono riportati in `comparison.md`; dove la ricostruzione a freddo è incompleta, non viene inventato un totale completo. Questi dati non dimostrano un'accelerazione equivalente dell'attuale T082 applicativo.

**7. Cosa deve restare deterministico?** Identità e versioni degli originali; hash e locator; selezioni/esclusioni e cutoff; collegamenti blocco → padre; conteggio del budget; registrazione dei candidati, dell'ordine e degli errori; verifica delle citazioni e conservazione delle alternative. Modelli AI possono proporre ranking, sommari e interpretazioni; nessuno di questi output è una prova di assenza, completezza o conferma umana. Numeri e unità possono essere confrontati deterministicamente solo quando operatore, unità, decorrenza, soggetto e ambito sono espliciti e comparabili; nessuna regex decide da sola un conflitto contrattuale.

**8. Quale pipeline suggeriscono i dati?** Per una futura decisione sulla 003: mantenere originali/versioni e struttura del parser, cercare su passaggi brevi con provenienza, confrontare dense e hybrid e usare il reranker quando serve una top list più stretta. Ricostruire il contesto necessario attraverso i riferimenti ai padri, con budget esplicito e conservazione di condizioni/eccezioni. Non usare come unico indice vettoriale i chunk grandi così come sono in questi corpus. Non è ancora giustificato adottare Qdrant, un nuovo framework o un knowledge graph semantico. Il beneficio di un ulteriore indice di sommari deve essere misurato separatamente.

## Risultato della prova blocchi → padri

Il recupero sui blocchi originali del parser con dense/Hybrid/reranker raggiunge Recall@10 100% su T082 e T083, senza dover inferire titoli mancanti. Aggiungere tutti i padri non aumenta questo Recall@10, già saturo, ma amplia il contesto. Con reranking, la mediana del testo per i primi 10 candidati passa da **2.028 a 28.963 caratteri** in T082 e da **1.435 a 26.364** in T083. L'espansione locale richiede circa 0,01 ms; il costo rilevante sarebbe il maggior input del modello, non il lookup.

Il beneficio misurato più chiaro dell'espansione è N4: permette a un titolo recuperato separatamente di portare con sé la risposta. Non dimostra che espandere sempre tutti i padri sia la scelta migliore, né che i sommari o un grafo semantico siano inutili. Dimostra l'utilità di distinguere unità di ricerca e unità di contesto, misurando anche l'aumento del testo.

## Riuso del progetto RAG precedente indicato dall'utente

È stato esaminato in sola lettura il progetto precedente indicato dall'utente, al commit `39b2b19024f32cc22922b80215c2ac5b81eed004`. Nessun suo codice, documento cliente, indice o configurazione riservata è stato copiato in questo repository pubblico.

Il codice corrente conferma tre rami: embedding dei sommari, embedding dei figli e BM25 dei figli, con RRF pesata verso il `parentId`, recupero del padre e reranking. Conserva inoltre evidenze dei figli, nome documento, titolo e gerarchia. I default dichiarati sono figli di circa 300 caratteri, overlap 60, padri di circa 2.500 caratteri; non sono token e non sono automaticamente la configurazione effettiva di prove storiche. I pesi iniziali sono 0,30 / 0,30 / 0,40; il runtime può modificarli.

È un riferimento più pertinente di un semplice «chunk grande vs piccolo», ma non un risultato sperimentale trasferibile automaticamente a bProjects. Il codice del precedente generatore di sommari limita l'input ai primi caratteri configurati (default 1.200); anche il formattatore limita il contesto finale. Questi comportamenti non possono essere importati come se dimostrassero copertura completa. Un adattamento per bProjects deve usare input completi o fallimento esplicito, tenere i sommari come strumenti di ricerca derivati e tornare agli originali per ogni riscontro.

La proposta di una prova ulteriore con sommari collegati e ablation senza sommari è stata presentata separatamente. **L'utente ha scelto di non eseguirla e di chiudere le prove attuali**, documentando il riuso possibile. Non sono stati generati sommari né avviati altri modelli; l'ispezione del codice precedente non viene contabilizzata come esecuzione di quella variante.

## Esiti e verifiche mancanti

- **PASS tecnico:** contratti embedding/reranker/NLI, otto conversioni reali Complete, verifica hash e range; audit della campagna principale: 12 combinazioni corpus/chunking, 224 query, 896 risultati metodo-query, gold invariati e metriche ricalcolate dalle classifiche. Audit aggiuntivo blocchi/padri: 56 query, metriche prima/dopo espansione e dimensioni del contesto ricalcolate. I locator delle righe sono mappati agli originali o ai paragrafi DOCX decodificati in `indexes/*--line-locators.json`.
- **Limiti di qualità misurati:** omissioni di retrieval riportate, peggioramenti da chunk grandi e da alcune fusioni; NLI 5/12 errori in ciascuna lingua. Non sono trasformati in PASS tramite esclusione dei casi.
- **NOT RUN / non attestato:** analisi e bozza bProjects, risoluzione semantica dei Gap, completezza sull'intero contenuto, accettazione PM, benchmark su dati cliente, PDF, isolamento multi-progetto/versioni escluse, affidabilità di un grafo semantico, revisione esatta dei pesi non esposta dalle API.

Questi risultati giustificano ulteriori decisioni mirate sulla 003; non modificano requisiti, gold o stato dei gate della 002.
