# Risultati laboratorio Slice 003

Corpus: C:\source\repos\babbubba\bProjects-slice-003-lab\tests\fixtures\documents
Chunk: 186; query misurate: 12; non presenti: 0
Durata complessiva: 0.04 s

| Metodo | Hit@1 | Hit@3 | Hit@5 |
|---|---:|---:|---:|
| bm25 | 11/12 | 12/12 | 12/12 |

## Casi non recuperati in top 5

## NLI diagnostico (non criterio di accettazione)

## Limiti
- Gold locale, non rappresentativo; nessuna verifica semantica automatica.
- Hit@k misura il recupero, non la correttezza dell'analisi.
- PDF richiedono testo estratto dal parser; file PDF saltati qui.
- I chunk a riga sono una baseline volutamente semplice, non il chunking Docling della produzione.
