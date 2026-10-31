# HANDOFF — verificatore di accettazione dopo l'audit V7

**Data:** 2026-10-31 · **Repository:** `bProjects-clean` (la cartella `bProjects` accanto è vuota, non è un repository)
**Branch:** `experiment/requirements-quality`, creato da `feature/001-acceptance-close` @ `2a2ef50` (commit della campagna V7). Nessun push.
**Ambito:** solo `AcceptanceChecker` (C#), il suo specchio TypeScript e i test deterministici. Non toccati: prompt, `analysis.md`,
`document.md`, parametri Qwen, persistenza, MAF. Nessuna esecuzione DGX né suite lunghe.
**Fonte:** report «bProjects — Affidabilità dell'interpretazione dei requisiti, revisione V2» e traccia locale
`docs/acceptance-trace/trace-qwen3.8-flash-next-smart-v7.md` (non versionata, resta tale).

## 1. Esito in una riga

Con gli **stessi output V7** il verificatore passa da 11 segnalazioni (3 PASS / 3 FAIL) a **4 segnalazioni (4 PASS / 2 FAIL)**: restano solo
le omissioni reali, barcode (legame strumento→operazione perso, analisi + bozza) e Colorweb (qualificatori «obbligatoria» e «in tempo reale»
assenti, analisi + bozza), con messaggi che le distinguono. N2 ferie passa. Verificato da un test che rilegge la traccia
(`V7_captured_outputs_keep_only_the_real_omissions`).

## 2. Anomalia aperta: la correzione PM di N2 elimina «rifiuta» (da verificare in 1B)

Il report attribuisce l'omissione «richiesta» nella bozza N2 alla divisione richiesta/approvazione in due voci. Le tracce dicono altro.

**Fatti osservati (traccia V7):**
- L'analisi AI (proxy 56) contiene, fra i requisiti, «Il dipendente invia la richiesta di ferie o permessi» e «Il responsabile diretto approva
  o rifiuta la richiesta». La seconda voce soddisfa da sola l'attesa `richiesta` (`richiest` + `approva` + `rifiut`): il controllo
  sull'analisi era infatti passato. Non c'è una voce separata sull'esclusività («solo il responsabile diretto»).
- La correzione prevista dalla fixture riguarda l'attesa `solo-responsabile` (`Match = ["responsabile diretto"]`). L'harness modifica la
  **prima** voce che combacia: `AcceptanceTests.cs` (`target = ... FirstOrDefault`, riga ~439); il percorso browser
  `project-draft-success.spec.ts` (~330-336) usa la stessa regola. Quella voce era «Il responsabile diretto approva o rifiuta la richiesta»,
  sostituita per intero con «Le richieste di ferie e permessi sono approvate esclusivamente dal responsabile diretto del dipendente».
- Nella revisione confermata (e quindi nella bozza, che la riproduce fedelmente) «rifiuta» non compare più.

**Due interpretazioni possibili, non ancora decise:**

| | A. Modifica intenzionale dei requisiti | B. Errore dell'harness nell'individuare la voce |
|---|---|---|
| Lettura | La correzione PM è autorevole: il PM ha riscritto la voce sull'approvazione e confermato la revisione; la bozza deve seguirla. | La correzione voleva riformulare l'esclusività dell'approvazione, non cancellare il rifiuto; l'harness ha colpito una voce che portava anche un altro fatto atteso. |
| Indizi a favore | Il server tratta la revisione confermata come unico input della generazione; l'harness esegue esattamente la correzione scritta nella fixture. | `casi-e-attese.md` descrive la correzione come «Modificare la voce «Le richieste sono approvate solo dal responsabile diretto»…», cioè una voce sull'esclusività che l'AI non aveva prodotto; un PM reale avrebbe probabilmente conservato «approva o rifiuta». |
| Conseguenza sul gold set | Incoerenza da risolvere: dopo la correzione la fixture si aspetta ancora `rifiut` nella bozza; l'autore dovrebbe dichiarare se la correzione toglie il rifiuto. | Gold set coerente; va corretto l'harness (scelta della voce, o correzione che non sostituisca l'intera voce). |
| Comportamento del checker in `66205ea` | Corretto: nota ««richiesta» presente nell'analisi AI ma non nella revisione confermata», nessun errore. | La stessa nota **maschera** un difetto dell'harness come esito accettabile della bozza. |

**Punti da verificare in 1B, senza presupporre A o B:**
1. Che l'analisi del proxy 56 e la revisione confermata del proxy 61 siano come descritte sopra.
2. Quale interpretazione è coerente con fixture, `casi-e-attese.md` e contratto della correzione PM; la decisione spetta all'autore.
3. Se la regola del checker «presente nell'analisi AI, assente nella confermata ⇒ nota» è troppo permissiva in generale: oggi vale per
   qualunque fatto, non solo per quelli toccati dalla correzione prevista.

Il checker non è stato cambiato per propendere verso una delle due letture oltre a quanto descritto; l'harness non è stato toccato.

## 3. Modifiche (diff)

```
 backend/tests/bProjects.IntegrationTests/AcceptanceCheckerSemanticsTests.cs | 458 +++ (nuovo)
 backend/tests/bProjects.IntegrationTests/AcceptanceFixtures.cs              | 201 ++-
 backend/tests/bProjects.IntegrationTests/AcceptanceFixtureTests.cs          |  18 +
 backend/tests/bProjects.IntegrationTests/AcceptanceTests.cs                 |   2 +-   (passa run.Ai a CheckDocument)
 specs/001-project-draft/acceptance/casi-e-attese.md                         |   2 +    (due nuovi «non deve comparire»)
 tests/e2e/acceptance-checker.spec.ts                                        |  69 +
 tests/e2e/project-draft-success.spec.ts                                     |   2 +-   (passa analisi AI e confermata a checkDraft)
 tests/e2e/support/acceptance-cases.json                                     |  43 +-   (rigenerato, BPROJECTS_ACCEPTANCE_REGENERATE=1)
 tests/e2e/support/acceptance.ts                                             |  81 +-   (specchio TS delle stesse regole)
```

### 3.1 Regole del verificatore (C# e TS identiche)

| Regola | Prima | Dopo |
|---|---|---|
| Polarità dei divieti «Affirmed» | Qualunque negazione in tutta la voce la esentava | Negazione **nella stessa proposizione** del termine vietato. Confini: `. ; : ! ?`, avversative (`ma`, `mentre`, `però`, `invece`, `bensì`, `tuttavia`), coordinata che si apre con negazione (`e non`, `, non`). La virgola semplice non separa. |
| Responsabilità esterna | Non esisteva: «resta nel software paghe» = affermazione | `Forbid.External`, annotato dal PM: la proposizione che attribuisce il termine al sistema esterno non viola il divieto **solo se il nostro sistema** (`portale/sistema/applicazione/app/sito/piattaforma/soluzione`) non vi compare. In Dependencies lascia una **nota** di collocazione. |
| Fatti divisi in più voci | Tutte le espressioni nella stessa voce | In alternativa, `Fact.Parts`: scomposizione atomica annotata, ogni parte (soggetto + azione/oggetto) in una voce, **ogni parte con fonte Declared corretta**. Nessuna somma libera di frammenti. |
| Diagnosi omissioni (analisi) | «non presente in X» | Aggiunge la voce più vicina e, per ogni espressione mancante, «solo in un'altra voce (Sezione)» → **legame perso fra voci** oppure «assente ovunque» → **qualificatore o dettaglio assente**. |
| Omissioni nella bozza | Sempre errore senza causa | Con l'analisi AI: *persa nella generazione* (errore), *tolta dalla correzione PM* (nota), *assente già dalla revisione confermata* (errore ereditato). Senza analisi AI il comportamento resta quello precedente (errore). |

### 3.2 Gold set (`AcceptanceFixtures.cs`) — solo restrizioni o scomposizioni vincolate

- `ferie/richiesta`: `Parts = [[dipendent, richiest], [responsabile, approva, rifiut]]`. `Match` invariato.
- `boreale/ordini`: `Parts = [[client, inser, ordini], [client, spedizion]]`. `Match` invariato.
- `boreale/integrazione`: `Match` **più severo**: aggiunto `obbligatori` (era già nel testo annotato prima delle prove: «Integrazione obbligatoria in tempo reale…»).
- `ferie/paghe`, `ferie/ratei`: `External` = attribuzione al «software paghe» (verbo di responsabilità entro 30 caratteri).
- Nuovo divieto `ferie/integrazione-paghe` (Anywhere): integrazione/API/import/export con il software paghe. È il contrappeso di `External`.
- Nuovo divieto `boreale/colorweb-indebolita` (Anywhere): Colorweb facoltativa/opzionale/non obbligatoria/non in tempo reale/differita/
  asincrona/batch/notturna/periodica/giornaliera — fallisce **anche se formulato come negazione**.
- `barcode` **non** scomposto: il legame strumento→operazione è l'attesa; separarli resta un'omissione (legame perso).

Invarianti nuove in `Annotations_are_consistent_...`: ogni espressione di `Match` compare in almeno una parte; ogni parte ha ≥ 2 espressioni
e il testo ideale la soddisfa; `External` solo su divieti Affirmed, presente in un messaggio PM e assente dall'esempio vietato; ogni esempio
Affirmed è un'affermazione per `Affirms`.

## 4. Confine fra controllo deterministico e revisione umana

Deterministico (regex, ripetibile): polarità per proposizione; soggetto «nostro sistema» vs sistema esterno **annotato**; scomposizioni
atomiche **annotate**; presenza dei qualificatori annotati; parole che contraddicono un qualificatore annotato; attribuzione delle omissioni
fra analisi, correzione PM e generazione.

**Non** deterministico (resta alla revisione umana, nessuna prova NLI inventata):
- predicati diversi con lo stesso soggetto e oggetto: «il portale **mostra** i ratei» fallisce come «calcola» (scelta conservativa);
- soggetti impliciti, pronomi, ellissi, sinonimi non previsti dalle espressioni;
- se una responsabilità esterna in Dependencies sia collocata bene (solo nota);
- modalità desiderata vs impegno (N1 «vorrebbero … a metà ottobre»);
- qualificatori non annotati nelle attese: il verificatore non scopre da solo «solo», «almeno», condizioni ed eccezioni mancanti.

## 5. Assunzioni

1. L'autore (sessione richiedente) autorizza il ritocco del gold set descritto in 3.2; nessuna attesa è stata indebolita.
2. Fra analisi AI e revisione confermata cambiano solo le modifiche PM (contratto del server), quindi «presente nell'AI, assente nella
   confermata» = tolto o riscritto dal PM.
3. La traccia V7 resta locale e non versionata (README di accettazione); la riesecuzione la legge da `BPROJECTS_ACCEPTANCE_TRACE` o da
   `docs/acceptance-trace/…` e risulta **ignorata** se assente.
4. In V7 l'indice dei messaggi PM è ricavato confrontando il testo dopo `[messaggio id=… sequenza=…]` con i messaggi della fixture.

## 6. Test da lanciare (sessione Sonnet) e risultati attesi

Backend, dalla cartella `backend/` (nessuna variabile d'ambiente necessaria, nessun DGX):

```bash
dotnet build bProjects.sln
dotnet test --solution bProjects.sln --filter-class "*AcceptanceCheckerSemanticsTests" --filter-class "*AcceptanceFixtureTests"
```

Atteso: **168 superati, 0 ignorati** con la traccia in `docs/acceptance-trace/`; senza traccia **167 superati, 1 ignorato** (la riesecuzione V7).
Traccia altrove: `BPROJECTS_ACCEPTANCE_TRACE=<percorso>/trace-qwen3.8-flash-next-smart-v7.md`.

Specchio TypeScript, dalla cartella `tests/e2e/` (il verificatore non usa il browser; `E2E_BASE_URL` fittizio evita l'avvio del dev server
Angular, che richiede Node ≥ 22.22.3 — su questa macchina c'è Node 20):

```bash
npx tsc --noEmit -p .
E2E_BASE_URL=http://127.0.0.1:9 npx playwright test acceptance-checker.spec.ts
```

Atteso: tsc senza errori, **36 superati** (18 per progetto Playwright × 2).

Facoltativo (suite backend completa senza servizi; Integration/MafReal risultano ignorati, mai superati):

```bash
dotnet test --solution bProjects.sln
```

Atteso: nessun nuovo fallimento rispetto alla baseline; non eseguita in questa sessione.

### Cosa dimostra la riesecuzione V7 (`V7_captured_outputs_keep_only_the_real_omissions`)

| Progetto | Prima (V7) | Dopo |
|---|---|---|
| officina | PASS | PASS |
| ferie | FAIL, 5 (4 paghe/ratei, 1 richiesta) | PASS; note: «richiesta» tolta dalla correzione PM (anomalia aperta, sezione 2), responsabilità esterna in Dependencies |
| magazzino | FAIL, 2 | FAIL, 2: `omissione: «barcode» … legame perso fra voci` + bozza `… (assente gia' dalla revisione confermata …)` |
| fornitori | PASS | PASS |
| aurora | PASS | PASS |
| boreale | FAIL, 4 (2 ordini, 2 integrazione) | FAIL, 2: `omissione: «integrazione» … qualificatore o dettaglio assente` (/tempo reale/, /obbligatori/ assenti ovunque) + bozza ereditata |

### Controfattuali coperti (`AcceptanceCheckerSemanticsTests`)

- Paghe/ratei, 13 frasi che variano soggetto (portale / software paghe), predicato (calcola / gestisce / resta), negazione (stessa o altra
  proposizione, inciso fra virgole, avversativa) e responsabilità; integrazione inventata con il software paghe.
- Richiesta: voci separate con soggetti propri (passa); soggetto diverso «il sistema» (omissione); «rifiuta» perso (omissione); parte con
  fonte del messaggio sbagliato (fonte errata); fatto tolto dalla correzione PM (nota) vs perso dal generatore (errore).
- Ordini: voci separate «i clienti…» (passa) vs spedizione del «corriere» (omissione), in analisi e bozza.
- Colorweb: senza «tempo reale» e/o «obbligatoria» (qualificatore assente); facoltativa, «non è obbligatoria», «ma non in tempo reale»,
  notturna, differita (divieto, anche in bozza); forme lecite riordinate (passa).
- Barcode: strumento negli obiettivi e operazione nei requisiti (legame perso, mai «qualificatore»); strumento legato all'operazione (passa).
- Restano errori: conflitto aperto riportato come requisito (analisi e bozza), citazione inventata.

## 7. Seguiti proposti (non eseguiti)

1. Decidere l'anomalia della sezione 2 (A o B). Solo se B: correggere la scelta della voce nell'harness (`AcceptanceTests.cs`,
   `project-draft-success.spec.ts`) e ripetere N2 sul DGX; se A: chiarire nella fixture l'effetto della correzione su `richiesta`.
   In entrambi i casi rivalutare la regola «tolta dalla correzione PM ⇒ nota» del checker.
2. Rieseguire i sei casi reali con questo verificatore (A1 del protocollo) prima di confrontare prompt V8 (B).
3. Estendere le annotazioni dei qualificatori (`solo`, `almeno`, eccezioni) ad altri casi: oggi sono coperti solo dove annotati.

---

# Verifica indipendente 1B (Sonnet) — 2026-10-31

**Esito del checker: PASS** (con limiti dichiarati sotto). **Difetto separato dell'harness: sì** (sezione 1B.5), da passare a una sessione 1C.
Commit verificato: `66205ea` (HEAD era `bf92286`, solo `HANDOFF.md`). Nessun DGX, nessun MafReal, nessuna suite completa. Codice di produzione intatto.
Il file `bProjects_Studio_Affidabilita_V2.md` non è stato trovato sul disco: le regole sono state ricostruite da questo HANDOFF e dal codice.

## 1B.1 Perimetro (`git diff 2a2ef50 66205ea`)

10 file, tutti nel perimetro: checker C# e fixture (`AcceptanceFixtures.cs`), test di regressione, specchio TS (`acceptance.ts`,
`acceptance-cases.json`, due spec), `casi-e-attese.md`, `HANDOFF.md`. **Non toccati:** prompt MAF, codice degli agenti, pipeline,
schema `AnalysisContent`, configurazione DGX. Le due modifiche all'harness (`AcceptanceTests.cs`, `project-draft-success.spec.ts`)
sono di una riga ciascuna e passano solo l'analisi AI al checker; la logica di applicazione della correzione PM è invariata.

## 1B.2 Replay V7 sulle tracce originali

Traccia: `docs/acceptance-trace/trace-qwen3.8-flash-next-smart-v7.md` (6 progetti; il test legge analisi AI, revisione confermata e bozza
reali, nessun dato sintetico). Il «prima» è stato misurato in uno snapshot di `2a2ef50` (`git archive`, fuori dal repo) con lo stesso lettore.

| Progetto | Prima (2a2ef50) | Dopo (66205ea) | FP rimossi | Omissioni reali conservate | FN introdotti |
|---|---|---|---|---|---|
| officina | PASS | PASS | 0 | – | 0 |
| ferie | FAIL 5 | PASS (3 note) | 4 (paghe/ratei ×2 voci) + 1 riclassificata a nota («richiesta», vedi 1B.5) | – | 0 |
| magazzino | FAIL 2 | FAIL 2 | 0 | barcode: legame lettore→operazione perso (analisi + bozza) | 0 |
| fornitori | PASS | PASS | 0 | – | 0 |
| aurora | PASS | PASS | 0 | – | 0 |
| boreale | FAIL 4 | FAIL 2 | 2 (ordini/spedizione in due voci col soggetto «clienti») | integrazione Colorweb: «tempo reale» e «obbligatori» assenti ovunque (analisi + bozza) | 0 |
| **Totale** | **11 segnalazioni, 3 PASS / 3 FAIL** | **4 segnalazioni, 4 PASS / 2 FAIL** | **6 FP + 1 riclassificata** | **4 segnalazioni (2 omissioni × analisi/bozza)** | **0 dimostrati** |

Le cifre di Opus 1A sono **confermate**. Verificato a mano sulla traccia: il boreale AI contiene «I clienti inseriscono gli ordini» e
«I clienti seguono la spedizione» (FP legittimo rimosso) ed «Integrazione con il gestionale ordini Colorweb» senza «tempo reale» né
«obbligator» (omissione reale); il magazzino ha il lettore di codici a barre solo negli obiettivi e «registrano carichi e scarichi» nei requisiti.

## 1B.3 Controfattuali indipendenti (`AcceptanceCheckerIndependentReviewTests.cs`, nuovo)

| Frase | Esito | Atteso |
|---|---|---|
| «Il software paghe calcola i ratei» | non violazione | ok |
| «Il portale calcola i ratei» | violazione | ok |
| «Il portale non calcola i ratei» | non violazione | ok |
| «Il portale calcola le paghe ma non le trasferte» | violazione | ok |
| «Il portale integra il software paghe» (anche «importa i ratei dal…») | `integrazione-paghe` segnalata | ok |
| «Colorweb è integrato in tempo reale ed è obbligatorio» | conforme | ok |
| «Colorweb è integrato ma può essere sincronizzato di notte» (in sostituzione) | omissione, «qualificatore assente» | ok |
| «…in tempo reale, ma non è obbligatorio» | `colorweb-indebolita` | ok |
| «Gli ordini… dai clienti» + «I clienti monitorano le spedizioni nel portale» | conforme | ok |
| «Gli ordini… dai clienti» + «spedizioni monitorate dal corriere esterno» | omissione | ok |
| «Gli ordini… dai clienti» + «le spedizioni sono monitorate **dal portale**» | **omissione (non conforme)** | **diverge dall'incarico** |

Ultima riga: la parte atomica richiede il soggetto «client». La frase dell'incarico non lo contiene e il soggetto è diverso («il portale»
anziché i clienti): scelta restrittiva, nessun falso negativo, ma possibile falso positivo se il modello usa il passivo. Da decidere se annotare
una variante di `Parts`. La sensibilità non è peggiorata: tutte le omissioni e violazioni dei test precedenti restano rilevate.

## 1B.4 Limiti del checker emersi (falsi negativi **dimostrati**, non nel replay V7)

Asseriti in `Known_limits_of_the_checker`, così un cambiamento futuro è visibile. Sono precedenti o ristretti rispetto a V7, non regressioni.

1. Una parola della lista `Negation` ovunque nella stessa proposizione esenta la violazione: «Il portale calcola i ratei **senza** intervento
   manuale», «…anche **fuori** orario», «Il portale calcola i ratei, il software paghe **no**» non sono segnalate. (Prima la lista valeva per l'intera voce.)
2. `colorweb-indebolita` non conosce «di notte» né «sincronizzat…»: «Colorweb può essere sincronizzato di notte», aggiunta a un'analisi
   per il resto ideale, passa. Solo in sostituzione dell'item corretto viene colta (come omissione).
3. Qualificatori non annotati («solo», «almeno», eccezioni) non sono verificati. Esempio concreto in 1B.5: `solo-responsabile` è soddisfatto
   da «Il responsabile diretto approva o rifiuta la richiesta», che non esprime l'esclusività.
4. Soggetti impliciti, pronomi, predicati diversi con lo stesso soggetto («mostra» vs «calcola»): restano alla revisione umana (già dichiarato).
5. La regola «presente nell'analisi AI, assente nella confermata ⇒ nota» vale per **qualunque** fatto, non solo per quelli toccati
   dalla correzione prevista.

## 1B.5 Anomalia della correzione PM (N2 ferie): diagnosi

**Conclusione: difetto di selezione della voce nell'harness (interpretazione B), non modifica lecita del PM.** Evidenze:

- `casi-e-attese.md` (righe 106-107) descrive la correzione come «Modificare la voce «Le richieste sono approvate solo dal responsabile diretto»
  in: «…approvate esclusivamente dal responsabile diretto…»». Nel V7 il modello **non ha prodotto** quella voce: aveva «Il responsabile diretto
  approva o rifiuta la richiesta».
- L'harness seleziona la **prima** voce (sezioni di `Accept` in ordine, Requirements per prima) che soddisfa `Match = [responsabile diretto]`:
  `AcceptanceTests.cs` ~439-441 e `project-draft-success.spec.ts` ~328-336. La regex è troppo generica e ha colpito la voce sull'approvazione,
  sostituendola per intero. Se il bersaglio non esiste, non c'è errore: si riscrive un'altra voce.
- La fixture dopo la correzione continua ad aspettarsi `rifiut` nella bozza (fatto `richiesta`): sotto A il gold set sarebbe incoerente.
- Il checker classifica correttamente quel che accade (nota «tolta o riscritta dalla correzione PM», nessun errore), perché la bozza segue
  fedelmente la revisione confermata. Ma la nota **maschera** il difetto dell'harness come esito accettabile: è una conseguenza, non un errore del checker.
- Doppio effetto: il vincolo «solo» non è stato né prodotto dal modello né verificato dal checker (limite 3 sopra).

**Serve una sessione 1C** (harness, non checker): rendere la selezione della voce specifica (es. scegliere solo voci che esprimono l'esclusività, oppure
fallire/aggiungere se l'anteprima non la contiene), allineare la fixture, rieseguire N2 sul DGX. La decisione A/B spetta all'autore; l'evidenza favorisce B.
Difetto indipendente dall'esito PASS del checker.

## 1B.6 Test eseguiti (PASS / FAIL / SKIP separati)

| Comando | PASS | FAIL | SKIP |
|---|---|---|---|
| `dotnet build bProjects.sln` | 0 errori, 0 avvisi | – | – |
| `dotnet test` classi `AcceptanceCheckerSemanticsTests` + `AcceptanceFixtureTests` (replay V7 incluso, traccia presente) | 168 | 0 | 0 |
| idem con `AcceptanceCheckerIndependentReviewTests` in più | 191 | 0 | 1 (`V7_replay_dump`, richiede `BPROJECTS_REVIEW_OUT`: stampa, non asserisce) |
| `npx tsc --noEmit -p .` (tests/e2e) | exit 0 | – | – |
| `playwright test acceptance-checker.spec.ts` (E2E_BASE_URL fittizio) | 36 | 0 | 0 |

Una mia aspettativa errata («…, il software paghe no» come violazione) ha fatto fallire un test in scrittura: il comportamento reale è
documentato come limite 1 e il test aggiornato. Non eseguiti: DGX, MafReal, E2E con modello, suite completa, carico, riavvii Docker.

## 1B.7 Da passare a Opus

1. Sessione 1C: correzione PM dell'harness (C# e Playwright) e coerenza di `richiesta`/`solo-responsabile`.
2. Decidere se annotare una variante di `Parts` per ordini/spedizioni senza soggetto «clienti» (passivo).
3. Restringere `Negation` (ora per proposizione, ma ancora generica) ai verbi vietati; aggiungere «di notte»/«sincronizzat…» a `colorweb-indebolita`.
4. Restringere la regola «tolta dalla correzione PM ⇒ nota» ai soli fatti che la correzione dichiara di toccare.

---

# Sessione 1C (Opus) — correzione PM dell'harness — 2026-10-31

**Base:** `experiment/requirements-quality` @ `986eaa2`. **Esito:** difetto 1B.5 corretto nell'harness (C# e Playwright). Checker C#/TS,
gold set, prompt, agenti, schema, backend, DGX, database: **non toccati**. Nessun DGX, nessuna suite completa, nessuna generazione.

## 1C.1 Causa esatta

`AcceptanceTests.ApplyCorrection` (ex righe 438-449) e `project-draft-success.spec.ts` (ex 330-344) sceglievano la **prima** voce delle
sezioni `Accept` che soddisfa `Match` del fatto da modificare. Per `solo-responsabile` `Match = [responsabile diretto]`: nel V7 l'unica
voce con quelle parole era «Il responsabile diretto approva o rifiuta la richiesta», l'unica a portare la parte
`[responsabile, approva, rifiut]` del fatto `richiesta`. La sostituzione integrale con il testo della correzione cancellava «rifiuta».
Voce assente = nessun errore se un'altra combaciava. Lo stesso difetto era nei simulatori dei test del checker
(`AcceptanceFixtureTests.Corrected`, che sostituiva **tutte** le voci combacianti della prima sezione, e `idealDraft` in
`acceptance-checker.spec.ts`).

## 1C.2 Strategia (`PmCorrection.cs` / `support/pm-correction.ts`, stesse regole e messaggi)

Le fixture non hanno id stabili delle voci prodotte dal modello: scelta per contenuto, deterministica, senza nuovo matching semantico.

1. **Candidate:** voci delle sezioni `Accept` del fatto da modificare che soddisfano il suo `Match` (come prima).
2. **Conservazione:** si scarta ogni candidata la cui sostituzione con `EditedText` renderebbe non coperto un altro fatto atteso che prima
   lo era, valutato con `AcceptanceChecker.Covers` / `covers` (la regola del checker: `Match` intero o tutte le `Parts`) sulle sezioni
   `Accept` di quel fatto. I fatti cosi' persi sono riportati (`Lost`).
3. **Voce dedicata:** fra le candidate sicure prevale quella che non soddisfa da sola nessun'altra attesa nella sua sezione; se nessuna e'
   dedicata si considerano tutte le sicure.
4. **Esito:**

| Caso | Esito | Effetto sull'harness |
|---|---|---|
| una sola candidata (dopo 2-3) | `Found` (sezione, indice) | sostituito **solo il testo** di quella voce; id, classificazione, `evidenceRefs` invariati; fonte PmRevision assegnata dal server (nel simulatore: aggiunta alla voce e alle integrazioni come prima) |
| nessuna candidata | `Absent` | `correzione non applicabile: la voce «k» da modificare non e' nell'anteprima (Sezioni)`; nessuna voce toccata |
| candidate, ma ognuna porta altri fatti | `Absent` con `Lost` | `correzione non applicabile: nessuna voce dedicata a «k» in …; sostituire «…» cancellerebbe «f» (fatti indipendenti dalla correzione)` |
| piu' candidate equivalenti | `Ambiguous` | `correzione non applicabile: «k» ambigua, N voci candidate (…), nessuna scelta silenziosa` |

`Absent`/`Ambiguous` sono **fallimenti del percorso** (`run.Fail` / `failures.push`), come gia' accadeva per «voce non nell'anteprima»;
integrazioni e risoluzione del conflitto si applicano comunque, `CorrectionApplied` resta legato all'esito del PUT. Scelta coerente con
il contratto: la correzione prevista e' una *modifica* («Modificare la voce …», `casi-e-attese.md`), quindi un bersaglio assente si
segnala; aggiungere la voce mancante avrebbe trasformato la lacuna del modello («solo» non espresso, limite 1B.4.3) in un successo.

## 1C.3 Caso ferie V7

Sulle analisi AI reali della traccia V7 (`V7_captured_analyses_select_the_same_items_except_ferie_which_is_reported`):
officina, magazzino, fornitori, aurora, boreale → `Found` sulla **stessa** voce che l'harness V7 aveva modificato (verificato sulla
revisione confermata registrata); ferie → `Absent`, `Lost = [richiesta]`. Con il nuovo harness la revisione confermata conserva
«Il responsabile diretto approva o rifiuta la richiesta»: **«rifiuta» non sparisce**, la bozza non eredita un'omissione prodotta
dall'harness e nessuna nota ««richiesta» presente nell'analisi AI ma non nella revisione confermata» la maschera. N2 risulterebbe invece
**FAIL** con il messaggio esplicito «nessuna voce dedicata a «solo-responsabile» …»: e' la lacuna reale (esclusivita' non espressa dal
modello), ora visibile invece che nascosta. Da confermare con una nuova esecuzione N2 sul DGX (non eseguita).

Nota: `V7_captured_outputs_keep_only_the_real_omissions` (1A/1B) rilegge la revisione confermata **storica** prodotta dal vecchio harness,
quindi continua ad attendersi la nota su «richiesta»: documenta il V7 com'era, non il comportamento attuale.

## 1C.4 Modifiche

```
backend/tests/bProjects.IntegrationTests/PmCorrection.cs                     nuovo: scelta della voce (Found/Absent/Ambiguous)
backend/tests/bProjects.IntegrationTests/PmCorrectionTests.cs                nuovo: 15 prove (9 + 6 progetti nominali)
backend/tests/bProjects.IntegrationTests/AcceptanceTests.cs                  ApplyCorrection usa PmCorrection (ora internal, provato direttamente)
backend/tests/bProjects.IntegrationTests/AcceptanceFixtureTests.cs           Corrected usa PmCorrection (una sola voce, come il percorso)
backend/tests/bProjects.IntegrationTests/AcceptanceCheckerSemanticsTests.cs  la prova «tolta dalla correzione PM ⇒ nota» costruisce la
                                                                             riscrittura PM in modo esplicito (il simulatore non la produce
                                                                             piu'); asserzioni sul checker invariate
tests/e2e/support/pm-correction.ts                                           nuovo: specchio TS
tests/e2e/pm-correction.spec.ts                                              nuovo: 11 prove × 2 progetti Playwright, stessi messaggi del C#
tests/e2e/project-draft-success.spec.ts                                      legge l'anteprima di tutte le sezioni attese e usa locateEdit
tests/e2e/acceptance-checker.spec.ts                                         idealDraft usa locateEdit
```

## 1C.5 Test eseguiti

| Comando | PASS | FAIL | SKIP |
|---|---|---|---|
| `dotnet build bProjects.sln` | 0 errori, 0 avvisi | – | – |
| `dotnet test --solution bProjects.sln --filter-class "*PmCorrectionTests" --filter-class "*AcceptanceCheckerSemanticsTests" --filter-class "*AcceptanceFixtureTests" --filter-class "*AcceptanceCheckerIndependentReviewTests"` (traccia V7 presente) | 206 | 0 | 1 (`V7_replay_dump`, richiede `BPROJECTS_REVIEW_OUT`, preesistente) |
| `npx tsc --noEmit -p .` (tests/e2e) | exit 0 | – | – |
| `E2E_BASE_URL=http://127.0.0.1:9 npx playwright test acceptance-checker.spec.ts pm-correction.spec.ts` | 58 | 0 | 0 |

Prima esecuzione backend: 2 FAIL, corretti nel codice e non rilanciando i test: la vecchia prova del checker riproduceva il difetto
dell'harness; una voce ridondante «approva o rifiuta» accanto a quella sull'esclusivita' dava `Ambiguous` (da qui la regola 3).
Senza traccia V7 le prove di riesecuzione risultano ignorate, mai superate. Non eseguiti: DGX, MafReal, E2E con stack, suite completa.

Copertura delle richieste: (1) voce presente; (2) parole simili, anche con voce ridondante; (3) voce assente; (4) ambigua; (5) «rifiuta»
conservato, percorso JSON e simulatore; (6) id/classificazione/fonti invariati, fonte PmRevision, validatori PmEdit/PmConfirm, bozza
conforme; (7) forma V7 sintetica e traccia V7 reale; (8) sei progetti nominali + riesecuzione V7 dei cinque con voce. Un'omissione vera
prima della correzione (nessun «rifiuta» nell'analisi AI) resta segnalata in analisi e in bozza («assente gia' dalla revisione confermata»).

## 1C.6 Stato dei problemi

**Risolto:** 1B.5 / 1B.7.1 lato harness (scelta della voce, target assente/ambiguo, conservazione dei fatti, parita' C#/TS).

**Aperti (fuori perimetro 1C, invariati):**
1. 1B.4.1: `Negation` per proposizione ancora generica («senza», «fuori», «no»).
2. 1B.4.2: `colorweb-indebolita` non conosce «di notte»/«sincronizzat…».
3. 1B.4.3: qualificatori non annotati; `solo-responsabile` (`Match = [responsabile diretto]`) non verifica l'esclusivita'. E' il motivo
   per cui serve la regola 3; restringere `Match` (es. `solo|esclusiv|soltanto`) e' una decisione sul gold set, non presa qui.
4. 1B.4.5 / 1B.7.4: «presente nell'analisi AI, assente nella confermata ⇒ nota» vale per qualunque fatto. L'harness non produce piu'
   quel caso, ma il checker resta permissivo verso una correzione reale che tolga un fatto.
5. 1B.7.2: variante passiva di `Parts` per ordini/spedizioni.
6. Rieseguire N2 (e i sei casi) sul DGX con il nuovo harness per confermare 1C.3.

## 1C.7 Per la sessione Sonnet 1D (verifica indipendente)

1. Diff di questo commit contro `986eaa2`: solo i file di 1C.4; nessun file di checker (`AcceptanceChecker` in `AcceptanceFixtures.cs`,
   `support/acceptance.ts`), fixture o produzione.
2. Rilanciare i comandi di 1C.5 (con la traccia V7 in `docs/acceptance-trace/`, non versionata).
3. Controfattuali propri su `PmCorrection.Locate`/`locateEdit`: voce in sezione non `Accept`; candidata che porta un fatto coperto anche
   altrove (deve restare sicura); due voci dedicate in sezioni diverse (Ambiguous); fatti con `Parts` divisi fra la candidata e un'altra voce.
4. Verificare la parita' C#/TS dei messaggi (le prove TS confrontano le stringhe esatte dei casi assente e V7).
5. Valutare se `Ambiguous` per duplicati letterali in sezioni diverse sia troppo severo per i casi reali (nel V7 non si presenta).

---

# Sessione 2A (Opus) — prompt di consolidamento V8 — 2026-10-31

**Base:** `experiment/requirements-quality` @ `35d9468`. **Esito:** una sola variante V8 del prompt di analisi, versione `analysis-v3`, e un
helper di replay offline della richiesta Boreale V7. Nessuna inferenza DGX, nessun benchmark, nessuna E2E. **Non toccati:** checker, gold set,
harness PM, `AnalysisContent`, JSON Schema, grafo MAF (`project-analysis-v2`), compattazione, DocumentAgent (`document-v2`), chat
(`conversation-v2`), database, modello, sampling, reasoning, promemoria finale del messaggio utente (`AnalysisWorkflow.cs` ~367).
`bProjects_Studio_Affidabilita_V2.md` non è sul disco (come in 1B): ipotesi prese dall'incarico e dalle sezioni 1A–1C.

## 2A.1 Prompt V7 effettivamente inviato

| | V7 (campagna del commit `2a2ef50`) | V8 (questa sessione) |
|---|---|---|
| Versione MAF | `analysis-v2` | `analysis-v3` |
| SHA-256 del system effettivo (risorsa dopo `Trim`, UTF-8) | `25adffa60b229be87f733061b746dd427ef9095accb83dd06b726b5d0d557c98` | `930d9b41223411b22cd87b8df35bcb7c288088fa33de66e06c79466e14e49588` |
| Lunghezza | 12 327 caratteri, 1 876 parole | 12 874 caratteri (+547), 1 967 parole (+91) |
| Token del system (Qwen) | **3 086**, misurati: `/tokenize` del proxy, id 44 | **~3 223 stimati** (+~137, in proporzione ai caratteri); non misurati, nessun DGX |

Ricostruzione: il `messages[0].content` della richiesta Boreale catturata (proxy **id 233**, `2026-10-31T12:59:47.837Z`,
`calls-qwen3.8-flash-next-smart-v7.jsonl`) è identico byte per byte ad `analysis.md` di `2a2ef50` e di `35d9468` (nessun commit successivo
ha toccato `backend/src`). Il prompt di `main` è diverso (precedente a `2a2ef50`) e non è stato usato. Lo stesso system è nelle sei
analisi V7 (proxy 56, 114, 215, 233, 291, 349).

## 2A.2 Perché le istruzioni V7 non bastavano (Colorweb)

Messaggio PM: «Integrazione con il gestionale ordini Colorweb: obbligatoria, gli ordini devono arrivare in tempo reale al gestionale.»
Voce V7 (dependencies): «Integrazione con il gestionale ordini Colorweb». Istruzioni V7 pertinenti e loro limite:

1. «Le restrizioni («solo», «esclusivamente», «almeno», «al massimo», «per tutti») fanno parte dell'informazione e restano»: è
   **un elenco lessicale** di esclusività e quantità; obbligo e tempi («obbligatoria», «in tempo reale») non vi rientrano e il modello li ha
   trattati come accessori.
2. «Il testo riprende le parole … del project manager»: la voce V7 la rispetta alla lettera copiando il **titolo prima dei due punti**;
   nessuna istruzione dice che il solo nome del tema è incompleto.
3. Spinta alla brevità: «una frase breve», regola 7 «Sii essenziale … nessun dettaglio», `dependencies` descritte per nomi («sistemi,
   integrazioni»), «Una integrazione sta qui e non si ripete»: tutte favoriscono la voce-etichetta.
4. «soggetto, azione, oggetto e condizione … restano insieme» esiste ma è astratta; il caso magazzino V7 (lettore separato dall'operazione)
   mostra che non basta per il legame strumento→operazione.
5. Il promemoria finale (messaggio utente, non system) ricorda fatti, numeri, nomi ed esclusioni, non i qualificatori. Lasciato invariato:
   fuori perimetro e deve restare identico nel replay.

## 2A.3 Modifica V8 (una sola: sostituzione di una frase nel paragrafo «In quale sezione va ogni informazione»)

```diff
-Le restrizioni («solo», «esclusivamente», «almeno», «al massimo», «per tutti») fanno parte dell'informazione e restano.
+Accorciare una voce non deve cambiarne il significato: restano chi compie ogni azione, lo strumento insieme all'operazione per cui si usa e ogni qualificatore che vincola il fatto (obbligo, tempi, frequenze, quantità, «solo», «esclusivamente», «almeno», «al massimo», «per tutti», negazioni). Il solo nome del tema non basta: se il project manager scrive «Il rinnovo della tessera è obbligatorio e lo conferma il tesoriere entro 3 giorni», la voce non è «Rinnovo della tessera» ma riporta obbligo, tesoriere e 3 giorni. Se un compito resta a un sistema esterno, la voce dice chi lo svolge, senza trasformarlo in un'integrazione che il project manager non ha chiesto.
```

Nel file la frase sta dentro la riga 22 (paragrafo unico): `git diff 35d9468 -- backend/src/bProjects.App/AI/Prompts/analysis.md` mostra
la riga intera; il resto della riga è invariato.

Motivazione: il vincolo diventa **semantico** (non cambiare il significato) invece di un elenco di parole; i qualificatori sono nominati per
categoria (obbligo, tempi, frequenze, quantità, esclusività, negazioni) e l'elenco V7 resta incluso per non perdere nulla; attore e
strumento→operazione sono esplicitati; un **contrasto** giusto/sbagliato in un dominio inventato (il circolo, già usato dall'esempio del
prompt: tessera, tesoriere) colpisce la voce-etichetta senza ricalcare Colorweb, ferie o magazzino; una frase distingue responsabilità
esterna e integrazione (caso ferie/paghe) senza nominarlo. Invariati: forma JSON, sezioni, classificazioni, fonti, regole, esempio.

Versioning: solo `ConversationalAgent.ConsolidationPromptVersion` `analysis-v2` → `analysis-v3`. È la versione congelata in
`ProcessingRuns.PromptVersions` al comando (`AnalysisEndpoints.cs` ~175) e confrontata prima di avvio o ripresa (`AnalysisWorkflow.cs` ~250):
un run o checkpoint nato con `analysis-v2` termina `workflow_incompatible` senza inferenza; i checkpoint appartengono al run, quindi nessun
checkpoint V7 viene ripreso con V8. Il grafo resta `project-analysis-v2` (la topologia non cambia); chat e documento invariati.

## 2A.4 Ipotesi verificabili con la sola prova Boreale (2B)

Una prova osserva un miglioramento, non ne dimostra la ripetibilità.

- **H1 (principale):** la voce Colorweb contiene `obbligatori` **e** `tempo reale` (attesa `boreale/integrazione`, `Match = [colorweb,
  tempo reale, obbligatori]`, sezione Dependencies, ammesse Requirements e Included), con fonte il messaggio sequenza 5 (`945031da-…`).
- **H2 (nessuna regressione):** restano coperte le altre attese Boreale (goal; ordini con `Parts` [client+inser+ordini], [client+spedizion];
  notifiche SMS/email; SMS per la consegna; SSO; «solo i propri ordini»; settembre; listino escluso) e nessun divieto scatta (`listino`,
  `password-locali` affermati, `colorweb-indebolita`, `progetto-aurora`); nessuna integrazione o voce inventata.
- **H3 (costo):** `finish_reason = stop`, JSON valido per lo schema, `completion_tokens` vicino ai 2 153 del V7 (soglia indicativa +15%).
  `prompt_tokens` V8 − 4 138 = differenza **esatta** di token del system (il resto del payload è identico): misura la stima di 2A.1.
- **Non verificabili con Boreale:** strumento→operazione (magazzino N3), responsabilità esterna e «solo» (ferie N2). Richiedono la
  riesecuzione di quei casi, non prevista in 2B.

## 2A.5 Replay della richiesta Boreale V7 (`tests/prompt-replay/replay_analysis.py`)

Script di sola libreria standard: legge il corpo **originale** dal JSONL del proxy, sostituisce soltanto `messages[0].content` con il file
indicato (dopo `strip()`, come `LoadPrompt`), verifica che tutto il resto coincida (hash canonico del payload col system azzerato, ordine
delle chiavi) e altrimenti rifiuta. Senza `--send` non usa la rete. Rifiuta cartelle di uscita sotto `docs/`. Scrive `payload.json`,
`report.json` e, con `--send`, `response.json` e `content.json`.

Verifica a secco eseguita: con il prompt V7 (`git show 2a2ef50:…/analysis.md`) il payload ricostruito è **identico** all'originale in ogni
chiave e messaggio; con V8 differisce **solo** il messaggio 0. Hash del resto del payload (originale = replay):
`6c38f81971a054199d2e42e7c337bd3d0a6d715e0975f0a3333c680ccf178239`; `response_format`: `671aa3319a54725eda11d16c33fc3bba0137622a01e1f1e947bc8311dced5350`.

Identici: 11 messaggi (system, 4 coppie user/assistant, user sequenza 9, user con `analysisId 50643b97-…`, `projectId 4360fc79-…`,
`sourceCutoffSequence 9` e promemoria), ruoli e ordine, `response_format` (schema `analysis_content`), `model Qwen3.8-Flash-Next-Smart`,
`temperature 0`, `max_completion_tokens 8192`, `chat_template_kwargs.enable_thinking false`; il corpo originale non ha altri campi.

Differenze inevitabili (non di contenuto), dichiarate:
1. **Byte del corpo HTTP:** il proxy ha registrato il JSON già interpretato, non i byte; il replay lo riserializza (compatto, UTF-8 senza
   escape). Spazi ed escape possono differire; il contenuto JSON è identico.
2. **Header HTTP:** non registrati dal proxy. Il replay invia `Content-Type: application/json` e, se `AI_API_KEY` è definita,
   `Authorization: Bearer …` (come il backend). Nessun header influisce sul campionamento di llama.cpp.
3. **Stato del server:** la V7 aveva 3 091 token in cache di prefisso (`cache_n`); il nuovo system cambia il prefisso, quindi la cache non
   coincide. Il server deve essere la stessa build: il report confronta `system_fingerprint` con `b10703-0b5be7e4a` (`sameServerBuild`).
   Se differisce, il confronto **non** è isolato.
4. **Percorso:** il replay chiama il DGX direttamente, non tramite backend/MAF né proxy: è voluto (la richiesta non è ricostruita dal nuovo
   workflow). Il backend con V8 produrrebbe lo stesso corpo salvo il system (stesso grafo, schema e promemoria), ma non è stato verificato.

## 2A.6 File modificati

```
backend/src/bProjects.App/AI/Prompts/analysis.md                       una frase sostituita (2A.3)
backend/src/bProjects.App/AI/ConversationalAgent.cs                    ConsolidationPromptVersion analysis-v3
backend/tests/bProjects.IntegrationTests/AnalysisPromptVersionTests.cs nuovo: hash V8 del prompt incorporato, versioni chat/documento/grafo
                                                                       invariate, run congelato con analysis-v2 non compatibile
tests/prompt-replay/replay_analysis.py                                 nuovo: helper di replay (2A.5)
HANDOFF.md                                                             questa sezione
```

## 2A.7 Test eseguiti

| Comando | PASS | FAIL | SKIP |
|---|---|---|---|
| `dotnet build bProjects.sln` | 0 errori, 0 avvisi | – | – |
| `dotnet test --solution bProjects.sln --no-build --filter-class "*AnalysisPromptVersionTests"` | 4 | 0 | 0 |
| `replay_analysis.py` a secco, V7 e V8, id 233 | payload V7 identico; V8 solo messaggio 0 | – | – |

Non eseguiti: DGX, MafReal, Integration (Postgres), E2E, suite completa. Le prove su Postgres che asseriscono `ConsolidationPromptVersion`
(`AnalysisDispatchTests` ~160) leggono la costante, quindi seguono la nuova versione.

## 2A.8 Istruzioni per la sessione Sonnet 2B (una sola inferenza)

Dalla radice del repository, con la traccia V7 in `docs/acceptance-trace/` (non versionata) e l'host del DGX di `infra/.env`
(`AI_ENDPOINT` senza il suffisso `/v1/`). Prima a secco:

```bash
python -I tests/prompt-replay/replay_analysis.py --calls docs/acceptance-trace/calls-qwen3.8-flash-next-smart-v7.jsonl --id 233 --system backend/src/bProjects.App/AI/Prompts/analysis.md --out ../replay-2b
```

Controllare nel report: `originalSystemSha256 = 25adffa6…`, `replaySystemSha256 = 930d9b41…`, `restOfPayloadIdentical = true`. Poi **una
sola** chiamata:

```bash
AI_API_KEY=<chiave> python -I tests/prompt-replay/replay_analysis.py --calls docs/acceptance-trace/calls-qwen3.8-flash-next-smart-v7.jsonl --id 233 --system backend/src/bProjects.App/AI/Prompts/analysis.md --out ../replay-2b --send http://<dgx>:8080
```

1. Verificare `sameServerBuild = true`; se falso, dichiarare il confronto non isolato.
2. Confrontare `../replay-2b/content.json` con la risposta V7 (proxy 233, `responseBody.choices[0].message.content`, oppure la traccia md,
   chiamata 6 di boreale, righe ~6667-7390) voce per voce, secondo H1–H3 di 2A.4.
3. Riportare `prompt_tokens` (differenza esatta del system), `completion_tokens`, `finish_reason`.
4. Non ripetere la chiamata, non cambiare prompt, non eseguire altri casi; output fuori da `docs/` e fuori dal repository.

---

# Sessione 2B (Sonnet) — esperimento V7 vs V8, una sola inferenza — 2026-10-31

**Base:** `experiment/requirements-quality` @ `26dddd3`. **Esito: NON MIGLIORATO.** Nessun codice modificato; una sola richiesta al DGX, senza retry,
prompt invariato dopo l'osservazione.

**Output** (fuori dal repository e da `docs/`, senza credenziali): `C:\source\repos\babbubba\replay-2b\` — `payload.json`, `report.json`, `response.json`
(risposta grezza con `timings`), `content.json` (AnalysisContent V8), `v8_pretty.json` e `v7_content.json` (versioni indentate per il confronto),
`stdout.txt`, `stderr.txt`.

## 2B.1 Verifiche offline
- Branch/commit corretti (`26dddd3`); albero pulito salvo `docs/` non versionato.
- SHA-256 del system dopo `strip()`: V7 (`2a2ef50`) `25adffa6…`, V8 `930d9b41…`: coincidono con 2A.1.
- Dry-run: `restOfPayloadIdentical = true`, `topLevelKeyOrderIdentical = true`, hash del resto `6c38f819…` uguale all'originale; cambia solo `messages[0].content`.
- Script: una sola `urlopen` in `--send`, nessun ciclo né retry; rifiuta uscite sotto `docs/`.
- Endpoint: `AI_ENDPOINT` di `infra/.env` è un segnaposto (`host.docker.internal:9`); usato il DGX `192.168.253.18:8080` dalle variabili di sessione
  `BPROJECTS_TEST_DGX_*` (stesso host dei test di integrazione). `/v1/models` (non inferenza) elenca `Qwen3.8-Flash-Next-Smart` (stato `sleeping`).

## 2B.2 Misure

| Metrica | V7 | V8 |
|---|---|---|
| Integrazione obbligatoria | **No** («Integrazione con il gestionale ordini Colorweb») | **Sì** («Integrazione obbligatoria con il gestionale ordini Colorweb», Dependencies, fonte seq. 5) |
| Trasferimento ordini in tempo reale | No | **No**: «tempo reale» e «ordini che arrivano al gestionale» assenti in tutto il JSON |
| Altri requisiti conservati | tutti | tutti (ordini, spedizione, SMS/email a scelta, SMS avviso consegna, SSO, solo propri ordini, settembre, listino escluso, niente password locali) |
| Omissioni reali | 2 (obbligo, tempo reale) | 1 (tempo reale / modalità di trasferimento ordini) |
| Affermazioni non supportate | 0 | 0 (nessuna integrazione, ruolo o funzione inventati; nessuna informazione dell'assistente promossa) |
| Validità JSON | valida, 14 ID unici | valida, stessa struttura AnalysisContent (15 chiavi, 14 ID unici) |
| Prompt tokens | 4 138 (3 091 da cache) | 4 269 (0 da cache): system +131 token esatti (stima 2A.1: ~+137) |
| Completion tokens | 2 153 | 2 136 (−0,8%, entro la soglia +15%) |
| Tempo totale | 80,1 s (proxy) | server: 85,6 s (prefill 6,7 s + generazione 78,9 s); a muro ~165 s (da mtime dei file) perché il modello era `sleeping` e senza cache di prefisso — non confrontabile col V7 |
| Finish reason | stop | stop |
| System fingerprint | `b10703-0b5be7e4a` | `b10703-0b5be7e4a` (`sameServerBuild = true`) |

Il fingerprint uguale indica la stessa build di llama.cpp, non prova identità di pesi, quantizzazione né configurazione del server.

## 2B.3 Valutazione semantica
- **H1 non soddisfatta:** V8 recupera il qualificatore «obbligatoria» e lo lega correttamente a Colorweb, ma perde «tempo reale» e il legame
  ordini→gestionale. La modifica V8 ha quindi aiutato su uno dei due qualificatori (obbligo) e non sull'altro (tempi).
- **H2 soddisfatta:** nessuna regressione. Differenze minori: l'obiettivo V8 ripete «per inserire ordini e seguirne la spedizione» (già in Requirements), duplicazione lieve
  contro «ogni informazione in una sola sezione», ma supportata dalla fonte seq. 1; «seguono la spedizione degli ordini» e «Niente password locali per l'accesso» sono
  riformulazioni fedeli.
- **H3 soddisfatta:** JSON valido, `finish_reason = stop`, fonti = messaggi PM (seq. 1, 3, 5, 7, 9), nessun messaggio dell'assistente come fonte. Il checker automatico non è stato rieseguito;
  il confronto è manuale sul JSON.

## 2B.4 Decisione: **NON MIGLIORATO**
Resta una delle due omissioni («tempo reale»). Una singola inferenza a temperatura 0 mostra un miglioramento parziale ma non dimostra ripetibilità né
generalità; V8 non va considerato migliore in assoluto. Non verificati: strumento→operazione (magazzino) e responsabilità esterna (ferie), non coperti da Boreale.
Possibile direzione (non eseguita, fuori incarico): il vincolo sui tempi è stato trattato come accessorio anche con l'esempio «entro 3 giorni»; valutare in una sessione
di sviluppo se serve un caso esplicito di tempi/ordine di trasferimento.

# Sessione 3 (Opus) — ID degli elementi di analisi — 2026-10-31

**Base:** `experiment/requirements-quality` @ `7cdd8ee`. **Esito: nessun bug contrattuale; riuso fra progetti tollerato dal perimetro degli ID.**
Nessuna modifica al codice di produzione, solo due test di gate. Zero DGX, nessuna suite lunga.

## 3.1 Fatto osservato
Report V2 (V7): 74 occorrenze di ID di elementi in sei analisi, 38 distinti globalmente, nessuna collisione interna a un payload.
Il modello produce ID-segnaposto ripetibili (es. `a1b2c3d4-e5f6-7890-abcd-ef1234567890`, `b2c3d4e5-…`), quindi progetti diversi riusano gli stessi ID.

## 3.2 Perché non è un bug (letture verificate nel codice)
- **Contratto** (`ai-workflow.md` §4, regola 1): «ID di elementi/fonti unici **nel payload**». Non c'è requisito di unicità globale né per progetto.
- **Validatore** (`AnalysisContentValidator.Checker`): un solo `_ids` per fonti, elementi, conflitti e alternative del payload. Rifiuta duplicati interni,
  compreso un elemento con l'ID di una fonte.
- **`evidenceRefs`** puntano solo a `sources[].id` (messaggi o revisioni reali, fidati dal server), mai a ID di elementi. `selectedAlternativeIds` punta ad
  alternative dello stesso conflitto, e l'AI non può produrre risoluzioni (regola 3).
- **Revisioni PM** (`PreviewAttribution.Apply`): confrontano gli ID solo con la revisione **corrente della stessa analisi** (`current`, catena `AnalysisId`).
  Una nuova analisi ha un nuovo `AnalysisId` e una propria catena, senza contenuto ereditato. Gli elementi aggiunti dal PM usano `crypto.randomUUID()` (frontend).
- **Persistenza:** `ContentJson` è `jsonb` senza indici o colonne sugli ID degli elementi. Nessuna query li risolve fuori dalla revisione.
- **Documento:** i marcatori `[fonte:<id>]` sono validati contro `SourceIds`, non contro gli ID degli elementi.
- **MAF, resume e checkpoint:** il checkpoint conserva l'output del modello. Il commit (`AnalysisRunCompletion`) valida e persiste quel JSON senza riassegnare ID,
  quindi un resume produce lo stesso contenuto. Nessuna dipendenza dall'unicità globale.
- **Frontend:** `track item.id` all'interno di una sola anteprima, dove l'unicità è garantita dal validatore.

## 3.3 Invariante proposta (ora fissata dai test)
1. Gli ID di fonti, elementi, conflitti e alternative sono unici **all'interno di un payload AnalysisContent** (anche tra categorie diverse).
2. L'identità di un elemento ha significato **solo nella catena di revisioni della stessa analisi** (`AnalysisId`). Lì è stabile, perché l'attribuzione PM si basa su di essa.
3. Un ID di elemento non è una chiave globale. Lo stesso valore in un'altra analisi o in un altro progetto non ha legame semantico.

## 3.4 Rischio residuo (potenziale, non attuale)
Diventa un problema reale solo se si introduce una lettura **tra analisi**: riferimenti a elementi di analisi precedenti, KB, indici o colonne sugli ID degli elementi,
oppure link esterni a un elemento. Per lo stesso motivo un ID-segnaposto non è un UUID v4 RFC: `Guid` lo accetta e il contratto chiede solo «UUID validi».
**Proposta tecnica, da attuare solo quando arriva quel requisito:** riassegnare gli ID nel solo commit della prima revisione AI (`AnalysisRunCompletion`, prima della validazione),
con un UUID v5 deterministico `(AnalysisId, id originale)`. È deterministico, quindi replay/resume/reload danno gli stessi ID e non nasce nessun GUID nuovo.
La mappatura è totale: gli `evidenceRefs` non citano elementi, e l'AI non produce risoluzioni da rimappare. Le revisioni PM ereditano gli ID già riassegnati.
Non va applicata a revisioni già persistite senza una migrazione con mappa esplicita.

## 3.5 Diff
```
 backend/tests/bProjects.IntegrationTests/AnalysisContentTests.cs | 42 +
   - caso semantico AiInvalid `id_elemento_uguale_a_fonte` (elemento con l'ID di una fonte ⇒ rifiutato in entrambe le modalità)
   - Fact `Item_ids_are_unique_per_payload_not_across_analyses_or_projects` (stessi ID degli elementi in due progetti/analisi ⇒ entrambi accettati)
```
Limite: il secondo test fissa il contratto del validatore (contesti indipendenti). Non dimostra l'assenza di vincoli DB futuri, che oggi non esistono (3.2).

## 3.6 Test eseguiti (solo mirati, offline)
- `dotnet build bProjects.sln --no-restore`: 0 avvisi, 0 errori.
- `dotnet test --solution bProjects.sln --no-build -- --filter-class "bProjects.IntegrationTests.AnalysisContentTests"`: **141 superati / 0 falliti / 0 ignorati**.
- `--filter-method "*Invalid_ai_output_is_rejected_identically*"`: 70 = 35 casi × 2 modalità, tutti superati (erano 34 casi prima).

## 3.7 Test mirati da lanciare in una sessione successiva (facoltativi)
```bash
dotnet test --solution bProjects.sln -- --filter-class "bProjects.IntegrationTests.AnalysisContentTests"
dotnet test --solution bProjects.sln --filter-trait "Category=Integration" -- --filter-class "bProjects.IntegrationTests.PreviewRevisionTests"   # richiede BPROJECTS_TEST_POSTGRES
```
Il secondo copre l'attribuzione PM sulla catena di revisioni (invariante 2) e non è stato lanciato qui perché richiede Postgres.

---

# Sessione 4 (Sonnet) — verifica thinking Qwen3.8 — 2026-10-31

**Base:** `experiment/requirements-quality`. **Esito: INCONCLUSIVE — gate tecnico non superato, nessuna inferenza di generazione eseguita.** Nessun codice di produzione modificato, `models.ini` non toccato, server non riavviato.
Evidenze (fuori dal repository): `C:\source\repos\babbubba\replay-3\` (`props.json`, `chat_template.jinja`, `applytpl_*.json`).

## 4.1 Gate (zero generazioni)

1. **Dal config alla richiesta HTTP — verificato.** `Ai:Phases:Analysis:EnableThinking` (vuoto = eredita `Ai:EnableThinking`, default `false`) → `AiOptions.ForPhase` → `InternalModelClient.CreateRawOptions` imposta con `Patch` `chat_template_kwargs = {"enable_thinking": true|false}`. Non è `reasoning_effort`. La V8 di 2B (`replay-2b/payload.json`) contiene `enable_thinking:false`, `temperature 0`, `max_completion_tokens 8192`, `response_format json_schema`; nessun altro campo di ragionamento.
2. **Configurazione server (sola lettura, `/v1/models`) — coincide col `models.ini` fornito:** `Qwen3.8-Flash-Next-Smart`, IQ4_XS, build `b10703-0b5be7e4a`, `--jinja`, `--reasoning on`, `--reasoning-budget 12288`, `--reasoning-format deepseek`, ctx 131072, `--n-predict 32768`, `temperature 1.0`/`seed 4242`/top-k 20/top-p 0.95 come default (la richiesta li sovrascrive con `temperature 0`).
3. **Il template supporta il commutatore per richiesta — verificato.** Il chat template (da `/props`) chiude il prompt con `<think>\n\n</think>\n\n` solo se `enable_thinking is false`; altrimenti (anche non definito) con `<think>\n`. Con `reasoning on` lato server il default sarebbe quindi thinking attivo.
4. **Applicazione osservata senza generare — verificato per il template.** `POST /apply-template` con il payload V8 completo: `enable_thinking=false` → prompt (16 249 car.) termina con `<think>\n\n</think>\n\n`; `true` → prompt (16 447 car.) termina con `<think>\n`. Il cambio è dunque recepito dal server, non solo trasmesso. Nota di stato: la prima chiamata ha risvegliato il modello (da `sleeping` a `loaded`, 82 s di caricamento); nessun token generato.
   Limite: `GET /props` riporta `reasoning_format: none` e `chat_format: Content-only` anche a modello caricato, in contrasto con `--reasoning-format deepseek`: **`/props` non è un'osservabilità attendibile** delle impostazioni effettive di parsing. La conferma a posteriori di thinking dipenderebbe solo da `reasoning_content`/token di completamento nella risposta.
5. **Compatibilità thinking + `response_format: json_schema` — NON dimostrata.** La documentazione non la tratta; la lettura (parziale, 100 000 su 174 534 caratteri) di `common/chat.cpp` al commit della build indica per il percorso Qwen: grammatica eager (non lazy) dal primo token, con un blocco `<think>` *opzionale* che il parser si aspetta di dover aprire il modello stesso. Con il prompt che già termina in `<think>\n` (caso `enable_thinking=true`), il blocco di ragionamento potrebbe non essere ammesso dalla grammatica: la richiesta restituirebbe HTTP 200 con JSON valido **senza** thinking, oppure con ragionamento troncato. È un'inferenza da codice parziale, non una verifica; la parte non letta potrebbe contenere un percorso diverso. Risolverla richiede un'inferenza, ossia proprio ciò che il gate doveva stabilire prima.
   Rischio aggiuntivo noto, non decisivo: `max_completion_tokens 8192` è inferiore a `reasoning-budget 12288`; il ragionamento consumerebbe lo stesso budget del JSON (~2 136 token), con `finish_reason=length` possibile.

## 4.2 Decisione: **INCONCLUSIVE**

Il commutatore è applicato dal template (punti 3–4), ma l'efficacia del thinking con `json_schema` non è dimostrabile senza generare e le evidenze disponibili suggeriscono che la grammatica eager possa sopprimerlo. Con un esito HTTP 200 non si potrebbe distinguere «thinking attivo» da «thinking soppresso», e il gate richiede che l'attivazione sia dimostrabile. Per questo la richiesta di trattamento **non è stata inviata**: V8 non-thinking resta l'unica misura (sezione 2B, NON MIGLIORATO). Nessun confronto, nessuna metrica di costo del trattamento, nessun diff del trattamento.
Nessuna ulteriore sperimentazione né modifica proposta in questa sessione.

## 4.3 Esperimento thinking (su indicazione esplicita dopo 4.2) — INCONCLUSIVE

Richiesta di trattamento = payload V8 di 2B (`replay-2b/payload.json`) con **solo** `chat_template_kwargs.enable_thinking` da `false` a `true`. Diff automatico dei due JSON: unica chiave diversa `chat_template_kwargs`, stesso ordine delle chiavi. Modello già `loaded` (nessun tempo di caricamento incluso), stesso server e build `b10703-0b5be7e4a`, `POST /v1/chat/completions`, non in streaming, **una sola** richiesta, nessun retry, timeout client 240 s.

**Esito:** nessuna risposta entro 240 s (`curl` rc=28, `http=000`, 240,02 s). Nessun corpo di risposta: nessun `reasoning_content`, `finish_reason`, `usage`, `timings` né JSON da valutare. Per regola dell'incarico: **INCONCLUSIVE**, nessuna seconda prova.

Cosa si può dire e cosa no:
- La V8 senza thinking aveva generato 2 136 token in 78,9 s (27 token/s); la richiesta con thinking non è terminata in 240 s, cioè l'attesa è almeno ~3 volte quella della V8 (ragionamento non osservabile, ma coerente con thinking attivo: non è una prova).
- Non si sa se il server stesse ragionando, fosse bloccato dalla grammatica o avesse un altro problema; il client ha chiuso la connessione e non c'è log lato server consultato.
- Nessuna affermazione su qualità («obbligatoria», «in tempo reale»), token o conservazione dei requisiti.
- Costo operativo documentato: con timeout di 240 s e lo stesso `max_completion_tokens 8192`, il trattamento non è completabile nei tempi della V8 su questa configurazione.

Evidenza: `C:\source\repos\babbubba\replay-3\applytpl_true.json` (corpo inviato). Nessun codice, modello, schema, prompt o sampling modificato; nessun ulteriore esperimento.
