# Lezioni apprese e azioni

| # | Problema | Causa verificata | Azione proposta (non eseguita) |
|---|---|---|---|
| 1 | La run si è fermata per un alias `E109` fuori dal batch in un `expand` | Il modello ha inventato un alias non presente nel catalogo (la sua rationale lo ammette); la validazione ha rifiutato correttamente | Decisione di prodotto da prendere prima di M6: oggi un singolo batch `expand` non valido termina l'intera run (nessun retry LLM, nessun salto del batch). Opzioni da valutare: ripresa sullo stesso run con un secondo tentativo del solo batch; degradare il batch a `NotProcessed` con motivo; invariare e accettare il rischio. Nessuna modifica fatta |
| 2 | Il carico residuo supera il budget sperimentale | 368 candidati dalle 23 espansioni committate contro 39 confronti eseguiti; ogni confronto è una chiamata | Prima di una nuova esecuzione misurare il numero di confronti e di espansioni rimanenti (frontiera) e decidere il budget con l'utente; non alzare il limite per far terminare la run |
| 3 | Costo fisso di ripresa ≈ 257 s senza chiamate | Replay/rivalidazione non scomposti dalla strumentazione | Aggiungere (in una futura attività) span dedicati a replay, rivalidazione e tokenizer; oggi non attribuibile al DGX |
| 4 | La run `Failed` non è riprendibile dal prodotto | Nessun endpoint valorizza `RetryOfId` per le analisi; la riapertura è una preparazione di test sulla copia | Decidere se il prodotto debba offrire la ripresa dai checkpoint (richiederebbe la decisione sul punto 1) |
| 5 | Le disposizioni dei candidati non sono persistite | Lo stato è ricostruito dal replay degli output | Valutare la persistenza per rendere il replay più economico e la frontiera ispezionabile |
| 6 | Harness | Il primo harness non generativo aspettava a torto che `expand 30` fosse reinviato; il probe aveva un tetto che si esauriva nell'harness | Corretto solo l'harness; il recovery di produzione ha già coperto `expand 30` |

## Cosa ha funzionato e va conservato
Correzione `candidates[].key` (676177a) confermata da una risposta reale; riuso dei 30 output e recovery da checkpoint senza chiamate; tetto sperimentale applicato prima dell'invio (0 rifiuti perché mai raggiunto); ledger e timeline autosufficienti per la diagnosi; telemetria persistita coerente con il ledger (43 chiamate, token identici).

## Per i prossimi test
Partire sempre dai dump originali (hash nel manifesto); non riusare database toccati da risposte finte; una sola ripresa per autorizzazione; archiviare fuori da TEMP.
