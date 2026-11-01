# Decisione di accettazione con riserve — Slice 1 (MVP pilota controllato)

**Data:** 2026-11-01 · **Decisore:** autore/responsabile del progetto · **Natura:** accettazione con riserve dei limiti noti.
**Non è** una dichiarazione che SC-002, SC-003 o SC-006 siano soddisfatti, né una chiusura di T052/T053.

## 1. Decisione

Non si eseguono ulteriori test N2/N4/N5 e non si fanno altre ottimizzazioni della Slice 1. I limiti documentati di N2, N4 e N5 sono accettati
ai soli fini di un rilascio **MVP pilota controllato**, con revisione e conferma obbligatorie del PM su ogni bozza. Nessuna modifica a codice,
prompt, checker o fixture è parte di questa chiusura. Le criticità e i miglioramenti del modello passano al [backlog](../backlog-post-slice1.md).

## 2. FAIL noti (nessun PASS dichiarato senza nuova esecuzione)

| Caso | Esito | Sintomo | Classe (vedi HANDOFF 10.5) |
| --- | --- | --- | --- |
| N2 | FAIL | `invalid_agent_output`: marcatore `[fonte:...]` malformato; 0/4 riprodotto in replica | causa non dimostrata (sospetto modello, intermittente) |
| N2 | FAIL (tempo) | Analizza >210 s alla prima esecuzione; rerun 171,6 s | limite di capacità modello/parallel=1 vs 180 s |
| N4 | FAIL | correzione PM non applicabile (analisi fonde due fatti in una voce) | limite del modello |
| N4 | FAIL | ripetizione tra sezioni (Requisiti e Vincoli) | limite del modello (1 campione) |
| N5 aurora | FAIL | domanda ripetuta in chat al turno 5 | limite del modello (1 campione) |
| N5 boreale | FAIL | «ordini» collocato in Goals anziché Requirements/Flows | limite del modello; registro non escluso |

N1 e N3: PASS (un campione ciascuno, non una serie ripetuta).

## 3. Limitazioni e rischi accettati

- **Ripetibilità non dimostrata:** i nominali sono stati eseguiti con un solo campione per caso; il comportamento intermittente di N2 non è caratterizzato.
- **Tempi:** analisi/generazioni possono superare il limite di 180 s (N2 prima esecuzione, N4 180,6 s). Il limite non è stato modificato.
- **Qualità semantica:** possibili omissioni, collocazioni errate di contenuto, ripetizioni tra sezioni e domande ripetute in chat. Il checker automatico segnala, non sostituisce il giudizio SC-006.
- **Diagnosi mancante:** l'output grezzo non valido del DocumentAgent non viene conservato, quindi la causa del marcatore malformato di N2 resta ignota.
- **MafReal completo e Playwright con modello reale** non rieseguiti dopo l'introduzione del registro (ultimo esito noto: MafReal 25 PASS / 7 FAIL).

## 4. Condizioni di utilizzo (pilota controllato)

1. Uso limitato a un gruppo ristretto di utenti pilota, non a produzione generale.
2. Ogni bozza è **base di revisione**: il PM la rilegge, la corregge e la conferma prima di qualunque uso a valle. Nessuna bozza è definitiva.
3. In caso di `invalid_agent_output` o di analisi oltre 180 s: ripetere l'operazione; se persiste, segnalare e non aggirare.
4. Segnalare al facilitatore ogni omissione, ripetizione o dato inventato rilevato, per alimentare il backlog.
5. Il modello di riferimento resta `Qwen3.6-35B-Smart` sul DGX, con le impostazioni attuali.
6. La decisione non estende il rilascio oltre il pilota: per l'uscita dal pilota servono la revisione umana e le rimanenti verifiche (sezione 5).

## 5. Stato dei requisiti: implementato ≠ verificato ≠ accettato

| Livello | Stato |
| --- | --- |
| **Implementato** | Funzionalità T001–T051 e T054–T073 chiuse in `tasks.md` (chat, analisi, correzione, conferma, generazione, compattazione, registro atomico delle evidenze, vista Evidenze, gate di Analizza, verificatore Colorweb, diagnostica). |
| **Verifiche automatiche superate** | Build; suite deterministica 1120 PASS / 0 FAIL / 39 SKIP; Integration su PostgreSQL; Vitest 210/210; Playwright offline; Compose con riavvio; estrattore reale 6/6 (T071); UI Evidenze con modello reale (T069, 1 PASS). Dettagli in HANDOFF 10.3–10.5. |
| **Verifiche NON superate** | N2, N4, N5 con modello reale (FAIL, sezione 2); SC-002 e SC-003 non dimostrati; MafReal 7 FAIL storici. |
| **Verifiche NON eseguite** | MafReal completo e Playwright con modello reale dopo il registro; serie ripetuta per tempi 180 s; riapertura in tre sessioni (SC-004) in manuale. |
| **Accettazione umana MANCANTE** | Revisione di due PM e di un tecnico estraneo al dialogo (SC-006); tempo attivo ≤ 5 min per PM; attestazione SC-001–SC-012 firmata. |

### T052 e T053

- **T052 — resta APERTO, non PASS.** Residuo: N2/N4/N5 non PASS; revisione umana (2 PM + tecnico estraneo) non svolta; campione ripetuto dei tempi assente; attestazione SC-001–SC-012 assente.
- **T053 — resta APERTO, non PASS.** Dipende da T052. Residuo: MafReal completo e prove con modello reale non rieseguiti dopo il registro; nessuna attestazione runtime senza evidenza.
- La decisione di cui sopra è un'**accettazione con riserve di rilascio pilota**, non il soddisfacimento dei criteri di T052/T053. Nessuna casella è stata spuntata.

## 6. Evidenze conservate

Nessuna evidenza è stata cancellata né riscritta: `t052-evidence/` (log N1–N5, T069 reale), `docs/acceptance-trace/` (tracce V7, locali non versionate),
`HANDOFF.md` sessioni 1–10, `acceptance/modulo-risultati.md` (ancora vuoto: nessuna valutazione umana svolta).
