# Contratto parser interno — document-parse-1

Nuovo servizio privato Python/Docling già previsto dal piano MVP. Non è accessibile al browser, non possiede job persistenti, non legge DB e non riceve credenziali DGX. Backend unico proprietario di autorizzazione, originali, lease, retry e stato.

## Operazioni

GET `/health` → disponibilità locale; GET `/version` → schema document-parse-1, Docling e docling-core version, config/model-artifact hash, formati ammessi. Nessuna informazione sensibile. POST `/convert` sincrono, una conversione per istanza; chiamante backend autenticato con segreto interno e rete Compose privata, nessuna porta pubblicata. Backend limita byte/timeout prima dell'invio. Richiesta multipart: file bytes, declaredFormat pdf/docx/md/txt, documentVersionId, originalSha256, configHash, limiti massimi di byte espansi/pagine/blocchi/output e timeout (non superiori a quelli server). Nessun path o URL sorgente ammesso.

Busy → 429 con attesa; config non compatibile → 409 parser_config_mismatch; formato/byte incoerenti → 415; input protetto/corrotto → 422; limite/timeout → 422 parse_limit_exceeded/parse_timeout; crash → 503. I codici sono trasferiti al job dal backend con classificazione transitorio/permanente e diagnostica redatta. Il parser non marca mai un job Completed autonomamente.

## Manifest di risposta

200 significa conversione terminata, non necessariamente documento Ready. Body:

- schemaVersion=document-parse-1, originalSha256, parserVersion, dependencyLockHash, modelArtifactsHash, configHash.
- status Complete/Incomplete, originFormat, originalByteLength; pageCount nullable per formati senza paginazione.
- `document`: rappresentazione DoclingDocument originale o equivalente testuale lossless per TXT, con versione serializzazione esplicita.
- `blocks[]`: ref stabile nell'artefatto, ordine, parentRef nullable, tipo heading/paragraph/list/table/code/picture/other, testo canonico, headingPath, locator con page/bbox/coordinateSystem nullable e righe sorgente ove disponibili; tabelle con coordinate righe/colonne/span/header e testo celle.
- `inventory[]`: ogni pagina/item/container informativo ispezionato con stato represented/unprocessed e blockRefs/issueRefs; nessuna immagine informativa sparisce perché OCR è disabilitato.
- `chunks[]`: gruppi strutturali proposti da HybridChunker, riferimenti ai blocchi e range, contesto aggiunto distinto dal testo originario. TokenCount facoltativo con tokenizerId e source estimated: non spacciarlo per misura del runtime DGX.
- `issues[]`: code, severity blocking/warning, page/ref/range ove disponibile, reason non sensibile, affectsCompleteness. Se un elemento potenzialmente informativo non è trattato, affectsCompleteness=true e status=Incomplete.

Backend valida hash originale, limiti, unicità refs, ordinamento, parent senza cicli, range nei testi, riferimenti chunks→blocks, celle e totale inventario. Persistenza del manifest e creazione delle porzioni solo sotto fence corrente. Conversione 200 con inventario incompleto non produce fonte Ready. Il backend calcola hash canonici propri e riconta token sul runtime, senza fidarsi del limite proposto dal chunker.

## Fedeltà e locator

PDF: Docling con OCR off, table structure on, niente VLM o picture description. Conservare pagina e bbox native e riferimenti agli item. Pagina senza testo/immagine potenzialmente informativa è issue; una pagina realmente vuota può essere marcata empty con motivazione. Non equiparare ogni decorazione a informazione persa: distinzione layout/decorazione registrata e validata sulle fixture, senza promettere riconoscimento semantico infallibile.

DOCX: testo, ordine, titoli e celle con percorso strutturale; page=null se non stabile, mai pagine simulate. Relazioni esterne/embedded non eseguite o recuperate; embedded informativi non trattati producono issue.

Markdown: originale, gerarchia, liste, tabelle, code fences e link testuali conservati; nessun fetch di immagini/link. Mapping al sorgente per righe/range. TXT: nessun significato di heading inventato, righe e paragrafi conservati, codifica UTF-8 o UTF-16 con BOM validata senza replacement silenziosi. Link e istruzioni restano testo non fidato.

Docling e i parser derivati non garantiscono fedeltà assoluta a qualsiasi file: golden e revisione di passaggi misurano la capacità supportata. Un locator cita il testo estratto e consente di aprire/downloadare l'originale alla posizione disponibile; non pretende offset binario nel PDF/DOCX. UTF-16 verificato dal backend sui testi canonici, per non confondere codepoint Python e unità .NET.

## Operatività e sicurezza

Modelli/parser caricati da artefatti locali con hash, mai download alla conversione. OCR/servizi remoti/plugin esterni disabilitati, rete senza egress come barriera effettiva. Root filesystem read-only, utente non root, tmpfs con limite, un processo figlio per conversione, RAM 4 GiB/CPU 2/pids limit; timeout parent con kill dell'intero gruppo. Archivio DOCX limitato per entry count, dimensione decompressa (250 MB iniziali) e traversal; nessuna estrazione di path assoluti o collegamenti esterni. Rifiutare formati macro-enabled non ammessi.

Risposta/output massimo 250 MB configurabile; interrompere con issue/errore esplicito prima di esaurire spazio, niente JSON troncato presentato come valido. Cancellare solo temporanei del proprio request workspace; non ha mount originali persistenti. Se backend muore, conversione può terminare ma il risultato non ha autorità: nuova richiesta è ripetibile. Nessuna coda o protocollo di recovery indipendente nel parser.

## Durata, proprietà e passaggi compositi (F1/F5/F8)

Il timeout della conversione decorre dall'avvio effettivo del subprocess, non dal caricamento utente; backend passa il minimo fra ParseTimeoutSeconds (iniziale 300 s), budget del tentativo e residuo della deadline effettiva del root. Trasporto con al massimo 5 s di cleanup aggiuntivi, mai oltre la deadline root. Heartbeat/lease della richiesta rimane nel backend: un rinnovo non prolunga timeout o deadline. Perdita lease/cancellazione del backend tenta di terminare il subprocess; un risultato tardivo non può essere committato da owner scaduto. Il parser non rinnova lease, non avvia retry e non scrive stato persistente. Errori/429/timeout sono interpretati dal backend entro budget e massimo tentativi congelati, non dalla libreria con retry indipendenti.

L'unità di provenienza è il range del blocco originale, non il chunk esportato. Ogni chunks.ranges specifica `{rangeKey,blockRef,start,end,ownership:Primary|Context}` con convenzione di offset dichiarata (`unicode-codepoint` nel parser, convertita e verificata UTF-16 dal backend) e hash del testo sorgente. Un header ripetuto usa lo stesso blockRef e range dell'originale in ogni gruppo; nessuna copia con nuova origine. Testo contestualizzato del chunk è derivato e non citabile da solo. Frasi, righe e tabelle su pagine diverse sono più ranges ordinati, mai una singola quote attribuita a una pagina. Il backend assegna SourceId/CitationGroupId e verifica quote sui testi immutabili secondo AI §2–3.

Il manifest versione document-parse-1 mantiene questi campi come parte del contratto nuovo ancora da implementare; non esiste una release runtime già pubblicata da migrare. Il nuovo confine è formalizzato in [ADR-0001](../../../docs/adr/0001-parser-docling-stateless.md). La conversione resta ripetibile e senza stato autorevole; qualsiasi stato Ready/Incomplete/Failed, preparazione candidata/corrente e pubblicazione Evidence appartiene soltanto al backend.
