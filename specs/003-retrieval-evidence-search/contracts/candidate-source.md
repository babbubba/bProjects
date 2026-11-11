# Contratto: candidati → stadio Gap (sorgente `retrieval`)

**Feature**: `003-retrieval-evidence-search` | **Data**: 2026-11-09 | Spec: FR-001–FR-003, FR-017–FR-025, FR-035 | Stato: disegno da verificare, nessuna prova runtime

## 1. Contratto d'ingresso dello stadio: INVARIATO

Il contratto è quello di [002 `ai-workflow.md` §13.7](../../002-project-documents/contracts/ai-workflow.md#137-stadio-gap-dedicato-su-ledger-completo-definizione-v6-d1-2026-11-09) e della sua implementazione (`GapStageInput`): **lista ordinata di evidenze candidate + `searchBasis` + `retrievalComplete`**, più i Gap e i riferimenti Open canonici. **La 003 non lo modifica e non lo copia**: sostituisce soltanto *chi produce la lista*.

| Elemento | Sorgente 002 (`ledger_exhaustive`) | Sorgente 003 (`retrieval`) |
|---|---|---|
| Candidati | intero ledger non-Gap in ordine di catalogo | evidenze risolte dai blocchi recuperati (vedi §2), ordine deterministico |
| `searchBasis` | `ledger_exhaustive` | `retrieval` |
| `retrievalComplete` | `true` | calcolato (§3) |
| Formato messaggio | `gap-stage-message-1` | **stesso** in M1–M3 (nessuna modifica); l’espansione ai padri (M4) richiederebbe un nuovo formato versionato e l’approvazione del committente |
| DTO, prompt, validatore, assembly | `gap-stage-alias-1`, `document-gap-v1`, `GapAssessmentRules`, §13.2 | **stessi** |
| Chiamate LLM dello stadio | una | una (il recupero non usa LLM) |
| `absenceProven` | calcolato (§13.7) | **sempre `false`** |

Nessuna interfaccia con una sola implementazione: le due sorgenti sono due funzioni che producono lo stesso record tipizzato. Se un test richiede una sostituzione, usa un test double del client HTTP dei modelli, non una nuova astrazione.

## 2. Da blocchi a evidenze (deterministico)

1. Ranking sui **blocchi figli** filtrati prima del ranking (progetto, versioni incluse dello snapshot, esclusioni PM).
2. Ogni blocco recuperato → le evidenze `Document` del ledger congelato con una fonte su quel `BlockId`; rango dell'evidenza = rango del suo blocco migliore; a pari rango, ordine di catalogo.
3. Blocchi senza evidenze: registrati (`no_evidence_for_block`), non candidati.
4. Evidenze `Message`/Declared non-Gap: incluse sempre (`always_included`, ordine canonico) per non perdere il lato PM di un conflitto (FR-006); se non entrano nel budget ⇒ `retrievalComplete = false`.
5. Unione fra Gap deduplicata; per ogni Gap il registro conserva il proprio ordine.
6. Pacchetto = discesa per rango finale a **passaggi interi** fino al budget di input dello stadio (`ContextBudget`); esclusi registrati per identificativo con motivo.

## 3. `retrievalComplete`

`true` **solo se** tutte le condizioni valgono; `false` se **una qualunque** è violata:

- nessun errore di servizio durante il recupero (embedding, ricerca, riordino);
- servizio di riordino disponibile (altrimenti modalità degradata, §5);
- nessun candidato entro `N_rerank` escluso per budget;
- nessuna evidenza `always_included` esclusa;
- indice `Ready` per tutte le versioni incluse dello snapshot, alla `IndexConfigHash` congelata.

`retrievalComplete` **non** è una prova di completezza semantica: indica soltanto che il procedimento dichiarato è stato eseguito per intero. **Mai** convertito in «assente» (FR-019, A-10). Con `searchBasis = retrieval` e Gap `NoAnswer` ⇒ «assenza non provata»; `absenceProven = false` sempre; `absenceProvenGaps = 0`.

## 4. Instradamento della definizione `project-analysis-documents-v7`

La v6 resta invariata (fail-fast `gap_ledger_exceeds_input` incluso). Per la v7 il routing di §13.7 è:

| Caso | Condizione | Comportamento |
|---|---|---|
| (a) single-call | come v6 | invariato |
| (b1-ledger) oltre contesto con ≥1 Gap, ledger entra nel budget dello stadio | misura `gap-stage-ledger-1` | sorgente `ledger_exhaustive` come v6 (**FR-002**: il recupero non interviene) |
| (b1-retrieval) oltre contesto con ≥1 Gap, ledger **non** entra | misura | recupero: se indice `Ready` e servizi in readiness ⇒ sorgente `retrieval`; altrimenti errore tipizzato a zero invii (§5) |
| (b2) oltre contesto senza Gap | come v6 | gerarchia v4-equivalente, nessuno stadio, nessun recupero |

La scelta (b1-ledger/b1-retrieval) è funzione deterministica di snapshot, misura e stato dell'indice, scritta nel manifest; ripresa e takeover tornano allo stesso esito. `gap_assessment_exceeds_output` e `MaxAiCallsPerRun` (gerarchia + 1) invariati.

### Chiavi di freeze nuove (solo definizioni v7)

Si **aggiungono** alle chiavi gerarchiche e `gap*` della v6 (che restano): `retrievalIndex` (`index-text-1` + `IndexConfigHash`), `retrievalPolicy` (`retrieval-policy-N`, `PolicyHash`), `retrievalRegistry` (`retrieval-registry-1`), `retrievalOutput` (`retrieval-output-1`), `retrievalModels` (nome+artefatto+dimensione dei due modelli). `ContractOf` restituisce 7 solo con l'insieme completo; la v6 rifiuta le chiavi `retrieval*`; combinazione mista ⇒ `workflow_incompatible`, mai reinterpretazione. Numero e nomi esatti sono **da fissare e verificare nel codice in T028** (la 002 ha 18 chiavi v6; il numero v7 è da calcolare, non assunto).

### 4.1 Definizione `project-analysis-documents-v8` (correzione T040 Run 2, 2026-11-11)

Decisione del committente dopo la chiamata 026 del Run 2 (foglia B con 139 `items`, rifiutata correttamente con `work_output_invalid`). v8 = v7 **identica** (routing, chiavi `gap*` e `retrieval*`, single-call, stadio Gap, recupero) più la chiave di freeze `reconcileSchemaProfile = reconcile-alias-schema-by-batch-1`. `ContractOf` restituisce 8 solo con tutte le chiavi v7 e quel valore; la chiave in v4–v7 è una combinazione mista (`workflow_incompatible`). Le **nuove** richieste con recupero congelano la v8; i run v7 esistenti restano v7 (nessuna conversione).

Nella gerarchia della v8 lo schema JSON inviato a ogni batch (`response_format`, grammatica del provider) è scelto da stadio e vista, con le stesse regole del compositore:

| Batch | Variante | Vincoli rispetto allo schema completo `reconcile-alias-1` (fissato) |
|---|---|---|
| foglia, vista A | `Full` | nessuno (schema invariato) |
| foglia vista B, riduzione, cross | `NoItems` | `items` e `patches` con `maxItems: 0`, `focus` di tipo `null` |
| espansione, confronto | `Candidate` | `items` con `maxItems: 0` |

Invariati: prompt `document-reconcile-v3`, DTO `reconcile-alias-1`, compositore e validatore (unica garanzia: lo schema è un aiuto alla generazione, non una validazione), WorkOutput e replay degli output `Committed`. Identità: il `configHash` della v8 aggiunge il profilo e gli hash delle tre varianti, il piano del manifest la chiave `reconcileSchemaProfile`; v4–v7 restano byte per byte invariati. Il Pack misura con lo schema completo (composizione dei batch invariata); `inputHash` è l'hash del payload realmente inviato, variante inclusa. Nessun retry.

## 5. Modalità degradata e errori tipizzati (D-3)

| Condizione | Esito del recupero | Esito del run | Invii LLM |
|---|---|---|---|
| Embedding non disponibile / modello o dimensione non conformi | errore tipizzato `embedding_unavailable` (o `embedding_model_mismatch`/`embedding_dimension_mismatch`) | `Failed`, causa tipizzata, azione utente | **0** |
| Indice non `Ready` | `retrieval_index_not_ready` | `Failed` (nessun fallback al ledger oltre budget) | **0** |
| Riordino non disponibile (embedding ok) | registro **RRF diagnostico**, `degraded = true`, `retrievalComplete = false`, `outcome = Diagnostic` | terminazione con causa tipizzata `reranker_unavailable`; **stadio Gap non invocato**; nessun `gapAssessments`; mai `Completed` | **0** |
| Entrambi ko | `embedding_unavailable` prevale | `Failed` | **0** |

In modalità degradata: nessun `AnsweredInSnapshot` definitivo, nessuna dichiarazione di assenza, nessuna attestazione di completezza dei conflitti, nessun risultato parziale presentato come valido; il registro diagnostico è consultabile e riporta la causa; **nessun aumento automatico di budget né cambio di modello**. La ripresa avviene con un nuovo run (o ripresa del run secondo la regola dell'errore) a servizio ripristinato.

*Lettura conservativa del piano:* poiché il committente vieta esiti definitivi in degrado, il piano non esegue lo stadio con ordine diagnostico. Un'alternativa (stadio eseguito con esiti marcati non definitivi) richiederebbe una nuova decisione del committente e un sottotipo di esito assente nel §13.2.

## 6. Validazione di identificativi e citazioni

Invariata (A-8, FR-014/015): ID e citazioni del modello sono validati dal codec/validatore della 002 **contro i candidati effettivamente forniti**; alias non risolvibili ⇒ `work_output_invalid` (terminale per lo stadio), nessuna correzione silenziosa; citazione = sottostringa letterale di un passaggio fornito. Un riferimento valido nel ledger ma non fornito allo stadio è **non risolvibile**.

## 7. Compatibilità

v2–v6, snapshot, WorkOutput, prompt, gold, checkpoint della 001/002 non riscritti; report v5/v6 byte-identici (test golden); estensioni di coverage additive e solo per v7; nessuna nuova tabella per il run (JSONB esistente).
