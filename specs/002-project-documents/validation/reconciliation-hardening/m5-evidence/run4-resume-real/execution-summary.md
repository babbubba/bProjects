# Sintesi tecnica della prosecuzione reale

## Cosa è stato fatto
1. **Probe non generativo chiuso** (modello finto, tetto 480): interrotto con i soli processi del probe (test host, `dotnet test`, script); Docker, PostgreSQL e DGX non toccati. Stato al momento dell'arresto: run `Running`, `CallCount=140`, 93 batch `compare` + 36 `expand`; `expand 30` e i 30 originali Committed. Dump in `probe\probe-scratch-after.dump`.
2. **Preparazione** su una copia nuova (`rh051_resume_real`) dal dump finale originale: SHA-256 verificati prima e dopo, 30 output Committed con `ManifestHash` = manifesto, unico batch senza output `expand 30`, 46 chiamate reali persistite (3 chat, 12 estrazione, 31 analisi) e nessun tentativo oltre il primo, `StorageKey` dei WorkOutput nullo (payload in database). Test di sola lettura `Rh051ResumeReadinessTests`: 30 Hit + 1 Miss. Modello `Qwen3.6-35B-Smart` caricato. Backup PRE con nome univoco.
3. **Prosecuzione reale (una sola)** con `Rh051ResumeRealTests`: riapertura della run con l'unico `UPDATE` documentato in `reopen-operation.sql` (stato `Queued`, errore azzerato, lease e backoff azzerati, tre scadenze rinnovate di +5 h, `MaxAttempts = AttemptCount + 1` per escludere retry automatici; contatori, checkpoint e snapshot dei limiti intatti), poi dispatcher applicativo.

## Risultato
| Voce | Valore |
|---|---|
| Stato finale | `Failed` / `work_output_invalid` (permanente) alle 23:10:43Z |
| Causa | batch `expand` ordinale 73 (view A, input `5a67ee9b…`): `candidates[13].right`: alias `E109` sconosciuto o fuori dal batch |
| Chiamate reali nuove | **43** (27 `compare`, 16 `expand`), tutte `finish_reason=stop`, 0 troncamenti, 0 errori di rete, 0 retry |
| Chiamate reali complessive RH-051 | **89** = 46 (run 4) + 43; budget sperimentale 200 → 111 residue, mai sfiorato (0 rifiuti del tetto) |
| `CallCount` del run | 31 → 74 (= 31 + 43; la 43ª è la chiamata il cui output è stato respinto) |
| Batch | 74 totali: 30 riusati da WorkOutput (run 4), **1 recuperato dal checkpoint** (`expand 30`, 0 chiamate), 42 generati e committati, 1 generato e respinto dalla validazione |
| Stadi (totali / committati) | leaf 8/8, reduce 2/2, cross 1/1, compare 39/39, expand 24/23 |
| Estrazione | invariata: 6 job, 9 output; nessuna nuova chiamata di estrazione |
| Token (solo nuove chiamate) | prompt 164.808 (di cui 75.726 in cache), completion 33.473; telemetria persistita (`InferenceCalls`, attempt 2) identica al ledger |
| Durata | 985 s totali: 259 s dal claim alla prima chiamata, 723 s dalla prima chiamata alla fine |
| Gap / revisione finale | **non raggiunti** |

## Integrità verificata dal test
Output dei 30 batch originali con hash invariati; nessuna richiesta fuori elenco (solo `/v1/models`, `/props`, `/tokenize`, `/apply-template`, `/completions`); nessuna riestrazione; tetto rispettato; nessun `leaf`/`reduce`/`cross` reinviato (la prima richiesta della ripresa è un `compare`, non `expand 30`).

## Stato del carico residuo (stima, non misura)
Le 23 espansioni committate hanno prodotto 368 candidati Evidence–Evidence (128 dalle 8 originali, 240 dalle 15 nuove), contro 39 confronti eseguiti; 10 confronti su 39 sono risultati `Open`, 29 `Compared`.
Anche senza la deduplica per coppia e senza ulteriori espansioni, restano nell'ordine di diverse centinaia di confronti (≥ ~290 come stima grezza) più le espansioni non ancora eseguite e lo stadio Gap: **il budget residuo di 111 chiamate non sarebbe bastato a completare la run neppure con un output valido**. È una stima dai conteggi persistiti, da confermare con una misura prima di una nuova esecuzione.

## Cosa NON dimostra
Non dimostra correttezza semantica, copertura del gold, comportamento di Gap e commit finale, né che l'`expand` 73 avrebbe superato una seconda generazione (la prosecuzione si è fermata; nessun retry LLM per scelta di prodotto).
