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

## 2. Scoperta che corregge il report V2

Il report attribuisce l'omissione «richiesta» nella bozza N2 alla divisione richiesta/approvazione in due voci. **Non è così.** L'analisi AI
(proxy 56) conteneva «Il responsabile diretto approva o rifiuta la richiesta», che soddisfa l'attesa intera (il controllo sull'analisi era
infatti passato). È la **correzione PM simulata dall'harness** a cancellare il fatto: `AcceptanceTests.cs` (`target = ... FirstOrDefault`,
riga ~439) e `project-draft-success.spec.ts` (~330) modificano la *prima* voce che combacia con l'attesa da correggere («responsabile
diretto») e la sostituiscono con «Le richieste ... sono approvate esclusivamente dal responsabile diretto del dipendente», perdendo
«rifiuta». La bozza riproduce fedelmente la revisione confermata.

- **Fatto qui:** il verificatore della bozza ora distingue (con l'analisi AI disponibile) un fatto *tolto dalla correzione PM* (nota, non
  errore) da uno *perso dalla generazione* (errore) e da uno *già assente dall'analisi* (errore ereditato).
- **Non fatto (fuori ambito, proposto come seguito):** scegliere nell'harness la voce da correggere evitando quelle che portano anche altri
  fatti attesi. Finché non si fa, ogni run reale di N2 può produrre la nota ««richiesta» presente nell'analisi AI ma non nella revisione
  confermata».

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
| ferie | FAIL, 5 (4 paghe/ratei, 1 richiesta) | PASS; note: «richiesta» tolta dalla correzione PM, responsabilità esterna in Dependencies |
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

1. Harness: scegliere la voce da correggere che combacia con l'attesa da modificare **e con nessun'altra attesa** (`AcceptanceTests.cs`,
   `project-draft-success.spec.ts`), poi ripetere N2 sul DGX.
2. Rieseguire i sei casi reali con questo verificatore (A1 del protocollo) prima di confrontare prompt V8 (B).
3. Estendere le annotazioni dei qualificatori (`solo`, `almeno`, eccezioni) ad altri casi: oggi sono coperti solo dove annotati.
