# Verbale della seconda riunione di progetto — Integrazione EDI

Documento sintetico di prova, successivo all'analisi funzionale: le regole qui sotto riprendono e modificano quelle già documentate.

## Partecipanti e ordine del giorno

- VB-01. Alla riunione hanno partecipato l'ufficio sistemi informativi, l'ufficio ordini e il referente di Hub Beta.
- VB-02. L'ordine del giorno comprendeva la revisione delle regole di riprocessamento e dei campi dei tracciati.

## Decisioni della riunione

- VB-03. Il timeout del riprocessamento automatico dei messaggi in errore passa da due ore a quattro ore.
- VB-04. Il motivo della modifica del timeout è il tempo di ripristino dichiarato da Hub Beta dopo un'interruzione.
- VB-05. Il campo codiceDestinazione dell'ordine diventa obbligatorio, mentre nell'analisi era facoltativo.
- VB-06. L'ufficio ordini comunicherà ai dodici clienti il nuovo obbligo del codice destinazione.
- VB-07. I tracciati INVOIC e DESADV restano invariati rispetto all'analisi funzionale.
- VB-08. Il numero massimo di tentativi automatici resta fissato a cinque.

## Azioni

- VB-09. L'ufficio sistemi informativi aggiorna l'analisi funzionale con le due regole modificate.
- VB-10. Il referente di Hub Beta conferma entro la settimana successiva la finestra di manutenzione.
