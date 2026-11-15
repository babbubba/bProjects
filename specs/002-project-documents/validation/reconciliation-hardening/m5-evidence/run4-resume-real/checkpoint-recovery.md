# Riuso dei checkpoint e recovery di `expand 30`

## Stato di partenza (copia dal dump finale originale, `preflight.txt`)
- Run `Failed`/`work_output_invalid`, `AttemptCount=1/3`, fence 1, `CallCount=31`, `RetryOfId` nullo, lease nullo, tre scadenze (`DeadlineAt`, `EffectiveDeadlineAt`, `HardDeadlineAt`) scadute alle 19:44:13Z.
- 31 batch pubblicati; 30 `WorkOutput` Committed (`reconcile-alias-1`, `reconcile-validation-v2`, `ManifestHash` = manifesto del run, `StorageKey` nullo: payload in database).
- Unico batch senza output committato: ordinale 30, `expand`, `bf787941c8fa…`. La risposta reale del modello (con la chiave locale `f13` duplicata) era nel checkpoint del run.
- Telemetria: 46 chiamate reali (Analysis 31, Extraction 12, Chat 3), nessun tentativo > 1.
- `Rh051ResumeReadinessTests` (sola lettura, `DocumentWorkStore.LookupAsync` di produzione): 30 Hit, 1 Miss (`30, expand`).

## Riapertura (unica operazione, solo sulla copia: `reopen-operation.sql`)
Riportare la run a `Queued` con errore/lease/backoff azzerati e le tre scadenze a +5 h; `MaxAttempts = AttemptCount + 1` (nessun retry automatico dell'intera run). Contatori, checkpoint e snapshot dei limiti (`maxAiCallsPerRun=512`) non toccati. Il dispatcher ha reclamato la run in 3 s: `Running`, tentativo 2, fence 2.

## Esito osservato
| Fatto | Evidenza |
|---|---|
| 30 output originali riusati senza nuove chiamate | ledger batch: 30 × `work-output-hit`, `CommittedFence=1`, hash invariati (asserzione del test) |
| `expand 30` committato dal recovery del checkpoint, 0 chiamate | `CommittedFence=2` alle 22:58:40Z, 1 s **prima** della prima chiamata reale; nessuna chiamata `expand` per quel batch nel ledger (prima chiamata = `compare` dell'ordinale 31) |
| Perché è possibile | `DocumentBatchPort.PersistAsync` rivalida la risposta salvata nel checkpoint con le stesse regole e, se valida, la committa sotto il fence corrente (nessuna nuova riserva né inferenza). Con la correzione di `candidates[].key` la risposta reale ora è valida (16 candidati) |
| Nessuna riestrazione | job di estrazione 6 → 6, output 9 → 9 |
| Contabilità continua | `CallCount` 31 → 74, nessun azzeramento; `InferenceCalls` 46 → 89 |
| Ordine atteso | solo `compare`/`expand` nuovi; mai `leaf`/`reduce`/`cross` reinviati |

## Costo del recovery (non attribuito)
La persistenza del primo batch (`port.persist`) è durata **257,1 s** (22:54:23Z → 22:58:40Z) con 0 chiamate al modello: è il 26 % della durata totale. Il tempo non è scomposto dalla strumentazione esistente (rivalidazione, replay della gerarchia sui 30 output, richieste non generative di `/tokenize` e `/apply-template` verso il DGX). Nessuna attribuzione al DGX senza evidenze. È un costo fisso per ogni ripresa.

## Limiti
- La run era `Failed` (terminale in produzione): la riapertura è una preparazione di TEST sulla copia; il prodotto non ha un percorso `RetryOfId` per le analisi.
- Le disposizioni dei candidati non sono persistite: la frontiera si ricostruisce rieseguendo la gerarchia sugli output committati (è ciò che il motore ha fatto nei 257 s).
