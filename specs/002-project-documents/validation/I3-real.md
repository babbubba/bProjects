# Validazione I3 reale (T055)

Esecuzione: 2026-11-02, serializzata (una prova DGX per volta, mai la suite completa). **Esito complessivo: T055 NON chiuso** (un FAIL reale di fedelta/validita, vedi sotto). Nessun segreto riportato.

## Configurazione
- Test: `backend/tests/bProjects.IntegrationTests/DocumentExtractionMafRealTests.cs` (categoria MafReal, collection `RealParser`). Pipeline di produzione: parser reale -> worker di parsing -> dispatcher -> `DocumentEvidenceAgent` (MAF) -> aggregatore, su PostgreSQL 18 reale (`bprojects-002-test-pg`).
- Parser: `bprojects-parser:dev` (Docling 2.129.0, linux/amd64 CPU), container locale su 127.0.0.1 con segreto generato per la prova. DGX interno via variabili d'ambiente, nessun fallback. Corpus: fixture sintetiche T002 (manifest `e5662c30...`).
- Opzioni AI: `RequireProductionLikeOptions()` (Extraction: thinking OFF, MaxOutput 16384, timeout 600 s). `Documents:Calibration` assente.
- Il DGX e in router mode: il modello risulta `unloaded` finche non riceve una richiesta; il test invia un warm-up da 16 token prima della sonda. Senza warm-up il preflight da `runtime_unverified` (finestra non verificata). Da considerare in produzione: a modello scaricato la prima unita puo fallire cosi.

## Runtime, tokenizer, usage — PASS
- Finestra misurata W=32768 (la config di test dichiara 131072: il profilo usa il minimo), O=16384, M=1024, B=15360; template esposto e tokenizer verificato: **nessuna calibrazione necessaria**.
- Preflight 2322 token vs usage reale 1679: il preflight senza i 643 token dello schema coincide esattamente (scarto 0). Il preflight e un limite superiore valido che sovrastima del 38% su unita piccole perche conta lo schema (llama.cpp lo traduce in grammatica, non lo mette nel prompt). Effetto: budget conservativo.
- Ipotesi "thinking incluso in max_tokens": **CONFERMATA** (thinking ON, max_tokens=256 -> completion=256, finish=length, 0 caratteri visibili). Con thinking OFF (config di produzione) nessun troncamento osservato.

## Esiti per prova
| Prova | Esito | Evidenze |
| --- | --- | --- |
| pdf-mixed (Incomplete) | PASS | 0 chiamate al modello, nessuna versione Evidence, issue bloccante |
| pdf-text.pdf | PASS | Ready, 1 unita, 4 evidenze; f1 24 mesi (Duration), f2 Prohibited |
| docx-basic.docx | PASS | f1-f3 corretti (Frequency/Deadline), f2 Excluded, f4 cella «Operatore \| Sola lettura» |
| md-basic.md | PASS | f1 Excluded, f2 data della riga di tabella; istruzione iniettata NON eseguita (nessuna Decision dichiarata) |
| txt-negations.txt | PASS | f1 Prohibited+Condition «senza consenso», f2 Excluded, f3 Prohibited, f4 Exception «tranne...», f5 «45 giorni, non 30» con Quantity 45 e 30, mai affermato 30 |
| md-non-bmp.md | PASS | citazioni esatte (offset UTF-16) dopo caratteri non BMP; f2 Negated |
| txt-long.txt (37 KB) | PASS | 13 porzioni, 11 unita, 11 chiamate, 80 evidenze, 538 s; 14/14 fatti gold con valori e polarita corretti; 500 e 2000 in evidenze distinte; F06 conserva l'eccezione (contratto quadro), F14 la condizione (12 mesi) |
| Zero evidenze motivato | PASS | testo di saluti: Ready, 0 evidenze, `NoEvidenceReason` sulla versione e su ogni porzione; distinto da una perdita (nessuna unita Failed) |
| **pdf-table-multipage.pdf** | **FAIL** | vedi sotto |

Per ogni prova PASS: preparazione Ready, tutte le unita/porzioni Completed, 1 solo tentativo, ogni chiamata `finish=stop`, usage registrato, ogni fonte e sottostringa esatta del testo canonico del blocco, ogni evidenza con fonte Primary.

### FAIL: pdf-table-multipage.pdf (tabella su due pagine con header ripetuto)
- 1a esecuzione: Ready ma **fedelta persa**: R-05 estratta come `Requirement/Affirmed` «Il sistema deve supportare il tema scuro» (priorita Bassa) **senza l'esclusione** «esclusa dal rilascio 1» (gold pdf-table-f2). R-03 corretta. Il controllo multi-blocco (riga + header di colonna come fonti su blocchi diversi) non e stato raggiunto perche il fatto e gia fallito.
- 2a e 3a esecuzione (stessa pipeline, stessa fixture): unita `Failed` con `work_output_invalid` (1 violazione di validazione, output non committato), preparazione Failed; nessuna evidenza. L'errore gestito NON e PASS. Il messaggio non dettaglia la violazione (da indagare: logging del validatore).
- Il comportamento e non deterministico sul server; non attribuito ad alcuna causa senza evidenza. Aperto: capire la violazione (probabile citazione su due blocchi/gruppi di tabella) e se il prompt/validatore vadano corretti; non modificati qui.

## Calibrazione OutputExpansion (valori osservati, ratio = completion / token Primary)
| Input | token Primary | completion | ratio |
| --- | --- | --- | --- |
| txt-long (11 chiamate, ~750 token Primary ciascuna) | 8275 | 21908 | **2,65** |
| pdf-text / docx / md / txt-negations / md-non-bmp (1 chiamata, 70-130 token) | 68-130 | 905-1659 | 12,8-20,3 |
- Le unita piccole hanno un overhead JSON fisso (~800-1000 token) che domina il rapporto; sulle unita da ~6000 caratteri il rapporto e 2,65 (< 8). Completion massima osservata 2734 token su un tetto di 16384. L'ipotesi 8.0 non e smentita per unita di dimensione di produzione ma e **superata su testi brevi**: il planner li tratta come batch unico, quindi senza rischio di troncamento osservato. Nessuna modifica al valore; decisione al coordinatore.
- Il test registra il rapporto come calibrazione e verifica il criterio di sicurezza (completion massima <= 50% del tetto): la prima versione del test lo trattava come assert (<= 8) e falliva sui testi brevi; il criterio e stato cambiato in nota + soglia di sicurezza (scelta dichiarata, non per aggirare un errore del modello).

## NOT VERIFIED
- Relazioni fra evidenze di unita diverse (conflitto F01/F13, qualificatore F06/F14 come relazioni): l'estrazione per unita persiste solo relazioni intra-unita (0 osservate su txt-long); riconciliazione = I5.
- Perdita/Incomplete provocata dal modello reale (unita Failed, coverage incompleta) in un caso controllato: provata solo con modello finto (T054); qui osservata solo la Failed involontaria del PDF a tabella.
- Conflitto fra file (txt-conflict-a/b), pdf-mixed come estrazione, tabelle PDF reali di grandi dimensioni, concorrenza con altri carichi DGX, tempi sotto carico (T109).
- Stabilita: ogni prova Ready e stata ripetuta una o due volte (4 formati 2 volte con esito concorde); la non determinatezza del PDF a tabella indica che un singolo PASS non basta.

## Condizione di chiusura T055
Non soddisfatta: fedelta e validita del PDF a tabella multipagina (FAIL), da risolvere o da accettare esplicitamente. T055 resta aperto in `tasks.md`.

## Indagine pdf-table (ripresa, 2026-11-02) — progressi
- Diagnostica aggiunta (permanente, minimale): il messaggio `work_output_invalid` dell'unita ora elenca le regole violate (max 5, citazioni fra «» sostituite da «…») e, per le part, i valori ricevuti, i ruoli e (ruolo/part/indice gruppo) delle fonti dell'evidenza. File: `AI/DocumentEvidenceAgent.cs` (`DescribeRules`), `Evidence/DocumentEvidenceValidation.cs` (`CheckGroups`). Nessun testo del modello/documento nei messaggi.
- CAUSA (8 run su 8, deterministica a temperature 0, stessa violazione): «evidenza e6: le part devono essere contigue da zero (ricevute [1], ruoli [Primary])». Output grezzo ispezionato una volta (corpus di test): e5 = R-05 intera `Affirmed` (riga con «esclusa dal rilascio 1» ma senza polarita di esclusione), e6 = `Excluded` con la sola fonte «(esclusa dal rilascio 1)» che RIUSA la `citationGroupKey` di e5 con part 1. Il validatore raggruppa per evidenza: gruppo con part [1] = violazione. L'intestazione di colonna non e mai citata.
- Classificazione: difetto del PROMPT (ambiguita su citationGroupKey/part: il modello li tratta come globali fra evidenze) piu infedelta semantica del modello (e5 Affirmed pur con l'esclusione nel passo). Il validatore e corretto e non e stato allentato; il gold e corretto. La 1a run Ready dell'esecuzione precedente (esclusione persa) e coerente con la stessa debolezza. Non e il difetto noto "header non replicato su split tabella oversize" (qui 1 sola unita/130 token).
- Fix applicato: 2 regole in `Prompts/document-extraction-v2.md` (part per evidenza da 0; citationGroupKey vale solo dentro una evidenza; limitazione nello stesso passo non separata ne omessa, polarita corretta). Esiti run dopo il fix: da registrare sotto.
- Dopo il fix di prompt (3 run seriali): Ready 3/3, 1 unita, 5 evidenze, finish=stop, R-05 come `Exclusion/Excluded` con la riga intera citata. Il gold falliva ancora per un difetto del TEST: richiedeva «r-03»/«r-05» nel testo dello statement/qualificatori, mentre l'id riga e nella fonte citata. Corretto con `CitesRow` (l'id deve comparire nella fonte Primary citata); restano richiesti i termini semantici, la priorita e la polarita negativa. Nessuna asserzione semantica rimossa.
- Ambiente: dopo il crash la porta 58000 era esclusa da Windows; parser ricreato come `bprojects-t055-parser` su 127.0.0.1:58300 (segreto nuovo, non registrato).
- 2a correzione del test (ripresa): il fatto pdf-table-f2 aveva una clausola AGGIUNTIVA («?? multi-blocco», di fatto un AND) che richiedeva riga+intestazione come fonti su blocchi diversi. Dal prompt reale (dump temporaneo, rimosso) il parser produce un blocco per pagina che contiene gia la propria intestazione («Codice | Descrizione | Priorita» + righe in c1:2 e c1:4, tutti Primary, 0 Context): la citazione multi-blocco non e ne necessaria ne naturale; l'esclusione R-05 e correttamente estratta e citata. La clausola e ora una nota (conteggio delle evidenze multi-blocco osservate), non un assert. Conseguenza dichiarata: «quote multi-blocco gold» resta NOT VERIFIED col modello reale (coperta solo dai test deterministici) => T055 NON spuntato.
- pdf-table dopo fix prompt + gold corretto: 4 run PASS su 5 (Ready, 1 unita, 5 evidenze, R-03 e R-05 Excluded corretti); la 5a e un guasto dell'infrastruttura parser (conversione terminata con rc=-4, job Requeued prima di qualunque chiamata al modello), non del modello. Regressione: intera classe `DocumentExtractionMafRealTests` sul DGX, seriale, con prompt/test corretti: 11/11 superati, 0 ignorati (pdf-mixed, 4 formati, md-non-bmp, txt-long 14/14 gold, zero evidenze, runtime/thinking, pdf-table). Unit/contratto `*DocumentEvidence*` 113 superati, 0 falliti (59 ignorati senza variabili).

## Esito aggiornato (sostituisce «FAIL» sopra per pdf-table)
- pdf-table-multipage: causa = prompt ambiguo su citationGroupKey/part (output rifiutato dal validatore, correttamente) + gold con requisito multi-blocco non naturale col layout del parser. Corretti prompt e test; validatore invariato. 4 PASS su 4 run valide (1 run scartata per guasto parser rc=-4, Requeued, non attribuibile al modello).
- Residuo onesto: relazioni fra evidenze e quote multi-blocco gold con il modello reale restano NOT VERIFIED (0 osservate; il parser non produce header in blocco separato in questa fixture); T055 NON spuntato in tasks.md: la verifica dichiara «relazioni e quote multi-blocco gold». Decisione al coordinatore: accettare come NOT VERIFIED o aggiungere una fixture con header in blocco separato.
- Nota di prod.: a modello DGX scaricato il preflight da `runtime_unverified` (warm-up); il parser a volte termina con rc=-4 (Requeued gestito, riprova automatica).

## Ripresa 3 (2026-11-02): fixture multi-blocco + relazione, R-05 riesaminato
Mandato: T055 non si chiude con relazioni e citazioni multi-blocco NOT VERIFIED.

### Fixture aggiunta (T002 coerente): `tests/fixtures/documents/pdf-split-clause.pdf`
- PDF sintetico a 2 pagine (1298 byte, hash nel manifest, licenza «sintetico»). Art. 4 «... entro il quinto giorno lavorativo del mese successivo,» a fine pagina 1 e continuazione «purche' il cliente non abbia comunicato per iscritto la sospensione del servizio.» a pagina 2; Art. 9 «In deroga all'Art. 4, per il mese di dicembre ... decimo giorno lavorativo.»
- Manifest: file `pdf-split-clause`; fatti `pdf-split-f1` (con `multiBlock.parts`: 2 blocchi Primary) e `pdf-split-f2`; nuova sezione `relations` (`pdf-split-rel`: f2 -> f1, tipi attesi Qualifies/Corrects/Supersedes, basis Declared). Nessun'altra fixture modificata.
- Verifica con parser REALE (Docling nel container `bprojects-t055-parser`, 127.0.0.1:58300, conversione diretta di /convert, poi test end-to-end): status Complete, 4 blocchi paragrafo: b2 (pagina 1, Art. 4 prima meta) e b3 (pagina 2, seconda meta) distinti, b4 (pagina 2, Art. 9); un chunk con tutti i blocchi Primary, range 0..len. Nessun manifest artificiale, nessun bypass del parser.

### Gold e asserzioni VINCOLANTI (test `A_clause_split_by_a_page_break_is_cited_on_two_distinct_Primary_blocks_and_the_explicit_derogation_is_related_to_it`)
1. Presupposto parser: ciascuna parte gold sta in un solo blocco e i due blocchi sono distinti.
2. Fatto f1: statement/qualificatori con rapporto mensile, quinto giorno lavorativo e sospensione del servizio, non negato.
3. Fatto f2: dicembre, decimo giorno lavorativo, rapporto.
4. Citazione multi-blocco: UN'evidenza con fonti Primary sui DUE blocchi gold, ciascuna che copre la sua parte, stesso gruppo, part contigue da 0 e in ordine di lettura.
5. Relazione: da evidenza dell'Art. 9 a quella dell'Art. 4, tipo Qualifies/Corrects/Supersedes, basis Declared (la formula «In deroga all'Art. 4» e letterale).
- Piu i controlli di pipeline comuni (Ready, 1 tentativo, finish=stop, usage, citazioni esatte UTF-16, Primary presente). Nessuna nota al posto di un assert.

### Gold R-05 (pdf-table-f2): ripartizione statement / fonte
- NELLO STATEMENT (struttura/qualificatori inclusi), perche perderlo cambia il significato: la funzione «tema scuro», l'esclusione (polarita negativa o kind Exclusion) e il suo AMBITO «rilascio 1» (senza, «esclusa» diventerebbe «mai prevista/mai da fare»).
- GARANTITO DALLA FONTE Primary citata (testo esatto, verificato dal validatore sul blocco): id riga R-05 e priorita «Bassa», attributi della riga che non cambiano l'esclusione; la fonte e la riga intera.
- Cambiamento rispetto a prima: l'assert non richiede piu «bassa» nello statement (richiesto invece nella fonte Primary: `CitesRow("bassa")`) e richiede in piu l'ambito «rilascio 1» e la polarita negativa (prima bastava il testo «esclus»). Non e un allentamento della fedelta: l'informazione critica (esclusione + ambito) e piu vincolata di prima, quella di attributo e garantita dal blocco citato.

### Esiti reali (DGX interno, parser Docling reale, PostgreSQL 18 reale; seriali; warm-up del router nel test)
- `pdf-split-clause.pdf`: 6 esecuzioni. 5 valide PASS (Ready, 1 unita, 2 evidenze, 1 chiamata, 1 tentativo, finish=stop, ~28-31 s): f1 con Deadline e Condition «purche'... sospensione del servizio», fonti Primary sui DUE blocchi (part 0 e 1, stesso gruppo); f2 dicembre/decimo giorno con «In deroga all'Art. 4» come Condition; relazione persistita `Supersedes/Declared` Art. 9 -> Art. 4 in 5/5. 1 esecuzione scartata: il parser ha risposto 422 (`parse_input_invalid`) prima di qualunque chiamata al modello (0 unita, 0 chiamate), non riprodotta su 5 rerun ne con conversione diretta della stessa fixture; classificata parser/infrastruttura (container, nel suo log compare `rc=-4` in conversioni anomale), non modello ne prompt ne validatore. Il tipo di relazione scelto dal modello (Supersedes) e uno dei tre ammessi dal gold; una deroga puntuale per dicembre e piu propriamente Qualifies: limite di precisione semantica non vincolato dal gold.
- `pdf-table-multipage.pdf` con il gold R-05 riesaminato: 3 esecuzioni PASS (R-05 `Exclusion/Excluded`, «escluso dal rilascio 1» nello statement, riga R-05/Bassa nella fonte Primary; priorita anche come qualificatore Other).
- `DocumentCorpusTests` (manifest/hash/licenza): 5/5 superati con la nuova fixture. Nessuna suite completa eseguita (come da mandato).
- Il processo di test termina a volte con codice 0xC0000005 dopo aver riportato 0 falliti e il report PASS (crash host intermittente noto).
- Nessun cambiamento a prompt, validatore, contratto o altri gold; la diagnostica e il prompt v2 di 34ea56b sono invariati.

## Esito finale T055 (sostituisce «NOT VERIFIED» di relazioni e multi-blocco)
- Relazioni e quote multi-blocco gold: VERIFICATE col modello reale sulla fixture dedicata (asserzioni vincolanti, non note).
- Condizione di chiusura soddisfatta: T055 spuntato in `tasks.md`.
- Limiti che restano dichiarati: relazioni FRA unita diverse (conflitto F01/F13, qualificatore F06/F14 come relazioni) = I5, non provate qui; la citazione multi-blocco e verificata su un caso (frase spezzata da salto pagina), non su tabelle con header in blocco separato (il parser di questo corpus unisce header e righe nello stesso blocco); perdita provocata dal modello reale in caso controllato = solo modello finto (T054); guasto intermittente del parser (rc=-4 / 422 spurio) puo far fallire una preparazione prima del modello; a modello DGX scaricato serve warm-up (`runtime_unverified`).
