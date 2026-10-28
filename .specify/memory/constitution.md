<!--
Sync Impact Report (temporaneo: rimuovere prima del commit)
Versione: template non ratificato → 1.0.0 (prima adozione).
Principi: segnaposto sostituiti dai nove principi I–IX richiesti dal committente.
Sezioni aggiunte: Perimetro e autorità; Verifica delle slice (al posto dei segnaposto).
Sezioni rimosse: nessuna sezione di governance ratificata.
Precedenza aggiornata: riferimento MAF esteso e ADR solo per scelte significative;
requisiti funzionali del piano approvato invariati.
Altri artefatti: invariati; template e comandi leggono la Constitution a runtime.
TODO differiti: nessuno.
-->
# bProjects Constitution

## Core Principles

### I. KISS e YAGNI

L'architettura DEVE essere modulare e limitata alle necessità della slice corrente.
Astrazioni, servizi e dipendenze senza un requisito attuale NON DEVONO essere introdotti.

### II. Microsoft Agent Framework come riferimento

Microsoft Agent Framework (MAF) DEVE essere il framework di riferimento per agenti,
sessioni, memoria conversazionale, compattazione, workflow, tool, RAG e human-in-the-loop.
Questo riferimento non obbliga a usare capacità estranee alla slice corrente.

### III. Primitive native e verifica .NET

Le primitive native MAF DEVONO essere privilegiate rispetto a equivalenti custom.
Prima di adottarle, disponibilità e comportamento nella versione .NET scelta DEVONO
essere verificati con documentazione ufficiale e prove eseguibili; la disponibilità
in altri linguaggi non basta. Se una primitiva manca o non soddisfa il requisito,
la soluzione minima necessaria DEVE avere una motivazione verificabile nella modifica.

### IV. Responsabilità applicative

L'applicazione DEVE gestire dati di dominio, autenticazione, permessi, documenti
e versioni indipendentemente dallo stato interno MAF. Storico originale ed evidenze
DEVONO restare persistenti; memorie e sintesi derivate non ne sostituiscono l'autorità.

### V. LLM esclusivamente interni

Gli LLM DEVONO essere eseguiti esclusivamente su infrastruttura interna autorizzata,
inizialmente sul DGX Spark. Fallback verso LLM SaaS e invio di dati interni a tali
servizi NON SONO consentiti.

### VI. Vertical slice complete

Lo sviluppo DEVE procedere per vertical slice piccole, utilizzabili e verificabili
da un utente, con criteri di accettazione espliciti. Le funzionalità successive
DEVONO restare nel perimetro di prodotto senza essere implementate anticipatamente.

### VII. Autorizzazione delle operazioni esterne

Nessuna operazione esterna irreversibile DEVE essere eseguita senza autorizzazione
esplicita riferita all'azione. Una proposta AI o un consenso generico non valgono
come autorizzazione; il controllo DEVE precedere l'esecuzione anche tramite tool.

### VIII. Integrazioni critiche provate realmente

Le integrazioni critiche usate dalla slice DEVONO essere provate con componenti reali,
inclusi MAF .NET, LLM interno e persistenza, verificando anche errori e ripresa quando
pertinenti. Mock e test unitari da soli non dimostrano il funzionamento integrato.

### IX. ADR proporzionati

Gli ADR DEVONO essere riservati a scelte architetturali significative, come nuovi
servizi, confini di responsabilità o dipendenze strutturali. Le decisioni implementative
ordinarie NON DEVONO richiedere ADR; la motivazione necessaria resta nella modifica.

## Perimetro e autorità

[AI-Project-Documentation-MVP-Plan.md](../../AI-Project-Documentation-MVP-Plan.md)
resta la fonte dei requisiti funzionali approvati. Questa Constitution governa
il modo di realizzarli e prevale sulle indicazioni precedenti incompatibili riguardo
all'impiego di MAF e all'obbligo di ADR, senza modificare i requisiti funzionali.
Il perimetro completo non impone di implementare tutto nella prima slice.

## Verifica delle slice

Ogni slice DEVE dimostrare il percorso utente completo con criteri di accettazione,
prove pertinenti ed esiti delle integrazioni critiche. Le verifiche di disponibilità
MAF e le motivazioni tecniche DEVONO restare negli artefatti di lavoro pertinenti,
senza produrre documentazione separata per decisioni ordinarie.

## Governance

Ogni specifica, piano e revisione DEVE verificare la conformità a questi principi.
Le modifiche alla Constitution richiedono approvazione esplicita del committente,
motivazione e valutazione degli impatti; gli artefatti dipendenti si aggiornano
nelle rispettive fasi, senza modifiche automatiche fuori scope.
Versionamento semantico: MAJOR per rimozioni o ridefinizioni incompatibili,
MINOR per nuovi principi o ampliamenti, PATCH per chiarimenti senza cambio di regole.

**Version**: 1.0.0 | **Ratified**: 2026-10-27 | **Last Amended**: 2026-10-27
