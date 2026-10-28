# Casi nominali e attese annotate (T052)

Elenco generato da `tests/e2e/support/acceptance-cases.json`, proiezione di `backend/tests/bProjects.IntegrationTests/AcceptanceFixtures.cs` (unica fonte autorevole: se i testi divergono vale il codice). Le attese sono state annotate PRIMA delle prove e non vanno modificate dopo aver visto l'output (quickstart, «Prove nominali separate»).

Ogni caso e' un percorso di **cinque messaggi PM preparati** (SC-001). Il PM invia i messaggi cosi' come sono scritti, nell'ordine indicato, senza riformularli.

## N1-frammenti — Note frammentarie dopo una call

Informazioni sparse in cinque messaggi brevi, senza ordine logico (SC-001, SC-002, SC-003, SC-006, SC-008).

### Progetto «Prenotazioni officina Rossi Auto»

**Messaggi PM da inviare**

1. Appunti veloci dopo la call con Rossi Auto. Vogliono un portale per prenotare gli interventi in officina: il cliente sceglie il tipo di intervento (tagliando, gomme, revisione) e vede gli slot liberi.
2. Slot da 30 minuti. L'officina ha 4 ponti, quindi massimo 4 interventi in parallelo. Al cliente arriva una conferma via email e un promemoria il giorno prima.
3. Accessi: il cliente si registra con email e password, niente login con i social. L'operatore dell'officina vede l'agenda del giorno e può spostare o annullare un appuntamento.
4. Pagamenti online no: si paga in sede, il portale non li gestisce. Deve funzionare bene da smartphone, perché i clienti prenotano dal telefono.
5. Integrazione con OffiGest, il gestionale dell'officina: serve solo leggere l'anagrafica clienti, in sola lettura. Vorrebbero andare online prima della stagione delle gomme invernali, a metà ottobre.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Realizzare un portale per prenotare gli interventi in officina | 1 |
| Requisito | Il cliente sceglie il tipo di intervento: tagliando, gomme o revisione | 1 |
| Requisito | Il cliente vede gli slot liberi | 1 |
| Vincolo | Gli slot hanno durata di 30 minuti | 2 |
| Vincolo | L'officina ha 4 ponti: al massimo 4 interventi in parallelo | 2 |
| Requisito | Il cliente riceve una conferma via email | 2 |
| Requisito | Il giorno prima il cliente riceve un promemoria | 2 |
| Requisito | Il cliente si registra con email e password | 3 |
| Escluso / negato | Il login con i social network non è previsto | 3 |
| Requisito | L'operatore vede l'agenda del giorno | 3 |
| Requisito | L'operatore può spostare o annullare un appuntamento | 3 |
| Escluso / negato | I pagamenti online sono esclusi: si paga in sede | 4 |
| Vincolo | Il portale deve funzionare bene da smartphone | 4 |
| Dipendenza / integrazione | Integrazione in sola lettura con OffiGest per l'anagrafica clienti | 5 |
| Vincolo | Messa online desiderata prima della stagione delle gomme invernali, a metà ottobre | 5 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «Il portale prevede il login con i social network»
- Come affermazione (mentre e' negato, escluso o aperto): «Il cliente effettua il pagamento online»
- In nessun punto (non presente nel dialogo): ad es. «Promemoria anche via SMS e WhatsApp»
- In nessun punto (non presente nel dialogo): ad es. «Realizzato con Angular e PostgreSQL»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione)**

- Modificare la voce «Gli slot hanno durata di 30 minuti» in: «Ogni slot di prenotazione dura 30 minuti»
- Aggiungere in «Vincolo»: «Il cliente può annullare la propria prenotazione fino a 24 ore prima»
- Aggiungere in «Assunzione»: «Si assume che i clienti usino un browser aggiornato su smartphone»

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

## N2-negazioni — Esclusioni e negazioni esplicite

Molte cose NON si fanno: negazioni ed esclusioni da non invertire (FR-010).

### Progetto «Gestione ferie e permessi»

**Messaggi PM da inviare**

1. Dobbiamo sostituire il foglio Excel con cui l'ufficio personale gestisce ferie e permessi di circa 120 dipendenti: il dipendente invia la richiesta e il suo responsabile la approva o la rifiuta.
2. Attenzione: il portale NON calcola le buste paga né i ratei di ferie maturati, quello resta nel software paghe. Anche le trasferte restano fuori.
3. Le richieste le approva solo il responsabile diretto, non l'ufficio personale. L'ufficio personale non approva niente, vede soltanto il riepilogo.
4. Non serve un'app mobile nativa: basta che il sito si usi bene da telefono. E niente notifiche push, solo email.
5. Le assenze per malattia non si gestiscono qui, il certificato resta fuori dal sistema. Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo, salvo urgenze che decide il responsabile.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Sostituire il foglio Excel di gestione di ferie e permessi dell'ufficio personale | 1 |
| Requisito | Il dipendente invia una richiesta e il responsabile la approva o la rifiuta | 1 |
| Contesto | Il portale serve circa 120 dipendenti | 1 |
| Escluso / negato | Il calcolo delle buste paga non è previsto: resta nel software paghe | 2 |
| Escluso / negato | Il calcolo dei ratei di ferie maturati non è previsto | 2 |
| Escluso / negato | Le trasferte restano fuori dal portale | 2 |
| Requisito | Le richieste sono approvate solo dal responsabile diretto | 3 |
| Requisito | L'ufficio personale non approva: vede soltanto il riepilogo | 3 |
| Escluso / negato | L'app mobile nativa non è prevista | 4 |
| Vincolo | Il sito deve essere ben utilizzabile da telefono | 4 |
| Escluso / negato | Le notifiche push non sono previste | 4 |
| Requisito | Le notifiche avvengono solo via email | 4 |
| Escluso / negato | Le assenze per malattia non si gestiscono nel portale: il certificato resta fuori dal sistema | 5 |
| Vincolo | Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo | 5 |
| Requisito | Le urgenze, su decisione del responsabile, fanno eccezione all'anticipo minimo | 5 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «Il portale calcola le buste paga dei dipendenti»
- Come affermazione (mentre e' negato, escluso o aperto): «Il portale calcola i ratei di ferie maturati»
- Come affermazione (mentre e' negato, escluso o aperto): «Il portale gestisce le trasferte»
- Come affermazione (mentre e' negato, escluso o aperto): «È prevista un'app mobile nativa»
- Come affermazione (mentre e' negato, escluso o aperto): «Il portale invia notifiche push»
- Come affermazione (mentre e' negato, escluso o aperto): «Il portale gestisce le assenze per malattia e i certificati»
- Come affermazione (mentre e' negato, escluso o aperto): «L'ufficio personale approva le richieste»
- In nessun punto (non presente nel dialogo): ad es. «Notifiche anche via SMS»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione)**

- Modificare la voce «Le richieste sono approvate solo dal responsabile diretto» in: «Le richieste di ferie e permessi sono approvate esclusivamente dal responsabile diretto del dipendente»
- Aggiungere in «Requisito»: «Il dipendente può annullare una richiesta non ancora approvata»
- Aggiungere in «Assunzione»: «Si assume che ogni dipendente abbia un responsabile diretto censito»

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

## N3-lacune — Informazioni mancanti dichiarate dal PM

Il PM dice espressamente cosa NON sa: le lacune restano lacune, il noto resta noto (SC-003).

### Progetto «Magazzino ricambi»

**Messaggi PM da inviare**

1. Progetto: gestire le scorte del magazzino ricambi con un lettore di codici a barre. Lo usano i magazzinieri per registrare carichi e scarichi.
2. Non so ancora quanti magazzini siano: forse uno solo a Brescia, forse due con quello di Verona. Lo chiarisco con il direttore operativo la prossima settimana.
3. L'anagrafica dei ricambi esiste già nel gestionale, ma non so in che formato si possa esportare. Devo chiederlo al fornitore del gestionale.
4. Quando la giacenza di un articolo scende sotto il minimo, l'app deve segnalarlo. I valori minimi per articolo però non li ho ancora: li deve dare il responsabile acquisti.
5. Budget e data di consegna: per ora nessuna informazione. L'unica cosa certa è che ogni scarico deve richiedere sempre il codice commessa.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Gestire le scorte del magazzino ricambi | 1 |
| Requisito | I magazzinieri registrano carichi e scarichi con un lettore di codici a barre | 1 |
| Lacuna (punto aperto) | Quanti magazzini sono coinvolti: uno solo a Brescia oppure due, con quello di Verona? | 2 |
| Dipendenza / integrazione | L'anagrafica dei ricambi esiste già nel gestionale | 3 |
| Lacuna (punto aperto) | In quale formato si può esportare l'anagrafica dei ricambi? Da chiedere al fornitore del gestionale | 3 |
| Requisito | L'app segnala quando la giacenza di un articolo scende sotto il minimo | 4 |
| Lacuna (punto aperto) | I valori minimi per articolo non sono disponibili: li deve fornire il responsabile acquisti | 4 |
| Lacuna (punto aperto) | Budget e data di consegna non sono definiti | 5 |
| Requisito | Ogni scarico richiede sempre il codice commessa | 5 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «Il sistema gestisce due magazzini»
- Come affermazione (mentre e' negato, escluso o aperto): «Il magazzino principale è a Brescia»
- In nessun punto (non presente nel dialogo): ad es. «L'anagrafica viene esportata in CSV»
- In nessun punto (non presente nel dialogo): ad es. «Budget di 50 mila euro»
- In nessun punto (non presente nel dialogo): ad es. «Sviluppato in Java»
- In nessun punto (non presente nel dialogo): ad es. «Consegna entro marzo»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione)**

- Modificare la voce «Ogni scarico richiede sempre il codice commessa» in: «Ogni scarico richiede obbligatoriamente il codice commessa»
- Aggiungere in «Vincolo»: «Il lettore di codici a barre è collegato via USB alla postazione»
- Aggiungere in «Assunzione»: «Si assume che il formato sia noto prima dell'avvio dello sviluppo»

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

## N4-conflitti — Affermazioni incompatibili tra reparti

Due conflitti: il modello non sceglie; il PM ne risolve uno con motivo e lascia l'altro aperto (FR-011, SC-011).

### Progetto «Portale fornitori»

**Messaggi PM da inviare**

1. Portale fornitori per la qualifica e il censimento dei nuovi fornitori: il fornitore compila un questionario e carica le certificazioni (ISO 9001, DURC).
2. Gli acquisti dicono che la qualifica deve essere approvata entro 5 giorni lavorativi dall'invio.
3. Il reparto qualità invece vuole 10 giorni lavorativi, perché le certificazioni le verificano a mano. Non c'è ancora una decisione.
4. Sull'accesso: l'IT vuole il login con SSO aziendale per tutti. Gli acquisti dicono che i fornitori esterni devono entrare con email e password, perché non hanno account aziendali.
5. Una cosa è certa: i dati dei fornitori non devono uscire dall'Italia, quindi hosting in un data center italiano. I documenti caricati vanno conservati per 10 anni.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Qualificare e censire i nuovi fornitori | 1 |
| Requisito | Il fornitore compila un questionario | 1 |
| Requisito | Il fornitore carica le certificazioni (ISO 9001, DURC) | 1 |
| Vincolo | I dati dei fornitori non escono dall'Italia: hosting in un data center italiano | 5 |
| Vincolo | I documenti caricati sono conservati per 10 anni | 5 |
| Conflitto (da lasciare **aperto** dall'AI) | Entro quanti giorni lavorativi va approvata la qualifica: 5 (acquisti) o 10 (qualità)? — alternativa 1: La qualifica va approvata entro 5 giorni lavorativi dall'invio (acquisti); alternativa 2: La qualifica richiede 10 giorni lavorativi per la verifica manuale delle certificazioni (qualità) | 2 e 3 |
| Conflitto (da lasciare **aperto** dall'AI) | Quale accesso per i fornitori esterni: SSO aziendale oppure email e password? — alternativa 1: L'IT chiede il login con SSO aziendale per tutti; alternativa 2: Gli acquisti chiedono che i fornitori esterni entrino con email e password | 4 e 4 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «La qualifica va approvata entro 5 giorni lavorativi»
- Come affermazione (mentre e' negato, escluso o aperto): «I fornitori accedono con email e password»
- In nessun punto (non presente nel dialogo): ad es. «Accesso tramite Entra ID»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione, una risoluzione di conflitto)**

- Modificare la voce «Il fornitore compila un questionario» in: «Il fornitore compila online un questionario di qualifica»
- Aggiungere in «Vincolo»: «Le certificazioni scadute bloccano la qualifica»
- Aggiungere in «Assunzione»: «Si assume che le certificazioni siano caricate in formato PDF»
- Risolvere il conflitto «Entro quanti giorni lavorativi va approvata la qualifica: 5 (acquisti) o 10 (qualità)?» scegliendo l'alternativa 2 («La qualifica richiede 10 giorni lavorativi per la verifica manuale delle certificazioni (qualità)») con motivo: «Decisione del responsabile qualità: servono 10 giorni lavorativi per la verifica manuale delle certificazioni». L'altro conflitto resta **aperto**.

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

## N5-progetti-distinti — Due progetti simili, decisioni opposte

Due progetti simili con decisioni opposte sugli stessi temi: nulla deve passare da un progetto all'altro (FR-015).

### Progetto «Aurora assistenza tecnica» (aurora)

**Messaggi PM da inviare**

1. Progetto Aurora: portale clienti per l'assistenza tecnica di Meccanica Verdi. I clienti aprono ticket e ne vedono lo stato.
2. Notifiche solo via email: gli SMS sono esclusi dal primo rilascio.
3. Integrazione con il gestionale: esclusa per ora, i ticket restano solo nel portale. Non so ancora se servirà più avanti.
4. Accesso con email e password, un utente per ogni azienda cliente.
5. Gli agenti vedono tutti i ticket, mentre ogni cliente vede solo i propri. Messa online a giugno.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Portale clienti per l'assistenza tecnica di Meccanica Verdi | 1 |
| Requisito | I clienti aprono ticket e ne vedono lo stato | 1 |
| Requisito | Le notifiche sono inviate solo via email | 2 |
| Escluso / negato | Le notifiche via SMS sono escluse dal primo rilascio | 2 |
| Escluso / negato | L'integrazione con il gestionale è esclusa per ora: i ticket restano solo nel portale | 3 |
| Lacuna (punto aperto) | L'integrazione con il gestionale servirà più avanti? | 3 |
| Requisito | Accesso con email e password, un utente per ogni azienda cliente | 4 |
| Requisito | Gli agenti vedono tutti i ticket; ogni cliente vede solo i propri | 5 |
| Vincolo | Messa online prevista a giugno | 5 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «Le notifiche arrivano anche via SMS»
- Come affermazione (mentre e' negato, escluso o aperto): «Integrazione obbligatoria con il gestionale»
- In nessun punto (non presente nel dialogo di questo progetto): ad es. «Integrazione in tempo reale con Colorweb»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione)**

- Modificare la voce «Accesso con email e password, un utente per ogni azienda cliente» in: «Accesso con email e password, con un utente per ogni azienda cliente»
- Aggiungere in «Requisito»: «Il cliente può allegare un file a un ticket»
- Aggiungere in «Assunzione»: «Si assume che ogni azienda cliente abbia un referente»

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

### Progetto «Boreale ordini clienti» (boreale)

**Messaggi PM da inviare**

1. Progetto Boreale: portale clienti per gli ordini del Colorificio Bianchi. I clienti inseriscono gli ordini e ne seguono la spedizione.
2. Notifiche via SMS e via email, sceglie il cliente. Gli SMS servono per l'avviso di consegna.
3. Integrazione con il gestionale ordini Colorweb: obbligatoria, gli ordini devono arrivare in tempo reale al gestionale.
4. Accesso con SSO aziendale del cliente, niente password locali.
5. I clienti vedono solo i propri ordini. Messa online a settembre. Il listino prezzi è gestito altrove e non entra nel portale.

**Attese (cosa deve esserci, con il messaggio che la sostiene)**

| Sezione attesa | Contenuto atteso (formulazione indicativa) | Fonte (messaggio PM) |
| --- | --- | --- |
| Obiettivo | Portale clienti per gli ordini del Colorificio Bianchi | 1 |
| Requisito | I clienti inseriscono gli ordini e ne seguono la spedizione | 1 |
| Requisito | Le notifiche arrivano via SMS e via email, a scelta del cliente | 2 |
| Requisito | Gli SMS servono per l'avviso di consegna | 2 |
| Dipendenza / integrazione | Integrazione obbligatoria in tempo reale con il gestionale ordini Colorweb | 3 |
| Requisito | Accesso con SSO aziendale del cliente, senza password locali | 4 |
| Requisito | I clienti vedono solo i propri ordini | 5 |
| Vincolo | Messa online prevista a settembre | 5 |
| Escluso / negato | Il listino prezzi è gestito altrove e non entra nel portale | 5 |

**Non deve comparire**

- Come affermazione (mentre e' negato, escluso o aperto): «Il portale gestisce il listino prezzi»
- Come affermazione (mentre e' negato, escluso o aperto): «Accesso con email e password»
- In nessun punto (non presente nel dialogo di questo progetto): ad es. «I ticket dell'assistenza»
- Stime di ore, costi o date non presenti nel dialogo; sezioni vuote o riempitivi; la bozza presentata come approvata o definitiva.

**Correzione d'anteprima prevista (SC-001: una correzione, una integrazione, un'assunzione)**

- Modificare la voce «I clienti vedono solo i propri ordini» in: «I clienti vedono esclusivamente i propri ordini»
- Aggiungere in «Requisito»: «Il cliente consulta lo storico dei propri ordini»
- Aggiungere in «Assunzione»: «Si assume che ogni cliente disponga dell'SSO aziendale»

Dopo la correzione: salvare, confermare, poi Genera documento. La bozza deve riportare tutte le attese sopra, le aggiunte del PM, la scelta motivata e i punti ancora aperti come aperti.

## E1-contesto-grande — Prova di limite (esplorativa, NON nominale)

Dieci messaggi PM di circa 18.500 caratteri ciascuno (~185.000 caratteri), come nel primo test manuale registrato in `plan.md` («Osservazioni dal primo test manuale»). Il testo e' generato dal codice (`AcceptanceCases.Exploratory`); non e' un dialogo da leggere per intero. I fatti da preservare sono: nome «Aurora» (messaggio 1), SMS esclusi dal primo rilascio (1), fatturazione fuori perimetro (1), lacuna sul numero di clienti (1), conflitto fra go-live a marzo (messaggio 3) e a settembre (messaggio 7), vincolo «i dati restano in Italia» (10).

Esiti ammessi e distinti: (a) analisi riuscita con le attese verificate; (b) errore **esplicito** `context_overflow` o `compaction_failed` con originali intatti. Nessuno dei due e' un caso nominale superato e nessuno certifica la gestione di documenti lunghi; qualunque altro esito (errore generico, omissione silenziosa, originali alterati) e' un fallimento. Non rientra nei criteri PASS dei cinque casi nominali.
