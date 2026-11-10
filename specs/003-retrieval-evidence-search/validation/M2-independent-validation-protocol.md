# Protocollo — validazione indipendente dell'input del riordino (003 T027)

**Stato: PREPARAZIONE TECNICA CONCLUSA, in attesa del revisore.** Il committente (2026-11-10) conferma che il dataset sarà assegnato a un revisore umano non coinvolto nelle valutazioni precedenti; nominativo e data definitiva da comunicare, **data indicativa 2026-11-17**. Nessun nuovo benchmark DGX eseguito. T027 resta APERTO e `retrieval-policy-2` NON è adottata; gold, baseline e soglie invariati. Questo documento è per il manutentore; il revisore riceve solo i pacchetti isolati descritti sotto.

## Obiettivo

Sapere se documento + sezione nell'input del riordino (B) migliora il ranking su query che il progetto non ha mai visto, scritte da chi non conosce i fallimenti di M2 né i risultati A/B (`T027-ab-report.md`). Le misure già fatte (`T027-dev`, gold storici) sono in-sample e restano descrittive.

## Ruoli e isolamento (cecità del revisore)

| Ruolo | Chi | Cosa vede | Cosa NON vede |
|---|---|---|---|
| Revisore-autore | persona indicata dal committente, non coinvolta in T027 | solo `independent-set-kit/` | `M2-diagnostic*`, `T027-ab-*`, proposta e protocolli, codice del riordino, gold e query storici |
| Revisore semantico (caso neg-45) | persona non coinvolta; può coincidere col revisore-autore **solo dopo** che questi ha consegnato l'insieme indipendente (la scheda non gli si dà prima) | solo la scheda cieca (`neg-45-review-sheet.md`, distribuita come `scheda-revisione-semantica`) | l'annotazione originale, i risultati di qualsiasi misura, il nome del caso |
| Manutentore | assistente + committente | tutto | — |

Regole: il kit si consegna come **copia isolata** della sola cartella (non l'intero repository, né un accesso al branch). Il revisore non riceve risultati prima del termine della valutazione. Il manutentore non modifica documenti, query o gold del revisore: se qualcosa non va, lo segnala al revisore, che produce una nuova versione (nuova cartella, nuovo manifest).

## Acquisizione (strumenti)

1. Il revisore compila l'insieme seguendo `independent-set-kit/README.md`: 3-8 documenti `.txt`/`.md`, **50-100 query**, riscontri (`file` + `contains` letterale e unico nel blocco), categorie obbligatorie: `explicit-doc` (≥10), `section-ref` (≥4), `implicit-unique` (≥10), `similar-docs` (≥6), `multi-doc` (≥6), `ambiguous` (≥4), `contradiction` (≥4), `identical-text` (≥3); fra il 20% e l'80% delle query cita il documento; `notes` obbligatorio per ambiguità e contraddizioni; dichiarazione di indipendenza firmata nel `cases.json`.
2. Il revisore esegue `node validate.mjs <insieme>` finché è valido e poi `--freeze`, che scrive `frozen-manifest.json` (SHA-256 con a capo LF, lo stesso calcolo dell'harness).
3. Consegna della cartella intera.

## Ingresso nel repository (congelamento versionato)

1. Il manutentore copia la cartella in `backend/tests/bProjects.IntegrationTests/Retrieval/Evaluation/<nome-insieme>/` **senza modificare nulla**, e riesegue `node independent-set-kit/validate.mjs <cartella>` (deve restare VALIDO e il manifest invariato).
2. Test deterministici mirati (`--filter-class "*RerankAbDatasetTests"`): verificano per ogni insieme `ab-set-1` presente manifest intatto, dichiarazione di indipendenza, 50-100 query, id unici, ogni riscontro risolto in un solo blocco, query con e senza riferimento al documento.
3. **Commit dedicato** dell'insieme *prima* di qualsiasi misura; l'hash del commit è registrato nel rapporto. Da lì in poi una modifica a un solo file ferma l'harness.

## Misura (solo con nuova autorizzazione DGX)

Stesso harness, stesse condizioni dell'A/B già eseguito (A = `retrieval-policy-1`, B = `retrieval-policy-2`; stessi candidati, modelli e RRF), limitando gli insiemi per evitare di rimisurare gli altri e per tenere i risultati indipendenti separati:

```bash
BPROJECTS_TEST_DGX_ALLOW=1 BPROJECTS_AB_SETS="<nome-insieme>" BPROJECTS_EVAL_OUT="$(pwd)/../specs/003-retrieval-evidence-search/validation/independent-<nome-insieme>" dotnet test --project tests/bProjects.IntegrationTests --filter-trait "Category=RetrievalAbReal"
```

(da `backend/`, con le variabili `BPROJECTS_TEST_POSTGRES` e `BPROJECTS_TEST_DGX_*` nell'ambiente; la cartella di uscita va creata prima). Per il controllo di regressione sui gold storici si usa l'esecuzione A/B già eseguita (`T027-ab-report.md`) o, se serve rimisurarla, una nuova autorizzazione esplicita. Il rapporto riporta autore e dichiarazione di indipendenza dell'insieme.

## Criteri di valutazione (prefissati; nessuna soglia ridotta)

Valutati **solo sull'insieme indipendente**, riportati separati dalle misure di sviluppo e dai gold storici:
- **Hit@1** di B ≥ 95% (soglia T027) e confronto con A; **Recall@5** e **Recall@10** di B ≥ 95% (Recall@10) e differenze rispetto ad A.
- **Regressioni**: elenco nominale delle query corrette in A e mancate in B; esame caso per caso.
- Risultati **per categoria** e **con/senza riferimento al documento**; le categorie `ambiguous` e `contradiction` si leggono con Recall (tutti i lati), non solo Hit@1.
- **Latenza** p50/p95 del solo riordino, A contro B.
- **Non regressione** sui gold storici: non peggiorare oltre 2 pt su Hit@1 e Recall@10 rispetto a M1/M2 (T027).
Il rapporto non contiene un giudizio di gate: la chiusura di T027 e l'adozione di `retrieval-policy-2` spettano al committente.

## Caso neg-45

La scheda `neg-45-review-sheet.md` è **cieca**: non rivela l'annotazione originale (né che esista), non mostra risultati, non usa il nome del caso né i nomi dei file del corpus, e chiede al revisore di **scegliere autonomamente** i blocchi riscontro sull'intero corpus dello scenario (S1–S15, compreso il messaggio del PM). Gold, query e baseline originali restano intatti.

Procedura (manutentore), in questo ordine:
1. Consegnare la scheda **solo dopo** la consegna dell'insieme indipendente se il revisore è lo stesso; riceverne la risposta compilata e **archiviarla senza modifiche** (commit della risposta prima di ogni confronto).
2. Solo dopo, confrontare la selezione del revisore con il gold originale (blocco S6 documentale; S15 è un riscontro Message, fuori dalle metriche a livello di blocco) e riportare le differenze in una nota separata, senza giudizio di gate.
3. Se le differenze motivano una correzione, creare una **nuova versione del dataset** (nuovi file e manifest, es. `N4-fixtures-v2`) con le metriche originali ancora confrontabili; le misure interessate si rieseguono solo con nuova autorizzazione DGX.

## Distribuzione isolata (cosa si consegna)

Due pacchetti distinti, ciascuno una lista chiusa di file, controllata contro termini che rivelerebbero diagnosi, risultati, nomi di casi o dei corpus (lo script si ferma se ne trova, o se il kit contiene file non previsti). Nulla del repository, dei rapporti o dei dataset di sviluppo è incluso.

```bash
node specs/003-retrieval-evidence-search/validation/make-reviewer-distribution.mjs --package kit   --out <cartella-di-uscita>
node specs/003-retrieval-evidence-search/validation/make-reviewer-distribution.mjs --package sheet --out <cartella-di-uscita>
```

- `independent-set-kit.zip` (README, `validate.mjs`, template inventato): al revisore-autore.
- `scheda-revisione-semantica.zip` (la sola scheda cieca): al revisore semantico, secondo la regola sopra.
- Lo script scrive anche `<pacchetto>.zip.sha256`; lo SHA-256 consegnato si registra per riconoscere il pacchetto restituito. Lo zip si consegna fuori dal repository (non committarlo).

## Decisioni ancora aperte

1. Nome del revisore-autore e data di consegna definitiva (indicativa: 2026-11-17), a cura del committente.
2. Nome del revisore semantico (può essere lo stesso, solo dopo la consegna dell'insieme).
3. Nuova autorizzazione DGX per la misura sull'insieme indipendente (dopo il congelamento e il commit dell'insieme).
4. Adozione di `retrieval-policy-2`: solo dopo la valutazione indipendente.
