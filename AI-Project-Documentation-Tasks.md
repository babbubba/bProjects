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
