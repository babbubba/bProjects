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

## 4.4 Seconda esecuzione (su indicazione esplicita): timeout 600 s — **REGRESSIONE**

Stessa richiesta di 4.3 (`replay-3/applytpl_true.json`, diff con la V8 di 2B: solo `enable_thinking`), unica modifica il timeout HTTP 240 → 600 s. Una sola chiamata, nessun retry, modello già `loaded`. Risposta: `replay-3/response_thinking.json`.

| Metrica | V8 senza thinking (2B) | V8 con thinking |
|---|---|---|
| HTTP / durata a muro | 200 / server 85,6 s (prefill 6,7 + generazione 78,9) | 200 / **313,3 s** (prefill 0,10 s; generazione 312,9 s) |
| `finish_reason` | `stop` | **`length`** |
| Completion tokens | 2 136 | **8 192** (= `max_completion_tokens`) |
| Prompt tokens | 4 269 (0 in cache) | 4 305 (4 301 in cache: prefisso già in cache) |
| Velocità di generazione | 27,1 tok/s | 26,2 tok/s |
| `reasoning_content` | assente | **presente, 31 304 caratteri** |
| `content` finale | JSON AnalysisContent valido | **vuoto (0 caratteri)** |
| JSON valido / schema | sì | **no: nessun JSON prodotto** |
| «obbligatoria» / «in tempo reale» | «obbligatoria» sì, «in tempo reale» no | non valutabile (nessun output) |
| Fingerprint | `b10703-0b5be7e4a` | `b10703-0b5be7e4a` |

- **Thinking effettivamente attivo:** sì, dimostrato da `reasoning_content` non vuoto (il ragionamento inizia con «We need answer user's request: produce only JSON object…» ed è in inglese; elenca gli ID delle fonti e abbozza la struttura). Quindi la grammatica `json_schema` non ha soppresso il ragionamento (chiude il dubbio di 4.1, punto 5), ma lo ha lasciato libero di consumare tutto il budget.
- **Ripartizione dei token:** `usage` non separa reasoning e output; poiché `content` è vuoto, tutti gli 8 192 token sono ragionamento (~3,8 caratteri/token). Il tempo è dunque ~3,7 volte quello della V8 senza produrre alcun risultato.
- **Causa dell'insuccesso:** il ragionamento esaurisce `max_completion_tokens=8192` (inferiore a `reasoning-budget=12288`) prima di chiudere `</think>` e di emettere il JSON, che da solo ne richiede ~2 100. Come da incarico il budget non è stato aumentato e non è stata fatta una seconda prova.
- **Qualità semantica:** nessun confronto possibile; nessuna delle due frasi critiche è presente in un output.

**Decisione: REGRESSIONE** — output inutilizzabile (nessun JSON), costo ~3,7× la V8 e fallimento per limite di output. Con questa configurazione (limite 8192, reasoning-budget 12288) il thinking non è compatibile con l'analisi Boreale. Non dimostrato se un budget maggiore migliorerebbe la fedeltà: non testato per vincolo dell'incarico. Nessun codice, server, modello, schema, prompt o sampling modificato; nessun'altra prova.

## 4.5 Terza esecuzione (su indicazione esplicita): `max_completion_tokens` 16384, timeout 900 s — **PROMETTENTE** (con costo alto)

Prima della chiamata: modello `loaded`, `/slots` con `is_processing=false` (nessuna richiesta in corso). Corpo = V8 di 2B (`replay-2b/payload.json`); diff automatico: **solo** `chat_template_kwargs.enable_thinking` (false→true) e `max_completion_tokens` (8192→16384), stesso ordine delle chiavi; prompt, schema, modello, `temperature 0` invariati; server invariato (`reasoning-budget 12288`). Una sola chiamata, nessun retry. File: `replay-3/payload_think16k.json`, `response_think16k.json`, `content_think16k_pretty.json`.

| Metrica | V8 senza thinking | V8 con thinking (16384) |
|---|---|---|
| `finish_reason` | `stop` | `stop` |
| Completion tokens | 2 136 | **10 625** (×4,97) |
| di cui reasoning / output | – / 2 136 | non separati da `usage`; stima ~8 400 / ~2 200 (output stimato dai caratteri: 4 645 vs 4 476 della V8, 2 136 token) |
| Prompt tokens | 4 269 (0 in cache) | 4 305 (4 301 in cache) |
| Generazione | 78,9 s (27,1 tok/s) | **414,1 s** (25,7 tok/s), ×5,25 |
| Durata a muro | 85,6 s lato server | 414,3 s |
| `reasoning_content` | assente | presente, 31 697 caratteri; **chiuso** (la sua coda dice «Now final… Done.») |
| `content` | JSON valido | JSON valido, 15 chiavi, 17 ID unici, tutte le `evidenceRefs` fra le 5 fonti |
| «obbligatoria» | sì | sì |
| «in tempo reale» | **no** | **sì** |

- **Chiusura del reasoning:** completa e naturale, a ~8 400 token, sotto il `reasoning-budget` 12 288 e sotto il limite 16384. Il fallimento di 4.4 è coerente: 8 192 non bastavano a chiudere il ragionamento (~8 400 token) e a scrivere il JSON. Non emerge un limite della build: il budget non è stato raggiunto.
- **Voce Colorweb (dependencies, fonte seq. 5):** «Integrazione obbligatoria con il gestionale ordini Colorweb, con gli ordini che devono arrivare in tempo reale al gestionale». Entrambi i vincoli e il legame ordini→gestionale sono conservati (H1 di 2A.4 soddisfatta).
- **Altri requisiti (H2):** tutti conservati (ordini, spedizione, SMS/email a scelta del cliente, SMS per l'avviso di consegna, SSO, solo i propri ordini, settembre, listino fuori dal portale, niente password locali). In più, rispetto alla V8, riporta «gestito altrove» per il listino, che è nel testo del PM (seq. 9).
- **Omissioni reali:** 0 (V8: 1). **Affermazioni non supportate:** 0 (nessuna integrazione, ruolo o funzione inventati; fonti = soli messaggi PM). Valutazione manuale, il checker automatico non è stato eseguito.
- **Decisione: PROMETTENTE.** Thinking attivo e dimostrato, entrambi i vincoli critici conservati, nessuna regressione, costo documentato: ~5× token e ~5× tempo (414 s a muro, oltre i timeout di 240 e 600 s provati prima), con limite di output raddoppiato.
- **Cautele:** una sola esecuzione a temperatura 0, indicazione esplorativa e non una misura attendibile (nessuna prova di ripetibilità; il ragionamento è vicino alla soglia di 8 192 e potrebbe superare 16384 su analisi più lunghe). Il confronto non è perfettamente isolato rispetto a V8: oltre a `enable_thinking` è cambiato `max_completion_tokens`, necessario per l'attivazione utile; il modello non era `sleeping` (V8 di 2B era freddo), per cui i tempi di generazione sono confrontabili e quelli a muro no. Non verificati: magazzino e ferie. Il prodotto non è stato modificato; tenere 414 s vs `RequestTimeoutSeconds` del backend è una considerazione per chi deciderà, non un'azione di questa sessione.

---

# Sessione 5 (Sonnet) — benchmark Qwen3.6-35B-Smart vs Qwen3.8-Flash-Next-Smart — 2026-10-31

**Esito: QWEN3.6 PROMETTENTE** (una sola esecuzione: indicazione esplorativa, non affidabilità statistica). Nessun codice, `models.ini`, prompt, schema o sampling modificati; un'unica inferenza DGX; nessun push/merge.
Evidenze (fuori dal repository): `C:\source\repos\babbubba\replay-qwen36\` — `payload.json`, `response.json` (grezza con `timings`), `content_pretty.json`, `content.txt`, `props.json`, `chat_template.jinja`, `applytpl_out.json`; baseline in `replay-3\` (`payload_think16k.json`, `response_think16k.json`, `content_think16k_pretty.json`).

## 5.1 Verifiche preliminari (zero generazioni)
- Profilo `Qwen3.6-35B-Smart` presente in `/v1/models`, distinto da `Qwen3.6-35B` (n-predict 8192, senza reasoning). GGUF: `Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf`. Build `b10703-0b5be7e4a` (stessa della baseline).
- Argomenti effettivi: `--reasoning on`, `--reasoning-budget 12288` (**uguale** a Qwen3.8), `--reasoning-format deepseek`, `--jinja`, ctx 131072 (n_ctx confermato da `/props` e `/slots`), `--n-predict 32768` (≥16384), `--parallel 1`.
- Template: `enable_thinking is false` → `<think>\n\n</think>\n\n`, altrimenti `<think>\n`. `POST /apply-template` col payload: `true` → prompt (16 162 car.) termina con `<think>\n`; `false` (controllo, non inviato) → con `<think>\n\n</think>\n\n`. `json_schema` accettato (grammatica applicata; confermato dall'esito).
- Prima della chiamata: nessun modello in elaborazione, `/slots` `is_processing=false`. Caricamento a freddo (innescato da `/apply-template`, non da generazione): **20,0 s**, escluso dalle velocità.

## 5.2 Variabili del confronto (il modello non è l'unica differenza)
| | Qwen3.8-Flash-Next-Smart | Qwen3.6-35B-Smart |
|---|---|---|
| Pesi | `Qwen3.8-Flash-Next` UD-IQ4_XS (~177 B parametri totali nei metadati) | `Qwen3.6-35B-A3B` UD-Q4_K_XL (35B-A3B) |
| Cache KV | f16 | q8_0 |
| ubatch | 512 | 1024 |
| `presence-penalty` server | 0.0 | **1.5** (la richiesta non lo invia: si applica il default del profilo) |
| Seed server | 4242 | non fissato (casuale; `temperature 0` rende il campionamento deterministico) |
| Cache di prefisso | 4 301 token riusati | 0 (prefill completo) |
| Tokenizer | non identico (4 305 vs 4 251 token di prompt per lo stesso testo) | |
Il numero di token **non** è una misura equivalente del lavoro svolto.

## 5.3 Richiesta
Payload = quello inviato nella prova riuscita di 4.5 (`payload_think16k.json`), diff automatico: **solo** `model` (`Qwen3.8-Flash-Next-Smart` → `Qwen3.6-35B-Smart`); `enable_thinking=true`, `max_completion_tokens=16384`, `temperature 0`, `response_format`, messaggi e identificativi invariati. Una chiamata, nessun retry, timeout 900 s.

## 5.4 Risultati affiancati
| Metrica | Qwen3.8 | Qwen3.6 | Rapporto 3.6/3.8 |
|---|---|---|---|
| HTTP / `finish_reason` | 200 / `stop` | 200 / `stop` | |
| Thinking (`reasoning_content`) | presente, 31 697 car., chiuso | presente, 19 706 car., chiuso | 0,62 (car.) |
| Completion tokens | 10 625 | 7 907 | **0,744** |
| di cui reasoning / output (stima*) | ~8 400 / ~2 200 | ~5 950 / ~1 950 | ~0,71 / ~0,89 |
| Prompt tokens | 4 305 (4 301 in cache) | 4 251 (0 in cache) | |
| Prefill | 0,09 s (4 token) | 1,99 s (4 251 token, 2 133 tok/s) | non confrontabile (cache) |
| Generazione | 414,1 s | 132,2 s | **0,319 (3,13× più veloce)** |
| Velocità | 25,7 tok/s | 59,8 tok/s | 2,33× |
| Durata a muro | 414,3 s | 134,3 s | 0,324 |
| Caricamento a freddo | modello già caricato (non misurato) | 20,0 s | |
| JSON valido, 15 chiavi, fonti = 5 messaggi PM | sì | sì | |
\* `usage` non separa reasoning e output: output stimato dalla lunghezza del contenuto (Qwen3.8: 4 645 car. ≈ 2 200 token; Qwen3.6: 4 068 car.), reasoning = resto. Stime, non misure.

## 5.5 Valutazione semantica (confronto manuale con la conversazione PM; checker automatico non eseguito)
Requisiti critici su Qwen3.6, tutti **conservati**: (1) portale ordini Colorificio Bianchi (goal e overview); (2) inserimento ordini e (3) monitoraggio spedizione («I clienti inseriscono gli ordini e ne seguono la spedizione»); (4) SMS/email a scelta del cliente; (5) SMS per l'avviso di consegna; (6) Colorweb **obbligatoria** e (7) ordini **in tempo reale** al gestionale («Integrazione con il gestionale ordini Colorweb: obbligatoria, gli ordini devono arrivare in tempo reale al gestionale», Dependencies, fonte seq. 5, con la condizione riportata alla lettera); (8) SSO aziendale; (9) niente password locali; (10) «solo i propri ordini»; (11) settembre; (12) listino «non entra nel portale ed è gestito altrove» (Excluded).
- **Omissioni critiche: 0. Omissioni minori: 0.** Nessuna perdita di qualificatore, soggetto o attribuzione di responsabilità (il listino è «gestito altrove», non assegnato a un sistema inventato).
- **Affermazioni non supportate: 0.** Nessun requisito, conflitto o domanda aperta inventati (`conflicts`, `openQuestions`, `assumptions` vuoti come in Qwen3.8); tutte le voci `Declared` con fonte il messaggio PM corretto; nessuna informazione dell'assistente promossa.
- **Differenze di formulazione (non errori):** Qwen3.6 fonde inserimento e spedizione in una voce e «niente password locali» dentro il requisito SSO, mentre Qwen3.8 le separa e mette le password locali fra le esclusioni; per Colorweb ricopia la frase del PM, Qwen3.8 la riformula. La V8 senza thinking restava l'unica con «tempo reale» perso (2B).

## 5.6 Decisione: **QWEN3.6 PROMETTENTE**
Qualità semantica comparabile a Qwen3.8 con thinking sul caso Boreale (0 omissioni, 0 affermazioni inventate, entrambi i vincoli Colorweb), output completo e generazione ~3,1× più veloce con ~26% di token in meno. Il vantaggio è reale ma confuso con le differenze 5.2 (quantizzazione, `presence-penalty 1.5`, KV q8_0, cache di prefisso).
- **Consolidamento finale:** appare adatto per questo caso, con tempi (134 s a muro) molto più vicini ai limiti pratici. Resta da stabilire ripetibilità e comportamento sugli altri casi (magazzino, ferie) prima di qualsiasi scelta; un solo run a `temperature 0` non è una misura statistica.
- **Chat conversazionale:** **non valutabile** da questo test (nessuna conversazione, solo analisi con ~5 950 token di ragionamento stimati prima della risposta). La velocità (59,8 tok/s) è favorevole; la latenza con thinking attivo in chat resta da misurare.
Nessun altro esperimento eseguito o proposto.

---

# Sessione 6 (Opus) — Qwen3.6-35B-Smart predefinito e thinking per fase — 2026-10-31

**Esito: CONFIGURATO, verificato offline.** Nessuna inferenza DGX, `models.ini` non toccato, prompt V8/`analysis-v3`, `AnalysisContent`, checker, correzione PM, formato del documento e frontend invariati.

## 6.1 Stato trovato
- `Ai:Phases:{Chat,Analysis,Document,Compaction}` esisteva già per `MaxOutputTokens` ed `EnableThinking`; `chat_template_kwargs.enable_thinking` è **sempre** inviato esplicito (`InternalModelClient.CreateRawOptions`), quindi il `reasoning=on` del profilo non prevale mai. Riusato senza modifiche.
- Mancavano: **modello per fase** (un solo `Ai:ModelId`), **timeout compatibili** con un consolidamento di minuti (`RequestTimeoutSeconds` 150 e `RunDeadlineSeconds` 180 per tutte le fasi, contro 134 s misurati su Boreale e ~550 s di caso peggiore a 32768 token), **errore esplicito** per un modello non esposto dal server (la sonda proseguiva e la richiesta finiva in un generico `model_rejected`).

## 6.2 Modifiche minime
| File | Modifica |
|---|---|
| `AI/AiOptions.cs` | `AiPhaseOptions.ModelId` e `RequestTimeoutSeconds` (vuoto = eredita); `ForPhase` li risolve; `MaxRequestTimeoutSeconds`; validazione dei timeout di fase. |
| `AI/InternalModelClient.cs` | `ChatOptions.ModelId` = modello della fase (verificato sul corpo HTTP: l'adattatore M.E.AI lo rispetta); timeout di `HttpClient`/`NetworkTimeout` = tetto della fase più lunga. |
| `AI/AgentFactory.cs` | `PhaseTimeoutChatClient` sotto la telemetria: ogni chiamata ha il timeout della **sua** fase (chat/documento/sintesi restano a 150 s); lo scadere è `TaskCanceledException`+`TimeoutException`, classificato `model_timeout` come prima. |
| `AI/ModelRuntimeMonitor.cs`, `AI/ModelFailure.cs` | La sonda conserva l'elenco di `/v1/models`; la guardia rifiuta prima dell'invio un modello di fase non elencato con `ModelNotAvailableException` → `model_not_available` **permanente**, dettaglio con modello e fase. Un modello elencato ma `unloaded/sleeping` passa: lo carica il server. Nessun fallback. |
| `AI/ExecutionOptions.cs`, `Analysis/AnalysisEndpoints.cs` | `Execution:AnalysisRunDeadlineSeconds` (vuoto = `RunDeadlineSeconds`) usato solo per i run di analisi; validazione timeout di fase < deadline della fase. Config congelata del run: modello, timeout e deadline **di fase**. |
| `AI/InferenceTelemetry.cs`, `Program.cs` | Telemetria con il timeout di fase; all'avvio una riga per fase: modello, `enable_thinking`, `max_completion_tokens`, timeout (nessun contenuto/credenziale). |
| `infra/compose.yaml`, `infra/.env.example` | Default Qwen3.6, finestra 131072, valori per fase sotto; variabili `AI_*_MODEL_ID`, `AI_*_REQUEST_TIMEOUT_SECONDS`, `EXECUTION_ANALYSIS_RUN_DEADLINE_SECONDS`. |
| `tests/.../PhaseModelRoutingTests.cs` | 7 test nuovi (sotto). |

## 6.3 Configurazione effettiva (default di compose) e corpo HTTP inviato
| Fase | `model` | `enable_thinking` | `max_completion_tokens` | `response_format` | Timeout chiamata |
|---|---|---|---|---|---|
| Chat PM (streaming SSE) | Qwen3.6-35B-Smart | false | 4096 | — | 150 s |
| Compattazione (sintesi MAF nativa) | Qwen3.6-35B-Smart | false | 4096 (eredita) | — | 150 s |
| Consolidamento AnalysisContent | Qwen3.6-35B-Smart | **true** | **32768** | `json_schema` | **900 s** (run: deadline **1200 s**) |
| Documento Markdown | Qwen3.6-35B-Smart | false | 4096 (invariato) | — | 150 s |
Finestra `Ai:ContextWindowTokens` 131072 (= ctx-size del profilo con parallel=1): budget di input dell'analisi 131072 − 32768 − 1024 = 97 280. Budget di ragionamento: quello del server (12 288). Chat, documento e turni restano nella deadline comune di 180 s. Il ragionamento non è mai esposto: la chat inoltra solo `update.Text`, l'analisi valida solo `content`.
**Alternativa Qwen3.8 solo per il consolidamento:** `AI_ANALYSIS_MODEL_ID=Qwen3.8-Flash-Next-Smart`, `AI_ANALYSIS_REQUEST_TIMEOUT_SECONDS=1500`, `EXECUTION_ANALYSIS_RUN_DEADLINE_SECONDS=1800` (~26 tok/s: 32768 token ≈ 1 275 s). Con `parallel=1` ogni passaggio chat↔analisi comporterà un caricamento a freddo: per questo il default è Qwen3.6 ovunque.

## 6.4 Test eseguiti (offline, handler HTTP simulato)
- `PhaseModelRoutingTests` 7/7 PASS: default di compose risolti e validi; chat/analisi/documento/chat in ordine alternato con modello, `enable_thinking`, `max_completion_tokens`, `response_format` attesi e nessuna contaminazione (anche con Qwen3.8 sulla sola analisi); sintesi nativa scattata **dentro** l'analisi inviata con `enable_thinking=false` mentre la chiamata finale ha `true`; modello di fase non esposto → `model_not_available`, zero richieste; timeout di fase → `model_timeout` transitorio, cancellazione del chiamante non trasformata; timeout dell'analisi rifiutato senza deadline dedicata.
- Regressione mirata: `AiPhaseOptionsTests`, `ModelRuntimeMonitorTests`, `ResilienceOptionsTests`, `LocalProviderTests`, `InferenceMetricsTests`, `CompactionTests`, `AnalysisContentTests`, `ExecutionFailureTests`, `ContentSecurityTests`: 343 totali, 0 falliti, 239 superati, 104 **Skipped** (richiedono `BPROJECTS_TEST_POSTGRES`/DGX, non impostati: non eseguiti, non superati).

## 6.5 Limiti noti
- Sonda della finestra (`/props`, `/slots`) e conteggio token (`/tokenize`) usano `Ai:ModelId`: un modello di fase diverso deve avere la stessa finestra (Qwen3.8: 131072, ok) e il tokenizer differisce di ~1% (4 305 vs 4 251 token sul payload Boreale), assorbito dal margine di 1024.
- Il controllo `model_not_available` dipende dall'elenco di `/v1/models`: con server offline o elenco non leggibile la richiesta parte e l'eventuale rifiuto del server resta `model_rejected`.
- `infra/.env` locale (non versionato) ha ancora `AI_CONTEXT_WINDOW_TOKENS=32768`: con l'output di analisi 32768 l'avvio fallisce la validazione (budget non positivo, messaggio esplicito). Allinearlo a 131072 insieme a `AI_MODEL_ID`/`AI_ENDPOINT` reali.
- Non verificabile senza DGX: durata reale dei run lunghi, caricamento a freddo, comportamento di `max_completion_tokens 32768` con `json_schema` su progetti più grandi di Boreale.

## 6.6 Smoke test end-to-end (sessione Sonnet)
1. `infra/.env`: `AI_ENDPOINT` reale, `AI_MODEL_ID=Qwen3.6-35B-Smart`, `AI_CONTEXT_WINDOW_TOKENS=131072`; nessun override di fase. Avviare e controllare nei log le 4 righe «Fase AI …» (Analysis: thinking True, 32768, 900 s).
2. Chat: un turno PM; risposta in streaming senza testo di ragionamento; `InferenceCalls` della chat con `ReasoningRequested=false`, modello Qwen3.6.
3. Analisi su Boreale: stato Running fino al completamento (atteso ~2–3 min), revisione AI valida; `InferenceCalls`: `ReasoningRequested=true`, `MaxOutputTokensRequested=32768`, `finish_reason=stop`; `AgentConfig` del run con `requestTimeoutSeconds 900`, `runDeadlineSeconds 1200`.
4. Revisione/conferma PM, poi generazione documento: `ReasoningRequested=false`, bozza Markdown generata.
5. Facoltativo: `AI_ANALYSIS_MODEL_ID=Qwen3.8-Flash-Next-Smart` (con timeout 1500 / deadline 1800) per verificare la selezione; un ID inesistente deve dare `model_not_available` senza chiamate.

---

# Sessione 7 (Sonnet) — smoke test reale Qwen3.6-35B-Smart sul DGX — 2026-10-31

**Esito: PASS** (percorso completo riuscito 1 volta + 2 ripetizioni del consolidamento). Nessuna modifica applicativa: nessun difetto riproducibile trovato. Nessun codice, `models.ini`, prompt V8, `AnalysisContent`, checker o fixture modificati; nessun push/merge, nessun nuovo branch/worktree. `docs/acceptance-trace/` intatta (non tracciata, come prima).
Evidenze (fuori dal repository, senza segreti): `C:\source\repos\babbubba\smoke-qwen36\` — `state.json` (osservazione 1, percorso completo), `state2.json`/`state3.json` (ripetizioni), `drive.py` (driver API), `sem.py` (controllo semantico sulle fixture).

## 7.1 Configurazione effettivamente usata
- DGX raggiungibile, autenticazione Bearer funzionante (401 senza chiave). Profilo `Qwen3.6-35B-Smart` in `/v1/models` (stato `sleeping` all'inizio): GGUF `Qwen3.6-35B-A3B-UD-Q4_K_XL`, `--ctx-size 131072`, `--parallel 1`, `--n-predict 32768`, `--reasoning on`, KV q8_0, ubatch 1024. `models.ini` non toccato.
- Stack isolato, **non** `infra/.env`: progetto compose `bprojects-smoke36`, file env fuori dal repository (segreti generati, endpoint e chiave del DGX di prova), `WEB_PORT=18095`, volume PostgreSQL nuovo, immagini costruite dal commit `11e0633`. `AI_MODEL_ID=Qwen3.6-35B-Smart`, `AI_CONTEXT_WINDOW_TOKENS=131072`, nessun override di fase. Gli ambienti esistenti (`bprojects-close-pg18`, `bprojects-ctxmon`) non sono stati toccati.
- Passi espliciti necessari su DB vuoto (procedura di CLAUDE.md, non un difetto): `app migrate`, poi `app bootstrap-accounts` (account PM di prova). Prima del `migrate` il dispatcher ha loggato 42 `fail` (`relation ... does not exist`), spariti subito dopo.
- Log all'avvio, una riga per fase: Chat/Document/Compaction `Qwen3.6-35B-Smart`, thinking **False**, 4096, 150 s; Analysis `Qwen3.6-35B-Smart`, thinking **True**, 32768, **900 s**. `AgentConfig` congelata nel run: analisi `requestTimeoutSeconds 900`, `runDeadlineSeconds 1200`, `inputBudgetTokens 97280`, `contextWindowTokens 131072`; documento 150/180 s, thinking false. Corrisponde a 6.3.

## 7.2 Esiti e misure (caso Boreale: 5 messaggi PM da `acceptance-cases.json`)
| Fase | Esito | Durata | Token (prompt / completion) | `finish_reason` | Thinking richiesto |
|---|---|---|---|---|---|
| Chat (5 turni × 3 progetti) | Completed, SSE `chat.delta` 53–68 eventi per turno | 2,0–5,1 s per turno (modello 1,0–1,5 s; primo delta 0,8–3,9 s; il 5,1 s è il primo turno a modello `sleeping`) | 47–81 completion | stop | false |
| Consolidamento #1 (percorso completo) | Completed, 1 tentativo | modello 114,1 s (muro 117,9 s) | 4258 / 6791 | stop | true |
| Consolidamento #2 | Completed, 1 tentativo | modello 134,3 s (muro 135,9 s) | 4261 / 8122 (cache 3230) | stop | true |
| Consolidamento #3 | Completed, 1 tentativo | modello 141,2 s (muro 144,9 s) | 4282 / 8483 (cache 3222) | stop | true |
| Documento Markdown (solo #1) | Completed, 1 tentativo | modello 13,8 s (muro 16,1 s) | 3397 / 759 | stop | false |
- Consolidamento, tre osservazioni: media 129,9 s (114,1–141,2), 7 799 completion token (6 791–8 483), circa 60 tok/s (59,5–60,5). Benchmark sessione 5: 132,2 s e 7 907 token, coerente. Tutti i tempi a muro (118–145 s) sono **entro il criterio nominale di 180 s**; analisi #1 più documento = 134 s a muro. Margine 20–35 %; la deadline di sicurezza di 1200 s non è mai stata vicina. Requisiti non modificati.
- Retry: 0. Timeout: 0. Errori applicativi nel log: nessuno dopo il `migrate`. `maxOutputTokens` risulta `confirmed:false` (`serverDefaultNPredict null`); il valore inviato è 32768 per l'analisi e 4096 altrove (`InferenceCalls.MaxOutputTokensRequested`).
- `InferenceCalls` (tutte le righe): `ReasoningRequested` = true **solo** per `Analysis`; chat e documento false; modello sempre `Qwen3.6-35B-Smart`.

## 7.3 Percorso funzionale (osservazione 1)
- Chat: risposte in streaming, persistite (10 messaggi: 5 PM e 5 assistant, con metriche per messaggio), nessun testo di ragionamento.
- Revisione PM: modifica «I clienti vedono esclusivamente i propri ordini», una voce in Requisiti e un'assunzione, quindi v2 `PmEdit` (fonti `PmRevision` assegnate dal server); conferma, quindi `PmConfirmed` da `smokepm`, `canGenerate=true`.
- Documento: `analysisVersion 2`, `analysisRevisionId` uguale alla revisione confermata, etichetta «Bozza AI — da revisionare», 1 667 caratteri, ogni voce con `[fonte:…]`, voci PM con la fonte della revisione PM, modifica e aggiunte presenti.
- Persistenza: riavvio del contenitore `app` e riapertura: bozza identica, analisi v2 `PmConfirmed`, 10 messaggi.
- `reasoning_content` e `<think` assenti da tutte le risposte API salvate e dai log dell'app.
- Nota di contratto (non un difetto): per un run di analisi `analysisId` della `ProcessingRunView` è `null`; l'id dell'analisi è l'id del run (`CurrentAnalysisId`).

## 7.4 Qualità semantica Boreale (confronto manuale con i 5 messaggi PM e con la fixture)
- JSON valido (schema applicato dal server), 5 fonti = 5 messaggi PM, ogni voce `Declared` con la fonte corretta; `conflicts`, `openQuestions`, `assumptions` vuoti; nessuna informazione dell'assistente promossa.
- #1 e #2: 9/9 fatti attesi, fonti uguali alle attese. #2 fonde «niente password locali» nel vincolo SSO e separa inserimento e spedizione; #1 mette le password locali tra le esclusioni.
- #3: 8/9 con il verificatore a regex della fixture. «integrazione» risulta **mancante** solo perché la fixture richiede `colorweb`, `tempo reale` e `obbligatori` in una sola voce, mentre il modello ha scritto «Integrazione obbligatoria con il gestionale ordini Colorweb» (Dependencies, fonte 3) e «Gli ordini devono arrivare in tempo reale al gestionale» (Requirements, fonte 3). Semanticamente nulla è perso (obbligatorietà e tempo reale presenti, stessa fonte); è una differenza di formulazione che il checker rigoroso non accetta (la fixture non ha `parts`). Checker e fixture non modificati.
- In tutte e tre: 0 omissioni critiche, 0 inversioni di negazione (il listino «non entra nel portale» ed è escluso; password locali escluse), 0 vincoli persi (Colorweb obbligatoria e in tempo reale, SSO, «solo i propri ordini», settembre), 0 affermazioni inventate, nessun contenuto del progetto Aurora.

## 7.5 Limiti e non verificato
- La compattazione MAF non è scattata (trigger a 97 280 token, contesto di circa 4 k): la regola «thinking OFF in compattazione» è provata solo dai test offline della sessione 6, non da una chiamata reale.
- Una sola osservazione completa (con revisione PM e documento); le ripetizioni coprono solo il consolidamento. Solo il caso Boreale (magazzino e ferie non provati). Qwen3.8 non provato (rinviato, come richiesto).
- Non è stato osservato un passaggio forzato fra modelli diversi (tutte le fasi usano lo stesso modello).
- Suite di test automatici non rieseguita (nessuna modifica al codice); stato test = sessione 6 (343 totali, 239 superati, 104 skipped).

## 7.6 Stato Git e ambiente
- Branch `experiment/requirements-quality`; unico commit di questa sessione: aggiornamento di `HANDOFF.md` (senza `Co-Authored-By`). Nessun push né merge. `docs/` resta non tracciata.
- Stack di prova `bprojects-smoke36` fermato (`docker compose stop`), volume conservato per ispezione; rimozione: `docker compose -p bprojects-smoke36 --env-file <env di prova> -f infra/compose.yaml down -v`.

## 7.7 Raccomandazione
**Pronto per la validazione finale**, con tre riserve: (1) verificare almeno un secondo caso (magazzino o ferie) e una conversazione abbastanza lunga da far scattare la compattazione; (2) decidere se rendere il verificatore tollerante alla separazione «obbligatoria»/«tempo reale» su due voci (`parts`) o accettarla come variabilità (non modificato qui); (3) i tempi (114–141 s, 6,8–8,5 k token) stanno nei 180 s nominali ma con margine non ampio e ragionamento variabile: la deadline di sicurezza a 1200 s va mantenuta.

---

# Sessione 8 (Sonnet) — accettazione finale T052/T053, verificatore Colorweb, compattazione reale — 2026-10-31

**Esito: T052 NON superato, T053 solo preliminare. Nessun merge.** Modello `Qwen3.6-35B-Smart`, configurazione di produzione (chat/compattazione/documento thinking OFF, analisi thinking ON, 32768 token, timeout 900 s, deadline di run 1200 s, finestra 131072). Evidenze locali (non versionate): `C:\sourceeposabbubba	052-evidence\`.

## 8.1 Verificatore Colorweb (ripetizione 3)
- I JSON originali (`smoke-qwen36/state3.json`) conservano tutte e tre le informazioni: «Integrazione obbligatoria con il gestionale ordini Colorweb» (Dependencies) e «Gli ordini devono arrivare in tempo reale al gestionale» (Requirements), entrambe con fonte = messaggio PM 3 (quello che nomina Colorweb). Non era un'ambiguità: era una formulazione distribuita.
- Correzione minima: `Parts` sul fatto `integrazione` (`AcceptanceFixtures.cs`): `[colorweb, obbligatori]` + `[tempo reale, ordini, gestionale]`; ogni parte deve avere una voce Declared con la fonte giusta. Testo, `Match`, sezioni e divieti invariati. `acceptance-cases.json` rigenerato. Il checker Markdown usa gli stessi `Parts` (stessa logica, nessuna altra modifica).
- 10 test nuovi (`Colorweb_constraints_split_over_linked_items_…`, analisi e bozza): stessa voce PASS; distribuito PASS; facoltativo, differito, batch, tempo reale di un altro sistema (notifiche; listino senza legame con gli ordini), obbligatorietà omessa, fonte errata (due direzioni) tutti FAIL. I tre JSON reali ora soddisfano entrambe le parti. Suite checker 202/202.

## 8.2 T052 nominale reale (N1–N5), prima esecuzione, un solo campione per caso
| Caso | Esito | Causa | Classificazione |
|---|---|---|---|
| N1 | FAIL | generazione: `invalid_agent_output` (motivo non registrato: il messaggio non era propagato; ora lo è) | modello/output non valido, **intermittente**: rieseguito da solo → PASS (analisi 137 s, documento 17 s) |
| N2 | FAIL | «solo email» riscritto come «Le comunicazioni avvengono esclusivamente via email» (perde «notifiche»); la correzione PM prevista non è applicabile (nessuna voce dedicata a «solo responsabile») | errore del modello (allarga l'ambito); checker non modificato |
| N3 | FAIL | lettore di codici a barre messo in Goals, non legato a carichi/scarichi | errore del modello (legame perso), checker non modificato |
| N4 | FAIL | analisi 180,6 s (oltre 180 s); 5 e 10 giorni e SSO/email+password affermati come vincoli/requisiti oltre al conflitto Open | errore del modello + limite di tempo superato di 0,6 s |
| N5 | FAIL | Aurora: «domanda ripetuta» in chat; Boreale: analisi ancora Running a 210 s | modello (Aurora, riprodotto in rerun, con in più un'omissione sulle notifiche); Boreale rerun: PASS |
Durate: chat 1,9–5,9 s; analisi 90–178 s (completion 5,4–10,6 k token, finish `stop`, 1 tentativo, thinking richiesto/effettivo ON solo per Analysis); documento 12,7–19,4 s (thinking OFF, `stop`). Nessun overflow/timeout infrastrutturale. Nessuna attesa modificata.

## 8.3 Compattazione reale (regressione mirata)
Nuovo test `Analysis_with_production_phases_…` (soglia ridotta ~6000 token, finestra 131072, fasi di produzione). Verifica sulla rete: richiesta di sintesi `enable_thinking=false`, consolidamento finale `true`, strategia attivata, budget finale entro il limite, originali invariati, SMS/fatturazione/ID fonte nella sintesi. Esiti: **1 FAIL + 1 PASS**: nel FAIL la sintesi conserva SMS ma l'analisi finale non lo riporta in nessuna sezione (perdita nel consolidamento, intermittente). Baseline T047 con Smart: chat a soglia ridotta PASS, analisi a soglia ridotta PASS (thinking OFF). Nel run MafReal completo (suite in parallelo su un DGX con `parallel=1`) i due test T047 chat-ridotta e analisi-10-messaggi sono falliti: non indagati, quindi non verificato se per contesa del DGX o del modello.

## 8.4 Stato
- T052 resta aperto: 0/5 nominali al primo giro, nessuna valutazione umana (due PM + tecnico) disponibile; il modulo `acceptance/modulo-risultati.md` non è compilato.
- Strumenti aggiunti ai test: `RequireProductionLikeOptions`, dump telemetria per chiamata, `BPROJECTS_ACCEPTANCE_ONLY`, messaggio d'errore del run nei fallimenti, `Thinking` nel recorder; `RequireDgxOptions` ora con finestra 131072 (l'attesa 32768 era superata).
