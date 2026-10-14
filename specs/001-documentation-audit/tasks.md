# Tasks: backlog derivato per audit

**Natura:** proiezione documentale per audit, autorizzata il 2026-10-14. Non e un output di speckit-specify/plan/tasks e non autorizza implementazione o passaggio di gate.

**Autorita:** i due documenti sorgente prevalgono. Estratti e copie conservano testo, vincoli e stati originali. Alias e relazioni di audit non sono nuove decisioni. A53/A54 rimangono proposte tecniche.

**Tracciabilita:** [research.md](research.md) contiene hash, provenienze, matrici nei due sensi, lacune e validazione. I link relativi dentro le copie integrali sono testo originale: vanno risolti nella directory dei sorgenti. Per navigare usare i link di provenienza.

**Input:** [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md). **Status:** non eseguito durante questa preparazione. Le caselle non dichiarano uno stato implementativo verificato.

## Regole di proiezione

T001-T059 sono alias biunivoci degli ID originali Mx-yy. Descrizioni, dipendenze, ore e AC sono trascritti esattamente. Le relazioni decisione/FR-task sono inferenze per audit, non dipendenze aggiuntive o copertura certificata. Nessun marcatore [P], priorita o percorso applicativo aggiunto. US aliasa azioni, senza assegnazioni task-story approvate. DoD e must pass sono integralmente nella copia originale sottostante e si applicano insieme agli AC.

## M0 — Proof tecnologico, contratti e ADR (38 h; range 30–45)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L22](../../AI-Project-Documentation-Tasks.md#L22); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T001 (M0-01) Rilevare disponibilità runtime DGX e contratti API di chat/embedding/rerank/discovery

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L26](../../AI-Project-Documentation-Tasks.md#L26); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** —

  **Stima originale (h):** 5

  **AC originali:** Report endpoint, alias, auth, JSON capability, limiti e fallback espliciti; zero cloud

  **Relazioni inferite di audit:** D05, D10, D25, D39, D55; alias requisiti: FR-005, FR-010, FR-025, FR-039, FR-055.

- [ ] T002 (M0-02) PoC Docling + HybridChunker su PDF testuale/DOCX/MD/TXT su host target

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L27](../../AI-Project-Documentation-Tasks.md#L27); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** —

  **Stima originale (h):** 8

  **AC originali:** Container riproducibile, estratti strutturati e locator; scansioni rifiutate chiaramente

  **Relazioni inferite di audit:** D04, D07, D08, D28, D55; alias requisiti: FR-004, FR-007, FR-008, FR-028, FR-055.

- [ ] T003 (M0-03) Valutare Tiptap JSON, Markdown/PDF/DOCX converter e policy licenze

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L28](../../AI-Project-Documentation-Tasks.md#L28); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** —

  **Stima originale (h):** 5

  **AC originali:** ADR, demo JSON->3 formati con heading/tabelle/lista e limiti documentati

  **Relazioni inferite di audit:** D20, D21, D22, D51, D55; alias requisiti: FR-020, FR-021, FR-022, FR-051, FR-055.

- [ ] T004 (M0-04) Definire bounded RAG, metadata/sourceRefs e schema output AI per fasi

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L29](../../AI-Project-Documentation-Tasks.md#L29); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-01,M0-02

  **Stima originale (h):** 7

  **AC originali:** Contratti JSON validabili, query scope e budget retrieval documentati

  **Relazioni inferite di audit:** D28, D32, D36, D55; alias requisiti: FR-028, FR-032, FR-036, FR-055.

- [ ] T005 (M0-05) Definire security/deployment/backup/telemetry ADR e threat model minimo, includendo ADR MAF, A53 e A54

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L30](../../AI-Project-Documentation-Tasks.md#L30); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** —

  **Stima originale (h):** 5

  **AC originali:** ADR autorizzazioni, JWT/refresh, web SSRF, backup executor, egress, perimetro MAF e confine sessione/memoria; niente spike compattazione

  **Relazioni inferite di audit:** D03, D09, D39, D40, D42, D48, D55, A49, A53, A54; alias requisiti: FR-003, FR-009, FR-039, FR-040, FR-042, FR-048, FR-055, FR-058.

- [ ] T006 (M0-06) Costruire 5 casi gold anonimizzati e quality rubric, baseline manuale

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L31](../../AI-Project-Documentation-Tasks.md#L31); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-01

  **Stima originale (h):** 5

  **AC originali:** Dataset revisionabile con evidenze attese e criteri valutazione

  **Relazioni inferite di audit:** D35, D43; alias requisiti: FR-035, FR-043.

- [ ] T007 (M0-07) Gate M0: consolidare ADR, open risks, revised estimates

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L32](../../AI-Project-Documentation-Tasks.md#L32); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-01..M0-06

  **Stima originale (h):** 3

  **AC originali:** Documento di decisioni tecniche, backlog aggiornato, PoC riproducibile

  **Relazioni inferite di audit:** D55, D56; alias requisiti: FR-055, FR-056.

### Gate M0 — alias SC-001

**Provenienza:** [AI-Project-Documentation-Tasks.md:L34](../../AI-Project-Documentation-Tasks.md#L34); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M0:** Docling tratta 4 formati; almeno una analisi campione su modello locale produce output JSON validato; RAG minimale prova citazioni; stack scelto compatibile e senza servizi AI cloud. Se uno di questi fallisce, stop e ADR correttivo.

## M1 — Foundation, autenticazione, deployment iniziale (78 h; 60–95)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L36](../../AI-Project-Documentation-Tasks.md#L36); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T008 (M1-01) Scaffold monorepo .NET, Angular, Python, test e docs

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L40](../../AI-Project-Documentation-Tasks.md#L40); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0

  **Stima originale (h):** 8

  **AC originali:** Solution workspace standard, lint/build automatizzati

  **Relazioni inferite di audit:** D51, D52; alias requisiti: FR-051, FR-052.

- [ ] T009 (M1-02) Compose dev/test/prod, rete, volumi, config e secrets

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L41](../../AI-Project-Documentation-Tasks.md#L41); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-01

  **Stima originale (h):** 9

  **AC originali:** Ambienti distinti; niente secrets nel repository; DGX LAN raggiungibile

  **Relazioni inferite di audit:** D10, D11, D44, D53; alias requisiti: FR-010, FR-011, FR-044, FR-053.

- [ ] T010 (M1-03) PostgreSQL/pgvector schema iniziale, migrazioni e repository

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L42](../../AI-Project-Documentation-Tasks.md#L42); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-01

  **Stima originale (h):** 10

  **AC originali:** Migration reversibile o strategia forward-compatible; test DB

  **Relazioni inferite di audit:** D17, D21; alias requisiti: FR-017, FR-021.

- [ ] T011 (M1-04) ASP.NET Core Identity, JWT e refresh rotation; Admin/PM

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L43](../../AI-Project-Documentation-Tasks.md#L43); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03

  **Stima originale (h):** 13

  **AC originali:** Token breve, revoca, lockout, CSRF se cookie, test authz

  **Relazioni inferite di audit:** D03, D12, D48; alias requisiti: FR-003, FR-012, FR-048.

- [ ] T012 (M1-05) Cataloghi Cliente/Prodotto/Progetto e UX Bootstrap

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L44](../../AI-Project-Documentation-Tasks.md#L44); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03,M1-04

  **Stima originale (h):** 12

  **AC originali:** CRUD, versione prodotto facoltativa, tutti PM vedono tutti progetti

  **Relazioni inferite di audit:** D03, D12, D30, D51; alias requisiti: FR-003, FR-012, FR-030, FR-051.

- [ ] T013 (M1-06) Storage originali, hash, upload limitato e sanitizzazione

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L45](../../AI-Project-Documentation-Tasks.md#L45); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03,M1-04

  **Stima originale (h):** 8

  **AC originali:** 50MB default config, MIME/signature, path traversal/zip bomb prevention

  **Relazioni inferite di audit:** D01, D04, D49; alias requisiti: FR-001, FR-004, FR-049.

- [ ] T014 (M1-07) Infrastructure coda PostgreSQL, lease, heartbeat, retry e API stato

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L46](../../AI-Project-Documentation-Tasks.md#L46); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03

  **Stima originale (h):** 7

  **AC originali:** crash/recovery test, nessun job duplicato

  **Relazioni inferite di audit:** D17, D26, D44, D45; alias requisiti: FR-017, FR-026, FR-044, FR-045.

- [ ] T015 (M1-08) CI GitHub Actions per build/test/image e primo environment gate

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L47](../../AI-Project-Documentation-Tasks.md#L47); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-01,M1-02

  **Stima originale (h):** 3

  **AC originali:** PR workflow passa, produzione richiede approvazione

  **Relazioni inferite di audit:** D41, D43, D52, D53; alias requisiti: FR-041, FR-043, FR-052, FR-053.

- [ ] T016 (M1-09) Storico conversazionale PostgreSQL e adapter MAF per sessioni persistenti

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L48](../../AI-Project-Documentation-Tasks.md#L48); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03,M1-04

  **Stima originale (h):** 8

  **AC originali:** messaggi originali immutabili e ordinati con riferimenti allegati; ripresa dopo restart; snapshot derivati separati, adapter testato con fake provider

  **Relazioni inferite di audit:** D26, A49, A54; alias requisiti: FR-026, FR-058.

### Gate M1 — alias SC-002

**Provenienza:** [AI-Project-Documentation-Tasks.md:L50](../../AI-Project-Documentation-Tasks.md#L50); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M1:** login, CRUD progetto e upload persistente end-to-end in ambiente test; accesso Admin/PM verificato; un job può sopravvivere al riavvio del worker.

## M2 — Knowledge, Docling, RAG avanzato (106 h; 80–135)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L52](../../AI-Project-Documentation-Tasks.md#L52); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T017 (M2-01) Entità/versioni KB, scope e approvazione Admin

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L56](../../AI-Project-Documentation-Tasks.md#L56); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1

  **Stima originale (h):** 15

  **AC originali:** PM può pubblicare nella KB progetto; prodotto/cliente attende Admin

  **Relazioni inferite di audit:** D01, D02, D13, D14, D38; alias requisiti: FR-001, FR-002, FR-013, FR-014, FR-038.

- [ ] T018 (M2-02) Worker Docling HTTP/queue parser/hybrid, locator e security sandbox

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L57](../../AI-Project-Documentation-Tasks.md#L57); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-02,M1-07

  **Stima originale (h):** 17

  **AC originali:** PDF/DOCX/MD/TXT, output typed; timeout + isolate temp files

  **Relazioni inferite di audit:** D04, D07, D08, D17, D49; alias requisiti: FR-004, FR-007, FR-008, FR-017, FR-049.

- [ ] T019 (M2-03) Adapter embedding e build chunk/parent relation

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L58](../../AI-Project-Documentation-Tasks.md#L58); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-01,M2-02

  **Stima originale (h):** 13

  **AC originali:** vettori dimensionati, source refs e parent chains persistenti

  **Relazioni inferite di audit:** D07, D28, D36, D37; alias requisiti: FR-007, FR-028, FR-036, FR-037.

- [ ] T020 (M2-04) Reindex cambio modello e cutover senza indice misto

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L59](../../AI-Project-Documentation-Tasks.md#L59); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-03

  **Stima originale (h):** 10

  **AC originali:** indice precedente usabile fino a cutover; rollback dopo errore

  **Relazioni inferite di audit:** D13, D37; alias requisiti: FR-013, FR-037.

- [ ] T021 (M2-05) Hybrid vector + FTS + RRF con filtri scope/esclusioni

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L60](../../AI-Project-Documentation-Tasks.md#L60); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-01,M2-03

  **Stima originale (h):** 16

  **AC originali:** query dimostrabili: codici esatti e descrizioni naturali; esclusioni rispettate

  **Relazioni inferite di audit:** D02, D30, D31, D32, D36; alias requisiti: FR-002, FR-030, FR-031, FR-032, FR-036.

- [ ] T022 (M2-06) Rerank, parent expansion, query rewriting, bounded iterative search

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L61](../../AI-Project-Documentation-Tasks.md#L61); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-05

  **Stima originale (h):** 18

  **AC originali:** limiti max iteration/time/tokens, evidence valid, benchmark retrieval

  **Relazioni inferite di audit:** D28, D35, D36; alias requisiti: FR-028, FR-035, FR-036.

- [ ] T023 (M2-07) UX KB: upload, versioni, approval queue, job e search-preview

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L62](../../AI-Project-Documentation-Tasks.md#L62); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-01..M2-06

  **Stima originale (h):** 8

  **AC originali:** workflow approvazione visibile, precedenti versioni tracciabili

  **Relazioni inferite di audit:** D01, D13, D14; alias requisiti: FR-001, FR-013, FR-014.

- [ ] T024 (M2-08) Integration & security testing knowledge path

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L63](../../AI-Project-Documentation-Tasks.md#L63); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-07

  **Stima originale (h):** 3

  **AC originali:** una fonte esclusa o pending non entra nel contesto, audit OK

  **Relazioni inferite di audit:** D02, D14, D32, D43; alias requisiti: FR-002, FR-014, FR-032, FR-043.

- [ ] T025 (M2-09) Retrieval selettivo per chat: trigger, filtri, evidenze e budget

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L64](../../AI-Project-Documentation-Tasks.md#L64); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2-06,M1-09

  **Stima originale (h):** 6

  **AC originali:** una domanda su prodotto attiva ricerca pertinente; messaggi ordinari non attivano RAG; scope e decisioni web rispettati; metriche trigger registrate

  **Relazioni inferite di audit:** D02, D32, D36, A53; alias requisiti: FR-002, FR-032, FR-036.

### Gate M2 — alias SC-003

**Provenienza:** [AI-Project-Documentation-Tasks.md:L66](../../AI-Project-Documentation-Tasks.md#L66); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M2:** il retrieval restituisce fonti/locator corretti da KB di prodotto e cliente senza attraversare esclusioni; test di cambio modello non restituisce vettori incompatibili. Baseline RAG vs avanzato misurata.

## M3 — Flusso PM, domande e ricerca web (121 h; 95–150)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L68](../../AI-Project-Documentation-Tasks.md#L68); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T026 (M3-01) Model provider registry e stage runner per llama.cpp con output schema

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L72](../../AI-Project-Documentation-Tasks.md#L72); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-01,M1-07

  **Stima originale (h):** 15

  **AC originali:** modello per fase, validazione JSON, no fallback cloud

  **Relazioni inferite di audit:** D05, D25, D26, D34, D39; alias requisiti: FR-005, FR-025, FR-026, FR-034, FR-039.

- [ ] T027 (M3-02) Run/on-demand e input normalization con versioni e checkpoint

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L73](../../AI-Project-Documentation-Tasks.md#L73); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2,M3-01,M1-09

  **Stima originale (h):** 14

  **AC originali:** aggiunta allegato non lancia analisi; restart riprende checkpoint

  **Relazioni inferite di audit:** D18, D26, D45, A50; alias requisiti: FR-018, FR-026, FR-045, FR-059.

- [ ] T028 (M3-03) Fact/requirement/assumption/conflict normalization + citazioni

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L74](../../AI-Project-Documentation-Tasks.md#L74); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-02

  **Stima originale (h):** 12

  **AC originali:** deduplica, fonti verificate, status senza invenzioni

  **Relazioni inferite di audit:** D28, D31, A51, A52; alias requisiti: FR-028, FR-031, FR-060, FR-061.

- [ ] T029 (M3-04) Domande guidate max 5 per round e risposta/status history

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L75](../../AI-Project-Documentation-Tasks.md#L75); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-03,M3-09,M3-11,M3-12

  **Stima originale (h):** 13

  **AC originali:** 2 round richiesti su comando, no domande già risolte, rinvio supportato

  **Relazioni inferite di audit:** D19; alias requisiti: FR-019.

- [ ] T030 (M3-05) `IWebSearchProvider` + SearXNG, 3 web modes, sanitize e SSRF policy

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L76](../../AI-Project-Documentation-Tasks.md#L76); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1,M3-01

  **Stima originale (h):** 15

  **AC originali:** AUTO/APPROVAL/OFF, solo query pubbliche, malicious redirect rejected

  **Relazioni inferite di audit:** D09, D15, D16; alias requisiti: FR-009, FR-015, FR-016.

- [ ] T031 (M3-06) Import web -> proposta KB con diritti e Admin approval

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L77](../../AI-Project-Documentation-Tasks.md#L77); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-05,M2-01

  **Stima originale (h):** 7

  **AC originali:** URL/hash/licenza/provenienza versionati; non auto-pubblicare

  **Relazioni inferite di audit:** D09, D38; alias requisiti: FR-009, FR-038.

- [ ] T032 (M3-07) UI chat-first di progetto, analisi/chiarimenti e progresso job

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L78](../../AI-Project-Documentation-Tasks.md#L78); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-02..M3-06,M3-09..M3-12

  **Stima originale (h):** 7

  **AC originali:** PM completa input->analisi->risposte, aggiornamento solo volontario

  **Relazioni inferite di audit:** D18, D19, D26, A50; alias requisiti: FR-018, FR-019, FR-026, FR-059.

- [ ] T033 (M3-08) E2E del vertical slice + cleanup fallimenti

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L79](../../AI-Project-Documentation-Tasks.md#L79); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-07

  **Stima originale (h):** 2

  **AC originali:** almeno 2 casi funzionali end-to-end senza editor finale, inclusa conversazione frammentata

  **Relazioni inferite di audit:** D43, D54; alias requisiti: FR-043, FR-054.

- [ ] T034 (M3-09) Context assembly con MAF: standard compaction, budget per modello, facts confermati e integrazione job

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L80](../../AI-Project-Documentation-Tasks.md#L80); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-09,M3-01,M3-03

  **Stima originale (h):** 12

  **AC originali:** conversa su più giornate; compattazione attivata oltre soglia senza perdere storico; fatti confermati preservati; niente trigger automatico di analisi; job riprende e output validato

  **Relazioni inferite di audit:** D18, D26, D45, A49, A54; alias requisiti: FR-018, FR-026, FR-045, FR-058.

- [ ] T035 (M3-10) Turni di chat conversazionale con chiarimenti mirati, retrieval selettivo e job AI

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L81](../../AI-Project-Documentation-Tasks.md#L81); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-09,M2-09

  **Stima originale (h):** 8

  **AC originali:** PM scrive in turni successivi; risposta AI pertinente e non ripetitiva; nessun update formale dei requisiti fino ad Analizza; job persistenti

  **Relazioni inferite di audit:** D18, D26, A49, A50, A53; alias requisiti: FR-018, FR-026, FR-058, FR-059.

- [ ] T036 (M3-11) Estrarre candidati e confermare selettivamente requisiti critici/ambigui

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L82](../../AI-Project-Documentation-Tasks.md#L82); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-03,M3-10

  **Stima originale (h):** 8

  **AC originali:** provenienza e stato conservati; critici richiedono conferma, non critici possono restare non validati; non equiparare conferma PM ad approvazione cliente

  **Relazioni inferite di audit:** A51; alias requisiti: FR-060.

- [ ] T037 (M3-12) Rilevazione, presentazione e risoluzione manuale dei conflitti

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L83](../../AI-Project-Documentation-Tasks.md#L83); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-11

  **Stima originale (h):** 8

  **AC originali:** fonti e date alternative mostrate; PM sceglie, supera o rinvia; conflitto differito persiste; nessuna prevalenza automatica per data

  **Relazioni inferite di audit:** D31, A52; alias requisiti: FR-031, FR-061.

### Gate M3 — alias SC-004

**Provenienza:** [AI-Project-Documentation-Tasks.md:L85](../../AI-Project-Documentation-Tasks.md#L85); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M3:** PM inserisce note frammentarie in chat, riceve chiarimenti mirati senza attivare analisi formale, con storico persistente e compattazione standard; requisiti critici passano per conferma PM; contraddizioni sono visibili e risolvibili o differibili; analisi parte solo su richiesta. PM lancia analisi reale e risponde; web pubblica controllata; progetto con ERP non conosciuto produce chiarimenti, non inventa integrazione API. Disponibile dimostrazione in ambiente test.

## M4 — Documento visuale, revisione, export (134 h; 105–165)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L87](../../AI-Project-Documentation-Tasks.md#L87); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T038 (M4-01) Canonical schema JSON document, migrations, section IDs, revision store

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L91](../../AI-Project-Documentation-Tasks.md#L91); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M0-03,M3

  **Stima originale (h):** 18

  **AC originali:** schemaVersion, revisioni immutabili, migration tests

  **Relazioni inferite di audit:** D21, D50; alias requisiti: FR-021, FR-050.

- [ ] T039 (M4-02) Quattro template configurabili e generator->JSON converter

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L92](../../AI-Project-Documentation-Tasks.md#L92); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-01

  **Stima originale (h):** 12

  **AC originali:** sezioni rilevanti, piano solo macroattività/dipendenze

  **Relazioni inferite di audit:** D06, D21, D23, D24; alias requisiti: FR-006, FR-021, FR-023, FR-024.

- [ ] T040 (M4-03) Tiptap WYSIWYG su Bootstrap con toolbar e persistenza optimistic

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L93](../../AI-Project-Documentation-Tasks.md#L93); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-01

  **Stima originale (h):** 23

  **AC originali:** heading/list/tables/link/code, 409 conflict e no data loss

  **Relazioni inferite di audit:** D20, D47, D50, D51; alias requisiti: FR-020, FR-047, FR-050, FR-051.

- [ ] T041 (M4-04) Document review AI automatico con findings e fonte/section

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L94](../../AI-Project-Documentation-Tasks.md#L94); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-02

  **Stima originale (h):** 13

  **AC originali:** severity e fonte valida; nessuna correzione automatica

  **Relazioni inferite di audit:** D28, D33; alias requisiti: FR-028, FR-033.

- [ ] T042 (M4-05) Section revision proposal AI (before/after; accept/reject)

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L95](../../AI-Project-Documentation-Tasks.md#L95); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-03,M4-04

  **Stima originale (h):** 19

  **AC originali:** revision guard e conflitto se editing concorrente

  **Relazioni inferite di audit:** D20, D27, D45, D47; alias requisiti: FR-020, FR-027, FR-045, FR-047.

- [ ] T043 (M4-06) Selective update a partire da nuove informazioni

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L96](../../AI-Project-Documentation-Tasks.md#L96); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-05,M3

  **Stima originale (h):** 14

  **AC originali:** proposte solo sezioni impattate; mai sostituzione automatica

  **Relazioni inferite di audit:** D18, D29; alias requisiti: FR-018, FR-029.

- [ ] T044 (M4-07) Export Markdown/PDF/DOCX + opzione citazioni

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L97](../../AI-Project-Documentation-Tasks.md#L97); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-03

  **Stima originale (h):** 18

  **AC originali:** golden fixtures e link/fonti, liste/tabelle coerenti

  **Relazioni inferite di audit:** D08, D21, D22, D28, D50; alias requisiti: FR-008, FR-021, FR-022, FR-028, FR-050.

- [ ] T045 (M4-08) Casi E2E e UX review del workflow completo

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L98](../../AI-Project-Documentation-Tasks.md#L98); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-01..M4-07,M4-09

  **Stima originale (h):** 13

  **AC originali:** dal progetto al DOCX PDF MD, storici e diff verificati; conflitto irrisolto è punto aperto

  **Relazioni inferite di audit:** D06, D22, D24, D27, D33, D43, D54; alias requisiti: FR-006, FR-022, FR-024, FR-027, FR-033, FR-043, FR-054.

- [ ] T046 (M4-09) Rendering delle conferme, ipotesi e conflitti nel documento

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L99](../../AI-Project-Documentation-Tasks.md#L99); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M4-02,M3-11,M3-12

  **Stima originale (h):** 4

  **AC originali:** bozza distingue confermato PM, non validato e conflitto aperto; citazioni puntano alle evidenze; non inventa approvazione cliente

  **Relazioni inferite di audit:** D28, D31, A51, A52; alias requisiti: FR-028, FR-031, FR-060, FR-061.

### Gate M4 — alias SC-005

**Provenienza:** [AI-Project-Documentation-Tasks.md:L101](../../AI-Project-Documentation-Tasks.md#L101); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M4:** **primo rilascio pilota-funzionale** a PM selezionati in test (non ancora production-ready): ciclo completo input->chiarimenti->bozza->revisioni->export, con verifiche sicurezza di base.

## M5 — Hardening, admin e operations (120 h; 90–150)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L103](../../AI-Project-Documentation-Tasks.md#L103); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T047 (M5-01) Provider multipli: adapter e capability/endpoint security

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L107](../../AI-Project-Documentation-Tasks.md#L107); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-01

  **Stima originale (h):** 15

  **AC originali:** almeno 2 runtime locali o contratti verificati, no egress cloud

  **Relazioni inferite di audit:** D05, D25, D39; alias requisiti: FR-005, FR-025, FR-039.

- [ ] T048 (M5-02) Prompt config per fase/modello/progetto, versioni, UI Admin

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L108](../../AI-Project-Documentation-Tasks.md#L108); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M3-01,M4

  **Stima originale (h):** 16

  **AC originali:** precedenza deterministica e ogni run riproducibile

  **Relazioni inferite di audit:** D05, D34; alias requisiti: FR-005, FR-034.

- [ ] T049 (M5-03) Scheduler quotas per tipo/provider e priorità production

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L109](../../AI-Project-Documentation-Tasks.md#L109); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-07,M2,M3

  **Stima originale (h):** 12

  **AC originali:** test di concorrenza e fairness, DGX non sovraccaricato

  **Relazioni inferite di audit:** D44, D53; alias requisiti: FR-044, FR-053.

- [ ] T050 (M5-04) OpenTelemetry, Collector, Prometheus, Tempo, Loki, Grafana

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L110](../../AI-Project-Documentation-Tasks.md#L110); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-02,M3

  **Stima originale (h):** 21

  **AC originali:** trace cross-service e dashboards; dati sensibili redatti

  **Relazioni inferite di audit:** D42; alias requisiti: FR-042.

- [ ] T051 (M5-05) Backup UI Admin: job, manifest, storage remoto, encryption

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L111](../../AI-Project-Documentation-Tasks.md#L111); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-03,M1-06

  **Stima originale (h):** 17

  **AC originali:** backup consistente recuperabile, checksum e retention

  **Relazioni inferite di audit:** D40; alias requisiti: FR-040.

- [ ] T052 (M5-06) Restore executor isolato, maintenance mode, test restore

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L112](../../AI-Project-Documentation-Tasks.md#L112); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M5-05

  **Stima originale (h):** 18

  **AC originali:** restore in test, preflight e audit; rollback documentato

  **Relazioni inferite di audit:** D40; alias requisiti: FR-040.

- [ ] T053 (M5-07) Delete/archive, tombstones, audit security e retention

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L113](../../AI-Project-Documentation-Tasks.md#L113); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M2,M3

  **Stima originale (h):** 9

  **AC originali:** eliminazione attivi senza toccare KB condivisa; restore non resurrezione silenziosa

  **Relazioni inferite di audit:** D46; alias requisiti: FR-046.

- [ ] T054 (M5-08) Full CI/CD release pipeline, migration + security/hardening

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L114](../../AI-Project-Documentation-Tasks.md#L114); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M1-08,M4

  **Stima originale (h):** 12

  **AC originali:** deploy approvato, regressioni/test green, DR runbook

  **Relazioni inferite di audit:** D39, D41, D43; alias requisiti: FR-039, FR-041, FR-043.

### Gate M5 — alias SC-006

**Provenienza:** [AI-Project-Documentation-Tasks.md:L116](../../AI-Project-Documentation-Tasks.md#L116); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M5:** hardening e restore testati, dashboard visibile, nessun flusso di esfiltrazione dei contenuti verso provider AI esterni, pipeline di rilascio controllata.

## M6 — Pilota reale e accettazione (50 h dev; range 35–65)

**Provenienza:** [AI-Project-Documentation-Tasks.md:L118](../../AI-Project-Documentation-Tasks.md#L118); versione e SHA-256 in [research.md](research.md#fonti).

- [ ] T055 (M6-01) Preparazione ambiente produzione/pilota, onboarding 2–3 PM

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L122](../../AI-Project-Documentation-Tasks.md#L122); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M5

  **Stima originale (h):** 9

  **AC originali:** accessi, help guide, dataset e backup verificati

  **Relazioni inferite di audit:** D40, D53, D54, D57; alias requisiti: FR-040, FR-053, FR-054, FR-057.

- [ ] T056 (M6-02) Setup feedback standardizzato e baseline operativa

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L123](../../AI-Project-Documentation-Tasks.md#L123); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M6-01

  **Stima originale (h):** 6

  **AC originali:** rubrica: chiarezza, domande, grounding, utilità per tecnico

  **Relazioni inferite di audit:** D35, D57; alias requisiti: FR-035, FR-057.

- [ ] T057 (M6-03) Supporto pilota e telemetria/feedback periodici

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L124](../../AI-Project-Documentation-Tasks.md#L124); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M6-02

  **Stima originale (h):** 13

  **AC originali:** issue classificate per severità, niente dati riservati nei log

  **Relazioni inferite di audit:** D42, D57; alias requisiti: FR-042, FR-057.

- [ ] T058 (M6-04) Remediation difetti e regression suite

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L125](../../AI-Project-Documentation-Tasks.md#L125); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M6-03

  **Stima originale (h):** 17

  **AC originali:** zero blocker/high severity aperti, test end-to-end

  **Relazioni inferite di audit:** D43, D57; alias requisiti: FR-043, FR-057.

- [ ] T059 (M6-05) Valutazione con PM e referente tecnico/delivery; go/no-go

  **Provenienza:** [AI-Project-Documentation-Tasks.md:L126](../../AI-Project-Documentation-Tasks.md#L126); versione e SHA-256 in [research.md](research.md#fonti).

  **Dipendenze originali (←):** M6-04

  **Stima originale (h):** 5

  **AC originali:** verbale finale, backlog post-pilota, accettazione esplicita

  **Relazioni inferite di audit:** D56, D57; alias requisiti: FR-056, FR-057.

### Gate M6 — alias SC-007

**Provenienza:** [AI-Project-Documentation-Tasks.md:L128](../../AI-Project-Documentation-Tasks.md#L128); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M6 / Definition of Ready for operational rollout:** alcuni progetti reali attraversano il ciclo fino a DOCX/PDF/MD; PM e tecnico attestano utilità; difetti bloccanti risolti; restore e policy di egress verificati. Il pilota dura **alcune settimane** con 2–3 PM; non confondere le 50 h di supporto/sviluppo con il tempo di calendario dei PM.

## Copia integrale del backlog sorgente

Preserva regole, decisioni aggiuntive, DoD, gate, must pass, stime, percorso critico, prompt e change log. Le righe tabellari sono evidenza, non altri 59 task: il catalogo canonico e T001-T059 sopra. Tutte le righe nel loro ordine originale; indice di provenienza in research.md.
**Provenienza:** [AI-Project-Documentation-Tasks.md:L1-L182](../../AI-Project-Documentation-Tasks.md#L1); versione e SHA-256 in [research.md](research.md#fonti).


<!-- SOURCE_SNAPSHOT_BEGIN AI-Project-Documentation-Tasks.md -->
# AI Project Documentation — Backlog di implementazione

**Versione:** 1.2 · **Baseline:** [AI-Project-Documentation-MVP-Plan.md](./AI-Project-Documentation-MVP-Plan.md) · **Data:** 2026-10-14

## Regole di lettura

- Le ore sono **ore uomo di implementazione, test e documentazione** per uno sviluppatore senior assistito da coding agent: non ore macchina né durata di calendario.
- Le milestone vengono approvate singolarmente. Il coding agent è autonomo **solo all'interno** della milestone approvata.
- Ogni task include: ID, dipendenze (dopo `←`), stima centrale e criteri di accettazione (AC). Le dipendenze sono di tipo finish-to-start salvo indicazione diversa.
- Gli importi di effort vanno aggiornati a ogni gate dopo misurazione: niente padding occulto. La stima centrale è una baseline, non un commitment contrattuale.
- Contratti/schema/ADR possono essere sviluppati in parallelo dentro M0 quando non interferiscono; non avviare lo sviluppo di M1 prima del gate M0.
- **DoD comune:** build/lint puliti, test appropriati, codice revisionato, migrazioni e contratti aggiornati, log senza contenuti sensibili, nessun comportamento in contrasto con le decisioni approvate, istruzioni di deploy/rollback quando pertinenti.

## Decisione A49 — MAF conversazionale

Microsoft Agent Framework è obbligatorio per gestione sessione conversazionale, compattazione standard e composizione dello storico. Lo storico originale resta persistito in PostgreSQL e i fatti/decisioni di progetto sono separati. È **escluso uno spike specifico di compattazione in M0**; non escludere i normali test M1/M3. Non utilizzare MAF per sostituire scheduler PostgreSQL, retrieval o orchestrazione deterministica dei job. Interfacce devono rendere sostituibile la strategia di compattazione senza svilupparne ora una custom. Tutta la generazione rimane asincrona, mentre inserimento messaggi non implica rianalisi automatica.

## Decisioni A50–A54 — Chat, conferme e conflitti

**A50, A51 e A52 approvate.** La chat può chiedere chiarimenti brevi contestuali, ma l'analisi formale è sempre on-demand. Il consolidamento classifica provenienza e stati, richiede conferme ai requisiti critici/ambigui e presenta i conflitti senza risolverli autonomamente. **A53–A54** saranno formalizzate in M0 come ADR tecnici: retrieval conversazionale selettivo e contesto ricostruibile indipendente dal modello. Non introdurre un secondo sistema agentico o una pipeline continua di estrazione a ogni messaggio.

## M0 — Proof tecnologico, contratti e ADR (38 h; range 30–45)

| ID | Task | ← | h | AC specifici |
|---|---|---|---:|---|
| M0-01 | Rilevare disponibilità runtime DGX e contratti API di chat/embedding/rerank/discovery | — | 5 | Report endpoint, alias, auth, JSON capability, limiti e fallback espliciti; zero cloud |
| M0-02 | PoC Docling + HybridChunker su PDF testuale/DOCX/MD/TXT su host target | — | 8 | Container riproducibile, estratti strutturati e locator; scansioni rifiutate chiaramente |
| M0-03 | Valutare Tiptap JSON, Markdown/PDF/DOCX converter e policy licenze | — | 5 | ADR, demo JSON->3 formati con heading/tabelle/lista e limiti documentati |
| M0-04 | Definire bounded RAG, metadata/sourceRefs e schema output AI per fasi | M0-01,M0-02 | 7 | Contratti JSON validabili, query scope e budget retrieval documentati |
| M0-05 | Definire security/deployment/backup/telemetry ADR e threat model minimo, includendo ADR MAF, A53 e A54 | — | 5 | ADR autorizzazioni, JWT/refresh, web SSRF, backup executor, egress, perimetro MAF e confine sessione/memoria; niente spike compattazione |
| M0-06 | Costruire 5 casi gold anonimizzati e quality rubric, baseline manuale | M0-01 | 5 | Dataset revisionabile con evidenze attese e criteri valutazione |
| M0-07 | Gate M0: consolidare ADR, open risks, revised estimates | M0-01..M0-06 | 3 | Documento di decisioni tecniche, backlog aggiornato, PoC riproducibile |

**Gate M0:** Docling tratta 4 formati; almeno una analisi campione su modello locale produce output JSON validato; RAG minimale prova citazioni; stack scelto compatibile e senza servizi AI cloud. Se uno di questi fallisce, stop e ADR correttivo.

## M1 — Foundation, autenticazione, deployment iniziale (78 h; 60–95)

| ID | Task | ← | h | AC specifici |
|---|---|---|---:|---|
| M1-01 | Scaffold monorepo .NET, Angular, Python, test e docs | M0 | 8 | Solution workspace standard, lint/build automatizzati |
| M1-02 | Compose dev/test/prod, rete, volumi, config e secrets | M1-01 | 9 | Ambienti distinti; niente secrets nel repository; DGX LAN raggiungibile |
| M1-03 | PostgreSQL/pgvector schema iniziale, migrazioni e repository | M1-01 | 10 | Migration reversibile o strategia forward-compatible; test DB |
| M1-04 | ASP.NET Core Identity, JWT e refresh rotation; Admin/PM | M1-03 | 13 | Token breve, revoca, lockout, CSRF se cookie, test authz |
| M1-05 | Cataloghi Cliente/Prodotto/Progetto e UX Bootstrap | M1-03,M1-04 | 12 | CRUD, versione prodotto facoltativa, tutti PM vedono tutti progetti |
| M1-06 | Storage originali, hash, upload limitato e sanitizzazione | M1-03,M1-04 | 8 | 50MB default config, MIME/signature, path traversal/zip bomb prevention |
| M1-07 | Infrastructure coda PostgreSQL, lease, heartbeat, retry e API stato | M1-03 | 7 | crash/recovery test, nessun job duplicato |
| M1-08 | CI GitHub Actions per build/test/image e primo environment gate | M1-01,M1-02 | 3 | PR workflow passa, produzione richiede approvazione |
| M1-09 | Storico conversazionale PostgreSQL e adapter MAF per sessioni persistenti | M1-03,M1-04 | 8 | messaggi originali immutabili e ordinati con riferimenti allegati; ripresa dopo restart; snapshot derivati separati, adapter testato con fake provider |

**Gate M1:** login, CRUD progetto e upload persistente end-to-end in ambiente test; accesso Admin/PM verificato; un job può sopravvivere al riavvio del worker.

## M2 — Knowledge, Docling, RAG avanzato (106 h; 80–135)

| ID | Task | ← | h | AC specifici |
|---|---|---:|---:|---|
| M2-01 | Entità/versioni KB, scope e approvazione Admin | M1 | 15 | PM può pubblicare nella KB progetto; prodotto/cliente attende Admin |
| M2-02 | Worker Docling HTTP/queue parser/hybrid, locator e security sandbox | M0-02,M1-07 | 17 | PDF/DOCX/MD/TXT, output typed; timeout + isolate temp files |
| M2-03 | Adapter embedding e build chunk/parent relation | M0-01,M2-02 | 13 | vettori dimensionati, source refs e parent chains persistenti |
| M2-04 | Reindex cambio modello e cutover senza indice misto | M2-03 | 10 | indice precedente usabile fino a cutover; rollback dopo errore |
| M2-05 | Hybrid vector + FTS + RRF con filtri scope/esclusioni | M2-01,M2-03 | 16 | query dimostrabili: codici esatti e descrizioni naturali; esclusioni rispettate |
| M2-06 | Rerank, parent expansion, query rewriting, bounded iterative search | M2-05 | 18 | limiti max iteration/time/tokens, evidence valid, benchmark retrieval |
| M2-07 | UX KB: upload, versioni, approval queue, job e search-preview | M2-01..M2-06 | 8 | workflow approvazione visibile, precedenti versioni tracciabili |
| M2-08 | Integration & security testing knowledge path | M2-07 | 3 | una fonte esclusa o pending non entra nel contesto, audit OK |
| M2-09 | Retrieval selettivo per chat: trigger, filtri, evidenze e budget | M2-06,M1-09 | 6 | una domanda su prodotto attiva ricerca pertinente; messaggi ordinari non attivano RAG; scope e decisioni web rispettati; metriche trigger registrate |

**Gate M2:** il retrieval restituisce fonti/locator corretti da KB di prodotto e cliente senza attraversare esclusioni; test di cambio modello non restituisce vettori incompatibili. Baseline RAG vs avanzato misurata.

## M3 — Flusso PM, domande e ricerca web (121 h; 95–150)

| ID | Task | ← | h | AC specifici |
|---|---|---:|---:|---|
| M3-01 | Model provider registry e stage runner per llama.cpp con output schema | M0-01,M1-07 | 15 | modello per fase, validazione JSON, no fallback cloud |
| M3-02 | Run/on-demand e input normalization con versioni e checkpoint | M2,M3-01,M1-09 | 14 | aggiunta allegato non lancia analisi; restart riprende checkpoint |
| M3-03 | Fact/requirement/assumption/conflict normalization + citazioni | M3-02 | 12 | deduplica, fonti verificate, status senza invenzioni |
| M3-04 | Domande guidate max 5 per round e risposta/status history | M3-03,M3-09,M3-11,M3-12 | 13 | 2 round richiesti su comando, no domande già risolte, rinvio supportato |
| M3-05 | `IWebSearchProvider` + SearXNG, 3 web modes, sanitize e SSRF policy | M1,M3-01 | 15 | AUTO/APPROVAL/OFF, solo query pubbliche, malicious redirect rejected |
| M3-06 | Import web -> proposta KB con diritti e Admin approval | M3-05,M2-01 | 7 | URL/hash/licenza/provenienza versionati; non auto-pubblicare |
| M3-07 | UI chat-first di progetto, analisi/chiarimenti e progresso job | M3-02..M3-06,M3-09..M3-12 | 7 | PM completa input->analisi->risposte, aggiornamento solo volontario |
| M3-08 | E2E del vertical slice + cleanup fallimenti | M3-07 | 2 | almeno 2 casi funzionali end-to-end senza editor finale, inclusa conversazione frammentata |
| M3-09 | Context assembly con MAF: standard compaction, budget per modello, facts confermati e integrazione job | M1-09,M3-01,M3-03 | 12 | conversa su più giornate; compattazione attivata oltre soglia senza perdere storico; fatti confermati preservati; niente trigger automatico di analisi; job riprende e output validato |
| M3-10 | Turni di chat conversazionale con chiarimenti mirati, retrieval selettivo e job AI | M3-09,M2-09 | 8 | PM scrive in turni successivi; risposta AI pertinente e non ripetitiva; nessun update formale dei requisiti fino ad Analizza; job persistenti |
| M3-11 | Estrarre candidati e confermare selettivamente requisiti critici/ambigui | M3-03,M3-10 | 8 | provenienza e stato conservati; critici richiedono conferma, non critici possono restare non validati; non equiparare conferma PM ad approvazione cliente |
| M3-12 | Rilevazione, presentazione e risoluzione manuale dei conflitti | M3-11 | 8 | fonti e date alternative mostrate; PM sceglie, supera o rinvia; conflitto differito persiste; nessuna prevalenza automatica per data |

**Gate M3:** PM inserisce note frammentarie in chat, riceve chiarimenti mirati senza attivare analisi formale, con storico persistente e compattazione standard; requisiti critici passano per conferma PM; contraddizioni sono visibili e risolvibili o differibili; analisi parte solo su richiesta. PM lancia analisi reale e risponde; web pubblica controllata; progetto con ERP non conosciuto produce chiarimenti, non inventa integrazione API. Disponibile dimostrazione in ambiente test.

## M4 — Documento visuale, revisione, export (134 h; 105–165)

| ID | Task | ← | h | AC specifici |
|---|---|---:|---:|---|
| M4-01 | Canonical schema JSON document, migrations, section IDs, revision store | M0-03,M3 | 18 | schemaVersion, revisioni immutabili, migration tests |
| M4-02 | Quattro template configurabili e generator->JSON converter | M4-01 | 12 | sezioni rilevanti, piano solo macroattività/dipendenze |
| M4-03 | Tiptap WYSIWYG su Bootstrap con toolbar e persistenza optimistic | M4-01 | 23 | heading/list/tables/link/code, 409 conflict e no data loss |
| M4-04 | Document review AI automatico con findings e fonte/section | M4-02 | 13 | severity e fonte valida; nessuna correzione automatica |
| M4-05 | Section revision proposal AI (before/after; accept/reject) | M4-03,M4-04 | 19 | revision guard e conflitto se editing concorrente |
| M4-06 | Selective update a partire da nuove informazioni | M4-05,M3 | 14 | proposte solo sezioni impattate; mai sostituzione automatica |
| M4-07 | Export Markdown/PDF/DOCX + opzione citazioni | M4-03 | 18 | golden fixtures e link/fonti, liste/tabelle coerenti |
| M4-08 | Casi E2E e UX review del workflow completo | M4-01..M4-07,M4-09 | 13 | dal progetto al DOCX PDF MD, storici e diff verificati; conflitto irrisolto è punto aperto |
| M4-09 | Rendering delle conferme, ipotesi e conflitti nel documento | M4-02,M3-11,M3-12 | 4 | bozza distingue confermato PM, non validato e conflitto aperto; citazioni puntano alle evidenze; non inventa approvazione cliente |

**Gate M4:** **primo rilascio pilota-funzionale** a PM selezionati in test (non ancora production-ready): ciclo completo input->chiarimenti->bozza->revisioni->export, con verifiche sicurezza di base.

## M5 — Hardening, admin e operations (120 h; 90–150)

| ID | Task | ← | h | AC specifici |
|---|---|---:|---:|---|
| M5-01 | Provider multipli: adapter e capability/endpoint security | M3-01 | 15 | almeno 2 runtime locali o contratti verificati, no egress cloud |
| M5-02 | Prompt config per fase/modello/progetto, versioni, UI Admin | M3-01,M4 | 16 | precedenza deterministica e ogni run riproducibile |
| M5-03 | Scheduler quotas per tipo/provider e priorità production | M1-07,M2,M3 | 12 | test di concorrenza e fairness, DGX non sovraccaricato |
| M5-04 | OpenTelemetry, Collector, Prometheus, Tempo, Loki, Grafana | M1-02,M3 | 21 | trace cross-service e dashboards; dati sensibili redatti |
| M5-05 | Backup UI Admin: job, manifest, storage remoto, encryption | M1-03,M1-06 | 17 | backup consistente recuperabile, checksum e retention |
| M5-06 | Restore executor isolato, maintenance mode, test restore | M5-05 | 18 | restore in test, preflight e audit; rollback documentato |
| M5-07 | Delete/archive, tombstones, audit security e retention | M2,M3 | 9 | eliminazione attivi senza toccare KB condivisa; restore non resurrezione silenziosa |
| M5-08 | Full CI/CD release pipeline, migration + security/hardening | M1-08,M4 | 12 | deploy approvato, regressioni/test green, DR runbook |

**Gate M5:** hardening e restore testati, dashboard visibile, nessun flusso di esfiltrazione dei contenuti verso provider AI esterni, pipeline di rilascio controllata.

## M6 — Pilota reale e accettazione (50 h dev; range 35–65)

| ID | Task | ← | h | AC specifici |
|---|---|---:|---:|---|
| M6-01 | Preparazione ambiente produzione/pilota, onboarding 2–3 PM | M5 | 9 | accessi, help guide, dataset e backup verificati |
| M6-02 | Setup feedback standardizzato e baseline operativa | M6-01 | 6 | rubrica: chiarezza, domande, grounding, utilità per tecnico |
| M6-03 | Supporto pilota e telemetria/feedback periodici | M6-02 | 13 | issue classificate per severità, niente dati riservati nei log |
| M6-04 | Remediation difetti e regression suite | M6-03 | 17 | zero blocker/high severity aperti, test end-to-end |
| M6-05 | Valutazione con PM e referente tecnico/delivery; go/no-go | M6-04 | 5 | verbale finale, backlog post-pilota, accettazione esplicita |

**Gate M6 / Definition of Ready for operational rollout:** alcuni progetti reali attraversano il ciclo fino a DOCX/PDF/MD; PM e tecnico attestano utilità; difetti bloccanti risolti; restore e policy di egress verificati. Il pilota dura **alcune settimane** con 2–3 PM; non confondere le 50 h di supporto/sviluppo con il tempo di calendario dei PM.

## Trasversalità e criteri aggiuntivi

### Sicurezza (must pass)

- Nessun invio di contenuti interni a motori di ricerca, servizi LLM SaaS, analytics o error reporting esterni.
- Solo PM/Admin autenticati; azioni KB approval, restore, modifiche modelli/prompt in UI Admin.
- Il fatto che tutti i PM vedano tutti i progetti **non** autorizza accesso amministrativo.
- PDF/DOCX, Markdown, web content e output LLM sono trattati come input non fidati.
- Test specifici su JWT refresh reuse, upload anomali, URL internal targets, query contamination, log redaction.

### Meccanismi LLM e KB (must pass)

- Claim privo di fonte viene marcato da confermare; referenceId deve esistere in DB e puntare alla giusta versione.
- Il modello non può usare un document version non ancora approvato/indicizzato nella KB prodotto/cliente.
- Distinguere embedding da reranking; se cambia embedder reindex completo e switch atomico.
- Il percorso storico cliente usa source metadata, non conferma implicitamente validità.
- Le risposte PM conservano la precedenza su inferenze/estratti non verificati.

### Rendering/editor (must pass)

- `DocumentRevision` e `schemaVersion` associati; nessuna modifica distruttiva del documento in caso di migrazione.
- Le proposte AI non applicate non modificano il documento corrente.
- La conversione deve preservare semanticamente titoli, liste, tabelle, link, blocchi codice e riferimenti; limitazioni note documentate.

## Stima e critical path

| Milestone | Centro h | Range h |
|---|---:|---:|
| M0 | 38 | 30–45 |
| M1 | 78 | 60–95 |
| M2 | 106 | 80–135 |
| M3 | 121 | 95–150 |
| M4 | 134 | 105–165 |
| M5 | 120 | 90–150 |
| M6 | 50 | 35–65 |
| **Totale** | **647** | **495–805** |

**Percorso di dipendenze critiche candidato (non un unico percorso seriale):** `M0-02 → M1-07 → M2-02 → M2-03 → M2-05 → M2-06 → M2-09 → M3-10 → M3-11 → M3-12 → M3-04 → M4-09 → M4-08 → M5 → M6`. **Nota:** M1-09 e M3-09 sono prerequisiti paralleli per M3-10; il critical path effettivo richiede un calcolo DAG dopo il gate M0. L'ordine dettagliato andrà convertito dal coding agent in DAG e aggiornato dopo M0; gli step M5-04/M5-05 possono iniziare prima di M4 se la milestone approvata lo prevede, ma i gate restano sequenziali per governance.

## Prompt iniziale per coding agent (non avviare task fuori milestone)

> Leggi integralmente `AI-Project-Documentation-MVP-Plan.md` e `AI-Project-Documentation-Tasks.md`. Considerali baseline vincolante. La sequenza di 48 domande confermate, più le decisioni originarie, è riportata nel registro. **Non sviluppare ora l'intero progetto.** Per prima cosa prepara M0: verifica API e deployment DGX/Docling/Angular editor, produzioni ADR, contratti minimi, dataset gold, proof tecnica e stima aggiornata. Prima di toccare componenti successivi, mostra un piano eseguibile di M0 e i criteri di accettazione. Durante una milestone approvata puoi operare autonomamente entro scope e test; fermati al gate. Non assumere servizi cloud, non modificare le decisioni UX/KB senza approvazione, non aggiungere framework agentici ulteriori rispetto a MAF per sessioni/chat/compattazione, non produrre stime AI nei documenti dei PM. Se trovi contraddizioni sostanziali, descrivile e proponi una soluzione tecnica minima prima dell'implementazione.


## Change log 1.1 (2026-10-14)

Aggiunti M1-09 (8 h) e M3-09 (12 h); aggiornati M0-05, M3-02, M3-07, M3-08, gate M3 e dipendenze. **Totale 613 h centrali, range 465–760 h.** Nessuna prova esplorativa dedicata alla compattazione in M0; validazione mediante normali test di integrazione ed E2E nelle milestone successive.

## Change log 1.2 (2026-10-14)

- Integrazione decisioni **A50–A52** confermate e proposte tecniche **A53–A54**, senza riaprire il questionario originale.
- Aggiunti task M2-09, M3-10, M3-11, M3-12, M4-09; riveduti gate, dipendenze e criteri di accettazione.
- **59 task** in 7 milestone, **647 h centrali** e range **495–805 h** (stima preliminare, da rivedere dopo M0).
<!-- SOURCE_SNAPSHOT_END AI-Project-Documentation-Tasks.md -->
