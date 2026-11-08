# Validazione accessibilita (T107)

Nessun DGX, nessun modello reale: variabili `BPROJECTS_TEST_DGX_*` non usate dalle prove (lo stack reale non le eredita: il launcher le filtra), `BPROJECTS_TEST_DGX_ALLOW` mai impostata. Nei livelli con modello l'unico modello e il finto (`tests/e2e/support/fake-llm.ts`).

**Questo file separa tre cose che NON si sostituiscono**: (a) controlli automatici, (b) ispezione di codice/DOM fatta dall'agente, (c) revisione umana reale, **non eseguita**. Il fake non e revisione PM e i controlli automatici non attestano la conformita WCAG.

## (a) Controlli automatici

I livelli sono distinti e i numeri non si sommano.

| Livello | File | Scoperti | Superati | Falliti | Saltati |
| --- | --- | --- | --- | --- | --- |
| Vitest (componenti) | `project-documents.component.spec.ts` (5 nuovi), `document-passage.component.spec.ts` (1 nuovo); suite completa | 429 (era 423) | 429 | 0 | 0 |
| E2E fake-stack, browser, API simulate (1440 e 390 px) | `tests/e2e/document-accessibility.spec.ts` | 12 (6 x 2 progetti) | 12 | 0 | 0 |
| E2E stack reale + LLM finto (backend, PostgreSQL isolato e parser REALI; solo desktop-1440) | `tests/e2e/document-accessibility-stack.spec.ts` | 2 | 2 | 0 | 0 |

Comandi (da `tests/e2e`, senza variabili DGX): `npx playwright test document-accessibility` (fake; il pattern non include `-stack`, ignorato dalla configurazione di default) e `npx playwright test -c playwright.real.config.ts document-accessibility-stack` (stack reale). Vitest: `npm --prefix frontend run test:ci`. Supporto condiviso: `tests/e2e/support/a11y.ts`.

Cosa controlla ogni fase del percorso (documenti -> chat con fonti -> analisi con fonti documentali -> conferma -> bozza -> avviso di fonti cambiate; stati di errore e lavorazione; recupero dopo eventi persi):

- **axe-core** (tag `wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa`, incluso contrasto): 0 violazioni in ogni fase, con soglia minima di regole valutate (>10) per evitare "0 violazioni" vuoti. Regole superate per fase: 22-31 a seconda della pagina (log per fase stampato dalla prova: `[T107 ...]`).
- **Scroll orizzontale** della pagina: nessuno (`scrollWidth - clientWidth <= 0`) in ogni fase, a 1440 e 390 px (stack reale solo 1440).
- **Audit del focus** (`focusAudit`) sulle sezioni documenti, versioni, chat, anteprima, bozza: ogni elemento focalizzabile ha area, nome accessibile e indicatore di focus visibile (outline >= 2 px o box-shadow); nessun `id` duplicato nella pagina.
- **Tastiera**: tutti i comandi del percorso (upload, retry, Aggiorna, apri fonte, Analizza, conferma, Genera, marcatori della bozza) azionati con focus reale + Invio.
- **Errori senza solo colore**: ogni stato di preparazione ha un testo distinto (7 stati, Vitest; 3 distinti nella prova browser), ogni `.alert`/`role=alert` visibile ha testo, gli errori d'azione sono `role=alert` e ricevono il focus, gli avvisi d'esito stanno nella live region `docs-live` (`role=status`, `aria-live=polite`).
- **Annunci**: lo stato "in corso" e `role=status` con spinner `aria-hidden`; il messaggio finale della chat sta nel `role=log`, il testo in streaming e fuori dalle live region attive.
- **Recupero dopo eventi persi** (fake): SSE caduto + riconnessione rifiutata -> l'analisi completata nel frattempo compare dai GET, nessun comando emesso, nessun alert residuo; documento terminato mentre il flusso era caduto -> il polling lo mostra e lo **annuncia** senza spostare il focus. Stack reale: offline/online dopo una risposta con fonti -> nessun comando automatico e nessun alert residuo.

### Difetti reali trovati e corretti (minimi, con test)

1. **Esito di preparazione non annunciato** (`project-documents.component`): un documento che passava da "In coda/in lavorazione" a Pronto/Incompleto/Non riuscito durante il polling cambiava solo il badge: chi usa un lettore di schermo non lo sapeva. Ora la live region riceve «Preparazione terminata. «nome»: stato.» senza spostare il focus (`progressNotice`). Test: Vitest (annuncio nella live region, focus invariato) e browser (fake).
2. **`id` duplicato e `aria-labelledby` sul titolo sbagliato** (`document-passage.component`): `passage-title` era fisso, ma i passaggi si aprono insieme in chat, anteprima e documenti. Ora ogni istanza ha un id proprio. Trovato dall'audit degli id (axe non lo segnala: la regola `duplicate-id` non e piu nei tag WCAG 2.1+). Test: Vitest (id distinti, regione -> proprio titolo) e browser.

### Osservazioni NON corrette

- Dopo "Riprova la lettura" la lista viene riletta ma non la selezione: il messaggio "N documenti non ancora pronti" puo restare finche non scatta il polling o si preme Aggiorna. Nel sistema reale il retry e "In coda" e il polling rilegge anche la selezione; nel fake il retry e immediato, quindi la prova preme Aggiorna. Non riprodotto come difetto reale; da tenere d'occhio.
- axe segnala come **incomplete** `color-contrast` su `#chat-text` e sulle `textarea` delle voci (testo parzialmente coperto dalla maniglia di ridimensionamento): non valutabile da axe. Supplito con il contrasto **calcolato** dei computed style (`fieldContrast`): 9 campi (fake) e 8 (reale), minimo 13.8:1 (soglia 4.5:1). E una verifica automatica sul colore del testo, non su cio che l'utente vede.

## (b) Ispezione di codice/DOM (fatta dall'agente, non e revisione umana)

- Focus: `styles.scss` definisce `:focus-visible` globale; `project-documents.component.scss` lo estende agli errori/avvisi con `tabindex=-1`. L'audit automatico conferma l'indicatore su tutti i focalizzabili esaminati.
- Struttura: un solo `main`, `banner`, `h1`; le schede progetto usano `nav` con `aria-current` (non `tablist`); `aria-expanded`/`aria-controls` puntano a id esistenti quando aperti; campi con `label` associata; `progress` con `aria-label`.
- Annunci: live region dei documenti (`docs-live`), dialogo (`role=log`), stato di analisi/generazione (`role=status`), errori (`role=alert`). Un `alert` appena inserito e annunciato dal lettore, ma l'effetto del **focus spostato** sull'errore insieme al ruolo `alert` puo produrre un doppio annuncio: da valutare con un lettore reale.
- Movimento ridotto: `prefers-reduced-motion` in `styles.scss`, verificato dal T051 per lo spinner.
- Contrasto: solo misurato da axe e dal calcolo sopra; i colori dei badge (`text-bg-*`) e degli avvisi superano le regole automatiche, ma non e stata fatta una verifica visiva.

## (c) Da fare a cura di una persona (NON eseguito, quindi il task non e chiuso per questa parte)

- Prova con lettore di schermo reale (NVDA/JAWS/VoiceOver) del percorso completo: ordine di lettura, qualita e **frequenza** degli annunci (live region dei documenti, `role=log` della chat, "Preparazione terminata"), eventuale doppio annuncio focus+`alert`, nome e ruolo dei marcatori di fonte `[F1]` nella bozza.
- Zoom 200-400 % e reflow reale a 320 px (la prova usa 390 px senza scroll orizzontale, che non equivale al criterio 1.4.10), testo ingrandito, modalita alto contrasto/scuro del sistema.
- Verifica visiva del contrasto e dello stato di focus su schermi e condizioni reali; dimensione dei bersagli touch su dispositivo reale.
- Usabilita da parte di un PM (tempi, comprensibilita dei messaggi): il fake non e revisione PM.
- Stack reale: non esercitato a 390 px.
