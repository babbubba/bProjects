# bProjects — Ricerca preliminare per la slice 003

**Data:** 8 novembre 2026  
**Stato:** studio esplorativo; **non** specifica approvata, piano o task esecutivo.  
**Vincolo:** non interferire con la chiusura della slice 002 (nessuna modifica a codice, branch, task o specifiche esistenti).

## 1. Baseline: i problemi che dobbiamo risolvere

| Problema osservato nella 002 | Implicazione per la 003 |
|---|---|
| **N4/T142:** la domanda del PM chiede cosa riporta «Scheda A»; E3 fornisce 30 giorni; il sistema non fornisce sempre al modello il nome/titolo della fonte; E1 del PM dichiara 45 giorni. | Separare risposta *documentale* e conflitto fra fonti; trasportare identità, titolo/sezione e versione documentale. |
| **T138:** Gap e riscontri originali possono trovarsi in batch differenti. Il grafo gerarchico non garantisce che un valutatore veda tutti i riscontri; oggi c'è un fail-fast oltre contesto con Gap. | Progettare valutazione dedicata con retrieval e indicatori di copertura, senza equiparare «non recuperato» a «assente». |
| **T082:** la nuova estrazione evita i troncamenti, ma nella sesta prova 165/250 unità sono state elaborate in 3600 s. Chiamata media 9 s; intervallo mediano fra chiamate 12,7 s; causa ancora da isolare. | Non scambiare il costo dell'orchestrazione con quello del modello. Il retrieval non elimina automaticamente l'obbligo di leggere tutto il documento. |
| **Benchmark classificatore mDeBERTa:** accuracy 36,1% su 72 casi con sei categorie; `Noise` mai prima classe. | Non introdurlo come filtro che possa scartare informazioni; scomporre il compito e misurare le false omissioni. |

**Fonti della baseline:** [spec 002](https://github.com/babbubba/bProjects/blob/feature/002-implementation/specs/002-project-documents/spec.md), [plan 002](https://github.com/babbubba/bProjects/blob/feature/002-implementation/specs/002-project-documents/plan.md), [T138/N4](https://github.com/babbubba/bProjects/blob/feature/002-implementation/specs/002-project-documents/validation/T130-N4-revision.md), [T082](https://github.com/babbubba/bProjects/blob/feature/002-implementation/specs/002-project-documents/validation/I5-single-real.md), [confronto A/B](https://github.com/babbubba/bProjects/blob/feature/002-implementation/docs/roadmap/slice-002-technical-closure.md).

**Vincolo funzionale:** SC-003/004 prevedono copertura dei fatti attesi annotati; una ricerca `top-k` non dimostra la completezza su un documento intero. Il risultato «nessuna evidenza pertinente recuperata» non autorizza a dichiarare che una risposta non esista nel corpus.

## 2. Architettura candidata da valutare

```text
Originale + versione immutabile
           |
    Docling strutturato
           |
 Passaggi originali + metadati
 (documentId, versionId, heading, pagina/range, hash)
           |
     +-----+--------------------+
     |                          |
 Ricerca esatta/lessicale   Embedding multilingue
     |                          |
     +------- Hybrid retrieval -+
                    |
              Reranking BGE
                    |
        Candidate pack verificabile
                    |
           Valutazione mirata Gap
                    |
       LLM composizione e confronto
                    |
     Evidence ledger / revisione PM
```

**Separare: copertura del corpus, ricerca dei candidati, valutazione della risposta e composizione finale.** La copertura deve rimanere verificabile, anche se una fase usa retrieval probabilistico.

### Parsing e conservazione della provenienza

Docling fornisce `HierarchicalChunker` e `HybridChunker`, che preservano heading e caption; il secondo introduce suddivisione e unione in base ai token. L'informazione di indicizzazione deve **derivare** dal parser e restare riconducibile al passaggio originale.

Da verificare nel codice della 002: disponibilità effettiva di titolo interno, struttura gerarchica, pagina, offset e coordinate; nessun titolo deve essere inventato. Un nome file `txt-conflict-a.txt` non equivale al titolo «Scheda A».

### Retrieval: confrontare prima di scegliere

| Variante | Cosa prova | Rischio/limite |
|---|---|---|
| **P0 — Lessicale con filtri esatti** (es. PostgreSQL full-text) | Riferimenti esatti, codici, nomi documento, numeri, basso costo operativo | Parafrasi e sinonimi |
| **P1 — Dense BGE-M3** | Parafrasi in italiano, somiglianza semantica | Similarità tematica non implica riscontro corretto |
| **P2 — Hybrid dense + lessicale** con fusione RRF | Complementarità fra semantica e stringhe esatte | Maggior indicizzazione e complessità |
| **P3 — Hybrid + BGE-reranker-v2-m3** | Miglior ordinamento dei candidati prima di chiamare l'LLM | Reranker valuta **candidati trovati**, non recupera quelli assenti |

Qdrant supporta query dense/sparse/BM25, fusione e pipeline multi-stage, oltre ai filtri su payload. **Non è ancora scelto**: prima comparare P0 con P1–P3 e misurare beneficio reale sul corpus bProjects. Se adottato, l'indice Qdrant sarà derivato/ricostruibile, non sistema autorevole di versioni o Evidence.

### Valutazione Gap e conflitti

Il componente di valutazione deve ricevere: domanda originale, fonti candidate, identità delle fonti, condizioni, citazioni e cutoff della snapshot. Nel caso N4:

- E2 «Cosa dice la scheda A ...?» può essere `AnsweredInSnapshot` solo con un riscontro attribuibile alla scheda richiesta (E3, 30 giorni).
- E1 «45 giorni» è una dichiarazione PM che va confrontata con E3, **non** un secondo riscontro al contenuto della scheda.
- Il conflitto fra dichiarazione PM e documento rimane `Open` finché il PM non decide, anche quando le frasi sono logicamente compatibili come vincoli di tipo «entro».

L'NLI generalista non deve essere arbitro finale di differenze numeriche, negazioni, condizioni e ambiti. I confronti quantitativi devono considerare operatore, unità, decorrenza, soggetto e dominio; la stessa evidenza può supportare fatti diversi, quindi *stesse fonti + stessa categoria* non dimostra sempre duplicazione.

### MAF, versioni e recovery

Studiare un eventuale stage dedicato per i Gap **solo dopo una decisione progettuale**. Congelare modello, versione prompt, versione indice, query, candidati, ordinamento, input hash e risultato per riproducibilità. Conservare il contratto `gapAssessments` se compatibile, senza reinterpretare retroattivamente vecchi run o cambiare checkpoint storici.

## 3. Esperimenti da eseguire separatamente dalla 002

**Corpus gold congelato, indipendente dalle variazioni dei prompt:**

1. **N4:** riferimento al titolo interno «Scheda A», nome file differente, risposta 30 giorni, dichiarazione PM 45 giorni.
2. **T082:** documento singolo >2× input budget, fatti distribuiti in inizio/metà/fine e contenuto narrativo irrilevante.
3. **T083:** almeno tre documenti, contraddizioni tra fonti e versioni incluse/escluse.
4. **Avversari:** Gap senza riscontro, con più riscontri, condizionale, numeri e unità, negazioni, documenti omonimi, contesti di progetto diversi.

Confrontare P0–P3 sugli **stessi chunk e stessi gold**. Misurare:

| Metrica | Scopo |
|---|---|
| Recall@10/20/50 | Quante evidenze annotate vengono recuperate per ogni Gap |
| nDCG@10 | Qualità dell'ordinamento/reranking |
| Falsi `AnsweredInSnapshot` | Rischio di considerare risolta una domanda senza fonte appropriata |
| Conflitti reali conservati / falsi conflitti | Affidabilità della riconciliazione |
| Provenienze esatte, versioni/cutoff e leakage | Integrità e isolamento |
| Copertura delle porzioni, evidenze mancanti | Distinguere indicizzazione completa da valutazione semantica completa |
| Tempo p50/p95, token e chiamate LLM per fase | Confrontare efficienza e deadline senza aumentare i limiti |

**Nota sulle soglie:** le soglie di accettazione di una nuova strategia devono essere concordate su gold rappresentativi. Il `100%` richiesto nei casi nominali già annotati da SC-003/004 non è una promessa generale di correttezza del motore di ricerca.

## 4. Decisioni ancora aperte

1. **Perimetro funzionale 003:** solo retrieval e Gap oppure anche ottimizzazione dell'estrazione completa? Dipende dall'esito finale e dalla diagnosi T082.
2. **Backend dell'indice:** PostgreSQL/lessicale come baseline oppure Qdrant denso-ibrido, a fronte di misurazioni.
3. **Granularità:** chunk originali, Evidence atomiche o due indici collegati.
4. **Completamento oltre contesto:** come comprovare copertura e come comportarsi quando i riscontri potrebbero non essere stati recuperati.
5. **T138:** eventuale adattatore A implementato nella 002 vs sua realizzazione organica nella 003.
6. **NLI/classificatori:** evitare filtro distruttivo; testarli soltanto su compiti ben circoscritti con gold appropriati.

## 5. Raccomandazione preliminare

**Priorità di ricerca:** (a) metadati strutturali e provenienza, (b) benchmark retrieval lessicale vs dense vs hybrid+rereanker, (c) valutatore Gap dedicato che separi risposte e conflitti, (d) misure di coverage e astensione esplicita.

**Non decidere ancora:** adozione definitiva di Qdrant, cambiamento globale del flusso di estrazione, nuove soglie, approvazione di T138-A, passaggio a Spec Kit o definizione dei task 003.

## 6. Documentazione esterna verificata

- [Docling — Chunking](https://docling-project.github.io/docling/concepts/chunking/)
- [Qdrant — Hybrid e multi-stage queries](https://qdrant.tech/documentation/search/hybrid-queries/)
- [Qdrant — Filtering](https://qdrant.tech/documentation/search/filtering/)
- [Qdrant — Retrieval relevance e metriche](https://qdrant.tech/documentation/search-evaluation/retrieval-relevance/)
- [BGE-M3 — Dense, sparse e multi-vector](https://bge-model.com/bge/bge_m3)
- [BGE Reranker v2 M3 — Model card](https://huggingface.co/BAAI/bge-reranker-v2-m3)
- [Microsoft Agent Framework — Checkpoints](https://learn.microsoft.com/agent-framework/workflows/checkpoints)
