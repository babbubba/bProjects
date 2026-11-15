# Valutazione sul gold RH-051 — NON ESEGUIBILE

Il gold resta quello fissato nel commit del test: **26 fatti, 2 conflitti, 2 Gap** (non modificato).

La run non è arrivata allo stadio Gap né al commit della revisione finale (`ResultRevisionId` assente, `Failed`/`work_output_invalid`). Non esiste quindi un risultato su cui misurare:

| Criterio | Stato |
|---|---|
| Copertura dei 26 fatti (presenti / parziali / assenti / inventati) | **non valutata** — nessuna revisione finale |
| Riconoscimento dei 2 conflitti e stato Open | **non valutato** (esistono 10 confronti `Open` intermedi, non un risultato del workflow) |
| Identificazione dei 2 Gap | **non valutata** — lo stadio Gap non è stato eseguito |
| Correttezza e provenienza delle citazioni | **non valutata** |
| Classificazione delle informazioni | **non valutata** |
| Coerenza della revisione finale / utilità per un PM | **non valutate** |

Come richiesto, nessun PASS semantico è dichiarato sulla base del solo ledger delle evidenze (194 evidenze estratte nella 4ª run, 0 troncamenti): quel risultato dice che l'estrazione ha prodotto un ledger, non che il workflow v11 abbia riconosciuto fatti, conflitti e Gap.

Distinzione degli esiti: completamento tecnico **no**; correttezza strutturale **parziale** (72/73 batch generati validi, 1 respinto); correttezza semantica **non valutata**; accettabilità PM **non valutata**.
