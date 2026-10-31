# Accettazione T052 — istruzioni per la revisione umana

**Stato: NON ESEGUITA.** Questo documento prepara la prova; non contiene giudizi di PM o tecnici e non attesta T052 né i criteri SC-001–SC-012.
T052 resta aperto finché: (1) esistono i risultati reali compilati in `modulo-risultati.md`, (2) il DGX reale ha eseguito i casi nominali
(oggi i test con DGX risultano *ignorati* per mancanza di `BPROJECTS_TEST_DGX_ENDPOINT/API_KEY/MODEL`) e (3) T047 è chiuso
(è una dipendenza di T052 in `tasks.md`). Un caso nominale con errore, timeout o test saltato **non è superato**.

Documenti collegati: [casi e attese](casi-e-attese.md) · [modulo risultati](modulo-risultati.md) · [spec](../spec.md) (SC-001–SC-012) · [quickstart](../quickstart.md) («Prove nominali separate») · [piano](../plan.md) («Osservazioni dal primo test manuale»).

## Cosa è automatico e cosa è umano

| Verifica | Come | Chi | Cosa dimostra | Cosa NON dimostra |
| --- | --- | --- | --- | --- |
| Percorso con modello finto «oracolo» | `AcceptanceDeterministicTests` (Integration, PostgreSQL) | CI/sviluppo | Provenienza, validazione, commit, isolamento, nessuna invocazione automatica, verificatore | Qualità del modello reale; comprensibilità |
| Percorso con DGX reale | `AcceptanceMafTests` (MafReal) | CI/sviluppo con DGX | Cinque scambi ≤180 s, analisi/bozza ≤180 s, attese lessicali del caso | Giudizio qualitativo (SC-006) |
| Percorso nel browser con stack reale | `tests/e2e/project-draft-success.spec.ts` (`T052 N1…N5`, `E2E_MODEL_MODE=real`) | CI/sviluppo con DGX | Stesso percorso via UI (streaming, correzione, conferma, Genera, riapertura) | Giudizio qualitativo |
| Verificatore delle regole | `AcceptanceFixtureTests` (backend), `acceptance-checker.spec.ts` (browser) | CI/sviluppo | Le regole rilevano omissioni, negazioni invertite, invenzioni, conflitti scelti in silenzio | Qualità del modello |
| Usabilità e tempo attivo (SC-001) | Percorso manuale di due PM con cronometro | **2 PM** | ≤5 minuti di interazione attiva, senza assistenza tecnica | — |
| Comprensibilità della bozza (SC-006) | Lettura della sola bozza | **1 tecnico estraneo al dialogo** + giudizio dei **2 PM** | Zero omissioni/contraddizioni/ripetizioni sostanziali; utilità come base di revisione | — |

Il confronto automatico è lessicale (espressioni regolari sul testo normalizzato): è condizione necessaria, **non** sufficiente. Un PASS automatico non sostituisce la revisione; un FAIL automatico va esaminato (può essere un sinonimo non previsto) ma non si «corregge» il test per farlo passare senza decisione dell'autore.

## Prerequisiti

- Stack reale: `docker compose -f infra/compose.yaml up -d --build` con `infra/.env` dello **stack di prova** (DB e volumi distinti da produzione; non eliminare i volumi). Endpoint DGX interno, `AI_MODEL_ID` e chiave forniti dall'utente: nessuna credenziale va cercata altrove.
- Due account PM locali (`bootstrap-accounts`), uno per PM valutatore. Un terzo profilo non serve al tecnico: riceve solo file.
- Servizio disponibile, **nessun altro carico** sul DGX durante le misure di 180 s.
- Registrare nel modulo: commit, versione MAF, modello, finestra reale, `MaxOutputTokens`, `SafetyMarginTokens`, `CompactionTriggerTokens`, `Execution__*` risolti. La capacità tecnica si misura **in token** (nessuna soglia di parole).
- Persone: **PM1 e PM2** (esperti del dominio, non sviluppatori), **un tecnico che non ha partecipato ai dialoghi** e non conosce messaggi, attese o analisi (vede solo `bozza.md`), **un facilitatore** (può essere lo sviluppatore) che cronometra, non suggerisce e confronta con le attese.

## Procedura

1. **Preparazione (una volta).** Il facilitatore stampa/mostra [casi-e-attese](casi-e-attese.md) ai soli PM (messaggi) e tiene per sé le attese. Le attese non si modificano dopo aver visto un output.
2. **Esecuzione automatica di supporto (consigliata prima dei PM)**, per verificare che lo stack regga e per produrre le bozze da far leggere al tecnico:

   ```bash
   # backend (dalla cartella backend/)
   export BPROJECTS_TEST_POSTGRES=... BPROJECTS_TEST_DGX_ENDPOINT=... BPROJECTS_TEST_DGX_API_KEY=... BPROJECTS_TEST_DGX_MODEL=...
   export BPROJECTS_ACCEPTANCE_EXPORT_DIR=<cartella locale non versionata>
   dotnet test --solution bProjects.sln --filter-class "*AcceptanceMafTests"
   # browser, stack gia' avviato
   E2E_BASE_URL=... E2E_USER=... E2E_PASSWORD=... E2E_MODEL_MODE=real E2E_ACCEPTANCE_EXPORT_DIR=<cartella> npm --prefix frontend run test:e2e -- --grep "T052"
   ```

   Ogni progetto esporta `messaggi-pm.md`, `risposte-assistente.md`, `analisi-ai-v1.json`, `analisi-confermata.json`, `bozza.md` ed `esito-automatico.txt`. I valori (token, durate, ID) vanno registrati; i contenuti restano in cartella locale, non nel repository né nei log.
3. **Percorso manuale dei PM (SC-001).** Ciascun PM, indipendentemente e senza assistenza tecnica, esegue **tutti e cinque i casi**, ognuno in un progetto nuovo: crea il progetto, invia i cinque messaggi preparati **uno alla volta nell'ordine dato**, preme Analizza, legge l'anteprima, esegue la correzione prevista dal caso (modifica, integrazione, assunzione; nel caso N4 anche la risoluzione motivata di un conflitto e il lasciare aperto l'altro), salva, conferma, preme Genera documento e legge la bozza. Il facilitatore cronometra l'**interazione attiva** (scrittura/invio, lettura dell'anteprima, correzione, conferma, richiesta di generazione, lettura) **escludendo** le attese AI e la preparazione dei messaggi; limite: **≤ 5 minuti per PM e per caso**. Registra anche i tempi di attesa AI (chat, analisi, generazione), ciascuno da confrontare con 180 s.
4. **Caso N5 (due progetti).** Il PM lavora i due progetti in sequenza o alternandoli; al termine verifica che ogni progetto contenga solo i propri dati.
5. **Riapertura (SC-004).** Dopo il caso N1 di ciascun PM: riavviare l'app (`docker compose -f infra/compose.yaml kill app` e `up -d app`, senza `down -v`) e riaprire il progetto in tre sessioni successive; dialogo, anteprima, conferma e bozza devono essere identici. Lo stesso controllo è automatizzato da `T043` con `E2E_RESTART_COMMAND`.
6. **Valutazione del tecnico (SC-006), alla cieca.** Riceve **solo** il `bozza.md` di ogni caso (una per caso; il facilitatore alterna le esecuzioni di PM1 e PM2, es. N1/N3/N5 da PM1 e N2/N4 da PM2, e sottopone anche le altre se emergono differenze). Prima scrive la propria ricostruzione (obiettivi, perimetro, flussi, requisiti e vincoli, dipendenze, punti aperti) **senza** altre fonti. Poi il facilitatore, con le attese, conta omissioni sostanziali, contraddizioni introdotte, ripetizioni sostanziali e informazioni inventate (definizioni nel modulo). Il tecnico non vede mai messaggi PM, attese o analisi.
7. **Giudizio dei PM (SC-006).** Ciascun PM compila la griglia del modulo per ogni caso (pertinenza della chat, completezza e correttezza dell'anteprima, utilità della bozza come base di revisione). «Utile» richiede un sì motivato di **entrambi** i PM per ogni caso.
8. **Prova di limite E1 (separata).** Eseguire `The_large_context_exploratory_case_…` (MafReal) con i valori di produzione e registrare l'esito (a) analisi riuscita con attese verificate oppure (b) errore esplicito `context_overflow`/`compaction_failed` con token misurati. **Non** entra nei criteri PASS dei casi nominali.
9. **Compilare il modulo** e solo dopo aggiornare `tasks.md`/checklist (vedi «Esito»).

## Criteri PASS

T052 è **PASS** solo se **tutte** le condizioni sono vere e documentate nel modulo con evidenze reali (ID di run/revisione/bozza, durate, conteggi — non contenuti integrali):

1. **Cinque casi × due PM** completati: chat (cinque scambi), analisi e generazione **riuscite** — nessun errore, nessun timeout, nessun test saltato — con ogni fase AI ≤ 180 s dal proprio comando e ≤ 5 minuti di interazione attiva per PM e per caso, senza assistenza tecnica (SC-001, SC-002, SC-008).
2. **Attese del caso** tutte riportate correttamente in analisi e bozza; vere lacune e alternative restano esplicite; nessuna informazione disponibile ridotta a punto aperto per soddisfare il criterio (SC-003).
3. **Zero** omissioni sostanziali, contraddizioni introdotte, ripetizioni sostanziali e informazioni inventate, secondo il tecnico estraneo al dialogo, **in ogni** caso; **entrambi** i PM giudicano utile la bozza come base di revisione (SC-006).
4. Nessun turno chat o riapertura avvia un'analisi; nessuna analisi o conferma avvia la generazione; Genera senza conferma è respinto prima di DocumentAgent (SC-005, SC-011).
5. Originali e risultati identici in tre sessioni con riavvio, senza duplicazioni (SC-004).
6. Le suite di guasto, recovery, compattazione e browser (T043–T051) sono state eseguite **con i servizi reali** e hanno esito registrato (SC-007, SC-009, SC-010, SC-012); non sostituiscono SC-002.
7. Il caso E1 è registrato come prova di limite separata (nessun valore PASS/FAIL nominale).
8. Capacità tecnica registrata solo in token; nessun requisito o soglia basato sulle parole.

Non valgono come PASS: errori ben gestiti, esito con modello finto, test *ignorati*, un solo PM o nessun tecnico estraneo, confronto lessicale senza lettura umana, correzione delle attese dopo aver visto l'output.

## Esito

- Se tutti i criteri sono soddisfatti: l'autore spunta T052 in `tasks.md` citando il modulo compilato; T053 aggiorna quickstart e checklist con gli esiti verificati.
- Altrimenti: T052 resta `[ ]`; si registrano i fallimenti nel modulo, si corregge la causa nel codice (non le attese) e si ripete l'intero caso non superato. Ogni ripetizione ha una nuova riga nel modulo.
