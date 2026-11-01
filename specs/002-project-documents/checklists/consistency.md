# Coerenza dei requisiti Checklist: 002-project-documents

**Purpose**: Riesame mirato di completezza, precisione e coerenza fra specifica e disegno tecnico prima dei task.
**Created**: 2026-11-01
**Feature**: [spec.md](../spec.md), [plan.md](../plan.md)
**Depth**: approfondita sui rischi richiesti, non audit generale del prodotto.
**Actor/timing**: autore e revisore, dopo plan e prima della decomposizione operativa.
**Review Ownership**: checklist personalizzata di proprietà del revisore. Gli item generati restano `[ ]`; la valutazione assistita richiesta dall'utente è riportata nelle note.
**Marker Semantics**: `[x]` significa approvazione della qualità del requisito da parte del revisore, non implementazione completata né test runtime superato. `$speckit-implement` legge lo stato ma non modifica i marker. `requirements.md` mantiene il proprio ciclo separato e non è stato aggiornato.

## Completezza e misurabilità della copertura

- [ ] CHK001 È definita una catena di copertura distinta per inventario, porzioni, evidenze, analisi e bozza, senza equiparare la visita dei contenuti alla fedeltà semantica? [Completeness, Spec FR-013–FR-015, Plan §Documenti oltre contesto, Contratto AI §2–4/6]
- [ ] CHK002 Sono misurabili i nominali oltre contesto senza consentire riduzione delle fonti, aumento della finestra o un semplice errore come successo? [Measurability, Spec SC-004, Quickstart §Scenari 2–3]
- [ ] CHK003 Sono documentati stati, localizzazione del limite e possibilità PM quando il trattamento completo non riesce, senza pubblicare risultati parziali come completi? [Coverage, Spec FR-006/011/014/016, Contratto parser §Manifest, Contratto HTTP §Selezione]
- [ ] CHK004 È univoco il contratto di provenienza per evidenze o citazioni che attraversano più blocchi, celle o pagine, inclusa la riconciliazione fra range proprietari e contesto replicato? [Clarity, Gap, Contratto AI §1–2, Contratto parser §Manifest/Fedeltà, Data model §Estensione ledger]

## Compatibilità e congelamento delle fonti

- [ ] CHK005 È esplicita la compatibilità di lettura, modifica e generazione di analisi 1.0.0, senza riscrittura dei JSON storici o conversione implicita a 1.1.0? [Consistency, Contratto AI §1, Contratto HTTP §Contratti esistenti, Data model §Migrazioni]
- [ ] CHK006 Sono definiti conservazione e accesso a citazioni, evidenze, documenti e checkpoint storici, distinguendo compatibilità del nuovo software da rollback al vecchio binario? [Coverage, Spec FR-018, Data model §Migrazioni, Plan §Fonti e compatibilità]
- [ ] CHK007 Il contratto delle nuove evidenze specifica completamente forma, campi obbligatori, discriminanti, nullabilità e relazione fra identità della fonte, identificatore del passaggio e alias del run? [Completeness, Gap, Contratto AI §1–2, Contratto HTTP §Evidence, Data model §Estensione ledger]
- [ ] CHK008 Sono coerenti momento di congelamento documentale, cutoff PM, selezione al retry/resume e cambi concorrenti, senza acquisire fonti diventate pronte dopo il comando? [Consistency, Spec FR-008/017–FR-019, Plan §Fonti, Contratto HTTP §Selezione, Data model §Selezioni]
- [ ] CHK009 È definito un solo criterio di versione/preparazione applicabile quando una rielaborazione candidata è completa ma non promossa, comprese revisione concorrente e rappresentazione frontend? [Clarity, Gap, Plan §Lifecycle, Data model §Documenti/ledger, Contratto HTTP §Documenti/Selezione]

## Precisione dei limiti e della ripresa

- [ ] CHK010 Sono distinti e raccordati timeout inferenza, timeout conversione, deadline dei job figli, deadline complessiva della preparazione e obiettivo dei comandi interattivi, con punto iniziale, code/retry inclusi e nessun reset implicito? [Clarity, Gap, Plan §Configurazione, Spec SC-012, Contratto AI §6, Quickstart §Gate]
- [ ] CHK011 È definito il dominio dei casi nominali a cui si applicano i 180 s, separato dalle capacità massime di upload e dalle elaborazioni asincrone lunghe, senza promettere la stessa latenza per qualunque file ammesso? [Measurability, Gap, Spec FR-002/013 e SC-004/012, Plan §Performance Goals/Configurazione/Rischi]
- [ ] CHK012 È documentata una regola riproducibile che renda compatibili topologia congelata e passi di riconciliazione/riduzione dimensionati da output AI non ancora disponibili? [Consistency, Gap, Contratto AI §3/5/6, Data model §WorkManifest/WorkOutput]
- [ ] CHK013 È identificato senza ambiguità il proprietario del commit aggregato Evidence e il suo fence/deadline rispetto ai job delle singole unità e al job Finalize? [Clarity, Gap, Data model §DocumentJob/Estensione ledger, Contratto AI §2, Plan §Lifecycle]

## Confini, sicurezza e integrazioni

- [ ] CHK014 Sono espliciti i punti di acquisizione durevole, riconciliazione filesystem/DB e idempotenza, senza attribuire atomicità inesistente ai due storage? [Completeness, Plan §Acquisizione, Data model §Upload, Contratto HTTP §Documenti]
- [ ] CHK015 Sono documentati isolamento di progetto, limiti dei file, contenuti attivi non fidati, egress e disponibilità degli originali senza introdurre ACL o KB estranee alla slice? [Coverage, Spec FR-021, Contratto parser §Operatività, Contratto HTTP §Sicurezza]
- [ ] CHK016 Sono distinte prove documentali, fake, SDK reale, parser/PostgreSQL reali, DGX ed E2E PM, con gate che non trasformino SKIP o errori gestiti in PASS nominali? [Acceptance criteria, Quickstart §Matrice/Registrazione, Plan §Constitution Check VIII]
- [ ] CHK017 La motivazione sull'ADR del parser distingue l'adozione Docling già approvata dalla decisione sul confine strutturale introdotto, con una valutazione esplicita conforme alla Constitution IX? [Consistency, Gap, Constitution IX, Piano MVP D07/§3, Plan §Constitution Check IX, Research R1/R4]
- [ ] CHK018 È mantenuta la separazione fra consultazione documentale in chat e consolidamento esplicito con Analizza, comprese fonti non pronte e invalidazione del contesto derivato dopo esclusioni? [Consistency, Spec FR-010–FR-012/019, Contratto AI §5, Contratto HTTP §Selezione]

## Notes — valutazione documentale assistita

Nuovo file: 18 item, nessuna duplicazione dei controlli generici di `requirements.md`. Tutte le caselle restano aperte per il revisore secondo lo skill; lo stato delle caselle non rappresenta il conteggio degli esiti seguenti. Nessun test runtime svolto e nessuna modifica agli artefatti valutati.

**10 criteri soddisfatti nella documentazione**: CHK001, CHK002, CHK003, CHK005, CHK006, CHK008, CHK014, CHK015, CHK016, CHK018. Le prescrizioni sono presenti e coerenti sul rispettivo punto. Questo non prova la fattibilità eseguibile, la qualità semantica o la conformità runtime.

**8 criteri con lacune**: CHK004, CHK007, CHK009, CHK010, CHK011, CHK012, CHK013, CHK017. Dettagli e correzioni richieste sotto; nessuna decisione funzionale viene cambiata da questa checklist.

### Lacune bloccanti per una decomposizione tecnica non ambigua

**F1 — CHK010: deadline padre/figli e retry.** Plan §Configurazione distingue parse 300 s, preparazione 3600 s, comandi 180 s e tentativi, ma Data model assegna DeadlineAt ai DocumentJob senza precisare derivazione dalla deadline della Preparation, campo padre autorevole e limite residuo dopo retry. Il contratto parser non raccorda esplicitamente timeout subprocess, richiesta backend e lease. Correggere Plan, Data model, AI §6 e parser: inizio e durata di ogni livello, ereditarietà del residuo, effetto delle code, nuovo retry manuale distinto dal recupero automatico, propagazione del fallimento del padre. Non cambiare automaticamente i valori approvati.

**F2 — CHK012: topologia immutabile e output variabili.** Data model dice «WorkManifest ... persistito ... prima del grafo»; AI §3 richiede sottogruppi bounded delle coppie di pacchetti prodotti dagli agenti; §5 riduce risposte di lunghezza non nota in anticipo. Non è stabilito come determinare e riprodurre tali confini preservando topologia/config al resume. Correggere Plan e contratto AI con la regola e i confini di checkpoint appropriati; non basta ripetere che il manifest è deterministico. È una lacuna di specificazione tecnica, non prova che MAF non possa realizzarla.

**F3 — CHK013: owner del risultato aggregato.** Data model estende EvidenceExtractionJob come aggregatore e introduce DocumentJob Finalize; AI §2 richiede «Finalize sotto fence» senza dire quale owner possa scrivere EvidenceExtractionVersion, promuovere la prima versione e rendere Ready la Preparation. Restano indefiniti relazione univoca fra aggregatore/Finalize, stato dell'aggregatore durante le unità e comportamento se il padre è scaduto mentre Finalize termina. Correggere Data model e AI §2/6 specificando autorità e commit atomico. Impatta idempotenza e recovery, non è solo un dettaglio di nome.

**F4 — CHK007: contratto Evidence documentale incompleto.** AnalysisContent 1.1 ha un delta descritto in §1, ma evidence-1.1.0 in §2 resta «stessi campi semantici ... sources discriminate» senza un payload completo o un riferimento univoco che definisca i nuovi source fields, identità e nullabilità. HTTP aggiunge «payload documentale versionato» senza discriminante/versione precisati per le viste. Correggere AI §2 e HTTP §Evidence con contratto chiuso e condiviso; includere anche regole di lettura dei record evidence-1.0.0, non soltanto AnalysisContent.

### Altre carenze concrete da risolvere

**F5 — CHK004: passaggi multi-blocco.** Parser produce chunk con più range/item, mentre AnalysisSource DocumentPassage identifica un solo blockId/start/end. È possibile usare più fonti, ma il documento non prescrive come rappresentare una citazione di tabella o frase che attraversa blocchi/pagine, né come un header replicato rimandi al suo range originale. Precisare AI §1–2, parser e il dettaglio HTTP con un esempio normativo, senza inventare locator o modificare gli originali.

**F6 — CHK009: Ready non equivale necessariamente a corrente.** È detto che le rielaborazioni successive sono candidate, ma LatestReady risolve «ultima versione completa» e DocumentVersionView offre un preparationId singolo senza definire corrente/candidata e expectedRevision della promozione. Precisare Data model e HTTP: puntatore applicabile e revisione, risoluzione della versione file separata dalla scelta della preparazione/estrazione promossa, campi per la UI e confronto. Il requisito di congelamento è chiaro; manca il raccordo fra i tre livelli di versione.

**F7 — CHK011: perimetro prestazionale nominale.** I documenti non promettono esplicitamente capacità illimitata e segnalano il rischio dei 180 s: questo è corretto. Tuttavia non definiscono ancora un inviluppo nominale riproducibile (volume delle evidenze, pacchetti/chiamate e carico) distinto dai limiti di upload/pagine e dalla preparazione asincrona. La scansione esaustiva O(n²) rende questa distinzione sostanziale. Specificare in Plan/Quickstart i profili delle prove e l'eventuale decisione necessaria su SC-012; non trasferire arbitrariamente l'intera elaborazione lunga sotto 180 s, non reinterpretare i criteri 001. Un'eventuale modifica funzionale della soglia richiede una scelta del committente, non una correzione automatica qui.

**F8 — CHK017: decisione architetturale del parser.** D07 e MVP §3 approvano già Docling/servizio Python separato: non occorre richiedere nuovamente la scelta Docling. Tuttavia la frase «già approvato ... nessun nuovo ADR» in Plan/Research non documenta da sola l'applicazione del principio IX al concreto servizio sincrono stateless, al nuovo confine operativo e alla divisione delle responsabilità. Correggere la valutazione di governance: se questo realizza un nuovo confine strutturale significativo, registrarne l'ADR proporzionato; se si ritiene già coperto da una decisione architetturale esistente, citarla precisamente e delimitare il solo dettaglio ordinario. Il piano MVP è una fonte esplicita da considerare, non va ignorata. Questa è una carenza di motivazione/tracciabilità, non un blocco automatico che richiede una nuova approvazione funzionale. Nessun ADR creato da questa checklist.

### Conclusione della revisione

Non considerare il piano integralmente pronto per task tecnici finché F1–F4 non sono risolti; completare gli altri raccordi prima dei relativi contratti/test. I gate futuri di Docling/MAF/PostgreSQL/DGX e la fattibilità entro le soglie non sono FAIL runtime attuali né risultati acquisiti. Lo stato della precedente checklist generica non viene alterato: il riesame mirato ha rilevato lacune fra artefatti tecnici prodotti dopo quella checklist.
