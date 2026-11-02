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
