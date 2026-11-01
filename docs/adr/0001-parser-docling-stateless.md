# ADR-0001 — Parser Docling isolato senza stato autorevole

**Stato**: accettato dal committente nella decisione D3 per la Slice 002.
**Data**: 2026-11-01. **Ambito**: confine parser/backend, non ripianificazione del prodotto.

## Contesto e decisione

Il [piano MVP D07 e §3](../../AI-Project-Documentation-MVP-Plan.md) già approva Docling e un servizio Python separato. La scelta concreta di responsabilità è significativa ai sensi della [Constitution IX](../../.specify/memory/constitution.md). Il committente approva il parser isolato senza stato persistente autonomo.

Backend .NET unico proprietario di originali/versioni, selezioni e provenienza, job, tentativi, lease/fencing/retry, checkpoint, validazione e pubblicazione dei risultati. Il parser converte bytes in contenuto strutturato e manifest con problemi/localizzazioni. Interfaccia sincrona interna e processo figlio limitato per conversione; nessun DB, coda durevole, LLM, mount degli originali o accesso browser nel parser. MAF e orchestration AI rimangono nel backend esistente.

## Alternative considerate

Worker Python che acquisisce direttamente lavori persistenti: separa l'esecuzione da una chiamata interna ma distribuisce accesso DB/storage, recovery e compatibilità dello schema fra due runtime. Respinto per accoppiamento e duplicazione di responsabilità. Parser incorporato nel processo .NET: non scelto, indebolisce isolamento dei file non fidati e contrasta col servizio Python già approvato. Nessun ulteriore servizio/broker introdotto.

## Conseguenze operative e recupero

Parser CPU offline sul server applicativo, non sul DGX; modelli locali versionati/licenziati, no egress/plugin/OCR remoto, risorse/tempo/tmp limitati e porta soltanto interna. Stato Ready o Incomplete è deciso esclusivamente dal backend dopo validazione del manifest e copertura. Errori e timeout sono restituiti senza contenuti sensibili; solo il backend classifica retry entro deadline padre e massimo tentativi.

Dopo crash può essere necessario ripetere la conversione non acquisita; è un costo accettato per mantenere un solo proprietario della durabilità. Un risultato tardivo non dà autorità a un worker scaduto: il commit backend richiede fence/lease/deadline validi. Output già persistiti compatibili sono riutilizzabili senza nuovi originali o versioni duplicate. Nessuna promessa exactly-once del calcolo; unicità degli effetti e originali persistenti sì.

Dettagli operativi nei [contratti parser](../../specs/002-project-documents/contracts/parser.md) e [piano 002](../../specs/002-project-documents/plan.md); prove reali ancora da eseguire. Questo ADR non accetta runtime né amplia la 002 a KB/RAG/OCR generalizzato.
