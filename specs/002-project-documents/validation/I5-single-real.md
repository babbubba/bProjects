# I5 — T082 (documento singolo oltre contesto): NOT VERIFIED / BLOCKED

**Stato**: test IMPLEMENTED (compila, discovery verificato, risulta *Ignorato* senza opt-in); prova DGX **NOT VERIFIED**, causa **BLOCKED**.
**Motivo**: alla verifica (2026-11-07, due controlli di sola lettura su `/v1/models`) tutti i modelli del router DGX risultano `unloaded`, incluso `Qwen3.6-35B-Smart` (ctx 131072). Il mandato vieta di caricare/sostituire modelli sul servizio condiviso e di fare warm-up; un'inferenza provocherebbe l'autoload del router, quindi non è stata eseguita. Nessuna chiamata di inferenza fatta.
**Verifiche offline eseguite** (senza DGX): generatore deterministico (seed 20261106, hash registrati), parser Docling reale, run v4 con modello finto su PostgreSQL che entra nel ramo gerarchico; DgxGuardTests 12/12.
**Non registrato** (non eseguito): modello/finestra effettivi, budget effettivo, numero chiamate, runtime, esiti semantici e citazioni.
**Comando** (da `backend/`, solo con modello già caricato e autorizzazione): `BPROJECTS_TEST_DGX_ALLOW=1 dotnet test --solution bProjects.sln --no-build --filter-method "*T082_a_single_large_document*"`
**Limite noto**: il ramo gerarchico dipende dal numero di evidenze estratte; il corpus include regole secondarie applicabili per superare il budget del payload.
