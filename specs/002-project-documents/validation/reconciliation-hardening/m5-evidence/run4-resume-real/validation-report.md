# Validazione strutturale

## Esito dei batch
74 batch: 73 con output validato e committato, 1 respinto (`expand`, ordinale 73). Nessun output committato è stato modificato; nessuna violazione sugli altri 72 batch generati/recuperati.

## L'unica violazione (permanente, fail-closed)
- Batch: `expand` ordinale 73, view A, input `5a67ee9b15dd5bad9b204c58a8c12969a22be33189f84138fc4b13156b59ab00`, chiamata reale n. 43 (`finish_reason=stop`, 1.809 token di completion, 31,5 s).
- Messaggio del motore: «Output del batch … non valido (1 violazioni): `candidates[13].right`: alias E109 sconosciuto o fuori dal batch.»
- **Causa verificata sui file grezzi** (`run\raw\0043-expand-request.json` / `-response.json`, fuori da Git): il catalogo del messaggio contiene 42 alias `E<n>` (da E3 a E194) e **E109 non è tra questi**; il modello ha restituito 16 candidati e il quattordicesimo (`f14`, `DependsOn`, `E59` → `E109`) cita E109. La sua stessa motivazione ammette «E109 (non presente nel batch …)». È un'invenzione dell'alias da parte del modello, non un difetto del composer né del contratto: la validazione ha fatto il suo lavoro.
- Conseguenza: in v11 un singolo batch `expand` non valido termina la run (`work_output_invalid`); per scelta di prodotto non c'è retry LLM e la run non salta il batch. Gli output già committati e il checkpoint della risposta rifiutata restano nel database.
- Tracciabilità: la risposta grezza è conservata a parte; `aiHash` e `canonicalHash` non sono stati alterati da alcuna normalizzazione.

## Controlli passati
Alias degli altri 15 `expand` e 27 `compare` validi; 0 risposte troncate; 0 `focus` anomali; i 29 `compare` `Compared` e i 10 `Open` rispettano `ReconcileFocusRule.ComparedOrOpen`; manifesto e `ManifestHash` coerenti su tutti i 73 output; 0 chiavi locali duplicate rifiutate (la correzione di 676177a ha retto: `expand 30` con `f13` ripetuta è stato accettato).

## Non verificato
Correttezza semantica dei candidati e dei confronti; fondatezza dei 10 `Open`; qualsiasi proprietà dello stadio Gap e della revisione finale (non raggiunti).
