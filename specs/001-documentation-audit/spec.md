# Feature Specification: baseline AI Project Documentation derivata

**Natura:** proiezione documentale per audit, autorizzata il 2026-10-14. Non e un output di speckit-specify/plan/tasks e non autorizza implementazione o passaggio di gate.

**Autorita:** i due documenti sorgente prevalgono. Estratti e copie conservano testo, vincoli e stati originali. Alias e relazioni di audit non sono nuove decisioni. A53/A54 rimangono proposte tecniche.

**Tracciabilita:** [research.md](research.md) contiene hash, provenienze, matrici nei due sensi, lacune e validazione. I link relativi dentro le copie integrali sono testo originale: vanno risolti nella directory dei sorgenti. Per navigare usare i link di provenienza.

**Feature directory:** `specs/001-documentation-audit/`. Branch non creato o selezionato. **Status:** derivata per audit; baseline funzionale approvata, pianificazione da revisionare.

## Overview / Context

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L8-L22](../../AI-Project-Documentation-MVP-Plan.md#L8); versione e SHA-256 in [research.md](research.md#fonti).

## 1. Risultato atteso e vincoli

Il PM conversa liberamente con un assistente che può formulare chiarimenti mirati senza consolidare automaticamente i requisiti. Il PM crea un progetto, associa cliente e prodotti/versioni (1bStore, 1bGold, Gamma, Sigla, Business Central, applicazioni custom...), inserisce note frammentarie e allegati, consulta una KB a scope distinto, può autorizzare ricerche web su **sola documentazione pubblica**, avvia un'analisi, riceve fino a cinque domande per ciclo, risponde e richiede nuove domande. Il sistema genera una bozza visuale revisionabile, con evidenze puntuali e controlli AI, più un piano operativo **senza ore/costi/date calcolati dall'AI**. Esporta MD, PDF e DOCX.

**Outcome:** un tecnico e un delivery manager riescono a comprendere il lavoro senza ricominciare la discovery tramite lunghe call. La decisione finale è sempre umana.

**Non è** un PM tool: nessuna WBS operativa dettagliata, assegnazioni, timesheet, gantt, sviluppo automatico, ERP writeback, integrazione diretta a ticketing o gestione autonoma dei task. Nessun LLM SaaS e nessun invio di informazioni interne ai search provider web.

### Definizione dei termini
- **KB:** documenti originali immutabili/versionati + rappresentazioni strutturate e indici derivati.
- **Scope:** `GLOBAL`, `PRODUCT`, `CUSTOMER`, `PROJECT`; le tre classi principali sono prodotto/cliente/progetto, con `GLOBAL` per standard comuni.
- **Sorgente di progetto:** allegato/note immediatamente utilizzabili nel proprio progetto, distinti da contenuti condivisi che richiedono approvazione.
- **Run:** esecuzione versionata della pipeline; **job:** unità persistente schedulata; **checkpoint:** esito validato e riutilizzabile di uno step.
- **Proposta AI:** contenuto non applicato, sempre soggetto ad accettazione umana in caso di revisione documentale.


## Requirements

### Functional Requirements

FR e solo un alias del record originale, inclusi record tecnici e di governance: non implica atomicita o classificazione funzionale pura. Nessuna nuova decomposizione normativa e introdotta.

#### FR-001 — alias D01

**ID originale:** D01. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L27](../../AI-Project-Documentation-MVP-Plan.md#L27); versione e SHA-256 in [research.md](research.md#fonti).

| D01 | KB gestita solo via upload UI | Nessuna directory pubblica/watch folder come canale utente |

#### FR-002 — alias D02

**ID originale:** D02. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L28](../../AI-Project-Documentation-MVP-Plan.md#L28); versione e SHA-256 in [research.md](research.md#fonti).

| D02 | Scope KB prodotto/cliente/progetto distinti | Aggiungere `GLOBAL` per standard; nessuna promozione automatica fra scope |

#### FR-003 — alias D03

**ID originale:** D03. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L29](../../AI-Project-Documentation-MVP-Plan.md#L29); versione e SHA-256 in [research.md](research.md#fonti).

| D03 | Account locali, ruoli Admin/PM | Autorizzazioni backend, non solo UI |

#### FR-004 — alias D04

**ID originale:** D04. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L30](../../AI-Project-Documentation-MVP-Plan.md#L30); versione e SHA-256 in [research.md](research.md#fonti).

| D04 | Input MD, TXT, PDF testuale, DOCX | PDF OCR e audio fuori scope |

#### FR-005 — alias D05

**ID originale:** D05. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L31](../../AI-Project-Documentation-MVP-Plan.md#L31); versione e SHA-256 in [research.md](research.md#fonti).

| D05 | Modelli per fase: default globale + override progetto | No hardcode di Qwen |

#### FR-006 — alias D06

**ID originale:** D06. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L32](../../AI-Project-Documentation-MVP-Plan.md#L32); versione e SHA-256 in [research.md](research.md#fonti).

| D06 | Documento snello 2–5 pagine indicative + piano minimo | Lunghezza orientativa, non limite rigido |

#### FR-007 — alias D07

**ID originale:** D07. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L33](../../AI-Project-Documentation-MVP-Plan.md#L33); versione e SHA-256 in [research.md](research.md#fonti).

| D07 | Docling + HybridChunker | Worker Python, struttura/citazioni preservate |

#### FR-008 — alias D08

**ID originale:** D08. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L34](../../AI-Project-Documentation-MVP-Plan.md#L34); versione e SHA-256 in [research.md](research.md#fonti).

| D08 | Markdown nativo nella KB | Conservare originale, gerarchia, tabelle e code fences |

#### FR-009 — alias D09

**ID originale:** D09. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L35](../../AI-Project-Documentation-MVP-Plan.md#L35); versione e SHA-256 in [research.md](research.md#fonti).

| D09 | Ricerca web opzionale solo su documentazione pubblica | Mai inviare dati riservati a motori esterni |

#### FR-010 — alias D10

**ID originale:** D10. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L36](../../AI-Project-Documentation-MVP-Plan.md#L36); versione e SHA-256 in [research.md](research.md#fonti).

| D10 | App server separato dal DGX Spark | DGX dedicato all'AI; rete interna controllata |

#### FR-011 — alias D11

**ID originale:** D11. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L37](../../AI-Project-Documentation-MVP-Plan.md#L37); versione e SHA-256 in [research.md](research.md#fonti).

| D11 | Docker Compose | Servizi isolati e volumi persistenti |

#### FR-012 — alias D12

**ID originale:** D12. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L38](../../AI-Project-Documentation-MVP-Plan.md#L38); versione e SHA-256 in [research.md](research.md#fonti).

| D12 | Tutti i PM vedono tutti i progetti | Nessuna ACL per singolo PM/progetto nell'MVP |

#### FR-013 — alias D13

**ID originale:** D13. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L39](../../AI-Project-Documentation-MVP-Plan.md#L39); versione e SHA-256 in [research.md](research.md#fonti).

| D13 | Versionamento KB con approvazione | Nuove versioni immutabili; pubblicazione controllata |

#### FR-014 — alias D14

**ID originale:** D14. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L40](../../AI-Project-Documentation-MVP-Plan.md#L40); versione e SHA-256 in [research.md](research.md#fonti).

| D14 | PM autonomi nella KB di progetto | KB cliente/prodotto con approvazione Admin |

#### FR-015 — alias D15

**ID originale:** D15. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L41](../../AI-Project-Documentation-MVP-Plan.md#L41); versione e SHA-256 in [research.md](research.md#fonti).

| D15 | Modalità web per progetto: off, approval, automatic | Default globale: approvazione query |

#### FR-016 — alias D16

**ID originale:** D16. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L42](../../AI-Project-Documentation-MVP-Plan.md#L42); versione e SHA-256 in [research.md](research.md#fonti).

| D16 | Provider web intercambiabili | Primo: SearXNG self-hosted; adapter per provider successivi |

#### FR-017 — alias D17

**ID originale:** D17. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L43](../../AI-Project-Documentation-MVP-Plan.md#L43); versione e SHA-256 in [research.md](research.md#fonti).

| D17 | Ingestion asincrona con coda PostgreSQL | Job persistenti, retry, idempotenza |

#### FR-018 — alias D18

**ID originale:** D18. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L44](../../AI-Project-Documentation-MVP-Plan.md#L44); versione e SHA-256 in [research.md](research.md#fonti).

| D18 | Aggiornamento dell'analisi solo su richiesta | Nuovi allegati non avviano automaticamente analysis run |

#### FR-019 — alias D19

**ID originale:** D19. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L45](../../AI-Project-Documentation-MVP-Plan.md#L45); versione e SHA-256 in [research.md](research.md#fonti).

| D19 | Domande iterative guidate, massimo 5 per round | Priorità, rinvio, non applicabile, risposta; PM può procedere |

#### FR-020 — alias D20

**ID originale:** D20. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L46](../../AI-Project-Documentation-MVP-Plan.md#L46); versione e SHA-256 in [research.md](research.md#fonti).

| D20 | Editor visuale tipo Word con AI | Editor strutturato, non semplice textarea Markdown |

#### FR-021 — alias D21

**ID originale:** D21. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L47](../../AI-Project-Documentation-MVP-Plan.md#L47); versione e SHA-256 in [research.md](research.md#fonti).

| D21 | JSON strutturato come documento canonico | Markdown solo import/export/interchange con LLM |

#### FR-022 — alias D22

**ID originale:** D22. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L48](../../AI-Project-Documentation-MVP-Plan.md#L48); versione e SHA-256 in [research.md](research.md#fonti).

| D22 | Export MD/PDF/DOCX | Test di fedeltà su tabelle, elenchi, fonti |

#### FR-023 — alias D23

**ID originale:** D23. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L49](../../AI-Project-Documentation-MVP-Plan.md#L49); versione e SHA-256 in [research.md](research.md#fonti).

| D23 | Template predefiniti per tipologia | 4 template: analisi funzionale, ERP/API, modifica, nuovo progetto |

#### FR-024 — alias D24

**ID originale:** D24. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L50](../../AI-Project-Documentation-MVP-Plan.md#L50); versione e SHA-256 in [research.md](research.md#fonti).

| D24 | Piano AI: soltanto attività e dipendenze | Vietate stime AI di ore/costi/date/risorse |

#### FR-025 — alias D25

**ID originale:** D25. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L51](../../AI-Project-Documentation-MVP-Plan.md#L51); versione e SHA-256 in [research.md](research.md#fonti).

| D25 | Provider LLM multipli | llama.cpp iniziale, adapter OpenAI-compatibili e capability registry |

#### FR-026 — alias D26

**ID originale:** D26. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L52](../../AI-Project-Documentation-MVP-Plan.md#L52); versione e SHA-256 in [research.md](research.md#fonti).

| D26 | Tutte le elaborazioni AI via job persistenti | Progress + storico; browser chiuso non interrompe run |

#### FR-027 — alias D27

**ID originale:** D27. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L53](../../AI-Project-Documentation-MVP-Plan.md#L53); versione e SHA-256 in [research.md](research.md#fonti).

| D27 | Revisione AI per sezione | Proposta prima/dopo, accept/reject, conflitti revisioni |

#### FR-028 — alias D28

**ID originale:** D28. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L54](../../AI-Project-Documentation-MVP-Plan.md#L54); versione e SHA-256 in [research.md](research.md#fonti).

| D28 | Citazioni per requisito/affermazione | Collegate a versioni e locator; export opzionalmente con fonti |

#### FR-029 — alias D29

**ID originale:** D29. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L55](../../AI-Project-Documentation-MVP-Plan.md#L55); versione e SHA-256 in [research.md](research.md#fonti).

| D29 | Aggiornamento selettivo bozza | Solo su richiesta; delta per sezione e approvazione PM |

#### FR-030 — alias D30

**ID originale:** D30. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L56](../../AI-Project-Documentation-MVP-Plan.md#L56); versione e SHA-256 in [research.md](research.md#fonti).

| D30 | Versione prodotto facoltativa per progetto | Retrieval filtra/preferisce documentazione compatibile |

#### FR-031 — alias D31

**ID originale:** D31. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L57](../../AI-Project-Documentation-MVP-Plan.md#L57); versione e SHA-256 in [research.md](research.md#fonti).

| D31 | Knowledge cliente + progetti precedenti | Provenienza e data visibili; storico non diventa verità corrente |

#### FR-032 — alias D32

**ID originale:** D32. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L58](../../AI-Project-Documentation-MVP-Plan.md#L58); versione e SHA-256 in [research.md](research.md#fonti).

| D32 | Scope fonti automatico con esclusioni PM | Esclusioni persistenti applicate prima del retrieval |

#### FR-033 — alias D33

**ID originale:** D33. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L59](../../AI-Project-Documentation-MVP-Plan.md#L59); versione e SHA-256 in [research.md](research.md#fonti).

| D33 | Revisione automatica AI con segnalazioni | Bloccanti/avvisi/suggerimenti; nessuna correzione automatica |

#### FR-034 — alias D34

**ID originale:** D34. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L60](../../AI-Project-Documentation-MVP-Plan.md#L60); versione e SHA-256 in [research.md](research.md#fonti).

| D34 | Prompt per fase, modello e progetto | Versioni, audit, risoluzione override deterministica |

#### FR-035 — alias D35

**ID originale:** D35. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L61](../../AI-Project-Documentation-MVP-Plan.md#L61); versione e SHA-256 in [research.md](research.md#fonti).

| D35 | Benchmark riproducibili nel backend | Runner CLI, dataset anonimizzati, confronto qualità/prestazioni |

#### FR-036 — alias D36

**ID originale:** D36. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L62](../../AI-Project-Documentation-MVP-Plan.md#L62); versione e SHA-256 in [research.md](research.md#fonti).

| D36 | RAG avanzato gerarchico | Hybrid+RRF+rerank+parent retrieval+query rewriting+iterazione bounded |

#### FR-037 — alias D37

**ID originale:** D37. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L63](../../AI-Project-Documentation-MVP-Plan.md#L63); versione e SHA-256 in [research.md](research.md#fonti).

| D37 | Un solo embedder attivo | Reindex completo al cambio; evitare indice parziale/misto |

#### FR-038 — alias D38

**ID originale:** D38. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L64](../../AI-Project-Documentation-MVP-Plan.md#L64); versione e SHA-256 in [research.md](research.md#fonti).

| D38 | Importazione web in KB | Promozione controllata da Admin, con licenze, URL, data, versione |

#### FR-039 — alias D39

**ID originale:** D39. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L65](../../AI-Project-Documentation-MVP-Plan.md#L65); versione e SHA-256 in [research.md](research.md#fonti).

| D39 | Solo provider LLM interni | Egress LLM bloccato verso cloud, validazione backend |

#### FR-040 — alias D40

**ID originale:** D40. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L66](../../AI-Project-Documentation-MVP-Plan.md#L66); versione e SHA-256 in [research.md](research.md#fonti).

| D40 | Backup e restore gestiti dalla UI Admin | Esecutore isolato, manifest, verifica, destinazione esterna |

#### FR-041 — alias D41

**ID originale:** D41. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L67](../../AI-Project-Documentation-MVP-Plan.md#L67); versione e SHA-256 in [research.md](research.md#fonti).

| D41 | CI/CD con deployment controllato | GitHub Actions, registry, approvazione produzione, migrazioni |

#### FR-042 — alias D42

**ID originale:** D42. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L68](../../AI-Project-Documentation-MVP-Plan.md#L68); versione e SHA-256 in [research.md](research.md#fonti).

| D42 | Osservabilità completa OpenTelemetry | OTel + Prometheus + Grafana + Tempo + Loki, log redatti |

#### FR-043 — alias D43

**ID originale:** D43. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L69](../../AI-Project-Documentation-MVP-Plan.md#L69); versione e SHA-256 in [research.md](research.md#fonti).

| D43 | Test approfonditi con pipeline AI reali | Deterministici in PR, AI eval reali programmate/on-demand |

#### FR-044 — alias D44

**ID originale:** D44. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L70](../../AI-Project-Documentation-MVP-Plan.md#L70); versione e SHA-256 in [research.md](research.md#fonti).

| D44 | Concorrenza limitata per tipologia | Code generazione/embedding/reranking, quote provider, priorità prod |

#### FR-045 — alias D45

**ID originale:** D45. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L71](../../AI-Project-Documentation-MVP-Plan.md#L71); versione e SHA-256 in [research.md](research.md#fonti).

| D45 | Retry e checkpoint per fase | Recoverable, idempotenti, no applicazione implicita di proposte AI |

#### FR-046 — alias D46

**ID originale:** D46. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L72](../../AI-Project-Documentation-MVP-Plan.md#L72); versione e SHA-256 in [research.md](research.md#fonti).

| D46 | Archiviazione PM e cancellazione definitiva Admin | Cascata attivi/embedding; fonti condivise protette |

#### FR-047 — alias D47

**ID originale:** D47. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L73](../../AI-Project-Documentation-MVP-Plan.md#L73); versione e SHA-256 in [research.md](research.md#fonti).

| D47 | Concorrenza ottimistica per bozze e analisi | Token di revisione; HTTP 409 e confronto; niente realtime CRDT |

#### FR-048 — alias D48

**ID originale:** D48. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L74](../../AI-Project-Documentation-MVP-Plan.md#L74); versione e SHA-256 in [research.md](research.md#fonti).

| D48 | Autenticazione Identity + JWT, refresh token | JWT corto, refresh ruotati e revocabili, browser sicuro |

#### FR-049 — alias D49

**ID originale:** D49. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L80](../../AI-Project-Documentation-MVP-Plan.md#L80); versione e SHA-256 in [research.md](research.md#fonti).

| D49 | Limiti upload/storage configurabili dall'Admin | Default 50 MB/file, quote progetto/KB, limiti parser |

#### FR-050 — alias D50

**ID originale:** D50. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L81](../../AI-Project-Documentation-MVP-Plan.md#L81); versione e SHA-256 in [research.md](research.md#fonti).

| D50 | JSON schema dell'editor versionato | Migrazioni esplicite; revisioni storiche immutabili |

#### FR-051 — alias D51

**ID originale:** D51. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L82](../../AI-Project-Documentation-MVP-Plan.md#L82); versione e SHA-256 in [research.md](research.md#fonti).

| D51 | Angular + Bootstrap, possibile tema AdminLTE | Nessuna libreria generalista Material/PrimeNG/DevExtreme; Tiptap ammesso per editor |

#### FR-052 — alias D52

**ID originale:** D52. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L83](../../AI-Project-Documentation-MVP-Plan.md#L83); versione e SHA-256 in [research.md](research.md#fonti).

| D52 | Monorepo GitHub | Angular/.NET/Python/infra/docs/tests insieme |

#### FR-053 — alias D53

**ID originale:** D53. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L84](../../AI-Project-Documentation-MVP-Plan.md#L84); versione e SHA-256 in [research.md](research.md#fonti).

| D53 | Ambienti applicativi prod e test separati, DGX condiviso | Config/db/storage isolati; prod prioritario nel scheduler |

#### FR-054 — alias D54

**ID originale:** D54. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L85](../../AI-Project-Documentation-MVP-Plan.md#L85); versione e SHA-256 in [research.md](research.md#fonti).

| D54 | Rilasci incrementali | Primo vertical slice completo, poi hardening e avanzate |

#### FR-055 — alias D55

**ID originale:** D55. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L86](../../AI-Project-Documentation-MVP-Plan.md#L86); versione e SHA-256 in [research.md](research.md#fonti).

| D55 | Stack principale vincolato, secondarie via ADR/task | Nuove infrastrutture o dipendenze sostanziali richiedono approvazione |

#### FR-056 — alias D56

**ID originale:** D56. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L87](../../AI-Project-Documentation-MVP-Plan.md#L87); versione e SHA-256 in [research.md](research.md#fonti).

| D56 | Coding agent autonomo dentro milestone approvata | Stop al gate milestone o se scopre modifiche di scope sostanziali |

#### FR-057 — alias D57

**ID originale:** D57. **Stato originale:** Approvata.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L88](../../AI-Project-Documentation-MVP-Plan.md#L88); versione e SHA-256 in [research.md](research.md#fonti).

| D57 | Accettazione con pilota 2–3 PM per alcune settimane | Feedback strutturato, correzione difetti, gate umano finale |

#### FR-058 — alias A49

**ID originale:** A49. **Stato originale:** Approvata dopo le 48 decisioni.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L121-L131](../../AI-Project-Documentation-MVP-Plan.md#L121); versione e SHA-256 in [research.md](research.md#fonti).

## Decisione aggiuntiva A49 — Microsoft Agent Framework per le conversazioni (2026-10-14)

**Approvata dopo le 48 decisioni; non sostituisce alcuna decisione precedente.** L'interazione PM è *chat-first*: messaggi discorsivi e frammentari, inviati in momenti diversi, con allegati e domande guidate integrati nella stessa esperienza. Adottare **Microsoft Agent Framework (MAF)** per gestione della sessione conversazionale, composizione del contesto e compattazione. **Non** adottare per questo solo motivo un framework di orchestrazione multiagente o un workflow engine MAF.

- Usare inizialmente la strategia di summarization/compattazione standard di MAF, configurando soglie per finestra di contesto, output riservato, ultimi turni preservati e margine di sicurezza **per modello/fase**. Evitare strategie custom nell'MVP; predisporre sostituibilità futura mediante adapter/configurazione e API supportate dalla versione MAF selezionata.
- **Nessuno spike o benchmark dedicato a dimostrare il funzionamento della compattazione in M0.** La capacità del framework viene assunta disponibile. Rimangono i normali test di integrazione, persistenza, ripresa e rispetto del budget di contesto durante M1/M3 e i test reali del pilota.
- Lo **storico originale completo** dei messaggi (ruolo/autore, ordine, data, riferimenti allegati e versioni) è persistente in PostgreSQL e non viene distrutto dalla compattazione. Snapshot/sommari compattati sono derivati, versionati e sostituibili, non fonte di verità.
- **Memoria di progetto distinta:** fatti, requisiti, vincoli, negazioni, decisioni e risposte confermate mantengono riferimenti ai messaggi sorgente e non dipendono dalla qualità della sintesi. Prima della generazione del documento, i dati confermati sono reinseriti nel contesto entro budget. Se il budget non basta, segnalare e recuperare evidenze mirate invece di ometterle silenziosamente.
- I messaggi spontanei del PM si salvano subito, ma **non avviano analisi o nuovi round**: la decisione 9 (aggiornamento on-demand) rimane valida. La risposta dell'assistente in chat può guidare l'inserimento e visualizzare stati già elaborati; elaborazioni generative sempre in job persistenti (decisione 17).
- Versionare pacchetti MAF e adapter; verificare nomi/API concreti della release adottata durante l'implementazione. Escludere fallback a provider cloud, tool autonomi non autorizzati e log contenenti i messaggi completi.


#### FR-059 — alias A50

**ID originale:** A50. **Stato originale:** Confermata dal committente.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L136](../../AI-Project-Documentation-MVP-Plan.md#L136); versione e SHA-256 in [research.md](research.md#fonti).

| **A50** | Confermata dal committente | Chat conversazionale con domande mirate: l'assistente può rispondere e chiedere chiarimenti pertinenti, evitando interruzioni e ripetizioni. Le risposte AI sono job persistenti. **Solo «Analizza» consolida formalmente lo stato progettuale**, in continuità con D18. |

#### FR-060 — alias A51

**ID originale:** A51. **Stato originale:** Confermata dal committente.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L137](../../AI-Project-Documentation-MVP-Plan.md#L137); versione e SHA-256 in [research.md](research.md#fonti).

| **A51** | Confermata dal committente | Conferma selettiva dei requisiti critici/ambigui durante il consolidamento. Distingue affermazioni PM, fonti documentate, deduzioni e ipotesi. Nessuna conferma PM implica un'approvazione formale del cliente. Elementi non critici conservano stato e fonte anche se inseriti nella bozza. |

#### FR-061 — alias A52

**ID originale:** A52. **Stato originale:** Confermata dal committente.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L138](../../AI-Project-Documentation-MVP-Plan.md#L138); versione e SHA-256 in [research.md](research.md#fonti).

| **A52** | Confermata dal committente | Contraddizioni rese esplicite con affermazioni alternative, date e riferimenti verificati. **Il PM decide** la versione valida, se una precedente sia superata oppure se mantenere un punto aperto. Mai scegliere automaticamente per data o tipo di fonte. |

### Decisioni tecniche proposte, non requisiti approvati

#### A53

**Stato originale:** ADR tecnico proposto, da formalizzare in M0. Nessun alias FR.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L139](../../AI-Project-Documentation-MVP-Plan.md#L139); versione e SHA-256 in [research.md](research.md#fonti).

| **A53** | ADR tecnico proposto, da formalizzare in M0 | Retrieval conversazionale selettivo: interrogare la KB solo quando la risposta dipende da conoscenze esterne al dialogo; il comando «Analizza» esegue il retrieval necessario al consolidamento. Trigger, limiti e fallback misurabili. |

#### A54

**Stato originale:** ADR tecnico proposto, da formalizzare in M0. Nessun alias FR.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L140](../../AI-Project-Documentation-MVP-Plan.md#L140); versione e SHA-256 in [research.md](research.md#fonti).

| **A54** | ADR tecnico proposto, da formalizzare in M0 | Contesto ricostruibile indipendentemente dal modello: storico originale, fatti/decisioni, fonti e sessioni MAF separati; compattazione derivata, budget per modello/fase. La sostituzione del modello non altera gli originali e non promuove sintesi a requisiti. |

### Regola di autorita originale

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L142](../../AI-Project-Documentation-MVP-Plan.md#L142); versione e SHA-256 in [research.md](research.md#fonti).

**Regola di autorità:** `MESSAGGIO/FONTE` è evidenza storica; `CANDIDATO` è estrazione proposta; `CONFERMATO_PM` è convalida del PM; `APPROVATO_CLIENTE` è ammesso solo con specifica evidenza di approvazione del cliente; `SUPERATO` e `IN_CONFLITTO` conservano la storia. I nomi concreti degli enum saranno fissati nei contratti, senza costruire un requirements-management system generalista. I fatti non critici possono essere presentati come **non validati** senza obbligo di approvazione puntuale.

## User Scenarios & Testing

US1-US9 sono alias dei nove passi originali, non nuove user story. Priorita P1/P2/P3, test indipendenti e scenari Given/When/Then non sono definiti e non vengono inventati. AC, gate e must pass originali sono in tasks.md.

### US1 — alias passo 1

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L235](../../AI-Project-Documentation-MVP-Plan.md#L235); versione e SHA-256 in [research.md](research.md#fonti).

1. **Crea progetto e conversazione**: cliente, prodotti e loro versioni facoltative, template, modello per fase (override facoltativo), policy web, descrizione, file; la chat conserva cronologicamente input frammentari, allegati e risposte.

### US2 — alias passo 2

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L236](../../AI-Project-Documentation-MVP-Plan.md#L236); versione e SHA-256 in [research.md](research.md#fonti).

2. **Indicizza allegati** in background, mostra avanzamento; blocca analysis run se le fonti richieste sono ancora in parsing, consentendo esplicita esclusione.

### US3 — alias passo 3

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L237](../../AI-Project-Documentation-MVP-Plan.md#L237); versione e SHA-256 in [research.md](research.md#fonti).

3. **Analizza su richiesta**: mentre la chat può rispondere e porre domande contestuali, soltanto questa azione deduce e consolida candidati di fatti/requisiti/assunzioni/conflitti, RAG ed eventuale web approvato o automatico controllato. Requisiti critici e ambigui richiedono conferma PM.

### US4 — alias passo 4

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L238](../../AI-Project-Documentation-MVP-Plan.md#L238); versione e SHA-256 in [research.md](research.md#fonti).

4. **Conflitti e chiarimenti**: mostra alternative incompatibili e fonti, consente risoluzione/rinvio espliciti; massimo cinque domande per gruppo, priority `BLOCKING/IMPORTANT/OPTIONAL`, status `OPEN/ANSWERED/DEFERRED/N_A`; risposte e motivazioni persistenti. Nuovo round solo quando richiesto.

### US5 — alias passo 5

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L239](../../AI-Project-Documentation-MVP-Plan.md#L239); versione e SHA-256 in [research.md](research.md#fonti).

5. **Bozza**: distingue requisiti confermati, non validati e conflitti aperti con fonti; template selezionato, documento JSON strutturato, blocchi con stabile `sectionId`, fonti per affermazione; piano minimo: macro-attività e dipendenze, nessuna stima.

### US6 — alias passo 6

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L240](../../AI-Project-Documentation-MVP-Plan.md#L240); versione e SHA-256 in [research.md](research.md#fonti).

6. **Review AI automatica**: finding per sezione con severity, supporto fonti e stato; non corregge automaticamente.

### US7 — alias passo 7

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L241](../../AI-Project-Documentation-MVP-Plan.md#L241); versione e SHA-256 in [research.md](research.md#fonti).

7. **Editor visuale** Angular + Bootstrap + Tiptap/ProseMirror o equivalente approvato: WYSIWYG; propone revisioni AI delle sezioni con diff prima/dopo e `accept/reject` guardato da revision token; aggiornamento selettivo dopo nuove informazioni solo su richiesta.

### US8 — alias passo 8

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L242](../../AI-Project-Documentation-MVP-Plan.md#L242); versione e SHA-256 in [research.md](research.md#fonti).

8. **Esporta** `.md/.pdf/.docx` dal JSON canonico con opzione citazioni sì/no; layout titoli/elenchi/tabelle/link consistente; conservare revisioni e schemaVersion.

### US9 — alias passo 9

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L243](../../AI-Project-Documentation-MVP-Plan.md#L243); versione e SHA-256 in [research.md](research.md#fonti).

9. **Archivia/elimina**: PM archivia; Admin elimina dati attivi del progetto (incluse embedding/project-only), senza cancellare fonti condivise; backup scadono e devono gestire tombstone di cancellazione durante restore.

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L245](../../AI-Project-Documentation-MVP-Plan.md#L245); versione e SHA-256 in [research.md](research.md#fonti).

**UI admin:** utenti e ruoli, KB proposta/approvata/versioni, provider/model registry, prompt editor versionato, policy/quote, backup/restore con executor esterno e audit, dashboard Grafana consultabile in modo sicuro. Non serve suite generalista di componenti; Bootstrap 5 è vincolante e AdminLTE opzionale.

### Key Entities

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L176-L181](../../AI-Project-Documentation-MVP-Plan.md#L176); versione e SHA-256 in [research.md](research.md#fonti).

### 4.1 Entità minime (da affinare in migration design)

`Users/Roles/RefreshSessions`, `Customers`, `Products`, `ProjectProducts` (versione opzionale), `Projects`, `ProjectParticipants` **non necessario per ACL**, `DocumentSources`, `DocumentVersions`, `ApprovalDecisions`, `ParsedArtifacts`, `DocumentSections`, `Chunks` (parentId, headingPath, pageLocator), `EmbeddingIndexGeneration`, `Embeddings`, `ProjectSourceExclusions`, `SourceReferences`, `Conversations`, `ConversationMessages`, `ConversationSessions`, `CompactionSnapshots`, `ProjectFacts`, `Requirements`, `RequirementConfirmations`, `RequirementConflicts`, `ConflictResolutions`, `ClarificationRounds`, `ClarificationQuestions`, `ClarificationAnswers`, `AnalysisRuns`, `StageCheckpoints`, `AiJobs`, `ModelProviders`, `ModelProfiles`, `StageAssignments`, `PromptTemplates`, `PromptVersions`, `DocumentDrafts`, `DocumentRevisions`, `SectionRevisions`, `AiRevisionProposals`, `DocumentReviewFindings`, `PublicWebSources`, `WebImports`, `BackupJobs`, `AuditEvents`.

Non costruire un requirements management system completo: entità semplici per conservare dati e tracciabilità. Impostare chiavi, versioning, unique constraints e deduplication semantica dei risultati LLM per evitare duplicati.


## Success Criteria

SC aliasa integralmente i gate M0-M6, inclusi criteri tecnici e accettazione umana. Non introduce metriche o soglie. Gli outcome del pilota M6 vanno distinti dai criteri che richiedono lavoro implementabile.

### SC-001 — alias Gate M0

**Provenienza:** [AI-Project-Documentation-Tasks.md:L34](../../AI-Project-Documentation-Tasks.md#L34); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M0:** Docling tratta 4 formati; almeno una analisi campione su modello locale produce output JSON validato; RAG minimale prova citazioni; stack scelto compatibile e senza servizi AI cloud. Se uno di questi fallisce, stop e ADR correttivo.

### SC-002 — alias Gate M1

**Provenienza:** [AI-Project-Documentation-Tasks.md:L50](../../AI-Project-Documentation-Tasks.md#L50); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M1:** login, CRUD progetto e upload persistente end-to-end in ambiente test; accesso Admin/PM verificato; un job può sopravvivere al riavvio del worker.

### SC-003 — alias Gate M2

**Provenienza:** [AI-Project-Documentation-Tasks.md:L66](../../AI-Project-Documentation-Tasks.md#L66); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M2:** il retrieval restituisce fonti/locator corretti da KB di prodotto e cliente senza attraversare esclusioni; test di cambio modello non restituisce vettori incompatibili. Baseline RAG vs avanzato misurata.

### SC-004 — alias Gate M3

**Provenienza:** [AI-Project-Documentation-Tasks.md:L85](../../AI-Project-Documentation-Tasks.md#L85); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M3:** PM inserisce note frammentarie in chat, riceve chiarimenti mirati senza attivare analisi formale, con storico persistente e compattazione standard; requisiti critici passano per conferma PM; contraddizioni sono visibili e risolvibili o differibili; analisi parte solo su richiesta. PM lancia analisi reale e risponde; web pubblica controllata; progetto con ERP non conosciuto produce chiarimenti, non inventa integrazione API. Disponibile dimostrazione in ambiente test.

### SC-005 — alias Gate M4

**Provenienza:** [AI-Project-Documentation-Tasks.md:L101](../../AI-Project-Documentation-Tasks.md#L101); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M4:** **primo rilascio pilota-funzionale** a PM selezionati in test (non ancora production-ready): ciclo completo input->chiarimenti->bozza->revisioni->export, con verifiche sicurezza di base.

### SC-006 — alias Gate M5

**Provenienza:** [AI-Project-Documentation-Tasks.md:L116](../../AI-Project-Documentation-Tasks.md#L116); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M5:** hardening e restore testati, dashboard visibile, nessun flusso di esfiltrazione dei contenuti verso provider AI esterni, pipeline di rilascio controllata.

### SC-007 — alias Gate M6

**Provenienza:** [AI-Project-Documentation-Tasks.md:L128](../../AI-Project-Documentation-Tasks.md#L128); versione e SHA-256 in [research.md](research.md#fonti).

**Gate M6 / Definition of Ready for operational rollout:** alcuni progetti reali attraversano il ciclo fino a DOCX/PDF/MD; PM e tecnico attestano utilità; difetti bloccanti risolti; restore e policy di egress verificati. Il pilota dura **alcune settimane** con 2–3 PM; non confondere le 50 h di supporto/sviluppo con il tempo di calendario dei PM.

## Testing e quality gates originali

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L260-L267](../../AI-Project-Documentation-MVP-Plan.md#L260); versione e SHA-256 in [research.md](research.md#fonti).

## 9. Testing e quality gates

Test livelli: chat lunga multi-sessione, risposte AI mirate non ripetitive, correzioni tardive, negazioni, conferme selettive, conflitti non risolti, passaggio di modello e compattazione senza perdita degli originali; unit .NET/Angular/Python; integrazione DB/pgvector/Docling; contratti llama.cpp e provider sostitutivi; sicurezza auth/SSRF/upload/prompt injection; E2E web; conversion golden MD/PDF/DOCX; recovery job, restore, concurrency 409; OpenTelemetry scrubbing; RAG evaluation (Recall@k, MRR/nDCG dove applicabile, citazioni corrette), LLM evaluation (domande utili, omissioni, non-invenzione), prestazioni e concorrenza DGX.

Dataset versionato: almeno cinque scenari funzionali (modifica semplice, ERP noto, ERP ignoto, conflitto fra note, note frammentarie). Per benchmark più robusti usare 15–20 documenti e 40–60 query con gold references, in forma anonimizzata/autorizzata. I test LLM reali non bloccano le PR su differenze stilistiche; gate metriche su soglie preventivamente documentate e valutazione umana.

**Pilota approvato:** 2–3 PM per alcune settimane su casi operativi reali, con feedback strutturato e almeno un revisore tecnico/delivery. L'accettazione finale dipende dall'utilità effettiva, non solo da test passing. Durata/partecipanti da fissare nel kick-off del pilota, non inventare date.


## Export e revisioni originali

**Provenienza:** [AI-Project-Documentation-MVP-Plan.md:L247-L250](../../AI-Project-Documentation-MVP-Plan.md#L247); versione e SHA-256 in [research.md](research.md#fonti).

## 7. Export e revisioni

Documento canonico JSON strutturato, `schemaVersion`, versioni immutabili e migrazioni su copie (mai sovrascrivere lo storico). Definire subset editor supportato: titoli H1-H4, paragrafi, bold/italic, tabelle, link, elenchi, blockquote, code block, citazioni/metadata; rifiutare feature non convertibili con perdita silenziosa. Export MD, PDF, DOCX con golden fixtures e controlli di round trip semanticamente significativi. Editor HTML non è il formato persistito canonico.


## Edge Cases e informazioni mancanti

Nessuna sezione autonoma Edge Cases nelle fonti. Casi limite e rischi sono preservati nelle sezioni 4-9/12 del piano integrale e nei must pass del backlog. Soglie quantitative, classificazione operativa di criticita/ambiguita, versioni precise, percorsi applicativi e contratti definitivi sono mancanti o incompleti. Nessun valore aggiunto; anomalie in research.md.

## Assumptions

Nessuna nuova assunzione. Il registro Q1-Q48 originale e preservato in plan.md e non e una corrispondenza numerica con D.
