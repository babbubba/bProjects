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

## Diagnosi offline del Run 1 (2026-11-10, nessuna inferenza, nessuna ripetizione)

Fonti: `diag/20261110-203418-68a02077/{summary.json,calls.jsonl,texts/0001-…-finish-length.txt}` (locali, `%TEMP%\bprojects-i5-real`), `T040-run1-*`; il DB di test non è stato usato. Il testo del prompt non è conservato (solo hash `4ddd4810…`, 18775 caratteri); non ricostruito. Revisione `ecc:csharp-reviewer` limitata al percorso di estrazione.

**Fatti osservati**
- Unità fallita: `ExtractUnit` job `a38ea178…` (unità `1559e77d…`), batch `e081fdae…` ord. 0 stage `extract`, porzioni [0,1], 32 range tutti Primary (`c1:1–19`, `c2:1–13`), `OwnedTokens`=1430 (5464 car.), payload misurato 5202, prompt riportato 4388. Le altre 3 unità `parent_failed` (0 tentativi).
- Schema `document_evidence_extraction` (`evidence-1.1.0`, hash `9eea16a6…`); nome/hash del prompt non ricavabili oltre l'hash. Limite richiesto O=16384, W=32768, B=15360; thinking off.
- Risposta: `finish=length`, 16384 token, 53359 car., 282 s, JSON non parsabile, tagliato a metà dell'evidenza e58. 58 evidenze con chiave `e1…e58` e citazioni **tutte distinte**, ordinate per range (`c1:3 … c2:9`; i range `c2:10–13` non raggiunti), 57 `Requirement` + 1 `Constraint`, 0 relazioni, 0 `Other`, nessun ciclo né duplicato. ~920 car. (~282 token) per evidenza: JSON indentato con campi `null`, una evidenza per frase.
- Le "tre estrazioni regolari" del riepilogo (prompt 2,3–2,5k, completion 257–310) non sono unità documento: sono le estrazioni delle 5 dichiarazioni PM in chat; non confrontabili. Nessuna chiamata di unità documento completata nel Run 1.
- Pianificatore: stima ceil(1430×8)=11440 ≤ O → unità non divisa (nessun difetto di calcolo: `Fits`, `OwnedTokens` solo Primary, B=W−O−M coerenti; ExtractionDiagnostics usa lo stesso 8.0). Generato/previsto ≥ 1,43; ≥ 11,46 token per token posseduto (limite inferiore: l'output è troncato).
- Stesso schema del run T082 (`I5-single-real.md`, ≥12,6 token/token, 16384 troncati), ma lì le evidenze erano `Other` di riempitivo; **qui sono tutte regole reali**, quindi la causa «estrazione del non pertinente» di quel run non si applica.
- Nessun indizio di anomalia del modello/router: modello richiesto = risposto (`Qwen3.6-35B-Smart`), fingerprint stabile, nessun errore HTTP, output ben formato fino al taglio.

**Causa (ordinata per probabilità; ipotesi, non dimostrate)**
1. (alta) `OutputExpansion`=8.0 non calibrato per testo denso di regole: il corpus T040 ha una regola per frase; ~72 frasi nell'unità × ~282 token ≈ 20k > O. Il fattore necessario sarebbe ≈ 14 (stima da conteggio, non misura). Coerente con il «~14–20» già annotato in I5-single-real e con la calibrazione I3 (2,65 su testo narrativo, ≥12,8 su unità piccole).
2. (media) Nessun recupero dopo `finish=length`: `BatchSender.SendAsync` lancia `work_output_invalid` e la partizione è calcolata una sola volta (`DocumentEvidenceAgent.cs:142`); un fattore errato fallisce l'intera unità.
3. (bassa) Verbosità del formato (indentazione, campi `null`) che gonfia i token per evidenza: non verificabile senza prompt e senza nuova inferenza.

**Verifica del budget del ledger (offline)**: il testo grezzo è 5570 token = 0,36×B; il vincolo «testo > 2×B» non è né richiesto da T040 né realizzabile senza alterare corpus/gold. L'asserzione `[STRUTTURALE] … supera 2x` con `MinimumRatio=0` (`x > 0`, sempre vera) era un PASS privo di significato: ora emette solo una nota `[MISURA]`; per T082/T083 (2,0) resta un controllo reale. Offline dimostrato (3 nuovi test in `RetrievalGapCampaignTests`, 47/47 verdi): testo < 2×B (stima per eccesso) e ledger stimato per difetto > B in entrambi i corpus. **Non** dimostrato offline: numero/dimensione reali delle evidenze, che l'estrazione completi, che il recupero venga attivato dal ledger reale.

**Difetto deterministico**: nessuno dimostrato nel codice; nessuna modifica a prompt, schema, parametri, planner, retry, corpus, gold o retrieval.

**Proposte (richiedono decisione, non applicate)**: (a) ricalibrare `OutputExpansion` a ≥14 (più batch, più chiamate) o ricavarlo da misura; (b) retry/divisione dell'unità dopo `finish=length` (nuovo perimetro, contratto §2.3); (c) per rendere T040 eseguibile a W=32768: unità più piccole via (a), oppure estrazione con output compatto. Regressione: caratterizzazione `Characterization_run1_…` + test del planner a fattore calibrato. T040 resta **aperto**, nessuna seconda campagna.
