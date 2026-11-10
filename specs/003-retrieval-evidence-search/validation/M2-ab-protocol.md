# Protocollo A/B — input del riordino (003 T027)

**Stato: PREPARATO, NON ESEGUITO.** Nessuna misura su DGX è stata fatta per questo protocollo: serve l'autorizzazione specifica del committente. T027 resta aperto, soglie invariate (Hit@1 ≥ 95%, Recall@10 ≥ 95%, nessuna regressione > 2 pt).

## Implementazione (commit `d3be4df`)

- `RerankInput` (`rerank-input-1`): per ogni candidato `Documento: <DisplayName>`, `Sezione: <percorso dei titoli>` (omessa se vuota), poi il testo indicizzato intero. Nome e titoli sono metadati non attendibili: ogni valore è ridotto a una riga sola (a capo, tabulazioni, caratteri di controllo e di formato non possono aprire un campo né imitare un'etichetta); nessun troncamento.
- `RetrievalPolicy.V2` = `retrieval-policy-2`: stessi valori di V1 (pool 50, riordino 50, RRF k=60) più `rerankInput = rerank-input-1`. La versione 1 e la sua impronta (`0c70492…2245`) non cambiano: `rerankInput` entra nel JSON canonico solo se diverso da `text-only`, e un test fissa l'impronta di entrambe. Nessuna politica diventa «corrente»: la scelta spetta alla misura e a una decisione.
- A/B senza duplicare codice: la modalità segue la politica passata a `CandidateReranker.OrderAsync` (V1 = A, V2 = B).
- Invariati: indice, `index-text-1`, embedding, ricerca lessicale, densa e RRF, gold, modelli. Nessuna migrazione.

## Varianti e controlli

| | Input del riordino | Politica |
|---|---|---|
| A | solo testo del blocco | `retrieval-policy-1` |
| B | documento + sezione + testo | `retrieval-policy-2` |

Per ogni query la ricerca ibrida è eseguita **una volta** e A e B ordinano gli **stessi candidati**; stessi documenti, query, indice, modelli e RRF. L'ordine di esecuzione A/B alterna fra le query (nessun vantaggio della prima richiesta). L'harness ferma la misura se il dataset non coincide con il manifest congelato, se i gold storici sono alterati o se A e B non sono permutazioni degli stessi candidati.

## Dataset

- **`T027-dev`** (sviluppo, 32 query): 5 documenti sintetici (`contratto-quadro`, `allegato-tecnico`, `offerta-commerciale`, `addendum-2027`, `verbale-collaudo`), con testi quasi identici e risposte diverse (pagamento 45/30/60/15 giorni, garanzia 24/12/36 mesi, penali, preavviso, assistenza), una frase **identica** in due documenti (penali 0,5%), distrattori lessicali e titoli di sezione nei file Markdown. Categorie: 15 `explicit-doc` (nominano il documento), 4 `section-ref` (nominano la sezione), 6 `implicit-unique` (nessun documento nominato, risposta unica), 7 `similar-multi` (nessun documento nominato, più documenti validi). `mentionsDocument` è dichiarato per ogni query.
- Congelato: `frozen-manifest.json` (SHA-256 con a capo LF) in `backend/tests/bProjects.IntegrationTests/Retrieval/Evaluation/T027-dev/`, committato **prima** di qualsiasi misura (commit `ebbb711`). Una modifica a query, gold o documenti ferma l'harness; cambiarli richiede un nuovo insieme (nuova cartella e nuovo manifest), mai una modifica sul posto.
- **Non è una validazione indipendente**: scritto dall'assistente dopo aver visto i fallimenti di M2 (progettato per esercitare quella classe di errori). Serve a misurare l'effetto, non a certificare la soglia.
- **Gold storici** (N4 12, T082 19, T083 23 con riscontri documentali): solo controllo di regressione. Non si usano per scegliere o tarare la forma dell'etichetta; per questi il riferimento al documento è stimato con parole chiave (euristica dichiarata).

## Validazione indipendente (condizione per dichiarare un risultato)

Richiede un autore o revisore **non coinvolto** nell'analisi dei fallimenti di M2 né nello sviluppo di `rerank-input-1`:
1. Crea una cartella `Retrieval/Evaluation/<nome>/` con i documenti, `cases.json` (stesso schema di `T027-dev`: `id`, `query`, `category`, `mentionsDocument`, `gold` con `file` e `contains` univoco nel file) e `frozen-manifest.json`.
2. Include query che citano il documento, query che non lo citano, documenti simili con risposte diverse e frasi identiche in documenti diversi.
3. Congela e committa **prima** della misura; l'assistente non modifica l'insieme.
4. Si aggiunge a `BPROJECTS_AB_SETS`. Sulla sola validazione indipendente si valuta il criterio Hit@1 ≥ 95%.

Finché non esiste, nessun risultato A/B va presentato come validazione.

## Metriche e criteri riportati dal rapporto

Hit@1, Recall@5 e Recall@10 per A, B e RRF, per insieme, per categoria e per «cita / non cita il documento»; elenco nominale delle regressioni (corrette in A, mancate in B) e dei miglioramenti; regressione sui gold storici con segnalazione oltre −2 pt su Hit@1 e Recall@10; latenza del solo riordino p50/p95 (lettura DB di testo e provenienza inclusa). Il rapporto non contiene alcun giudizio di gate.

## Comando per la misura (SOLO con autorizzazione DGX specifica)

Dalla cartella `backend/`, con `BPROJECTS_TEST_POSTGRES`, `BPROJECTS_TEST_DGX_ENDPOINT`, `BPROJECTS_TEST_DGX_MODEL` e `BPROJECTS_TEST_DGX_API_KEY` già nell'ambiente:

```bash
BPROJECTS_TEST_DGX_ALLOW=1 BPROJECTS_EVAL_OUT="$(pwd)/../specs/003-retrieval-evidence-search/validation" dotnet test --project tests/bProjects.IntegrationTests --filter-trait "Category=RetrievalAbReal"
```

Scrive `T027-ab-raw.json` e `T027-ab-report.md` in `validation/`; non tocca le baseline. Insiemi diversi: `BPROJECTS_AB_SETS="T027-dev,<nome-indipendente>"`. Durata attesa: pochi minuti (indicizzazione dei corpora, ~110 query × 2 riordini). Senza `BPROJECTS_TEST_DGX_ALLOW=1` il test risulta ignorato e non contatta alcun servizio.

## Caso neg-45 (gold e query non modificati)

«Il termine dei pagamenti è 45 oppure 30 giorni?» ha come unico riscontro il blocco di `txt-negations` che contiene la frase dei 45 giorni, un paragrafo lungo con più prescrizioni. Il riordino preferisce «Il termine di pagamento e' 30 giorni» (`txt-conflict-a`), anch'esso pertinente a metà della domanda. Ambiguità: (a) la domanda cita due valori e il gold ne copre uno; (b) la frase pertinente è una fra molte nello stesso blocco. Il dataset congelato e le baseline restano com'erano; né il gold né la query sono stati toccati.

**Proposta:** una revisione semantica indipendente dell'annotazione di neg-45 (revisore non coinvolto in T027), che stabilisca se il blocco «30 giorni» sia un riscontro valido o se la query vada riformulata. Qualunque correzione produce una **nuova versione del dataset** (nuovo file e nuovo manifest, con le metriche M1/M2 originali intatte e confrontabili) e un nuovo confronto, mai una riscrittura della baseline.

## Decisioni ancora aperte

1. Autorizzare la misura A/B su DGX con il comando sopra.
2. Indicare l'autore o revisore dell'insieme indipendente e la data entro cui congelarlo.
3. Indicare il revisore di neg-45.
4. Dopo la misura: adottare o no `retrieval-policy-2` come politica corrente (non prima).
