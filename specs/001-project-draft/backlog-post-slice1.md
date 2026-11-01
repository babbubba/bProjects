# Backlog successivo alla Slice 1

Voci **non pianificate** e **non eseguite**, spostate qui dalla chiusura della Slice 1 (decisione in [acceptance/decisione-accettazione.md](acceptance/decisione-accettazione.md)).
Non sono task della Slice 1 e non hanno ID in `tasks.md`. Le evidenze di origine restano dove sono (HANDOFF 10.5, `t052-evidence/`).

## A. Criticità N2 / N4 / N5

| ID | Criticità | Evidenza | Nota |
| --- | --- | --- | --- |
| B-01 | N2: `invalid_agent_output` per marcatore `[fonte:...]` malformato, intermittente (0/4 in replica) | HANDOFF 10.5 | Causa non dimostrata. |
| B-02 | N2: tempo di Analizza (>210 s prima esecuzione, 171,6 s rerun) vs limite 180 s | HANDOFF 10.3, 10.5 | Capacità modello, parallel=1. |
| B-03 | N4: analisi fonde «questionario» e «certificazioni» in una voce; la correzione PM è rifiutata per conservazione | HANDOFF 10.5, sessione 1C | Harness corretto; limite del modello. |
| B-04 | N4: ripetizione tra Requisiti e Vincoli nella bozza | HANDOFF 10.5 | 1 campione. |
| B-05 | N5 aurora: domanda ripetuta al turno 5 | HANDOFF 10.5 | 1 campione. |
| B-06 | N5 boreale: «ordini» in Goals invece di Requirements/Flows | HANDOFF 10.5 | Registro non escluso. |
| B-07 | MafReal: 7 FAIL storici; MafReal e Playwright reali non rieseguiti dopo il registro | HANDOFF sessione 8, 10.3 | Da riesaminare prima dell'uscita dal pilota. |

## B. Miglioramenti possibili (non decisi)

| ID | Idea | Nota |
| --- | --- | --- |
| B-08 | Conservare l'output grezzo non valido del DocumentAgent nella diagnostica locale (non nelle risposte API) | Abilita la classificazione di B-01. |
| B-09 | Controllo deterministico di ripetizione tra sezioni nel validatore | Cambio di contratto. |
| B-10 | Valutare modello/thinking/parametri alternativi (es. Qwen3.8-Flash-Next-Smart, thinking per fase) | Sessioni 4–6 dell'HANDOFF: risultati inconcludenti o costosi. |
| B-11 | Campioni ripetuti N1–N5 per tempi e tasso di FAIL | Prerequisito per un'eventuale dichiarazione su SC-002/SC-003. |

## C. Gate residui per chiudere T052/T053

Revisione umana (2 PM + tecnico estraneo, `acceptance/modulo-risultati.md`), SC-004 manuale, attestazione SC-001–SC-012, MafReal e Playwright reali. Restano aperti.
