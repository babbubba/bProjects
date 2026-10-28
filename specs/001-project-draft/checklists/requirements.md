# Checklist canonica: 001-project-draft

**Purpose**: Qualità/coerenza documentale prima dei task e verifiche tecniche della futura implementazione.
**Created**: 2026-10-27
**Updated**: 2026-10-28
**Feature**: [spec.md](../spec.md)
**Baseline**: decisioni approvate F-01–F-07 e nuovo flusso con anteprima/conferma.

## Semantica degli esiti

- DOC: valutazioni documentali effettive, con evidenze e ambito indicati. Una spunta non certifica codice.
- TEC: verifiche non eseguite, da realizzare durante implementazione con componenti reali; non possono essere spuntate sulla sola documentazione.
- Lacune documentali aperte: nessuna dopo le correzioni e il controllo finale; trasferimento e cleanup DOC22 verificati prima e dopo la rimozione del duplicato.
- La readiness per generare i task riguarda DOC; i TEC sono lavoro di validazione dell'implementazione, non prerequisiti da dichiarare superati prima del codice.
- Nessuna conferma PM dell'analisi equivale ad approvazione cliente o della bozza.

## Controlli documentali valutati

- [x] DOC01 Specifica leggibile, sezioni complete e requisiti orientati a valore/comportamenti, con sole scelte tecnologiche imposte dal prodotto. **Evidenza**: Spec §User Scenarios, §FR-001–025, §Scope.
- [x] DOC02 Chat, Analizza, conferma e Genera documento hanno scopi ed esiti distinti; nessun consolidamento/generazione impliciti. **Evidenza**: Spec FR-005/006/021/022; Plan §Analisi, anteprima e generazione.
- [x] DOC03 MAF è l'unico motore AI; dominio, adapter SQL e dispatcher non duplicano un workflow engine. **Evidenza**: Constitution II–IV; Plan §Constitution Check, §Recovery.
- [x] DOC04 Stati persistenti chat/fasi formali, attesa PM e risultato/errore sono distinti. **Evidenza**: Data §ChatTurn, §ProcessingRun, §AnalysisRevision; HTTP §AnalysisView.
- [x] DOC05 KnowledgeAgent, KB, Docling, RAG, web, review e pubblicazioni restano futuri senza placeholder. **Evidenza**: Spec §Evoluzione prevista; Research §9; AI §9.
- [x] DOC06 FIFO, cutoff, rebase della sessione avanzata e caso U1/U2/A1 sono normativi, senza copie integrali per turno. **Evidenza**: AI §1; Data §ChatTurn; Quickstart §FIFO.
- [x] DOC07 Claim/rinnovo/reclaim, backoff, errori transitori/permanenti, fencing e limiti persistenti sono definiti. **Evidenza**: AI §2; Data §Transizioni e recovery.
- [x] DOC08 Identificativi, stati, input e risultati sono coerenti fra API, data model e piano. **Evidenza**: HTTP §ProcessingRunView/AnalysisView/DraftView; Data entità corrispondenti.
- [x] DOC09 Originali, assistant non validati e modifiche PM attribuite hanno autorità/provenienza distinte. **Evidenza**: Spec FR-010/011/012; AI §4 regole semantiche.
- [x] DOC10 AnalysisContent 1.0.0 definisce tipi, obbligatorietà, ID/fonti, array vuoti, conflitti e validazione prima dell'uso. **Evidenza**: AI §4; Data §AnalysisRevision.
- [x] DOC11 Input mancante, schema/fonti invalidi, guasti modello/compattazione e contesto eccessivo hanno esiti espliciti. **Evidenza**: Spec scenari 6/15/16/18/20; HTTP §Errori; Quickstart §Errori dedicati.
- [x] DOC12 Replay HTTP, crash pre/post commit e risultati unici sono distinti dalla ripetizione possibile delle inferenze. **Evidenza**: Data §ChatTurn/ProcessingRun/Draft; AI §8.
- [x] DOC13 Checkpoint compatibili, processo nuovo, identità/versioni e ripresa senza checkpoint sono descritti senza promesse intra-token. **Evidenza**: AI §8; Research §3/5; Quickstart §Crash.
- [x] DOC14 Solo LLM interni, assenza di cloud fallback, autenticazione, sanitizzazione e redazione log coprono tutti i percorsi. **Evidenza**: Spec FR-001/016; HTTP §Sessione/Progetti/Errori; AI §7/9.
- [x] DOC15 Monolite, PostgreSQL e runner esistenti sono mantenuti; nessuna infrastruttura, ADR o generalizzazione futura inutile. **Evidenza**: Plan §Technical Context/Project Structure; Data introduzione; Constitution I/IX.
- [x] DOC16 Casi nominali richiedono successi reali e hanno soglie distinte da guasti, attesa in coda e tempo umano. **Evidenza**: Spec SC-001/002/007/008; Quickstart §Prove nominali.
- [x] DOC17 Compattazione nativa corrente, soglia configurabile, budget finale e protezione degli snapshot confermati sono coerenti. **Evidenza**: Spec FR-017/SC-010; Plan §Documento adattivo; AI §6/7.
- [x] DOC18 Lunghezza editoriale adattiva e capacità tecnica a token sono separate, senza soglia/obiettivo di parole. **Evidenza**: Spec FR-024/Assumptions; Research §6; Quickstart §Compattazione.
- [x] DOC19 Tutti i requisiti hanno scenari/criteri e attese qualitative predefinite, con completezza/evidenze verificabili. **Evidenza**: Spec FR-001–025, scenari 1–22, SC-001–011; Quickstart §Prove nominali.
- [x] DOC20 Riavvio preserva originali e attesa umana; gli stati pendenti possono avanzare senza confondere immutabilità e completamento. **Evidenza**: Spec scenario 14; Data §AnalysisRevision; AI §3.
- [x] DOC21 API .NET/documentazione/versioni e maturità sono distinte da compile, provider locale e test reali ancora da eseguire. **Evidenza**: Research §1/7/Esito; Plan §Research; sezione TEC sotto.
- [x] DOC22 Controlli precedenti trasferiti/deduplicati, collegamenti e assenza di modifiche estranee verificati; unica checklist canonica. **Evidenza**: 32 controlli readiness e 16 requirements mappati prima della rimozione; riferimenti e inventario finale verificati.
- [x] DOC23 Anteprima modificabile, conferma esplicita corrente e conflitti aperti ammessi hanno concorrenza e provenienza definite. **Evidenza**: Spec FR-012/021/025; HTTP §Modifica; Data §AnalysisRevision.
- [x] DOC24 Generazione separata usa una revisione confermata immutabile; edit successivi richiedono nuova conferma e non retargettano run avviati. **Evidenza**: Spec FR-022/023; HTTP §Generazione; AI §5.

## Verifiche tecniche da eseguire durante l'implementazione

- [ ] TEC01 Compilazione e round-trip con i package MAF 1.26.0 effettivi e le primitive C# scelte. **Riferimento**: Research §1; Quickstart §Avvio.
- [ ] TEC02 Modello DGX reale, endpoint interno, capacità/tokenizer e modalità JsonSchema/ValidatedTextJson con validatori conformi. **Riferimento**: Research §4/7; Quickstart §Schema e provenienza.
- [ ] TEC03 Compattazione realmente attivata a soglia ridotta, conteggio finale entro budget, fonti/negazioni preservate e guasti espliciti. **Riferimento**: Quickstart §Compattazione; Spec SC-010.
- [ ] TEC04 FIFO/cutoff e rebase avanzato nel caso U1/U2/A1 con storico PostgreSQL reale e nessuna fuga dal cutoff. **Riferimento**: Quickstart §FIFO; Spec SC-008.
- [ ] TEC05 Lease, rinnovo, crash/retry, fencing e deduplicazione con concorrenza e contatori reali. **Riferimento**: Quickstart §Crash; Spec SC-009.
- [ ] TEC06 Checkpoint MAF persistenti e resume in processo nuovo per entrambe le fasi, inclusi crash dopo commit. **Riferimento**: Quickstart §Crash; AI §8.
- [ ] TEC07 Anteprima/edit/conferma/generazione, race If-Match, immutabilità dello snapshot e divieto di generare senza validazione. **Riferimento**: Quickstart §Guardie; Spec SC-005/011.
- [ ] TEC08 Casi nominali con analisi e Markdown riusciti, comprensione del tecnico ed errori separati; rubriche ed evidenze reali. **Riferimento**: Quickstart §Prove nominali/Esito; Spec SC-002/003/006/007.
- [ ] TEC09 Auth/sessioni, egress solo interno, integrità originali e redazione/sanitizzazione con stack effettivo. **Riferimento**: Quickstart §Errori dedicati; Spec SC-004/007.

## Copertura F-01–F-07

| Decisione | Recepimento documentale | Verifiche runtime pendenti |
| --- | --- | --- |
| F-01 | DOC06: FIFO/cutoff e rebase dagli originali, senza snapshot completi per turno | TEC04 |
| F-02 | DOC07/12/13: recovery distinto da retry, lease/fence/limiti e checkpoint quando disponibili | TEC05/06 |
| F-03 | DOC09/10: schema 1.0.0 normativo, fonti e validazione prima di preview/uso | TEC02/07 |
| F-04 | DOC02/04/16/20/23/24: anteprima, edit, conferma e generazione separata su snapshot | TEC07/08 |
| F-05 | DOC18/19: documento adattivo e completo, capacità verificata a token | TEC03/08 |
| F-06 | DOC17: compattazione nativa corrente per chat/analisi, snapshot confermato non ridotto | TEC03 |
| F-07 | DOC22: trasferimento, deduplicazione e checklist unica senza PASS runtime | Tutti i TEC restano non eseguiti |

## Trasferimento e deduplicazione dei controlli precedenti

La precedente requirements.md conteneva 16 controlli non numerati: qui si usa il loro
ordine originale per dimostrarne la conservazione dell'intento. Controlli sovrapposti
sono accorpati; non si trasferiscono automaticamente le precedenti spunte.
Le valutazioni DOC sopra si riferiscono agli artefatti consolidati in questa data.

| Controllo precedente requirements.md (ordine) | Controllo canonico |
| --- | --- |
| 1 | DOC01 |
| 2 | DOC01 |
| 3 | DOC01 |
| 4 | DOC01 |
| 5 | DOC22 |
| 6 | DOC19 |
| 7 | DOC16 |
| 8 | DOC18 |
| 9 | DOC11 |
| 10 | DOC11 |
| 11 | DOC05 |
| 12 | DOC21 |
| 13 | DOC19 |
| 14 | DOC02 |
| 15 | DOC24 |
| 16 | DOC01 |

Tutti i 32 controlli utili della precedente readiness.md sono ricondotti ai DOC sotto.
Le vecchie lacune F-01–F-07 sono sostituite dalle decisioni approvate e dalle evidenze
aggiornate, senza cancellare il loro intento di controllo.

| Controllo precedente readiness.md | Controllo canonico |
| --- | --- |
| CHK001 | DOC02 |
| CHK002 | DOC03 |
| CHK003 | DOC04 |
| CHK004 | DOC10 |
| CHK005 | DOC05 |
| CHK006 | DOC06 |
| CHK007 | DOC18 |
| CHK008 | DOC07 |
| CHK009 | DOC16 |
| CHK010 | DOC03 |
| CHK011 | DOC06 |
| CHK012 | DOC07 |
| CHK013 | DOC08 |
| CHK014 | DOC09 |
| CHK015 | DOC17 |
| CHK016 | DOC16 |
| CHK017 | DOC19 |
| CHK018 | DOC16 |
| CHK019 | DOC20 |
| CHK020 | DOC21 |
| CHK021 | DOC11 |
| CHK022 | DOC12 |
| CHK023 | DOC13 |
| CHK024 | DOC11 |
| CHK025 | DOC06 |
| CHK026 | DOC07 |
| CHK027 | DOC07 |
| CHK028 | DOC14 |
| CHK029 | DOC15 |
| CHK030 | DOC21 |
| CHK031 | DOC05 |
| CHK032 | DOC22 |

DOC23/24 coprono in più il nuovo passaggio di anteprima/conferma/snapshot generativo.
La checklist duplicata è stata rimossa dopo conteggio, copertura e verifica dei
riferimenti sopra. Rimane soltanto requirements.md; nessun nuovo registro o checklist ulteriore.

## Notes

Fonti: [spec](../spec.md), [plan](../plan.md), [research](../research.md),
[data model](../data-model.md), [HTTP](../contracts/http-api.md),
[AI](../contracts/ai-workflow.md), [quickstart](../quickstart.md),
[Constitution](../../../.specify/memory/constitution.md).
Il contenuto di questa checklist è l'unica fonte canonica degli esiti; non si
riutilizza il precedente 16/16 o PASS del piano come certificazione trasversale.

Verifica documentale finale eseguita: 24/24 DOC superati e 9 TEC non eseguiti.
check-prerequisites.ps1 -Json -RequireSpec e risoluzione dei template spec/plan riusciti,
senza generare task. Copertura: 25 FR in 22 scenari; schema JSON 1.0.0 parsabile,
16 riferimenti interni risolti; 31 collegamenti locali verificati, incluse le ancore.
Nessun placeholder irrisolto o soglia editoriale a parole. Inventario confrontato con
l'inizio del consolidamento: soltanto gli otto artefatti autorizzati modificati e
readiness.md rimosso dopo il trasferimento; nessun file aggiunto o modifica estranea.
Nessuna build, chiamata DGX, prova di checkpoint o test applicativo è stata eseguita
nel consolidamento. Nessun TEC può essere marcato superato finché non ne esiste l'esito reale.
Nessun tasks.md, codice applicativo, ADR, feature o documento di governance aggiunti.
