# M3 — prima campagna reale T040 (retrieval → stadio Gap)

- Data: 2026-11-10, una sola esecuzione autorizzata (solo T040, `BPROJECTS_TEST_DGX_ALLOW=1`); nessun retry, nessuna modifica di configurazione, gold, soglie, attese, prompt o schemi.
- Commit di partenza `ce2c66e` (+ correzione asserzione del rischio A, nello stesso commit di questo rapporto). Manifesto `T040-campaign-manifest.json` v2, hash congelato `fbc0ecb348c8568bf8f799a0c1c4b79161087e95c2f2f2621fb2f713da79eefb` (verificato dai 44 test offline `RetrievalGapCampaignTests`, superati prima del run).
- Comando: `dotnet test --solution bProjects.sln --no-build -- --filter-class "*RetrievalGapStageMafRealTests*"` (selezionato un solo test: `RetrievalGapStageMafRealTests+Campaign.T040_…`). Durata 5 min 46 s.

## Configurazione effettiva e prerequisiti
- Definizione v7, politica `retrieval-policy-1`, LLM `Qwen3.6-35B-Smart`, embedding `bge-m3-Q5`, reranker `bge-reranker-v2-m3` (tutti e tre presenti nel router, stato `sleeping`). Profilo misurato: W=32768, O=16384, M=1024, thinking=off ⇒ B=15360 (compatibile, `RejectProfile` superato).
- PostgreSQL di test (`bprojects-test-pg`, porta 15432) e parser reale (`bprojects-t055-parser`, riavviato per il run, health 200) disponibili. Sonde non generative: solo `GET /models` del router.

## Verifiche preliminari dei due rischi
- **A (Gap senza candidati)**: `CandidateReranker.OrderAsync` non invia nulla se non ci sono fusioni ⇒ un Gap senza candidati legittimamente non chiama il reranker. L'asserzione per Gap pretendeva sempre ≥1 chiamata di riordino: corretta (`GapLatency.IsMeasuredCompletely(candidati)`: riordino obbligatorio solo con candidati) con test offline mirato. Motore invariato, manifesto invariato.
- **B (ledger oltre budget)**: in `DocumentAnalysisWorkflow.RunStagedAsync` la modalità retrieval si attiva solo se `GapStageBudget.Evaluate` dice che il ledger integrale NON entra in B (`gap_ledger_exceeds_input`); se entra, il run resta esaustivo e non esiste alcun batch `retrieval`. Il controllo (1) del test richiede esattamente un batch `retrieval`, quindi un instradamento esaustivo sarebbe FAIL, mai PASS. Nessuna modifica. **Riserva**: il corpus Single congelato ha 5570 token di testo (0,36×B) con riempitivo zero; non è dimostrato prima del run che il ledger che ne deriva superi B. Da verificare quando la fase di estrazione sarà superata.

## Risultati
| Scenario | Esito | Dettaglio |
|---|---|---|
| Single | **Blocked** (13 verifiche PASS / 1 FAIL) | preparazione di `capitolato-aurora.txt` (117 blocchi, parser reale OK) fallita: un'unità `ExtractUnit` ha prodotto `finish=length` con 16384 token di completamento (prompt 4388, 282 s) ⇒ `work_output_invalid`, altre unità `parent_failed`. Altre 3 chiamate di estrazione dello stesso documento terminate `finish=stop` (257–310 token). |
| Multi | **NotRun** | non eseguito per regola del sequencer (blocco COMPLETAMENTO di Single). |

Superate prima del blocco: configurazione/profilo, corpus generato (hash e testo = manifesto), upload 201, 5 dichiarazioni del PM.
**Non raggiunti, quindi NOT VERIFIED**: indicizzazione (tempo), routing verso retrieval, candidati per Gap, esclusioni, dimensione pacchetto, stadio Gap, citazioni, conflitti, falsi `AnsweredInSnapshot`, latenze per Gap e condivise, token dello stadio. Nessuna misura di retrieval è stata prodotta.

## Classificazione e limiti
- Il fallimento è nella fase di estrazione del documento (precedente al retrieval), non una violazione dei criteri T040 nel merito. Sintomo coerente con un output non terminante del modello (generazione fino al limite O su un input di 4,4k token) — **causa non diagnosticata**: non è dimostrato se sia comportamento del modello, del prompt/schema di estrazione o del router; senza nuove inferenze (non autorizzate) non si attribuisce. Classe: FAIL non attribuito (semantico/applicativo da accertare); non infrastrutturale (servizi raggiungibili, nessun timeout/5xx).
- Limite del run: la preparazione non ha un retry nel harness (per istruzione); lo stato del sequencer è persistito.

## Artefatti
- `T040-run1-summary.txt` (esito e verifiche), `T040-run1-state.json` (stato sequencer); log completo (12,9k righe di EF SQL, nessun segreto) in locale: `%TEMP%\claude\…\scratchpad\t040-run.log`; corpus e hash: `%TEMP%\bprojects-i5-real\T040-single-20261110203427\`.

## Verdetto
**T040: NOT PASS (non verificato nel merito).** Single bloccato in estrazione, Multi non eseguito, nessun criterio di retrieval misurato. T040 resta aperto in `tasks.md`.
