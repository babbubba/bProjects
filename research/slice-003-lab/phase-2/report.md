# Slice 003 · Gap, estrazione autonoma e SKRAG

Campagna del 9 novembre 2026. Gli input, il gold, i parametri e i risultati storici usati nel replay sono fissati per hash. Gli originali sono sintetici. Gli esiti qui riportati riguardano il laboratorio e i suoi corpus; le verifiche applicative della 002 hanno i propri contratti. [Manifesto](freeze.json) · [Protocollo](protocol.json) · [Campagna precedente](../report-finale.md)

## Codice SKRAG verificato

Riferimento locale `italcomspa/SkRag.Aspire`, commit `39b2b19024f32cc22922b80215c2ac5b81eed004`. `TripleHybridRetriever.RetrieveSingleQueryAsync` consulta tre liste da `2 × TopK` ciascuna. `ApplyRrf` somma `peso / (60 + posizione)` per ogni occorrenza del padre. Più figli densi possono contribuire allo stesso padre. Il ramo lessicale applica una deduplicazione per padre prima della fusione. `CurrentForSource` risolve la configurazione per fonte. I valori attivi di default nel codice sono `HybridVectorWeight=0,60` e `HybridBm25Weight=0,40`; `HalfVectorWeight` divide 0,60 a metà. Risultano quindi 0,30 sommari, 0,30 figli densi, 0,40 figli lessicali. TopK è 20, quindi ogni ramo parte da un massimo di 40. Gli override di un'installazione SKRAG possono differire e non sono stati usati come input della prova. [Codice al commit](https://github.com/italcomspa/SkRag.Aspire/tree/39b2b19024f32cc22922b80215c2ac5b81eed004)

- `IndexerService.GenerateSummaryAsync` passa i primi **1.200 caratteri UTF-16** di `parent.RawContent` al modello. Il prompt chiede 2–4 frasi e il default di output è 512 token con temperatura 0,1. Il vettore è calcolato sul sommario generato; i figli hanno embedding separati.
- `OllamaRerankService.TruncateDocumentForRerank` limita `PageContent` a **2.400 caratteri UTF-16**, compreso il marcatore di troncamento. Il reranker restituisce al massimo 10 padri. L'orchestratore SKRAG può astenersi per confidenza bassa. Il laboratorio usa l'ordine del reranker BGE e delega il giudizio sulla sufficienza al valutatore Gap comune.
- `ContextFormatter.Format` concatena intestazione e testo dei padri, poi taglia l'intero risultato a **12.000 caratteri UTF-16** e aggiunge un avviso. L'avviso segnala il taglio senza indicare quali evidenze siano state escluse. Il confezionamento comune del laboratorio usa invece interi passaggi e registra gli ID che eccedono il budget. Il formato originale è misurato separatamente.
- SKRAG conserva file, titolo, sezione, H1/H2/H3 e riscontri figli sul padre. Nel commit verificato il percorso ispezionato è query → ranking → risposta; l'esplorazione B3 del laboratorio usa domande generiche per categoria distribuite per zona e costituisce un adattamento sperimentale.

L'adattatore mantiene la semantica RRF per occorrenza del padre, le finestre e i tagli verificati. Condivide i passaggi Docling e gli endpoint BGE del laboratorio. Nella prima ablazione usa il BM25 del laboratorio con candidati a punteggio positivo. Nel codice SKRAG la classe `Bm25Retriever` usa `StandardAnalyzer` e `QueryParser` di Lucene.NET 4.8.0-beta00016 e mantiene il primo figlio per padre entro i primi 120 hit. L’`IndexSearcher` mantiene `DefaultSimilarity`, che in quella versione usa TF-IDF. Il secondo confronto riusa questa versione di Lucene e replica la deduplicazione. [IndexSearcher al tag fissato](https://github.com/apache/lucenenet/blob/Lucene.Net_4_8_0_beta00016/src/Lucene.Net/Search/IndexSearcher.cs#L82-L95) · [DefaultSimilarity](https://github.com/apache/lucenenet/blob/Lucene.Net_4_8_0_beta00016/src/Lucene.Net/Search/Similarities/DefaultSimilarity.cs) I default SKRAG prevedono riscrittura delle query, che qui è disattivata per isolare i componenti richiesti. Vettori, BM25, chunking, configurazione sorgente, soglia e orchestrazione differiscono quindi da una replica integrale dell'applicazione SKRAG. [Adattatore](retrieval.py)

## A · Gap

Il set congelato contiene i dieci casi richiesti e due controlli per il limite dei sommari. Il corpus diagnostico riusa 6 padri N4 e 22 padri T083, inclusi passaggi di disturbo, più una versione esclusa e due controlli sintetici. La coorte arricchita è comune alle configurazioni. La selezione delle fonti in snapshot precede l'indicizzazione. I prompt ricevono query e contesto, mentre il gold resta nel programma di scoring. [Corpus e domande](a-input.json) · [Gold](a-gold.json)

Il replay su **sei query storiche identiche**, con gli originali N4 e T083 della fase precedente, trova tutte le evidenze attese nel budget comune per Hybrid, Hybrid con padri e fusione SKRAG senza sommario/reranking. Nel caso Scheda A il pacchetto contiene sia i 30 giorni documentali sia i 45 del PM. Nel conflitto garanzia include 24 e 36 mesi. Nel caso penali include il rinvio del contratto e la formula dell'allegato. Questo misura disponibilità al valutatore. Il caso della versione esclusa, i falsi `AnsweredInSnapshot`, le citazioni e la conservazione dei conflitti richiedono le nuove chiamate su A. [Disponibilità nel replay](results/a-historical-availability.json)

### Valutazione generativa dei dieci Gap

La prima ablazione misura quaranta risposte Qwen sui dieci casi, più otto sui controlli Orione/Vega. Tutte usano lo stesso modello `Qwen3.6-35B-Smart`, lo stesso prompt, temperatura 0, thinking disabilitato e budget di 12.000 caratteri UTF-16 per le fonti. Lo slot da 131.072 token e il modello coincidono con quelli documentati nella 002. Il prompt e il formato ridotto del laboratorio sono dedicati all’esperimento. [Runtime](results/runtime.json) Le due prove di controllo sono escluse dalle tabelle Gap. Il conteggio di citazioni somma risposte e lati dei conflitti, incluse ripetizioni.

| Metodo | Stato corretto | Stato e motivo corretti | Esito completo verificabile | Falsi AnsweredInSnapshot | Citazioni invalide grezze / dopo recupero ID | Conflitti persi grezzi / dopo recupero ID | Gruppi di conflitto spurii |
|---|---:|---:|---:|---:|---:|---:|---:|
| Hybrid + reranker figli | 8/10 | 6/10 | 4/10 | 0 | 12 / 1 | 2 / 1 | 2 |
| Hybrid + reranker figli + padri | 10/10 | 7/10 | 2/10 | 0 | 15 / 2 | 2 / 1 | 1 |
| SKRAG senza sommari | 10/10 | 7/10 | 2/10 | 0 | 13 / 1 | 2 / 2 | 2 |
| SKRAG con sommari | 10/10 | 7/10 | 2/10 | 0 | 13 / 1 | 2 / 2 | 2 |

L'esito completo richiede stato e motivo previsti, risposta fedele con supporti originali validi e conflitti annotati preservati senza gruppi spurii. Superano il criterio A01, A02, A07 e A08 per Hybrid, A02 e A10 per ciascun metodo con padri. Il recupero diagnostico dell'ID separa `id | file` solo se il file coincide con quello dell'ID e la citazione è letterale in quel passaggio. I valori grezzi conservano il difetto dell'output. Tutti i 40 oggetti sono decodificabili come JSON. [Score A e citazioni](results/ac-scores.json) · [Gold](a-gold.json)

| Caso | Riscontro |
|---|---|
| A03 parziale | Tutti riconoscono il motivo parziale. I quattro valutatori mescolano comunque il pagamento N4 con termini T083 di un altro progetto e producono un gruppo spurio. |
| A04 garanzia discordante | Hybrid e Hybrid con padri hanno entrambi i lati nel contesto e li riportano con intestazioni copiate nell'ID. Hybrid usa il motivo fuori enum `conflitto`; con i padri usa `parziale`. In entrambe le configurazioni SKRAG il riscontro da 24 mesi manca dal pacchetto e il conflitto si perde prima della valutazione. |
| A05 fonte diversa | I quattro metodi mantengono Open. Usano `assente` o `parziale` invece di identificare esplicitamente il riscontro dell'allegato come fuori fonte. |
| A06 e A07 | Le quattro configurazioni preservano la condizione del collaudo e l'eccezione Gold nello statement. Sette citazioni su otto richiedono una correzione di ID o testo, contando una citazione per risposta. |
| A08 versione esclusa | La versione con bonifico è assente dai pacchetti di tutti i metodi. Tutti mantengono Open. I metodi con padri scelgono `parziale` sulla base di altri termini di pagamento, anziché `assente` per la modalità richiesta. |
| A09 Scheda A e PM | Il testo dei 30 giorni e il messaggio PM dei 45 giorni sono disponibili in tutti i pacchetti. Hybrid resta Open e accosta impropriamente due clausole da 30 giorni. I metodi con padri rispondono 30 giorni, ma omettono il conflitto con il PM. |
| A10 penali multifonte | Tutti ricevono il rinvio e la formula. Hybrid dichiara Open parziale; con i padri i tre valutatori danno importo, base, tetto e fonte corretti. |

I gruppi spurii usano spesso frasi esistenti, accostate fuori dal loro ambito. Il loro errore è semantico e distinto dalla validazione letterale della citazione. La coorte combina corpus di progetti sintetici diversi per osservare questo tipo di confusione. In applicazione, il filtro di progetto va eseguito prima del ranking. Sul pilot, i padri migliorano lo stato in A09 e A10 e peggiorano l'esito grezzo delle citazioni; il beneficio sulla valutazione completa dei Gap non è dimostrato.

Sulle 12 chiamate per metodo, Hybrid usa **17.096 token di input** e 30,5 s HTTP. L'espansione usa **36.512 token** e 43,8 s. SKRAG senza sommari usa **36.201 token** e 40,0 s. SKRAG con sommari usa **36.441 token** e 31,7 s. Sono token del prompt intero riportati dal server e durate di una singola campagna seriale. Per entrambi i controlli i quattro metodi enunciano correttamente il valore. Solo Hybrid su Orione fornisce anche una citazione originale valida. [Uso effettivo per metodo](results/call-totals.json)

### Conferma con il ramo lessicale Lucene

Altre 20 valutazioni Gap e quattro controlli sullo stesso corpus, con indice dei figli, vettori dei sommari, reranker, prompt e budget invariati. La ricerca lessicale usa il comportamento verificato del commit SKRAG. Le 24 risposte terminano e sono tutte JSON decodificabili.

| Variante SKRAG con Lucene | Stato corretto | Stato e motivo corretti | Esito completo verificabile | Falsi AnsweredInSnapshot | Citazioni invalide grezze / dopo recupero ID | Conflitti persi grezzi / dopo recupero ID | Gruppi spurii |
|---|---:|---:|---:|---:|---:|---:|---:|
| Senza sommari | 10/10 | 7/10 | 3/10 | 0 | 11 / 1 | 2 / 2 | 2 |
| Con sommari | 9/10 | 6/10 | 2/10 | 0 | 15 / 3 | 2 / 2 | 3 |

Senza sommari, A02, A06 e A10 superano l'intero criterio. Con sommari passano A02 e A10. Il conflitto A09 è perso per motivi diversi. Senza sommari il modello riceve il messaggio dei 45 giorni e lo omette. Con sommari il messaggio esce da TopK prima del reranking e il modello risponde Open. Le risposte al controllo Orione/Vega esprimono ancora i valori corretti, ma le citazioni non sono letterali. Su 12 chiamate per variante il modello usa 36.851 token di input e 35,2 s HTTP senza sommari, 36.687 token e 41,8 s con sommari. [Ranking Lucene](results/ac-lucene) · [Risposte Lucene](results/gap) · [Token e tempi](results/call-totals.json)

## B · Estrazione senza elenco di fatti

Il TXT T082 ha lo SHA-256 della prova originale. Le 260 regole sono estratte letteralmente applicando le tre forme di `LargeDocumentCorpus.BulkRule` al testo immutabile, con un'occorrenza per regola. I 16 fatti principali restano quelli originali. Il campione arricchito include 24 padri, 183 blocchi, 16 fatti principali, 33 regole secondarie e controlli di narrativa. La posizione del campione è congelata. Nessun fatto o domanda è inserito nel prompt. L'estrattore conserva il principio della 002 di citare un intervallo originale e tenere insieme vincoli, condizioni ed eccezioni. Usa un output ridotto a identificativo, citazione e statement, comune a B1/B2/B3. [Campione](b-input.json) · [Gold con offset originali](b-gold.json) · [Generatore](../../../backend/tests/bProjects.IntegrationTests/LargeDocumentCorpus.cs)

| Misura a monte del modello | B1 esaustivo | B2 selettivo |
|---|---:|---:|
| Blocchi pilot | 183 | 88 |
| Caratteri originali pilot | 69.358 | 32.545 |
| Chiamate pianificate pilot | 6 | 4 |
| Fatti principali disponibili nel pilot | 16/16 | 14/16 |
| Regole secondarie disponibili nel pilot | 33/33 | 33/33 |
| Blocchi sul T082 intero | 3.116 | 1.342 |
| Caratteri originali sul T082 intero | 1.317.753 | 572.271 |
| Chiamate pianificate sul T082 intero | 112 | 64 |
| Regole secondarie disponibili sul T082 intero | 260/260 | 260/260 |

I conteggi delle chiamate pianificate derivano dalla partizione locale del runner. I limiti di gruppo di B2 sono 9.500 caratteri e 24 blocchi, contro 12.500 caratteri per B1. Il risparmio teorico sul corpus intero è 48 chiamate (42,9%) e 56,6% dei caratteri originali, con due omissioni prima dell'inferenza. S04 contiene l'eccezione fatture di agosto e dicembre espressa con «Fanno eccezione»; S16 contiene «terza domenica di ogni mese». Il secondo costituisce anche un lato del conflitto sulla manutenzione. Il gold non è stato usato per modificare il filtro. La copertura delle 260 regole riflette anche il fatto che tutte contengono quantità nel sintetico. [Piano pilot](results/b-offline-plans.json) · [Proiezione corpus intero](results/b-full-projection.json) · [Audit selezione](b-selection-audit.json)

B3 prova un uso senza domande specifiche dei componenti SKRAG. Sommarizza i padri del pilot, cerca con quattro probe generiche prefissate, seleziona al massimo un padre per probe e terzo del documento, poi usa lo stesso estrattore. Il costo dei sommari va incluso nel confronto delle chiamate e dei token. La selezione produce proposte da originali e la misura vale sul pilot arricchito. [Script generativo](generation.py) · [Scoring semantico e provenienza](score.py)

### Misure Qwen sul campione

| Metrica osservata | B1 esaustivo | B2 selettivo |
|---|---:|---:|
| Fatti principali corretti con provenienza | 15/16 | 10/16 |
| Fatti principali omessi o senza provenienza | 1 | 6 |
| Regole secondarie corrette con provenienza | 33/33 | 21/33 |
| Estrazioni irrilevanti nelle risposte leggibili | 0 | 4 |
| Negazioni gold conservate e ancorate | 3/3 | 2/3 |
| Citazioni con identificativo errato anche dopo normalizzazione | 1 | 1 |
| Chiamate di estrazione | 6 | 4 |
| Risposte JSON dirette valide | 3/6 | 2/4 |
| Token input / output | 18.148 / 3.747 | 8.837 / 6.714 |
| Tempo HTTP inferenza | 68,3 s | 112,0 s |
| Tempo totale runner | 76,6 s | 117,1 s |

I giudizi confrontano proposizione completa e citazione. La revisione manuale convalida parafrasi equivalenti e, per B1, S06, S08 e S15 quando tutte le parti della stessa clausola sono presenti con la stessa citazione. La tabella misura la copertura semantica dopo due correzioni meccaniche dichiarate: rimozione di una recinzione Markdown che racchiude tutto il JSON e rimozione delle parentesi quadre intorno agli ID. I payload originali conservano i difetti. Un batch B2 da 4.096 token termina con JSON incompleto e non contribuisce alla copertura. Il conteggio degli irrilevanti riguarda le risposte decodificate. [Scoring](results/b-scores.json) · [Adjudicazioni](extraction-review.json) · [Chiamate e token](results/call-totals.json)

- **S04 e S16** restano fuori dall'input di B2. B1 conserva sia l'eccezione stagionale sia la terza domenica del mese.
- **S07, S08, S15 e 12 regole secondarie** arrivano al modello nel batch B2 che esaurisce l'output. Tra gli effetti osservati ci sono la perdita del divieto di reso a Verona e della finestra di manutenzione del primo sabato.
- **S12** viene enunciato in entrambi gli approcci con il canale PEC, ma la citazione è legata a un blocco diverso da `b2765`. È un errore di provenienza e la regola non conta fra le estrazioni verificabili.
- B2 riduce l'input del modello del **51,3%** e le chiamate del **33,3%**. Sul campione impiega **52,9%** di tempo totale in più e perde cinque fatti principali e dodici regole secondarie rispetto a B1. L'aumento dell'output e il batch troncato spiegano perché meno input e meno richieste non hanno prodotto una prova più rapida.

### B3 · Esplorazione a categorie

B3 seleziona 6 dei 24 padri e 49 dei 183 blocchi. Tutti i 23 gold consegnati al modello vengono estratti con citazione valida dopo la normalizzazione meccanica degli ID. Il risultato è **3/16 fatti principali**, **20/33 regole secondarie**, **26 omissioni di selezione** e zero estrazioni irrilevanti nelle due risposte. Conserva una delle tre negazioni gold. Le mancanze in questa prova sono attribuibili alla selezione dei padri.

| Fase B3 | Chiamate | Token input / output | Tempo HTTP |
|---|---:|---:|---:|
| Generazione sommari | 24 | 9.461 / 2.532 | 47,4 s |
| Embedding sommari e probe | 10 | 2.670 / — | 1,1 s |
| Estrazione | 2 | 4.800 / 1.658 | 29,1 s |

La preparazione e selezione richiedono **95,8 s** di wall clock, con cache dei vettori dei figli già disponibile. Il runner termina in **127,8 s**. Le due chiamate di estrazione presuppongono 24 chiamate generative ai sommari. La campagna misura questo costo sullo stesso campione B1/B2. [Padri e probe scelti](results/b3-selection.json) · [Risposte e omissioni](results/b-scores.json)

## C · Contributo dei componenti

Replay su **56 query storiche**. Nel confronto precedente sugli stessi blocchi il solo BM25 aveva Recall@10 del 78,9% su T082 e 100% su N4 e T083. Dense BGE-M3 e Hybrid avevano Recall@10 del 100% su questi corpus. Il pilot A/C usa quei componenti come controlli senza rieseguire la campagna. [Misure storiche](../results/parent-comparison.md) Hybrid riusa il reranking reale sui figli. «+ padri» mantiene esattamente il ranking dei figli e deduplica i padri in ordine di primo incontro. «RRF padre» applica i pesi 0,30 figli densi e 0,40 figli BM25 sullo stesso indice, limitando i rami a 40 e i padri a 20, senza sommari e senza reranking dei padri. Questa terza riga misura la configurazione combinata e non isola l'effetto del reranking. Le tre configurazioni passano i primi dieci elementi al confezionamento comune con budget di 12.000 caratteri UTF-16.

| Corpus | Metodo | Recall@5 | Recall@10 | Recall@20 | Tutti i gold nel contesto | Caratteri contesto p50 |
|---|---|---:|---:|---:|---:|---:|
| N4, 12 query | Hybrid | 100% | 100% | 100% | 12/12 | 1.897 |
| N4 | Hybrid + padri | 100% | 100% | 100% | 12/12 | 1.668 |
| N4 | RRF padre | 100% | 100% | 100% | 12/12 | 1.668 |
| T082, 19 query | Hybrid | 100% | 100% | 100% | 19/19 | 3.187 |
| T082 | Hybrid + padri | 100% | 100% | 100% | 19/19 | 11.675 |
| T082 | RRF padre | 84,2% | 94,7% | 94,7% | 16/19 | 11.659 |
| T083, 25 query | Hybrid | 100% | 100% | 100% | 25/25 | 2.726 |
| T083 | Hybrid + padri | 100% | 100% | 100% | 25/25 | 11.372 |
| T083 | RRF padre | 100% | 100% | 100% | 22/25 | 9.673 |

La RRF per padre perde S11 (reperibilità a Padova) già prima del confezionamento. Il figlio è primo nel ranking denso e assente dal ramo lessicale a punteggio positivo. Il suo padre scende al 27º posto dopo la fusione e resta oltre TopK=20. Su T082 S06 e la query che chiede entrambi i termini di pagamento perdono evidenze nel budget. Su T083 M03 e M05 sono presenti nella classifica, ma assenti nei pacchetti di tre query, compresa la domanda che richiede entrambi i riscontri sui ricambi. Il ranking e la selezione di contesto sono due punti di perdita distinti. [Replay con omissioni](results/historical-replay.json)

Il taglio originale di 1.200 caratteri, applicato ai padri Docling storici, esclude la frase gold integrale in **12/16 fatti T082** e **12/19 evidenze T083**, comprese le due del messaggio PM nel denominatore T083. Per T082 S06, S08, S09 e S12 mancano persino dall'input del reranker limitato. I 1.200 caratteri fanno parte della configurazione SKRAG; questi conteggi riguardano i padri del laboratorio, la cui dimensione differisce dai padri nativi SKRAG. Sul controllo Orione la clausola è nel figlio e nel padre oltre il limite, mentre la clausola Vega è all'inizio del padre. Il ramo dei figli resta la via per recuperare il testo originale oltre il prefisso. [Audit dei prefissi](results/prefix-audit.json) · [Controlli sintetici](a-input.json)

### Ablazione con sommari reali sul pilot A/C

Il confronto usa le stesse 12 query, 173 figli e 30 padri eleggibili per tutte le configurazioni. Le tre righe SKRAG senza suffisso Lucene usano il BM25 comune. Le due query senza evidenze attese sono escluse da Recall e dalla colonna sul contesto. Per SKRAG Recall@20 riguarda l'ordine esteso dei candidati; il contesto generativo rispetta TopN=10. Le due dichiarazioni PM partecipano come passaggi originali senza un vettore di sommario.

| Metodo | Recall@5 | Recall@10 | Recall@20 | Tutte le evidenze nel pacchetto | Caratteri p50 |
|---|---:|---:|---:|---:|---:|
| Hybrid + reranker figli | 95% | 100% | 100% | 10/10 | 2.270 |
| Hybrid + reranker figli + padri | 95% | 100% | 100% | 10/10 | 10.218 |
| SKRAG senza sommari, reranker padri | 95% | 95% | 100% | 9/10 | 10.018 |
| SKRAG con sommari, reranker padri | 95% | 95% | 100% | 9/10 | 9.933 |
| SKRAG con sommari, senza reranking | 60% | 85% | 100% | 5/10 | 10.141 |
| SKRAG Lucene senza sommari, reranker padri | 95% | 95% | 100% | 9/10 | 10.209 |
| SKRAG Lucene con sommari, reranker padri | 90% | 90% | 95% | 8/10 | 10.216 |

Il ramo dei sommari sposta la formula delle penali M11 dal quarto al terzo posto prima del reranking, ma sposta il rinvio M06 dal terzo al quarto. Entrambe le evidenze arrivano comunque nel pacchetto. Nel caso A09 la dichiarazione PM passa dall'ottavo all'undicesimo posto prima del reranking e viene recuperata di nuovo dal reranker. Nessuna delle variazioni dei sommari aumenta il numero di query con tutte le evidenze disponibili dopo reranking. Nel confronto con Lucene originale il messaggio PM N4 esce dai 20 padri quando si aggiungono i sommari. Il Recall@10 scende da 95% a 90% con lo stesso ramo lessicale, gli stessi vettori figli e lo stesso reranker. [Rank lessicali](results/lucene-lexical.json) L'ablazione senza reranking mostra un calo di copertura del pacchetto da 9/10 a 5/10 sullo stesso indice a tre rami.

Orione ha il vincolo a 73 ore oltre il prefisso. Il sommario generato parla del cortile, degli abitanti e delle fotografie; l'indice sommari pone il padre al 17º posto. La fusione mantiene il padre al quarto posto grazie ai figli e il pacchetto finale conserva la frase originale. Vega ha la regola a 91 ore prima del limite, il sommario la conserva e il padre è primo nel ramo sommari. Entrambe le configurazioni con reranking recuperano entrambi i controlli. Nel conflitto garanzia, M04 arriva al 5º posto dopo la fusione completa, ma la clausola dei 24 mesi è oltre il prefisso del reranker. M04 passa al 19º posto dopo reranking, oltre TopN, con e senza sommario. Nel controllo successivo con i medesimi 20 padri nello stesso ordine, togliendo solo il taglio del testo, M04 sale dal 19º al 2º posto e M13 resta primo. Questo isola l’effetto del limite su quella query. [Controllo del reranker](results/rerank-prefix-diagnostic.json)

| Indicizzazione incrementale osservata | Chiamate | Token input / output | Tempo HTTP |
|---|---:|---:|---:|
| Embedding figli, 3 input nuovi | 1 | 315 / — | 1,1 s |
| Generazione dei 28 sommari | 28 | 9.863 / 2.443 | 47,5 s |
| Embedding sommari, 27 input distinti | 7 | 2.497 / — | 1,0 s |

Il wall clock di preparazione dell'indice misura 2,6 s senza sommari e 93,9 s con sommari, inclusi cache e controlli di disponibilità prima delle richieste. Gran parte dei vettori dei figli era già nella cache. Questi numeri misurano il lavoro incrementale effettivo. [Score C](results/ac-scores.json) · [Sommari](results/ac-index-full.json) · [Ledger e token](results/call-totals.json)

## Lettura per la slice 003

1. **Vantaggio di SKRAG.** Sul pilot Hybrid con reranker sui figli ottiene Recall@10 del 100%. SKRAG con reranker sui padri e ramo Lucene verificato ottiene il 90% con sommari e il 95% senza sommari. L’ablazione con BM25 comune ottiene il 95% in entrambi i casi. Il confronto riguarda la baseline del laboratorio e la coorte arricchita.
2. **Componenti.** Il collegamento ai padri è utile per trasportare titolo, ambito e riscontri vicini. Espandere tutti i padri costa circa 3,7× caratteri su T082 e 4,2× su T083 a parità di gold disponibile nel replay. Mantenere separati il ranking dei figli e la decisione su quanto contesto espandere.
3. **Sommari.** Il limite di ingresso crea un punto cieco misurabile. Il sommario di Vega conserva la clausola iniziale, quello di Orione omette la clausola oltre il limite. Il ramo figli salva il riscontro. L'indice aggiunge costo. Con BM25 comune la copertura dopo reranking è invariata; con il ramo Lucene verificato perde il riscontro PM N4.
4. **Valutazione Gap.** L'espansione porta lo stato da 8/10 a 10/10, soprattutto su A09 e A10. L'esito completo passa da 4/10 a 2/10 per errori di risposta e ancoraggio. Il conflitto PM viene perso anche con entrambi i lati disponibili. Nel ramo Lucene la variante con sommari perde inoltre il messaggio prima della valutazione.
5. **Riduzione delle inferenze T082.** La selezione deterministica testata riduce la quantità di testo e il numero di batch pianificati, con perdita accertata di S04 e S16. Nel pilot la riduzione da 6 a 4 chiamate perde cinque fatti e dodici regole verificabili rispetto a B1 e aumenta il tempo. Il confronto sul documento intero resta una proiezione a monte del modello.
6. **Attribuzione degli errori.** S11 nel replay e S04/S16 nella selezione B2 sono errori a monte del modello. S06 e i termini di pagamento T082 con RRF padre, M03/M05 su T083, sono omissioni durante il confezionamento. A04 mostra anche un errore di ranking dei padri, mentre A09 e il mancato riscontro multifonte di Hybrid A10 dipendono dal valutatore con fonti disponibili. Il batch troncato B2 e gli ID errati per S12 dipendono dall'output generativo.
7. **Architettura candidata.** Applicare filtri di progetto, versione ed esclusioni prima di BM25/dense, ricercare sui figli originali, rerankare i figli e aggiungere solo titolo, heading e finestre originali rilevanti tramite il collegamento al padre. Usare l'attuale Evidence ledger per citazioni e alternative, il budget del modello per confezionare il pacchetto e un valutatore Gap dedicato per stato, motivo e conflitti separati. Registrare cosa è stato candidato e cosa è rimasto fuori dal pacchetto. Tenere l'estrazione autonoma separata dal retrieval a domanda. Una selezione preliminare può ordinare il lavoro e richiede una seconda passata sui passaggi esclusi quando l'obiettivo è la copertura completa.

## Riproduzione

Eseguire dalla radice del worktree di ricerca con Python 3.13.18 e le variabili DGX configurate fuori da Git. Per il solo controllo Lucene serve anche .NET SDK 10.0.403. Il replay e i piani offline non richiedono DGX. La campagna ha atteso che si liberasse lo slot usato dalla prova T082. Prima di ogni richiesta di inferenza il runner verifica che la prova applicativa locale e lo slot generativo siano liberi. Le chiamate sono seriali. I JSON in `results/calls/` conservano payload sintetici, risposte e uso token restituito dal runtime. Un riavvio riusa risposte coerenti con i payload già registrati. Per misure a freddo serve una nuova campagna distinta dalla cache della fase precedente.

```text
python research/slice-003-lab/phase-2/retrieval.py replay
python research/slice-003-lab/phase-2/generation.py offline
python research/slice-003-lab/phase-2/generation.py B2
python research/slice-003-lab/phase-2/generation.py B1
python research/slice-003-lab/phase-2/retrieval.py no-summary
python research/slice-003-lab/phase-2/retrieval.py full
python research/slice-003-lab/phase-2/generation.py gap --variants hybrid expanded sk_no_summary sk_full
python research/slice-003-lab/phase-2/generation.py B3
python research/slice-003-lab/phase-2/retrieval.py rerank-diagnostic
dotnet build research/slice-003-lab/phase-2/lexical/Lexical.csproj -c Release
python research/slice-003-lab/phase-2/retrieval.py lucene
python research/slice-003-lab/phase-2/generation.py gap --variants sk_lucene_full sk_lucene_no_summary
python research/slice-003-lab/phase-2/score.py
python research/slice-003-lab/phase-2/measure.py
```
