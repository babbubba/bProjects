# Modulo risultati T052 (da compilare — vuoto)

Compilare con valori **reali** osservati. Nessuna cella è precompilata: un campo vuoto significa «non eseguito». Non annotare contenuti integrali di dialoghi o bozze (conservarli in una cartella locale); registrare ID, durate, conteggi, esiti. Istruzioni e criteri PASS: [README](README.md); attese: [casi-e-attese](casi-e-attese.md).

Esiti ammessi: **PASS** · **FAIL** · **NON ESEGUITO** · **SKIP** (servizio mancante). Un errore, un timeout o uno SKIP in un caso nominale non è un PASS.

## A. Sessione

| Campo | Valore |
| --- | --- |
| Data / sede | |
| Commit del repository (`git rev-parse HEAD`) | |
| Versione MAF, modello e model ID esposto da `/v1/models` | |
| Endpoint DGX (solo host, mai chiavi) | |
| Finestra reale (token), `MaxOutputTokens`, `SafetyMarginTokens`, `CompactionTriggerTokens`, `StructuredOutputMode` | |
| `Execution__*` risolti (lease, rinnovo, tentativi, backoff, deadline) | |
| Stack di prova (volume/DB distinti da produzione) | |
| Altro carico sul DGX durante le misure? (deve essere «no») | |
| PM1 / PM2 / tecnico estraneo / facilitatore (nomi) | |
| Il tecnico non ha partecipato ai dialoghi né visto messaggi, attese o analisi? | |

## B. Esecuzione per PM e per caso (SC-001, SC-002, SC-008)

Una tabella per PM (copiare per PM2). Tempo attivo = scrittura/invio, lettura anteprima, correzione, conferma, richiesta di generazione, lettura bozza; **escluse** attese AI e preparazione. Limiti: attese ≤ 180 s ciascuna; attivo ≤ 05:00.

**PM1**

| Caso | Assistenza tecnica? | Tempo attivo (mm:ss) | Chat: 5 tempi (s) | Analisi (s) / esito | Generazione (s) / esito | ID run analisi · revisione confermata · bozza | Errori/timeout | Esito caso |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| N1-frammenti | | | | | | | | |
| N2-negazioni | | | | | | | | |
| N3-lacune | | | | | | | | |
| N4-conflitti | | | | | | | | |
| N5-progetti-distinti (A / B) | | | | | | | | |

**PM2**: stessa tabella.

Esito automatico di supporto (`esito-automatico.txt`, MafReal ed E2E): per ogni caso, PASS/FAIL e fallimenti elencati (solo chiavi, es. «omissione: agenda»): ______

## C. Griglia dei PM (per PM e per caso)

Rispondere **Sì / No / Con riserve** e motivare ogni No o riserva (cosa, dove). Confrontare con le attese del caso solo dopo aver letto anteprima e bozza.

| # | Domanda | Sì/No/Riserve | Note |
| --- | --- | --- | --- |
| C1 | Le risposte della chat erano pertinenti, con al massimo una domanda per turno e nessuna domanda già risolta ripetuta? | | |
| C2 | L'anteprima contiene tutto ciò che avevo scritto (requisiti, vincoli, decisioni, flussi)? Omissioni: | | |
| C3 | Negazioni ed esclusioni sono riportate come tali, nessuna invertita? | | |
| C4 | Le vere lacune sono punti aperti e ciò che sapevo non è stato ridotto a punto aperto? | | |
| C5 | I conflitti mostrano tutte le alternative con le fonti e l'AI non ne ha scelta una? | | |
| C6 | Nessuna informazione inventata (numeri, date, tecnologie, impegni, stime)? | | |
| C7 | Correzione, conferma e generazione erano chiare, con una sola azione primaria per fase, senza assistenza? | | |
| C8 | La bozza riporta le mie correzioni, integrazioni, assunzioni e (N4) la scelta motivata, e lascia aperto il resto? | | |
| C9 | Ripetizioni o contraddizioni sostanziali nella bozza (numero, dove)? | | |
| C10 | **La bozza è utile come base di revisione?** (richiesto Sì per SC-006) | | |
| C11 | Nel caso N5: ogni progetto contiene solo i propri dati? | | |

## D. Griglia del tecnico estraneo (per caso, alla cieca)

**D.1 Ricostruzione** (scritta dal tecnico leggendo SOLO `bozza.md`, prima di ogni confronto): obiettivi · perimetro (incluso/escluso) · flussi applicabili · requisiti e vincoli · dipendenze/integrazioni · punti aperti e conflitti. Allegare il testo (locale) e indicare qui solo: completata? Sì/No · tempo di lettura: ______

**D.2 Confronto con le attese** (a cura del facilitatore; il tecnico può assistere dopo aver consegnato D.1). Definizioni:
- *Omissione sostanziale*: manca (nella bozza, o ricostruita in modo errato) un requisito, vincolo, esclusione, decisione, flusso, dipendenza, lacuna o alternativa di conflitto atteso dal caso. Non è omissione una diversa formulazione né un dettaglio non annotato.
- *Contraddizione introdotta*: la bozza dice qualcosa di incompatibile con il dialogo o con se stessa (es. un'esclusione presentata come requisito, un conflitto aperto presentato come deciso).
- *Ripetizione sostanziale*: lo stesso contenuto compare più volte in modo da allungare o confondere (non lo è un rimando coerente fra sezioni).
- *Informazione inventata*: requisito, numero, data, tecnologia, impegno o stima non presenti nel dialogo né nelle modifiche del PM.

| Caso | Omissioni sostanziali (N · quali) | Contraddizioni introdotte (N) | Ripetizioni sostanziali (N) | Informazioni inventate (N) | Punti non comprensibili al tecnico | Ricostruzione corretta? Sì/No | Esito caso |
| --- | --- | --- | --- | --- | --- | --- | --- |
| N1-frammenti | | | | | | | |
| N2-negazioni | | | | | | | |
| N3-lacune | | | | | | | |
| N4-conflitti | | | | | | | |
| N5-progetti-distinti (A / B) | | | | | | | |

SC-006 per un caso è PASS solo con 0 / 0 / 0 / 0, ricostruzione corretta e C10 = Sì di **entrambi** i PM.

## E. Riepilogo per criterio

| Criterio | Evidenza (suite eseguita con servizi reali, ID, tempi) | Esito |
| --- | --- | --- |
| SC-001 due PM, cinque messaggi, correzione, conferma, generazione, ≤5 min attivi, senza assistenza | Sezione B | |
| SC-002 100% analisi valide e generazioni, ≤180 s, 5 casi | Sezione B + `AcceptanceMafTests` / E2E `T052` (non SKIP) | |
| SC-003 attese riportate, lacune e alternative esplicite | D.2 + esito automatico | |
| SC-004 tre sessioni con riavvio identiche | E2E `T043` con `E2E_RESTART_COMMAND` + controllo manuale | |
| SC-005 nessun avvio automatico; Genera senza conferma respinta prima di DocumentAgent | Esito automatico (runner/E2E) | |
| SC-006 tecnico ricostruisce; 0 omissioni/contraddizioni/ripetizioni; PM giudicano utile | Sezioni C e D | |
| SC-007 prove di guasto (chat, analisi, compattazione, generazione) | `ExecutionFailureTests`, `ModelFailureTests`, E2E `project-draft-errors` | |
| SC-008 cinque scambi ≤180 s, ≤1 domanda, nessuna ripetuta; FIFO/cutoff | Sezione B + C1 + `ChatRecoveryTests` | |
| SC-009 recovery/retry, un solo risultato | `ChatRecoveryTests`, `WorkflowRecoveryTests`, `ExecutionFailureTests` | |
| SC-010 compattazione attiva, contesto entro budget, fonti/negazioni conservate | `CompactionMafTests` (T047, ancora aperto) | |
| SC-011 bozza legata a una sola revisione confermata; conflitto su base obsoleta | `RevisionConcurrencyTests`, E2E | |
| SC-012 E2E browser + 1440/390 + tastiera + controllo manuale di focus, contrasto, annunci | E2E `T043/T050/T051` + checklist manuale: focus ___ contrasto ___ annunci ___ | |

## F. Prova di limite E1 (separata, non nominale)

Esito: (a) analisi riuscita con attese verificate · (b) errore esplicito `context_overflow`/`compaction_failed` · (c) altro (**FAIL**). Token misurati (originale, soglia, finale, budget), sintesi attivate, durata: ______ . Nota: nessuno dei due esiti ammessi certifica la gestione di documenti lunghi.

## G. Decisione

| Domanda | Risposta |
| --- | --- |
| Tutti i criteri PASS del README sono soddisfatti con evidenze reali? | |
| Dipendenze T043–T051 completate (T047 incluso)? | |
| Casi da ripetere dopo correzione (cosa è stato cambiato, nel codice e non nelle attese) | |

Valutatori (nome, ruolo, data, firma a mano o conferma scritta): PM1 ______ · PM2 ______ · Tecnico ______ · Facilitatore ______

Solo con «sì» alle prime due righe l'autore può spuntare T052 in `tasks.md`.
