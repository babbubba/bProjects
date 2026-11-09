# bProjects — istruzioni per Codex

## Autorità e contesto brownfield

Operare nel solo repository `C:\source\repos\babbubba\bProjects`. Prima di ogni
intervento verificare branch, stato Git e istruzioni locali; conservare le modifiche
preesistenti. Non creare cloni o worktree. La presenza del branch non avvia una slice.

Leggere, nell'ordine:
1. `.specify/memory/constitution.md`: governa il metodo; modificabile solo con approvazione esplicita.
2. `AI-Project-Documentation-MVP-Plan.md`: requisiti funzionali approvati del prodotto; non impone di implementarli tutti nella slice corrente.
3. `docs/roadmap/implementation-coverage.md`: orientamento verificabile su implementazione, riserve e incrementi previsti; non è un piano operativo.
4. Gli artefatti `specs/<slice-corrente>/` e i contratti delle slice precedenti coinvolte, più codice e prove pertinenti.

Per la 001 leggere anche backlog, decisione di accettazione e HANDOFF. Distinguere
sempre implementato, verificato e accettato. La 001 è accettata con riserve per il
solo pilota controllato; T052/T053 restano aperti, non PASS. Non correggere documenti
o aspettative storiche per far passare una prova; segnalare le contraddizioni.

## Spec Kit con più vertical slice

- Una directory `specs/NNN-nome/` per incremento. Le specifiche e i contratti della
  001 sono baseline storiche: i loro divieti di funzionalità future valgono per la
  001, non impediscono nuove slice autorizzate. Non rigenerare la 001 per pianificare la 002.
- Solo dopo il mandato di avvio usare gli skill Spec Kit installati: Specify,
  Clarify se necessario, Plan, Tasks, Analyze e Implement secondo prerequisiti e
  istruzioni dei rispettivi skill. Leggere lo skill prima di applicarlo.
- `specs/<slice-corrente>/tasks.md` è l'unica baseline operativa di quella slice.
  Non creare piani o checklist di lavoro paralleli; la roadmap registra copertura
  e confini, non task. Gli ID Txxx sono locali alla slice: citarli con il percorso.
- Prima di eseguire strumenti verificare il contesto feature: gli script locali
  usano `SPECIFY_FEATURE_DIRECTORY` o `.specify/feature.json`, non deducono la
  directory dal solo branch Git (`.specify/scripts/powershell/common.ps1`).
  Per letture diagnostiche usare `SPECIFY_FEATURE_NO_PERSIST=1`; non puntare alla
  001 per supplire all'assenza degli artefatti della nuova slice.
- Nei nuovi artefatti descrivere il delta sul codice esistente, i componenti da
  riusare, compatibilità, migrazioni e regressioni pertinenti. Non riscrivere
  retroattivamente le specifiche/contratti della 001: un'evoluzione va esplicitata
  nella nuova slice, conservando tracciabilità e storico.
- Spuntare un task solo con evidenza dei suoi criteri. SKIP, test finti e guasti
  gestiti non equivalgono a prove nominali reali o ad accettazione umana.

## Convenzioni e verifiche

KISS/YAGNI: monolite modulare .NET, Angular + Bootstrap, PostgreSQL. Riusare AI,
Evidence, ContextBudget, dispatcher e persistenza prima di aggiungere astrazioni.
MAF .NET è il riferimento; verificare le API nella versione fissata dal progetto.
Dominio, originali, revisioni e provenienza restano applicativi e persistenti;
sessioni e sintesi MAF sono derivate. Nessun taglio silenzioso per rientrare nel
contesto e nessuna promozione automatica di ipotesi a fatti confermati.

LLM solo interni autorizzati, nessun fallback SaaS. Segreti fuori dal repository;
documenti e chat sono dati non fidati. Nessuna stima AI di ore/costi/date/risorse.
ADR solo per scelte significative secondo Constitution, non per decisioni ordinarie.
Comandi di sviluppo in `CLAUDE.md`; scegliere prove proporzionate al delta e
integrazioni reali pertinenti, riportando PASS/FAIL/SKIP e verifiche mancanti.

## Stato preparatorio e Git

Prossimo incremento previsto: `002-project-documents`, branch
`feature/002-implementation`: allegati PDF testuali, DOCX, Markdown e TXT,
parsing, provenienza e trattamento dei documenti oltre contesto. KB condivise e
RAG esclusi. Questa registrazione non autorizza l'avvio di Spec Kit o l'implementazione.

Non modificare Constitution o artefatti storici della 001 nel lavoro preparatorio.
Non cancellare né aggiungere ai commit i file locali in `docs/acceptance-trace/`;
evitare staging indiscriminato (`git add .`). Nessun push o merge senza mandato;
non aggiungere `Co-Authored-By`. Per eventuali commit usare modifiche piccole e
coerenti, Conventional Commits e riferimenti alla slice/task quando applicabili.
