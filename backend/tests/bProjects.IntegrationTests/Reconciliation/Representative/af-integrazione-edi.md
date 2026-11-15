# Analisi funzionale — Integrazione EDI fra Gestionale Alfa e Hub Beta

Documento sintetico di prova: tutti i nomi, i codici e i valori sono inventati.

## 1. Contesto e obiettivi

- OB-01. L'integrazione automatizza lo scambio di ordini, avvisi di spedizione e fatture fra Gestionale Alfa e Hub Beta.
- OB-02. L'obiettivo è eliminare la digitazione manuale degli ordini ricevuti dai clienti tramite Hub Beta.
- OB-03. L'obiettivo è rendere disponibile l'avviso di spedizione al cliente entro la chiusura della spedizione.
- OB-04. L'obiettivo è inviare le fatture ai clienti in formato elettronico tramite Hub Beta.
- OB-05. L'obiettivo è ridurre gli scarti dei messaggi sotto l'uno per cento dei messaggi scambiati.
- CT-01. Gestionale Alfa è il sistema gestionale dell'azienda e custodisce anagrafiche, ordini, spedizioni e fatture.
- CT-02. Hub Beta è l'hub EDI del fornitore di servizi che instrada i messaggi verso i clienti.
- CT-03. I clienti coinvolti nella prima fase sono dodici e usano lo standard EDIFACT D96A.
- CT-04. L'integrazione sostituisce lo scambio attuale di file CSV via posta elettronica.
- CT-05. Il volume atteso è di circa quattrocento ordini al giorno con punte di mille nei periodi promozionali.
- CT-06. Il responsabile funzionale dell'integrazione per l'azienda è l'ufficio sistemi informativi.

## 2. Flussi

### 2.1 Flusso ORDERS

- FO-01. Hub Beta consegna ogni ordine cliente a Gestionale Alfa come messaggio ORDERS in formato JSON.
- FO-02. Gestionale Alfa riceve gli ordini tramite un endpoint HTTPS esposto solo verso Hub Beta.
- FO-03. Alla ricezione Gestionale Alfa risponde con una conferma tecnica entro cinque secondi.
- FO-04. L'ordine viene creato in stato «da validare» e passa a «confermato» dopo i controlli di anagrafica.
- FO-05. Un ordine duplicato, riconosciuto dal numero ordine del cliente, non crea un secondo ordine.
- FO-06. L'ordine con articoli non presenti in anagrafica viene sospeso e segnalato all'ufficio ordini.

### 2.2 Flusso DESADV

- FD-01. Gestionale Alfa genera un messaggio DESADV alla chiusura di ogni spedizione.
- FD-02. Il messaggio DESADV riporta per ogni collo il codice SSCC e le quantità spedite.
- FD-03. Gestionale Alfa invia il DESADV a Hub Beta tramite chiamata HTTPS autenticata.
- FD-04. Hub Beta restituisce una conferma di consegna del messaggio al cliente.
- FD-05. Una spedizione parziale genera un DESADV per ogni parte spedita.
- FD-06. Gli scarti del DESADV restituiti da Hub Beta vengono registrati in una coda di errori.

### 2.3 Flusso INVOIC

- FI-01. Gestionale Alfa genera un messaggio INVOIC per ogni fattura emessa a un cliente collegato all'hub.
- FI-02. La fattura viene inviata a Hub Beta solo dopo la registrazione contabile definitiva.
- FI-03. La fattura va inviata a Hub Beta entro 24 ore dalla registrazione contabile.
- FI-04. Le note di credito seguono lo stesso flusso della fattura con tipo documento diverso.
- FI-05. Hub Beta restituisce l'esito di accettazione della fattura da parte del cliente.
- FI-06. Una fattura respinta dal cliente torna in stato «da correggere» in Gestionale Alfa.

## 3. Requisiti funzionali e operativi

- RF-01. Il sistema deve validare ogni ordine ricevuto rispetto all'anagrafica clienti di Gestionale Alfa.
- RF-02. Il sistema deve accettare ordini con un massimo di cinquecento righe.
- RF-03. Il sistema deve convertire le unità di misura del cliente in quelle di Gestionale Alfa.
- RF-04. Il sistema deve conservare il messaggio ORDERS originale per cinque anni.
- RF-05. Il sistema deve assegnare a ogni ordine un numero interno progressivo.
- RF-06. Il sistema deve confermare l'ordine al cliente con un messaggio ORDRSP entro un'ora dalla validazione.
- RF-07. Il sistema deve calcolare la data di consegna richiesta in base al calendario del magazzino.
- RF-08. Il sistema deve rifiutare gli ordini di clienti sospesi per insolvenza.
- RF-09. Il sistema deve generare i colli del DESADV rispettando il peso massimo di venticinque chilogrammi per collo.
- RF-10. Il sistema deve riportare nel DESADV il numero ordine originale del cliente.
- RF-11. Il sistema deve stampare l'etichetta SSCC per ogni collo.
- RF-12. Il sistema deve impedire l'invio del DESADV se la spedizione non è chiusa.
- RF-13. Il sistema deve emettere la fattura con aliquote IVA coerenti con la destinazione.
- RF-14. Il sistema deve riportare nell'INVOIC il riferimento al DESADV di spedizione.
- RF-15. Il sistema deve inviare le note di credito entro lo stesso termine delle fatture.
- RF-16. Il sistema deve registrare ogni messaggio inviato o ricevuto con data, esito e identificativo.
- RF-17. Il sistema deve permettere all'ufficio ordini di consultare lo stato di ogni messaggio.
- RF-18. Il sistema deve segnalare via posta elettronica i messaggi scartati dopo l'ultimo tentativo.
- RF-19. Il sistema deve permettere il riprocessamento manuale di un singolo messaggio scartato.
- RF-20. Il sistema deve consentire la sospensione dell'integrazione per un singolo cliente.
- RF-21. Il sistema deve produrre ogni giorno un riepilogo dei messaggi scambiati per cliente.
- RF-22. Il sistema deve cifrare i messaggi in transito con TLS 1.2 o superiore.
- RF-23. Il sistema deve autenticare Hub Beta con un certificato client.
- RF-24. Il sistema deve rispondere alle richieste di Hub Beta entro dieci secondi.
- RF-25. Il sistema deve poter gestire un ambiente di collaudo separato dalla produzione.

## 4. Tracciati

### 4.1 Tracciato ORDERS (testata)

| ID | Campo | Tipo | Lunghezza | Obbligatorio | Note |
|---|---|---|---|---|---|
| ORD-01 | numeroOrdineCliente | testo | 20 | sì | identifica l'ordine presso il cliente |
| ORD-02 | dataOrdine | data | 10 | sì | formato AAAA-MM-GG |
| ORD-03 | codiceCliente | testo | 12 | sì | codice assegnato da Gestionale Alfa |
| ORD-04 | partitaIvaCliente | testo | 16 | sì | senza prefisso del paese |
| ORD-05 | codiceDestinazione | testo | 8 | no | facoltativo, se vuoto vale la sede legale |
| ORD-06 | dataConsegnaRichiesta | data | 10 | no | se assente si applica il calendario del magazzino |
| ORD-07 | divisa | testo | 3 | sì | codice ISO 4217 |
| ORD-08 | condizioniPagamento | testo | 6 | no | codice delle condizioni concordate |
| ORD-09 | noteOrdine | testo | 200 | no | testo libero senza vincoli di formato |
| ORD-10 | numeroRighe | numero | 3 | sì | non superiore a cinquecento |

### 4.2 Tracciato DESADV (testata)

| ID | Campo | Tipo | Lunghezza | Obbligatorio | Note |
|---|---|---|---|---|---|
| DES-01 | numeroSpedizione | testo | 15 | sì | numero interno di Gestionale Alfa |
| DES-02 | numeroOrdineCliente | testo | 20 | sì | ripetuto dall'ordine originale |
| DES-03 | dataSpedizione | data | 10 | sì | data di chiusura della spedizione |
| DES-04 | codiceDestinazione | testo | 8 | sì | destinazione fisica della merce |
| DES-05 | vettore | testo | 30 | no | ragione sociale del vettore |
| DES-06 | numeroColli | numero | 4 | sì | somma dei colli della spedizione |
| DES-07 | pesoLordoKg | decimale | 9 | sì | peso lordo complessivo in chilogrammi |
| DES-08 | codiceSSCC | testo | 18 | sì | un codice per ogni collo |
| DES-09 | tipoSpedizione | testo | 1 | sì | T totale oppure P parziale |
| DES-10 | noteSpedizione | testo | 100 | no | testo libero per il cliente |

### 4.3 Tracciato INVOIC (testata)

| ID | Campo | Tipo | Lunghezza | Obbligatorio | Note |
|---|---|---|---|---|---|
| INV-01 | numeroFattura | testo | 15 | sì | progressivo annuale |
| INV-02 | dataFattura | data | 10 | sì | data di registrazione contabile |
| INV-03 | tipoDocumento | testo | 2 | sì | FT fattura oppure NC nota di credito |
| INV-04 | codiceCliente | testo | 12 | sì | stesso codice dell'ordine |
| INV-05 | riferimentoSpedizione | testo | 15 | sì | numero del DESADV collegato |
| INV-06 | imponibile | decimale | 12 | sì | due decimali |
| INV-07 | aliquotaIva | decimale | 5 | sì | percentuale con due decimali |
| INV-08 | importoIva | decimale | 12 | sì | due decimali |
| INV-09 | totaleDocumento | decimale | 12 | sì | imponibile più imposta |
| INV-10 | scadenzaPagamento | data | 10 | no | calcolata dalle condizioni di pagamento |

## 5. Trascodifica

### 5.1 Unità di misura

| ID | Codice cliente | Codice Gestionale Alfa | Descrizione |
|---|---|---|---|
| UM-01 | PCE | PZ | pezzi |
| UM-02 | KGM | KG | chilogrammi |
| UM-03 | LTR | LT | litri |
| UM-04 | MTR | MT | metri |
| UM-05 | PK | CF | confezioni |
| UM-06 | BX | SC | scatole |
| UM-07 | PF | PL | pallet |
| UM-08 | SET | KT | kit |

### 5.2 Causali di scarto

| ID | Codice Hub Beta | Codice Gestionale Alfa | Significato |
|---|---|---|---|
| CS-01 | E01 | SCH01 | formato del messaggio non valido |
| CS-02 | E02 | SCH02 | cliente non riconosciuto |
| CS-03 | E03 | SCH03 | articolo non riconosciuto |
| CS-04 | E04 | SCH04 | destinatario non raggiungibile |
| CS-05 | E05 | SCH05 | messaggio duplicato |
| CS-06 | E06 | SCH06 | firma o certificato non validi |
| CS-07 | E07 | SCH07 | quantità non coerente con l'ordine |
| CS-08 | E08 | SCH08 | errore tecnico dell'hub |

## 6. Gestione errori e riprocessamento

- ER-01. Gli errori di formato vengono respinti subito e non vengono ritentati automaticamente.
- ER-02. Gli errori tecnici di Hub Beta vengono ritentati automaticamente fino a cinque volte.
- ER-03. Fra un tentativo e il successivo trascorrono quindici minuti.
- ER-04. Dopo l'ultimo tentativo fallito il messaggio passa nello stato «scartato».
- ER-05. Il riprocessamento automatico dei messaggi in errore scade dopo un timeout di due ore.
- ER-06. Un messaggio scaduto resta disponibile per il riprocessamento manuale.
- ER-07. Il riprocessamento manuale non modifica il contenuto originale del messaggio.
- ER-08. Ogni riprocessamento genera una nuova riga nel registro dei messaggi.
- ER-09. Un messaggio riprocessato con successo chiude automaticamente lo scarto collegato.
- ER-10. Gli scarti per cliente non riconosciuto vengono assegnati all'ufficio anagrafiche.
- ER-11. Gli scarti per articolo non riconosciuto vengono assegnati all'ufficio ordini.
- ER-12. Gli scarti tecnici ripetuti per più di un'ora generano una segnalazione al referente dell'hub.
- ER-13. Il registro degli errori non contiene mai il testo completo delle note libere.
- ER-14. Un messaggio duplicato viene scartato con la causale SCH05 senza notifica al cliente.

## 7. Vincoli di integrazione

- VN-01. L'integrazione deve usare esclusivamente il protocollo HTTPS.
- VN-02. Hub Beta accetta messaggi di dimensione non superiore a cinque megabyte.
- VN-03. Gestionale Alfa non deve esporre endpoint verso reti diverse da quella di Hub Beta.
- VN-04. L'invio dei messaggi non può avvenire fuori dalla finestra di manutenzione di Hub Beta.
- VN-05. La finestra di manutenzione di Hub Beta è la domenica dalle due alle quattro del mattino.
- VN-06. I messaggi devono usare la codifica UTF-8.
- VN-07. I certificati client scadono dopo dodici mesi e vanno rinnovati prima della scadenza.
- VN-08. Il numero di richieste verso Hub Beta non può superare dieci al secondo.
- VN-09. Le anagrafiche dei clienti non vengono scambiate tramite l'integrazione.
- VN-10. Il collaudo con i clienti avviene solo nell'ambiente di prova di Hub Beta.

## 8. Monitoraggio

- MN-01. Il sistema deve mostrare il numero di messaggi in coda per ciascun flusso.
- MN-02. Il sistema deve segnalare una coda ferma da più di trenta minuti.
- MN-03. Il sistema deve conservare le statistiche giornaliere per un anno.
- MN-04. Il sistema deve permettere l'esportazione del registro dei messaggi in formato CSV.
- MN-05. Il sistema deve tracciare la latenza media di risposta di Hub Beta.
- MN-06. Il sistema deve notificare un'interruzione del servizio di Hub Beta entro cinque minuti.

## 9. Decisioni, assunzioni e questioni aperte

- DC-01. È stato deciso di usare un solo endpoint di ricezione per tutti i flussi in ingresso.
- DC-02. È stato deciso di conservare i messaggi originali in un archivio separato dal database operativo.
- DC-03. È stato deciso di avviare la prima fase con i dodici clienti già collegati ad Hub Beta.
- DC-04. È stato deciso di non gestire in prima fase i messaggi di variazione ordine.
- EX-01. Sono esclusi dalla prima fase i clienti che richiedono lo standard ANSI X12.
- EX-02. Sono esclusi dall'integrazione gli ordini di assistenza tecnica.
- EX-03. È esclusa la gestione delle fatture proforma.
- DP-01. L'integrazione dipende dal rilascio del certificato client da parte di Hub Beta.
- DP-02. L'invio delle fatture dipende dalla chiusura contabile giornaliera di Gestionale Alfa.
- DP-03. La stampa delle etichette SSCC dipende dalla configurazione delle stampanti di magazzino.
- DP-04. Il collaudo dipende dalla disponibilità dell'ambiente di prova di Hub Beta.
- AS-01. Si assume che i clienti usino sempre le unità di misura della tabella di trascodifica.
- AS-02. Si assume che il volume degli ordini non superi mille al giorno anche nei periodi promozionali.
- AS-03. Si assume che Hub Beta mantenga invariato il formato dei messaggi durante la prima fase.
- AS-04. Si ipotizza che i clienti accettino la conferma d'ordine ORDRSP entro un'ora.
- IN-01. Dal volume giornaliero se ne deduce che il carico medio è inferiore a un messaggio ogni minuto.
- IN-02. Dalla presenza dell'SSCC si deduce che i colli devono essere identificati singolarmente.
- IN-03. Dal vincolo di manutenzione si deduce che le fatture della domenica notte vengono inviate dopo le quattro.
- IN-04. Dalla conservazione per cinque anni si deduce che l'archivio dei messaggi cresce di circa duecentomila messaggi l'anno.
- AL-01. Per il riprocessamento si valuta un'alternativa: la scadenza automatica oppure un riprocessamento senza scadenza.
- AL-02. Per gli avvisi di spedizione si valuta un'alternativa: un unico DESADV per ordine oppure uno per spedizione.
- AL-03. Per l'archivio dei messaggi è da chiarire se usare lo storage interno o quello del fornitore dell'hub.
- DA-01. Resta aperta la decisione su chi gestisce i rinvii dei messaggi dopo la scadenza del riprocessamento.
- DA-02. Resta aperta la decisione sul termine massimo di invio della fattura, ventiquattro o quarantotto ore.
- DA-03. Resta aperta la decisione sul formato del numero ordine per i clienti con più sedi.
