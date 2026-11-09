# M1 — prima misura reale (T019): BLOCCATA, nessuna misura eseguita

- Data: 2026-11-09. Autorizzazione del committente: solo embedding `bge-m3`, solo per T019 (nessun LLM generativo, riordino, load/unload esplicito, cambio di modello).
- Stato: **T019 aperto**. `baseline-m1.json` NON esiste; nessuna metrica reale è stata prodotta; nessun esito PASS o FAIL.
- Harness pronto e committato: `RetrievalEvaluationRealTests` (`Category=RetrievalEvalReal`, opt-in `BPROJECTS_TEST_DGX_ALLOW=1`); ignorato senza opt-in (verificato); `DgxGuardTests` 12/12 e `RetrievalEvaluationTests` (simulata) 5/5 superati con il nuovo codice.

## Evidenza del blocco

1. `GET /v1/models` del router: `bge-m3` presente, stato **unloaded**, artefatto dagli argomenti `bge-m3-Q5_K_M.gguf` (coincide con l'atteso); tutti gli altri modelli elencati sono `unloaded`. Readiness iniziale osservata dal prodotto: `ModelUnloaded` (artefatto e dimensione non ancora dimostrabili, SHA-256 `NotVerified`).
2. Prima richiesta di embedding (la sola che, in router mode, carica il modello on-demand): HTTP **500** dopo ~5 s, `"model name=bge-m3 failed to load"` (tipo `server_error`). Un secondo tentativo isolato (`curl`, una sola riga di input) ha dato lo stesso esito. Nessun altro tentativo, nessun alias o modello alternativo (`bge-m3-Q5`, `bge-m3-Q8`) provato: sarebbe un cambio di modello non autorizzato.
3. Causa non diagnosticata da questo task (non si ha accesso ai log del router): è un guasto lato DGX nel caricamento del modello (memoria/risorse o configurazione del router), non del codice del repository.

## Cosa manca per chiudere T019

Un `bge-m3` caricabile sul router (verifica lato DGX), poi: `BPROJECTS_TEST_DGX_ALLOW=1 BPROJECTS_EVAL_WRITE_BASELINE=1 dotnet test --project tests/bProjects.IntegrationTests --filter-class "*RetrievalEvaluationRealTests"` (stima: ~6,3k blocchi in batch da 4; il test stima i tempi su 16 blocchi e si ferma oltre 90 min). Produce rapporto, supplemento (a)-(e) e `baseline-m1.json`; poi si compila qui il rapporto completo.

## Checklist T020

Non compilata: nessun criterio è valutabile senza la misura (Recall@10 ibrido ≥ 95% sul gold congelato; perdita di mappatura blocco→evidenza = 0; aggiornamento di R-1, R-3, R-5 con la misura). Stato: **da valutare** (la valutazione/gate spetta al committente).
