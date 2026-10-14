# Documentation audit — Spec Kit 1.2.0

Workflow separato: `.specify/workflows/documentation-audit.yml`. Eseguire dalla radice
del progetto usando il percorso YAML, senza installarlo nel registry e senza avviare
`speckit`. I companion file in questa directory sono parte del workflow: copiarli
insieme al YAML se lo si riusa altrove. Nessun report dell'audit reale e generato
durante la creazione o validazione del workflow.

## Avvio manuale

Prerequisiti: CLI `specify` 1.2.0, PowerShell 7 (`pwsh`), integrazione Codex installata,
eseguibile Codex raggiungibile dal dispatcher e autenticazione/provider configurati.
Codex deve poter leggere la baseline e scrivere i rapporti nella nuova directory audit.
La configurazione di modello/provider e quella esistente di Codex; il YAML non la cambia.
Prima dell'avvio verificare che il trattamento dei documenti con tale provider sia autorizzato.
Non eseguire altri lavori sul progetto durante l'audit o mentre il gate e in pausa.

```powershell
specify workflow run .specify/workflows/documentation-audit.yml
```

Lasciare `human_verdict` vuoto all'avvio. Al gate leggere **tutti** i report, anche
quando `show_file` visualizza soltanto le prime 200 righe del consolidamento. `approve`
accetta esclusivamente il rapporto; `reject` interrompe il workflow e conserva i report.
La scelta di rifiuto resta nei checkpoint, senza report 07 di approvazione.

In terminale non interattivo il gate si mette in pausa. Usare il run ID comunicato
dal motore; non confonderlo con il nome della directory audit:

```powershell
specify workflow status <run-id>
specify workflow resume <run-id>
# Solo dopo una reale decisione del revisore, per una ripresa non interattiva:
specify workflow resume <run-id> --input human_verdict=approve
# Oppure:
specify workflow resume <run-id> --input human_verdict=reject
```

Non predisporre `human_verdict=approve` all'avvio in automazione. Il gate non autentica
il revisore e la CLI permette di fornire il verdetto come input: la decisione deve
provenire da una persona. Non c'e una proprieta YAML che imponga tale autenticazione.
Se la baseline cambia durante la pausa, il controllo successivo all'approvazione fallisce.

## Output e protezioni

Ogni esecuzione crea `audits/documentation-audit/<UTC>-<GUID>/` con report distinti:

- `00-baseline-integrity.md`: sette hash fissati, inclusa la costituzione.
- `01-requirements-consistency.md`: inventario e copertura bidirezionale.
- `02-architecture-review.md`: decisioni, contratti, rischi e governance.
- `03-task-dependencies.md`: dipendenze, gate, cicli, somme e ipotesi.
- `04-ai-task-readiness.md`: una riga per ogni task originale, con stato di readiness.
- `05-consolidated-findings.md`: registro classificato e domande al revisore.
- `06-final-checks.md`: presenza, SHA-256 e integrita prima del gate.
- `07-human-review.md`: accettazione dell'audit e ricontrollo dopo il gate.

I JSON `project-baseline.json` e `report-manifest.json` sono metadati di controllo,
non nuovi piani o task. Il motore salva inoltre i checkpoint obbligatori in
`.specify/workflows/runs/<run-id>/`. Il workflow non modifica registry o feature attiva.

`baseline.lock.json` blocca la baseline validata il 2026-10-14: sorgenti, quattro
proiezioni e costituzione. Una differenza blocca l'avvio senza aggiornamenti automatici.
Per riusare su una baseline diversa occorrono una validazione umana separata e un nuovo
lock autorizzato; non cambiare il lock soltanto per far passare il controllo.

Lo snapshot iniziale protegge anche tutti gli altri file del progetto, inclusi file
non tracciati, salvo internals `.git`, checkpoint del motore e sola directory audit
della sessione. Dopo ogni fase verifica aggiunte, rimozioni e cambiamenti dei byte.
I link/reparse point nel perimetro protetto sono rifiutati; i documenti non vengono
resettati, cancellati o ripristinati automaticamente. Le alterazioni rilevate producono
un report di fallimento nella directory audit e interrompono la sequenza.

## Compatibilita e limiti verificati nel codice locale

Fonte primaria: workflow ufficiale `.specify/workflows/speckit/workflow.yml` e package
installato `specify_cli/workflows/engine.py`, `base.py`, `expressions.py`, istruzioni
e validatori `step/prompt`, `step/shell`, `step/gate`, integrazione `integrations/codex`.
Usati solo schema `1.0`, header workflow, requires advisory, input string,
step `shell`/`prompt`/`gate`, timeout, output_format JSON e le espressioni supportate.

- Il prompt invia un testo libero a `codex exec`; non invoca comandi Spec Kit.
  Il motore non cattura il testo completo della risposta come artefatto: Codex deve
  scrivere i report, poi la shell ne verifica presenza e struttura minima.
- `requires.speckit_version` e `requires.integrations` sono advisory: il primo
  controllo shell verifica CLI e installazione locali effettive.
- Non esistono nel workflow permessi di scrittura o sandbox per path. Le istruzioni
  e i controlli SHA-256 rilevano violazioni ma non impediscono materialmente una
  scrittura. Per impedimento a livello OS occorre configurare separatamente una
  sandbox/ACL con baseline in sola lettura e directory audit scrivibile. Nessuna
  proprieta `permissions`, `read_only`, `output_file` o `sandbox` e stata inventata.
- Gli step shell usano i privilegi dell'operatore. La verifica tutela contenuti e
  presenza dei file, non ACL, identita del revisore, dati esterni o internals Git.
- Le verifiche meccaniche non attestano correttezza semantica dei report. Il gate
  umano rimane necessario; il workflow non certifica l'eseguibilita del software.
- Nessuna ricerca online, prova target o implementazione: compatibilita, licenze,
  capacita e prestazioni senza evidenza sono informazioni non verificabili.
- `speckit-analyze` non e usato: `tasks.md` non soddisfa la sua precondizione
  procedurale. Nessuna skill di generazione/implementazione ne hook e invocata.

## Validazione senza esecuzione

Non esiste un sottocomando `workflow validate` nella CLI locale. Lo script seguente
usa `WorkflowEngine.load_workflow/validate` e i validatori nativi degli step, controlla
espressioni, dispatch Codex e hash, poi testa solo le guardie in fixture temporanee:
report mancanti, readiness incompleta, modifiche alla baseline, file fuori perimetro,
snapshot alterato, report alterati durante revisione e percorso di successo.
Non esegue `WorkflowEngine.execute/resume`, agenti o workflow reali.

```powershell
& "$env:APPDATA/uv/tools/specify-cli/Scripts/python.exe" -B .specify/workflows/documentation-audit-assets/validate-workflow.py
```

Se Spec Kit e installato altrove, usare il Python del suo ambiente, con il package
`specify_cli` 1.2.0 gia disponibile. Nessuna dipendenza viene installata dallo script.
