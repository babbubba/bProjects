# Guida operativa alla demo (002, documenti piccoli) — 2026-11-03

Nessun segreto in questo file: utente e password demo sono in `infra/.env` (ignorato da Git, chiavi `DEMO_PM_USER` / `DEMO_PM_PASSWORD`).

## URL e servizi
- Applicazione: <http://localhost:8080/> (servizio `web`; le API sono sotto `/api`).
- Stack: `docker compose -f infra/compose.yaml --profile parser up -d --build` (db, app, parser Docling su rete interna, web).
- Verifica: `docker compose -f infra/compose.yaml --profile parser ps` → `db`, `app`, `parser` **healthy**, `web` Up; `curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/` → 200.
- DGX: `GET http://192.168.253.18:8080/props?model=Qwen3.6-35B-Smart` (header Bearer con `AI_API_KEY`) deve riportare `n_ctx` 131072 (deve coincidere con `AI_CONTEXT_WINDOW_TOKENS`, altrimenti `context_window_mismatch`).
- Primo avvio su DB nuovo (una tantum): `docker compose -f infra/compose.yaml run --rm app migrate`, poi `bootstrap-accounts` (vedi CLAUDE.md); la password richiede almeno un carattere non alfanumerico.

## Modello e configurazione (in `infra/.env`)
- Modello: **Qwen3.6-35B-Smart**, finestra 131072, un solo slot (una inferenza alla volta). Non usare Qwen3.8 (peggiore nelle prove).
- Analisi: reasoning OFF, output 16384 (`AI_ANALYSIS_ENABLE_THINKING=false`, `AI_ANALYSIS_MAX_OUTPUT_TOKENS=16384`).
- `AI_DOCUMENT_REQUEST_TIMEOUT_SECONDS=120`: necessario perché il retry guidato della bozza (T129) parta. Il run Ordinary ha deadline 180 s; con il timeout di default (150 s) il retry non parte se la prima chiamata dura più di ~30 s.

## Percorso della demo
1. Accedi con l'utente demo → **Nuovo progetto**.
2. Carica i documenti (fixture in `tests/fixtures/documents/`: `pdf-text.pdf`, `docx-basic.docx`, `md-basic.md`, `txt-negations.txt`). Attendi che tutti siano **Pronto** (parsing Docling).
3. **Chat**: chiedi dei requisiti; la risposta cita fonti `F1…` apribili (passaggio con range esatto).
4. **Analizza** (circa 1–3 min): compare l'anteprima con le voci, ciascuna con «Vedi nei documenti».
5. **Correggi** una voce (es. «24 mesi») → Salva (nuova revisione), poi **Conferma** (non genera).
6. **Genera** (circa 1–2 min, con retry automatico se il modello omette voci): appare «Bozza AI — da revisionare», stato Proposta / non approvata, con citazioni cliccabili.

Se l'analisi o la generazione falliscono con «output non valido» è varianza del modello (contratto non allentato): riprovare **Analizza/Genera**.

## Limitazioni note
- Varianza tra run: l'analisi ha fallito 1 volta su 3 per chiavi locali duplicate. N1 backend PASS; E2E browser reale: FAIL sull'asserzione testuale «ordini annullati ESCLUSI» (l'esclusione è una voce di ambito escluso senza parola negativa).
- Analizza è sempre Extended `workload_unknown` (T124); `recoveryMs` null; `hasSourceChanges` non esposto.
- Una sola inferenza DGX alla volta: non lanciare test e demo in parallelo.
- Solo documenti piccoli (nessun documento oltre contesto: US2/I5 non avviato). Il parser è CPU, solo linux/amd64.
- Il retry della bozza è uno solo: se anche il secondo output omette voci, il run fallisce (per contratto, §9).
- Playwright reale (`E2E_MODEL_MODE=real`) ripete l'intero percorso e crea un nuovo progetto a ogni esecuzione.
