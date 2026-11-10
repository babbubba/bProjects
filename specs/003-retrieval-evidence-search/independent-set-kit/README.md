# Kit per il revisore — insieme di valutazione della ricerca nei documenti

Grazie per la collaborazione. Questo kit è autosufficiente: lavora **solo** con i file di questa cartella. Non ti serve, e ti chiediamo di **non cercare**, altro materiale del progetto, dei suoi risultati o dei suoi errori: il valore del tuo lavoro sta proprio nel non conoscerli.

## Cosa ti chiediamo

Un sistema cerca, in un gruppo di documenti di progetto, i passaggi che rispondono a una domanda di un project manager. Per misurare quanto lo faccia bene serve un insieme di prova scritto da una persona indipendente:

1. **Documenti** (da 3 a 8, `.txt` o `.md`): testi realistici di progetto (contratto, allegato tecnico, offerta, verbale, specifica...). Possono essere inventati o ricavati da materiale anonimizzato. **Nessun dato di clienti reali, nessun dato personale.** Lunghezza libera, da una pagina a decine di pagine; i file `.md` devono avere titoli di sezione (`#`, `##`).
2. **Da 50 a 100 domande** come le scriverebbe un project manager, in italiano.
3. Per ogni domanda, i **riscontri**: il/i passaggio/i dei documenti che la risolvono, indicati con il nome del file e una frase o parte di frase letterale, unica nel file.

Scrivi i documenti e le domande **per come ti sembrano naturali**, non per mettere in difficoltà uno strumento che non conosci.

## Come sono fatti i documenti

Un **blocco** è un paragrafo: testo separato dal successivo da una riga vuota (nei `.md`, anche un titolo è un blocco). Un riscontro punta a UN blocco. Scrivi paragrafi che contengano un'informazione compiuta; evita blocchi enormi con molte informazioni diverse.

## Cosa deve contenere l'insieme

Ogni domanda ha una **categoria** (campo `category`):

| Categoria | Cosa è | Minimo |
|---|---|---|
| `explicit-doc` | la domanda nomina il documento («secondo il verbale di collaudo…») | 10 |
| `section-ref` | la domanda nomina una sezione di un documento `.md` | 4 |
| `implicit-unique` | non nomina il documento e la risposta è in un solo posto | 10 |
| `similar-docs` | più documenti trattano lo stesso tema con valori diversi; la domanda ne vuole uno solo, senza citarlo o citandolo | 6 |
| `multi-doc` | la risposta completa è distribuita fra più documenti (tutti i riscontri servono) | 6 |
| `ambiguous` | la domanda è ambigua rispetto ai documenti: più passaggi sono risposte legittime (tutti nei riscontri; spiega in `notes`) | 4 |
| `contradiction` | i documenti si contraddicono sul punto; i riscontri sono tutti i lati del conflitto (spiega in `notes`) | 4 |
| `identical-text` | la stessa frase compare identica in due o più documenti; la domanda indica quale (per documento o sezione) | 3 |

Inoltre: fra il 20% e l'80% delle domande deve nominare il documento (`mentionsDocument: true`). Scrivi anche domande "difficili ma oneste", non solo facili. Non riutilizzare formulazioni identiche.

## Cosa consegni

Una cartella `<nome-insieme>/` (lettere, cifre, `-`, `_`) con:
- i documenti `.txt` / `.md`;
- `cases.json` secondo lo schema qui sotto (parti da `template/cases.json`, che mostra tre esempi);
- dopo la validazione, `frozen-manifest.json` generato dallo strumento.

### Schema di `cases.json`

```json
{
  "schema": "ab-set-1",
  "name": "<nome-insieme>",
  "author": "<il tuo nome>",
  "independenceDeclaration": {
    "notSeenPriorFailures": true,
    "notSeenPriorResults": true,
    "statement": "Dichiaro di non conoscere errori, esiti o misure di precedenti valutazioni di questo sistema."
  },
  "scenarios": {
    "<nome-insieme>": [
      {
        "id": "Q01",
        "category": "explicit-doc",
        "mentionsDocument": true,
        "query": "…",
        "notes": "obbligatorio per ambiguous e contradiction",
        "gold": [ { "id": "Q01-g1", "file": "nome-file.md", "contains": "frase letterale unica nel file" } ]
      }
    ]
  }
}
```

Regole dei riscontri: `file` esiste nella cartella; `contains` ha almeno 12 caratteri, è un testo **letterale** copiato dal documento e compare in **un solo blocco** di quel file (maiuscole indifferenti); l'insieme dei riscontri di una domanda è **completo** (tutti i passaggi che la risolvono, non solo il primo che trovi).

La dichiarazione di indipendenza è vera solo se non hai visto errori, esiti o misure di precedenti valutazioni di questo sistema. Se li hai visti, non firmarla e avvisa chi ti ha consegnato il kit.

## Strumento di controllo (serve Node 18 o successivo)

```bash
node validate.mjs <nome-insieme>             # controlla schema, riscontri unici, quote e dichiarazione
node validate.mjs <nome-insieme> --freeze    # se valido, scrive frozen-manifest.json
node validate.mjs template --allow-small     # prova dello strumento sul template
```

Il controllo segnala per ogni errore la domanda e la ragione. **Congela solo quando hai finito**: dopo il congelamento nessun file dell'insieme può cambiare (un solo carattere modificato invalida il manifest e la misura si ferma). Se devi cambiare qualcosa, ripeti `--freeze` prima della consegna e comunicalo.

## Dopo la consegna

Non ti chiederemo di partecipare all'esecuzione, e non ti comunicheremo i risultati finché non è completata la valutazione. Se per un'eventuale correzione del tuo insieme servisse una nuova versione, sarà una cartella nuova con un nuovo manifest, mai una modifica sul posto.
