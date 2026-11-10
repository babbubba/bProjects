# Proposta — contesto di provenienza nell'input del riordino (003 T027)

**Stato: PROPOSTA, non implementata, in attesa di decisione esplicita.** Nessuna modifica a codice, policy, indice, gold o soglie. T027 resta aperto. Evidenze: `M2-diagnostic.md`.

## Problema

7 delle 8 query con Hit@1 mancato dopo il riordino (N4-doc-B, M03, M04, M05, M09, M13, M14) falliscono perché il candidato corretto non dice da quale documento o sezione viene, mentre la query lo nomina e altri documenti hanno blocchi quasi identici. `CandidateReranker` oggi invia `IndexText.Of(CanonicalText)` e nient'altro.

## Perché non contraddice A-5 / research.md

A-5 vieta di ripetere il titolo nel **testo indicizzato**, perché peggiora il lessicale (T082 BM25 78,9% → 73,7%). La proposta non tocca né l'indice né `index-text-1`: il contesto viene composto **solo al momento del riordino**, a partire da dati già persistiti. `research.md` indica già il prefisso di titolo come «opzione da misurare (non adottata)».

## Cosa cambierebbe

- `CandidateReranker.OrderAsync`: per ogni blocco della finestra, oltre a `CanonicalText`, legge `Document.DisplayName` e `HeadingPathJson` (la stessa proiezione di `BlockEvidenceMapper`, nessuna nuova fonte di verità) e invia `rerank-input-1` = etichetta + testo indicizzato.
- Forma candidata (da fissare prima di misurare, non da scegliere sul gold): `Documento: <DisplayName>` e, se presente, `Sezione: <titolo1 > titolo2>`, su righe proprie, poi il testo del blocco. Funzione pura e versionata, come `IndexText`.
- La versione di `rerank-input-1` entra nell'hash di policy (`RetrievalPolicy.Hash`), così i run storici restano interpretabili.
- Invariati: indice, `IndexConfigHash`, ricerca lessicale e densa, RRF k=60, pool 50, finestra di riordino 50, punteggi usati solo per ordinare (FR-009), nessuna soglia, nessun troncamento (blocchi ≤ 558 caratteri più etichetta, contesto 8192), citazioni e provenienza dal blocco originale.
- Il nome del file è un dato fornito dall'utente: finisce solo nell'input del riordino, già sanitizzato (`SanitizeFileName`).

## Costo e rischi

- Complessità bassa: una funzione pura, una proiezione in più nella query dei testi, test unitari e un test di integrazione. Nessuna migrazione, nessuna re-indicizzazione.
- Latenza: input più lunghi di circa 40-100 caratteri per candidato; il riordino misurato era 407 ms p50. Da rimisurare.
- Rischio: il riordino potrebbe dare troppo peso al nome del file anche quando la query non lo cita (regressione sulle query oggi corrette: M06, M11, M12, T082). La sonda esplorativa non lo ha verificato.
- Limite noto: non risolve neg-45 (causa C, gold incompleto).

## Piano di validazione (senza ottimizzare sul gold di accettazione)

1. **Decisione preliminare** del committente: sì/no al principio e forma dell'etichetta, fissata prima di qualsiasi misura.
2. **Query indipendenti** (SC-012): un nuovo insieme di query scritto prima di rieseguire, non derivato dai fallimenti noti; stessi corpora. Servono sia query che citano il documento sia query che non lo citano.
3. **Misura A/B** con lo stesso harness: input attuale contro `rerank-input-1`, su (a) query indipendenti, (b) gold congelato come controllo di non regressione (Hit@1 e Recall@10 non peggiori di 2 pt, T082 incluso). Il gold congelato non serve a scegliere la forma.
4. Se Hit@1 ≥ 95% sulle query indipendenti **e** nessuna regressione: ripetere T026 con la nuova versione e riesaminare T027. Altrimenti: esperimento con un secondo riordino sullo stesso input arricchito.
5. Ogni esecuzione DGX richiede una tua autorizzazione esplicita separata.

## Cosa non è incluso

Nessun cambio di modello, di strategia di fusione o di policy diversa da `rerank-input-1`; nessuna modifica ai gold (inclusa neg-45); nessuna riduzione della soglia.

## Decisioni richieste

1. Approvare o respingere il principio (contesto di provenienza solo nell'input del riordino).
2. Se approvato: confermare la forma dell'etichetta (solo documento, oppure documento + sezione).
3. Autorizzare la scrittura delle query indipendenti e la misura A/B (autorizzazione DGX separata al momento dell'esecuzione).
4. Decidere su neg-45 (gold da integrare o query da riformulare).
