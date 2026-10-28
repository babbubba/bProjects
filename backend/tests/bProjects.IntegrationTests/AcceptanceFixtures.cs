using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bProjects.App.Analysis;

namespace bProjects.IntegrationTests;

// T052 - fixture di accettazione: cinque casi nominali (frammenti, negazioni/esclusioni, informazioni mancanti, conflitti,
// progetti distinti) e un caso esplorativo di contesto grande (plan.md, "Osservazioni dal primo test manuale"), ciascuno con le
// attese annotate PRIMA delle prove (quickstart.md, "Prove nominali separate"): requisiti dichiarati, esclusioni e negazioni,
// lacune, conflitti, fonti, nessuna invenzione, nessun mescolamento fra progetti.
//
// Le attese sono verificate da <see cref="AcceptanceChecker"/> (deterministico) sull'analisi e sulla bozza, qualunque sia il
// modello che le ha prodotte: un modello finto "oracolo" (<see cref="AcceptanceOracle"/>, solo dove la logica e' nostra:
// provenienza, validazione, isolamento) oppure il DGX reale (categoria MafReal). Il confronto lessicale e' condizione necessaria e
// non sufficiente: la qualita' (SC-006) la giudicano persone, non queste regole (specs/001-project-draft/acceptance/).

/// <summary>Sezione dell'analisi (AnalysisContent) in cui un fatto atteso deve comparire.</summary>
internal enum Sec { Goals, Overview, Included, Excluded, Requirements, Flows, Constraints, Dependencies, Assumptions, Open }

/// <summary>
/// Informazione attesa. <see cref="Text"/> e' la formulazione ideale (usata dall'oracolo e dalle prove del verificatore);
/// <see cref="Match"/> sono le espressioni regolari (senza accenti, minuscole) che devono combaciare TUTTE con la stessa voce.
/// <see cref="From"/> sono gli indici (da 1) dei messaggi PM che la sostengono: una voce Declared deve citarne almeno uno.
/// </summary>
internal sealed record Fact(string Key, Sec Section, string Text, AnalysisClassification Class, int[] From, string[] Match, Sec[]? Also = null)
{
    public IReadOnlyList<Sec> Accept => Also is null ? [Section] : [Section, .. Also];
}

internal enum ForbidMode
{
    /// <summary>Vietata come affermazione: ammessa solo negata (esclusione) o come punto aperto/conflitto.</summary>
    Affirmed,

    /// <summary>Vietata ovunque: informazione inventata o appartenente a un altro progetto.</summary>
    Anywhere,
}

/// <summary><paramref name="Sample"/> e' un testo che viola il divieto: serve alle prove del verificatore (mutazioni).</summary>
internal sealed record Forbid(string Key, string Pattern, ForbidMode Mode, string Sample, string? ConflictKey = null);

internal sealed record ConflictSpec(string Key, string Description, string[] AltText, string[] AltMatch, int[][] AltFrom);

/// <summary>Informazione aggiunta dal PM nell'anteprima: la fonte e' la revisione PM, non un messaggio.</summary>
internal sealed record Addition(string Key, Sec Section, string Text, AnalysisClassification Class, string[] Match);

internal sealed record Resolution(string ConflictKey, int Alternative, string Reason, string ReasonMatch);

/// <summary>Correzione d'anteprima del percorso nominale (SC-001): una modifica, un'integrazione, un'assunzione, a richiesta una risoluzione.</summary>
internal sealed record Correction(string EditKey, string EditedText, IReadOnlyList<Addition> Additions, Resolution? Resolve);

internal sealed record ProjectScript(
    string Key, string Name, string[] Messages, Fact[] Facts, ConflictSpec[] Conflicts, Forbid[] Forbids, Correction Correction);

internal sealed record AcceptanceCase(string Id, string Title, string Focus, string Criteria, ProjectScript[] Projects, bool Exploratory = false);

internal static class AcceptanceCases
{
    private const AnalysisClassification D = AnalysisClassification.Declared;
    private const AnalysisClassification I = AnalysisClassification.Inferred;
    private const AnalysisClassification A = AnalysisClassification.Assumed;

    private static Fact F(string key, Sec sec, string text, int from, string[] match, params Sec[] also) =>
        new(key, sec, text, D, [from], match, also.Length == 0 ? null : also);

    private static Fact Gap(string key, string text, int from, params string[] match) =>
        new(key, Sec.Open, text, I, [from], match, [Sec.Assumptions]);

    /// <summary>I cinque casi nominali: ciascuno e' un percorso completo di cinque messaggi PM preparati (SC-001/SC-002/SC-008).</summary>
    public static IReadOnlyList<AcceptanceCase> Nominal { get; } = [Fragments, Negations, MissingInfo, Conflicts, DistinctProjects];

    /// <summary>Prova di limite separata dai casi nominali: dieci messaggi per ~185.000 caratteri (plan.md). Un errore gestito non e' un successo nominale.</summary>
    public static AcceptanceCase Exploratory { get; } = BuildExploratory();

    public static IEnumerable<object[]> NominalIds() => Nominal.Select(c => new object[] { c.Id });

    // ------------------------------------------------------------------------------------------------ 1. frammenti

    private static AcceptanceCase Fragments => new(
        "N1-frammenti", "Note frammentarie dopo una call", "Informazioni sparse in cinque messaggi brevi, senza ordine logico",
        "SC-001, SC-002, SC-003, SC-006, SC-008",
        [
            new ProjectScript(
                "officina", "Prenotazioni officina Rossi Auto",
                [
                    "Appunti veloci dopo la call con Rossi Auto. Vogliono un portale per prenotare gli interventi in officina: il cliente sceglie il tipo di intervento (tagliando, gomme, revisione) e vede gli slot liberi.",
                    "Slot da 30 minuti. L'officina ha 4 ponti, quindi massimo 4 interventi in parallelo. Al cliente arriva una conferma via email e un promemoria il giorno prima.",
                    "Accessi: il cliente si registra con email e password, niente login con i social. L'operatore dell'officina vede l'agenda del giorno e può spostare o annullare un appuntamento.",
                    "Pagamenti online no: si paga in sede, il portale non li gestisce. Deve funzionare bene da smartphone, perché i clienti prenotano dal telefono.",
                    "Integrazione con OffiGest, il gestionale dell'officina: serve solo leggere l'anagrafica clienti, in sola lettura. Vorrebbero andare online prima della stagione delle gomme invernali, a metà ottobre.",
                ],
                [
                    F("goal", Sec.Goals, "Realizzare un portale per prenotare gli interventi in officina", 1, [@"portale", @"prenot"], Sec.Overview),
                    F("tipi", Sec.Requirements, "Il cliente sceglie il tipo di intervento: tagliando, gomme o revisione", 1, [@"tagliando", @"gomme", @"revisione"]),
                    F("slot-liberi", Sec.Requirements, "Il cliente vede gli slot liberi", 1, [@"slot.*(liber|disponibil)|(liber|disponibil).*slot"], Sec.Flows, Sec.Included),
                    F("slot-30", Sec.Constraints, "Gli slot hanno durata di 30 minuti", 2, [@"30 ?min"], Sec.Requirements),
                    F("ponti-4", Sec.Constraints, "L'officina ha 4 ponti: al massimo 4 interventi in parallelo", 2, [@"\b(4|quattro)\b", @"ponti|paralle"], Sec.Requirements),
                    F("conferma-email", Sec.Requirements, "Il cliente riceve una conferma via email", 2, [@"conferma", @"email"]),
                    F("promemoria", Sec.Requirements, "Il giorno prima il cliente riceve un promemoria", 2, [@"promemoria"]),
                    F("registrazione", Sec.Requirements, "Il cliente si registra con email e password", 3, [@"registr", @"email", @"password"], Sec.Flows),
                    F("no-social", Sec.Excluded, "Il login con i social network non è previsto", 3, [@"social"]),
                    F("agenda", Sec.Requirements, "L'operatore vede l'agenda del giorno", 3, [@"agenda"], Sec.Flows),
                    F("sposta", Sec.Requirements, "L'operatore può spostare o annullare un appuntamento", 3, [@"sposta", @"annull"], Sec.Flows),
                    F("no-pagamenti", Sec.Excluded, "I pagamenti online sono esclusi: si paga in sede", 4, [@"pagament"]),
                    F("mobile", Sec.Constraints, "Il portale deve funzionare bene da smartphone", 4, [@"smartphone|telefono|mobile"], Sec.Requirements),
                    F("offigest", Sec.Dependencies, "Integrazione in sola lettura con OffiGest per l'anagrafica clienti", 5, [@"offigest", @"anagrafica"], Sec.Requirements, Sec.Included),
                    F("ottobre", Sec.Constraints, "Messa online desiderata prima della stagione delle gomme invernali, a metà ottobre", 5, [@"ottobre"], Sec.Goals),
                ],
                [],
                [
                    new("social", @"social", ForbidMode.Affirmed, "Il portale prevede il login con i social network"),
                    new("pagamenti", @"pagament", ForbidMode.Affirmed, "Il cliente effettua il pagamento online"),
                    new("canali-inventati", @"\b(sms|whatsapp|push)\b", ForbidMode.Anywhere, "Promemoria anche via SMS e WhatsApp"),
                    new("tecnologie-inventate", @"\b(react|angular|java|python|postgres\w*|sql|aws|azure)\b", ForbidMode.Anywhere, "Realizzato con Angular e PostgreSQL"),
                ],
                new Correction(
                    "slot-30", "Ogni slot di prenotazione dura 30 minuti",
                    [
                        new("pm-annulla", Sec.Constraints, "Il cliente può annullare la propria prenotazione fino a 24 ore prima", D, [@"annull", @"24"]),
                        new("pm-browser", Sec.Assumptions, "Si assume che i clienti usino un browser aggiornato su smartphone", A, [@"browser"]),
                    ],
                    null)),
        ]);

    // ------------------------------------------------------------------------------------------------ 2. negazioni ed esclusioni

    private static AcceptanceCase Negations => new(
        "N2-negazioni", "Esclusioni e negazioni esplicite", "Molte cose NON si fanno: negazioni e perimetro da non invertire",
        "SC-002, SC-003, SC-006, SC-008 (FR-010: negazioni ed esclusioni non invertite)",
        [
            new ProjectScript(
                "ferie", "Gestione ferie e permessi",
                [
                    "Dobbiamo sostituire il foglio Excel con cui l'ufficio personale gestisce ferie e permessi di circa 120 dipendenti: il dipendente invia la richiesta e il suo responsabile la approva o la rifiuta.",
                    "Attenzione: il portale NON calcola le buste paga né i ratei di ferie maturati, quello resta nel software paghe. Anche le trasferte restano fuori.",
                    "Le richieste le approva solo il responsabile diretto, non l'ufficio personale. L'ufficio personale non approva niente, vede soltanto il riepilogo.",
                    "Non serve un'app mobile nativa: basta che il sito si usi bene da telefono. E niente notifiche push, solo email.",
                    "Le assenze per malattia non si gestiscono qui, il certificato resta fuori dal sistema. Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo, salvo urgenze che decide il responsabile.",
                ],
                [
                    F("goal", Sec.Goals, "Sostituire il foglio Excel di gestione di ferie e permessi dell'ufficio personale", 1, [@"excel", @"ferie"], Sec.Overview),
                    F("richiesta", Sec.Requirements, "Il dipendente invia una richiesta e il responsabile la approva o la rifiuta", 1, [@"richiest", @"approva", @"rifiut"], Sec.Flows),
                    F("dipendenti", Sec.Overview, "Il portale serve circa 120 dipendenti", 1, [@"120"], Sec.Constraints, Sec.Requirements, Sec.Included),
                    F("no-paghe", Sec.Excluded, "Il calcolo delle buste paga non è previsto: resta nel software paghe", 2, [@"buste paga|paghe"]),
                    F("no-ratei", Sec.Excluded, "Il calcolo dei ratei di ferie maturati non è previsto", 2, [@"ratei"]),
                    F("no-trasferte", Sec.Excluded, "Le trasferte restano fuori dal portale", 2, [@"trasfert"]),
                    F("solo-responsabile", Sec.Requirements, "Le richieste sono approvate solo dal responsabile diretto", 3, [@"responsabile diretto"], Sec.Constraints),
                    F("ufficio-riepilogo", Sec.Requirements, "L'ufficio personale non approva: vede soltanto il riepilogo", 3, [@"ufficio personale", @"riepilogo"], Sec.Constraints, Sec.Included),
                    F("no-app", Sec.Excluded, "L'app mobile nativa non è prevista", 4, [@"\bapp\b", @"nativ|mobile"]),
                    F("sito-mobile", Sec.Constraints, "Il sito deve essere ben utilizzabile da telefono", 4, [@"telefono|mobile|smartphone"], Sec.Requirements),
                    F("no-push", Sec.Excluded, "Le notifiche push non sono previste", 4, [@"push"]),
                    F("solo-email", Sec.Requirements, "Le notifiche avvengono solo via email", 4, [@"notific", @"email"], Sec.Constraints),
                    F("no-malattia", Sec.Excluded, "Le assenze per malattia non si gestiscono nel portale: il certificato resta fuori dal sistema", 5, [@"malattia"]),
                    F("anticipo", Sec.Constraints, "Le ferie vanno richieste con almeno 5 giorni lavorativi di anticipo", 5, [@"\b(5|cinque)\b", @"giorni", @"anticip"], Sec.Requirements),
                    F("urgenze", Sec.Requirements, "Le urgenze, su decisione del responsabile, fanno eccezione all'anticipo minimo", 5, [@"urgen"], Sec.Constraints),
                ],
                [],
                [
                    new("paghe", @"buste paga|paghe", ForbidMode.Affirmed, "Il portale calcola le buste paga dei dipendenti"),
                    new("ratei", @"ratei", ForbidMode.Affirmed, "Il portale calcola i ratei di ferie maturati"),
                    new("trasferte", @"trasfert", ForbidMode.Affirmed, "Il portale gestisce le trasferte"),
                    new("nativa", @"nativ", ForbidMode.Affirmed, "È prevista un'app mobile nativa"),
                    new("push", @"\bpush\b", ForbidMode.Affirmed, "Il portale invia notifiche push"),
                    new("malattia", @"malattia", ForbidMode.Affirmed, "Il portale gestisce le assenze per malattia e i certificati"),
                    new("ufficio-approva", @"ufficio personale.{0,30}approv", ForbidMode.Affirmed, "L'ufficio personale approva le richieste"),
                    new("sms-inventato", @"\b(sms|whatsapp)\b", ForbidMode.Anywhere, "Notifiche anche via SMS"),
                ],
                new Correction(
                    "solo-responsabile", "Le richieste di ferie e permessi sono approvate esclusivamente dal responsabile diretto del dipendente",
                    [
                        new("pm-annulla", Sec.Requirements, "Il dipendente può annullare una richiesta non ancora approvata", D, [@"annull", @"non ancora approvat"]),
                        new("pm-censito", Sec.Assumptions, "Si assume che ogni dipendente abbia un responsabile diretto censito", A, [@"responsabile diretto", @"censit"]),
                    ],
                    null)),
        ]);

    // ------------------------------------------------------------------------------------------------ 3. informazioni mancanti

    private static AcceptanceCase MissingInfo => new(
        "N3-lacune", "Informazioni mancanti dichiarate dal PM", "Il PM dice espressamente cosa NON sa: le lacune restano lacune, il noto resta noto",
        "SC-002, SC-003 (vere lacune esplicite, nessun noto ridotto a punto aperto), SC-006, SC-008",
        [
            new ProjectScript(
                "magazzino", "Magazzino ricambi",
                [
                    "Progetto: gestire le scorte del magazzino ricambi con un lettore di codici a barre. Lo usano i magazzinieri per registrare carichi e scarichi.",
                    "Non so ancora quanti magazzini siano: forse uno solo a Brescia, forse due con quello di Verona. Lo chiarisco con il direttore operativo la prossima settimana.",
                    "L'anagrafica dei ricambi esiste già nel gestionale, ma non so in che formato si possa esportare. Devo chiederlo al fornitore del gestionale.",
                    "Quando la giacenza di un articolo scende sotto il minimo, l'app deve segnalarlo. I valori minimi per articolo però non li ho ancora: li deve dare il responsabile acquisti.",
                    "Budget e data di consegna: per ora nessuna informazione. L'unica cosa certa è che ogni scarico deve richiedere sempre il codice commessa.",
                ],
                [
                    F("goal", Sec.Goals, "Gestire le scorte del magazzino ricambi", 1, [@"scort", @"ricambi"], Sec.Overview),
                    F("barcode", Sec.Requirements, "I magazzinieri registrano carichi e scarichi con un lettore di codici a barre", 1, [@"barre", @"scaric"], Sec.Flows),
                    Gap("gap-magazzini", "Quanti magazzini sono coinvolti: uno solo a Brescia oppure due, con quello di Verona?", 2, @"magazzin", @"quanti|numer|uno|due"),
                    F("anagrafica", Sec.Dependencies, "L'anagrafica dei ricambi esiste già nel gestionale", 3, [@"anagrafica", @"gestionale"], Sec.Overview, Sec.Included, Sec.Constraints, Sec.Requirements),
                    Gap("gap-formato", "In quale formato si può esportare l'anagrafica dei ricambi? Da chiedere al fornitore del gestionale", 3, @"formato"),
                    F("soglia", Sec.Requirements, "L'app segnala quando la giacenza di un articolo scende sotto il minimo", 4, [@"giacenza", @"minim"], Sec.Flows),
                    Gap("gap-minimi", "I valori minimi per articolo non sono disponibili: li deve fornire il responsabile acquisti", 4, @"minim", @"acquist"),
                    Gap("gap-budget", "Budget e data di consegna non sono definiti", 5, @"budget"),
                    F("commessa", Sec.Requirements, "Ogni scarico richiede sempre il codice commessa", 5, [@"scaric", @"commessa"], Sec.Constraints),
                ],
                [],
                [
                    new("magazzini-inventati", @"(un solo|uno solo|due|2|tre|3) magazzin", ForbidMode.Affirmed, "Il sistema gestisce due magazzini"),
                    new("sedi-decise", @"brescia|verona", ForbidMode.Affirmed, "Il magazzino principale è a Brescia"),
                    new("formato-inventato", @"\b(csv|xml|json|rest|sql|excel)\b", ForbidMode.Anywhere, "L'anagrafica viene esportata in CSV"),
                    new("importi-inventati", @"€|\beuro\b|\b\d+ ?(k|mila)\b", ForbidMode.Anywhere, "Budget di 50 mila euro"),
                    new("tecnologie-inventate", @"\b(java|python|angular|react|azure|aws)\b", ForbidMode.Anywhere, "Sviluppato in Java"),
                    new("date-inventate", @"\b(entro|a|per) (gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre)\b", ForbidMode.Anywhere, "Consegna entro marzo"),
                ],
                new Correction(
                    "commessa", "Ogni scarico richiede obbligatoriamente il codice commessa",
                    [
                        new("pm-usb", Sec.Constraints, "Il lettore di codici a barre è collegato via USB alla postazione", D, [@"usb"]),
                        new("pm-avvio", Sec.Assumptions, "Si assume che il formato sia noto prima dell'avvio dello sviluppo", A, [@"avvio", @"svilupp"]),
                    ],
                    null)),
        ]);

    // ------------------------------------------------------------------------------------------------ 4. conflitti

    private static AcceptanceCase Conflicts => new(
        "N4-conflitti", "Affermazioni incompatibili tra reparti", "Due conflitti: il modello non sceglie; il PM ne risolve uno con motivo e lascia l'altro aperto",
        "SC-002, SC-003 (alternative esplicite), SC-006, SC-011 (FR-011: nessuna risoluzione automatica)",
        [
            new ProjectScript(
                "fornitori", "Portale fornitori",
                [
                    "Portale fornitori per la qualifica e il censimento dei nuovi fornitori: il fornitore compila un questionario e carica le certificazioni (ISO 9001, DURC).",
                    "Gli acquisti dicono che la qualifica deve essere approvata entro 5 giorni lavorativi dall'invio.",
                    "Il reparto qualità invece vuole 10 giorni lavorativi, perché le certificazioni le verificano a mano. Non c'è ancora una decisione.",
                    "Sull'accesso: l'IT vuole il login con SSO aziendale per tutti. Gli acquisti dicono che i fornitori esterni devono entrare con email e password, perché non hanno account aziendali.",
                    "Una cosa è certa: i dati dei fornitori non devono uscire dall'Italia, quindi hosting in un data center italiano. I documenti caricati vanno conservati per 10 anni.",
                ],
                [
                    F("goal", Sec.Goals, "Qualificare e censire i nuovi fornitori", 1, [@"qualific", @"censi"], Sec.Overview),
                    F("questionario", Sec.Requirements, "Il fornitore compila un questionario", 1, [@"questionario"], Sec.Flows),
                    F("certificazioni", Sec.Requirements, "Il fornitore carica le certificazioni (ISO 9001, DURC)", 1, [@"certificazion", @"durc"], Sec.Flows),
                    F("hosting", Sec.Constraints, "I dati dei fornitori non escono dall'Italia: hosting in un data center italiano", 5, [@"ital"], Sec.Requirements),
                    F("conservazione", Sec.Constraints, "I documenti caricati sono conservati per 10 anni", 5, [@"\b(10|dieci) anni"], Sec.Requirements),
                ],
                [
                    new("tempi", "Entro quanti giorni lavorativi va approvata la qualifica: 5 (acquisti) o 10 (qualità)?",
                        ["La qualifica va approvata entro 5 giorni lavorativi dall'invio (acquisti)", "La qualifica richiede 10 giorni lavorativi per la verifica manuale delle certificazioni (qualità)"],
                        [@"\b(5|cinque) giorni", @"\b(10|dieci) giorni"], [[2], [3]]),
                    new("accesso", "Quale accesso per i fornitori esterni: SSO aziendale oppure email e password?",
                        ["L'IT chiede il login con SSO aziendale per tutti", "Gli acquisti chiedono che i fornitori esterni entrino con email e password"],
                        [@"\bsso\b", @"email.*password|password"], [[4], [4]]),
                ],
                [
                    new("tempi-5", @"\b(5|cinque) giorni|\b(10|dieci) giorni", ForbidMode.Affirmed, "La qualifica va approvata entro 5 giorni lavorativi", "tempi"),
                    new("accesso-scelto", @"\bsso\b|email e password", ForbidMode.Affirmed, "I fornitori accedono con email e password", "accesso"),
                    new("idp-inventati", @"\b(entra|okta|azure|keycloak)\b", ForbidMode.Anywhere, "Accesso tramite Entra ID"),
                ],
                new Correction(
                    "questionario", "Il fornitore compila online un questionario di qualifica",
                    [
                        new("pm-scadute", Sec.Constraints, "Le certificazioni scadute bloccano la qualifica", D, [@"scadut"]),
                        new("pm-pdf", Sec.Assumptions, "Si assume che le certificazioni siano caricate in formato PDF", A, [@"pdf"]),
                    ],
                    new Resolution("tempi", 1, "Decisione del responsabile qualità: servono 10 giorni lavorativi per la verifica manuale delle certificazioni", @"verifica manuale"))),
        ]);

    // ------------------------------------------------------------------------------------------------ 5. progetti distinti

    private static AcceptanceCase DistinctProjects => new(
        "N5-progetti-distinti", "Due progetti simili, decisioni opposte", "Stesso lessico (portale clienti, notifiche, integrazione, accesso) e scelte contrarie: nulla deve passare da un progetto all'altro",
        "SC-002, SC-003, SC-006, SC-008 (FR-015: dati e revisioni appartengono al progetto corretto)",
        [
            new ProjectScript(
                "aurora", "Aurora assistenza tecnica",
                [
                    "Progetto Aurora: portale clienti per l'assistenza tecnica di Meccanica Verdi. I clienti aprono ticket e ne vedono lo stato.",
                    "Notifiche solo via email: gli SMS sono esclusi dal primo rilascio.",
                    "Integrazione con il gestionale: esclusa per ora, i ticket restano solo nel portale. Non so ancora se servirà più avanti.",
                    "Accesso con email e password, un utente per ogni azienda cliente.",
                    "Gli agenti vedono tutti i ticket, mentre ogni cliente vede solo i propri. Messa online a giugno.",
                ],
                [
                    F("goal", Sec.Goals, "Portale clienti per l'assistenza tecnica di Meccanica Verdi", 1, [@"portale", @"assistenza"], Sec.Overview),
                    F("ticket", Sec.Requirements, "I clienti aprono ticket e ne vedono lo stato", 1, [@"ticket", @"stato"], Sec.Flows),
                    F("solo-email", Sec.Requirements, "Le notifiche sono inviate solo via email", 2, [@"notific", @"email"], Sec.Constraints),
                    F("no-sms", Sec.Excluded, "Le notifiche via SMS sono escluse dal primo rilascio", 2, [@"sms"]),
                    F("no-integrazione", Sec.Excluded, "L'integrazione con il gestionale è esclusa per ora: i ticket restano solo nel portale", 3, [@"integrazion", @"gestionale"]),
                    Gap("gap-futuro", "L'integrazione con il gestionale servirà più avanti?", 3, @"integrazion|gestionale", @"futur|avanti|serv"),
                    F("login", Sec.Requirements, "Accesso con email e password, un utente per ogni azienda cliente", 4, [@"email", @"password"], Sec.Flows, Sec.Constraints),
                    F("ruoli", Sec.Requirements, "Gli agenti vedono tutti i ticket; ogni cliente vede solo i propri", 5, [@"agenti", @"propri"], Sec.Constraints),
                    F("giugno", Sec.Constraints, "Messa online prevista a giugno", 5, [@"giugno"], Sec.Goals),
                ],
                [],
                [
                    new("sms", @"\bsms\b", ForbidMode.Affirmed, "Le notifiche arrivano anche via SMS"),
                    new("integrazione", @"integrazion.{0,40}gestionale|gestionale.{0,40}integrazion", ForbidMode.Affirmed, "Integrazione obbligatoria con il gestionale"),
                    new("progetto-boreale", @"boreale|bianchi|colorweb|ordini|spedizion|consegna|settembre|\bsso\b|listino", ForbidMode.Anywhere, "Integrazione in tempo reale con Colorweb"),
                ],
                new Correction(
                    "login", "Accesso con email e password, con un utente per ogni azienda cliente",
                    [
                        new("pm-allegati", Sec.Requirements, "Il cliente può allegare un file a un ticket", D, [@"allegar"]),
                        new("pm-referente", Sec.Assumptions, "Si assume che ogni azienda cliente abbia un referente", A, [@"referente"]),
                    ],
                    null)),
            new ProjectScript(
                "boreale", "Boreale ordini clienti",
                [
                    "Progetto Boreale: portale clienti per gli ordini del Colorificio Bianchi. I clienti inseriscono gli ordini e ne seguono la spedizione.",
                    "Notifiche via SMS e via email, sceglie il cliente. Gli SMS servono per l'avviso di consegna.",
                    "Integrazione con il gestionale ordini Colorweb: obbligatoria, gli ordini devono arrivare in tempo reale al gestionale.",
                    "Accesso con SSO aziendale del cliente, niente password locali.",
                    "I clienti vedono solo i propri ordini. Messa online a settembre. Il listino prezzi è gestito altrove e non entra nel portale.",
                ],
                [
                    F("goal", Sec.Goals, "Portale clienti per gli ordini del Colorificio Bianchi", 1, [@"portale", @"ordini"], Sec.Overview),
                    F("ordini", Sec.Requirements, "I clienti inseriscono gli ordini e ne seguono la spedizione", 1, [@"ordini", @"spedizion"], Sec.Flows),
                    F("notifiche", Sec.Requirements, "Le notifiche arrivano via SMS e via email, a scelta del cliente", 2, [@"sms", @"email"], Sec.Constraints),
                    F("sms-consegna", Sec.Requirements, "Gli SMS servono per l'avviso di consegna", 2, [@"sms", @"consegna"], Sec.Constraints, Sec.Flows),
                    F("integrazione", Sec.Dependencies, "Integrazione obbligatoria in tempo reale con il gestionale ordini Colorweb", 3, [@"colorweb", @"tempo reale"], Sec.Requirements, Sec.Included),
                    F("sso", Sec.Requirements, "Accesso con SSO aziendale del cliente, senza password locali", 4, [@"\bsso\b"], Sec.Constraints, Sec.Flows),
                    F("propri", Sec.Requirements, "I clienti vedono solo i propri ordini", 5, [@"propri", @"ordini"], Sec.Constraints),
                    F("settembre", Sec.Constraints, "Messa online prevista a settembre", 5, [@"settembre"], Sec.Goals),
                    F("no-listino", Sec.Excluded, "Il listino prezzi è gestito altrove e non entra nel portale", 5, [@"listino"]),
                ],
                [],
                [
                    new("listino", @"listino", ForbidMode.Affirmed, "Il portale gestisce il listino prezzi"),
                    new("password-locali", @"password", ForbidMode.Affirmed, "Accesso con email e password"),
                    new("progetto-aurora", @"aurora|verdi|ticket|assistenza|giugno|agenti", ForbidMode.Anywhere, "I ticket dell'assistenza"),
                ],
                new Correction(
                    "propri", "I clienti vedono esclusivamente i propri ordini",
                    [
                        new("pm-storico", Sec.Requirements, "Il cliente consulta lo storico dei propri ordini", D, [@"storico"]),
                        new("pm-sso", Sec.Assumptions, "Si assume che ogni cliente disponga dell'SSO aziendale", A, [@"dispong", @"sso"]),
                    ],
                    null)),
        ]);

    // ------------------------------------------------------------------------------------------------ esplorativo: contesto grande

    public const int ExploratoryMessageCount = 10;
    public const int ExploratoryMessageChars = 18_500;

    private static AcceptanceCase BuildExploratory()
    {
        // Messaggi PM molto lunghi come nel primo test manuale (10 x ~18.500 caratteri, ~185.000 complessivi). Il primo contiene i fatti da
        // preservare (CompactionDialogs.KeyFacts: nome, esclusione SMS, fatturazione fuori perimetro, lacuna); il resto e' materiale di
        // dettaglio compattabile con in piu' un conflitto fra due messaggi e un vincolo dichiarato nell'ultimo.
        var needles = new Dictionary<int, (string Text, double At)>
        {
            [3] = ("Aggiornamento di riunione: la direzione commerciale fissa il go-live a marzo.", 0.5),
            [7] = ("Il responsabile IT invece sostiene che il go-live sia a settembre; fra marzo e settembre non c'è ancora una decisione.", 0.4),
            [10] = ("Vincolo certo: i dati dei clienti devono restare in Italia.", 0.6),
        };
        var messages = new string[ExploratoryMessageCount];
        for (var i = 1; i <= ExploratoryMessageCount; i++)
        {
            if (!needles.TryGetValue(i, out var needle))
            {
                messages[i - 1] = CompactionDialogs.Message(i - 1, ExploratoryMessageChars);
                continue;
            }

            var filler = CompactionDialogs.Message(i - 1, ExploratoryMessageChars - needle.Text.Length - 2);
            var cut = filler.IndexOf(". ", (int)(filler.Length * needle.At), StringComparison.Ordinal) + 2;
            messages[i - 1] = filler[..cut] + needle.Text + " " + filler[cut..];
        }

        var script = new ProjectScript(
            "grande", "Contesto grande Aurora",
            messages,
            [
                F("nome", Sec.Overview, "Il portale clienti si chiama Aurora", 1, [@"aurora"], Sec.Goals, Sec.Included),
                F("no-sms", Sec.Excluded, "Le notifiche via SMS sono escluse dal primo rilascio", 1, [@"sms"]),
                F("no-fatturazione", Sec.Excluded, "La fatturazione resta fuori perimetro", 1, [@"fattur"]),
                Gap("gap-clienti", "Non è noto quanti clienti useranno il portale", 1, @"client", @"quanti|numer"),
                F("dati-italia", Sec.Constraints, "I dati dei clienti devono restare in Italia", 10, [@"ital"], Sec.Requirements),
            ],
            [new("go-live", "Il go-live è a marzo (direzione commerciale) o a settembre (responsabile IT)?",
                ["Il go-live è fissato a marzo (direzione commerciale)", "Il go-live è a settembre (responsabile IT)"],
                [@"marzo", @"settembre"], [[3], [7]])],
            [
                new("sms", @"\bsms\b", ForbidMode.Affirmed, "Le notifiche arrivano anche via SMS"),
                new("fatturazione", @"fattur", ForbidMode.Affirmed, "Il portale emette le fatture"),
                new("go-live-scelto", @"\bmarzo\b|\bsettembre\b", ForbidMode.Affirmed, "Il go-live è a marzo", "go-live"),
            ],
            new Correction("nome", "Il portale clienti si chiama Aurora", [], null));
        return new("E1-contesto-grande", "Contesto grande (prova di limite)", "10 messaggi, ~185.000 caratteri: compattazione nativa, nessuna perdita silenziosa di negazioni e fonti",
            "FR-017, SC-010; prova separata: non sostituisce ne' certifica i casi nominali", [script], Exploratory: true);
    }
}

// ==================================================================================================== verificatore

internal sealed record AcceptanceFindings(List<string> Failures, List<string> Notes)
{
    public AcceptanceFindings() : this([], [])
    {
    }

    public bool Passed => Failures.Count == 0;

    public void Merge(AcceptanceFindings other)
    {
        Failures.AddRange(other.Failures);
        Notes.AddRange(other.Notes);
    }
}

/// <summary>
/// Controlli deterministici delle attese su analisi e bozza. Il confronto e' su testo normalizzato (minuscolo, senza accenti, senza
/// marcatori di fonte): NON giudica chiarezza, completezza sostanziale o contraddizioni sottili, che restano alla revisione umana (SC-006).
/// </summary>
internal static class AcceptanceChecker
{
    private static readonly Regex Negation = new(
        @"\b(non|niente|nessun\w*|senza|esclus\w*|fuori|no|neanche|rimand\w*|rinvi\w*|differit\w*|altrove)\b", RegexOptions.CultureInvariant);

    private static readonly Regex OpenMarker = new(
        @"(da (definire|chiarire|verificare|decidere|confermare|chiedere)|non (e|sono|ancora|noto|nota|noti|note|definit\w*|disponibil\w*|chiar\w*|decis\w*)|ancora (da|non)|apert[oa]|domand\w*|incert\w*|\?)", RegexOptions.CultureInvariant);

    private static readonly Regex ConflictMarker = new(
        @"(conflitt\w*|contrast\w*|discord\w*|alternativ\w*|non risolt\w*|apert[oa]|da (chiarire|decidere)|oppure|\bo\b)", RegexOptions.CultureInvariant);

    private static readonly Regex OpenHeading = new(@"apert|conflitt|assunzion|ipotes|da chiarire|da verificare|incertezz|lacun|domand|dubb", RegexOptions.CultureInvariant);

    private static readonly Regex ExclusionHeading = new(@"esclus|fuori perimetro|non inclus|non previst|fuori ambito", RegexOptions.CultureInvariant);

    private static readonly Regex Estimates = new(
        @"ore[- ]uomo|giorni[- ]uomo|man[- ]?days?|\bstim\w+\s+(di\s+|del\s+|dei\s+)?(costi|ore|tempi|impegno|budget)|€|\beuro\b", RegexOptions.CultureInvariant);

    private static readonly Regex ApprovalClaim = new(
        @"\b(documento|bozza|analisi|specifica)\b[^.]{0,30}\b(approvat[oa]|definitiv[oa]|validat[oa]|accettat[oa])\b|\b(approvat[oa]|validat[oa]|accettat[oa]) dal cliente\b|\bversione definitiva\b", RegexOptions.CultureInvariant);

    private static readonly Regex CitationMarker = new(@"\[fonte:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Regex> Cache = [];

    public static string Norm(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('’', '\'');
    }

    public static bool HasEstimate(string text) => Estimates.IsMatch(Norm(text));

    public static bool Matches(string normalizedText, IEnumerable<string> patterns) => patterns.All(p => Rx(p).IsMatch(normalizedText));

    private static Regex Rx(string pattern)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(pattern, out var regex))
            {
                Cache[pattern] = regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            }

            return regex;
        }
    }

    private static readonly Sec[] Positive = [Sec.Goals, Sec.Overview, Sec.Included, Sec.Requirements, Sec.Flows, Sec.Constraints, Sec.Dependencies];

    public static IReadOnlyList<AnalysisItem> Items(AnalysisContent c, Sec section) => section switch
    {
        Sec.Goals => c.Goals,
        Sec.Overview => c.ContextAndScope.Overview,
        Sec.Included => c.ContextAndScope.Included,
        Sec.Excluded => c.ContextAndScope.Excluded,
        Sec.Requirements => c.Requirements,
        Sec.Flows => c.OperationalFlows,
        Sec.Constraints => c.Constraints,
        Sec.Dependencies => c.Dependencies,
        Sec.Assumptions => c.Assumptions,
        Sec.Open => c.OpenQuestions,
        _ => [],
    };

    // ------------------------------------------------------------------------------------------------ analisi

    /// <param name="userIndex">messageId -> indice (da 1) del messaggio PM nel progetto.</param>
    /// <param name="projectMessageIds">tutti i messaggi (PM e assistant) del progetto.</param>
    public static AcceptanceFindings CheckAnalysis(
        ProjectScript script, AnalysisContent c, IReadOnlyDictionary<Guid, int> userIndex, IReadOnlySet<Guid> projectMessageIds, AnalysisContentState expectedState = AnalysisContentState.Unvalidated)
    {
        var f = new AcceptanceFindings();
        if (c.ValidationState != expectedState)
        {
            f.Failures.Add($"stato di validazione {c.ValidationState}, atteso {expectedState}");
        }

        // Fonti: solo messaggi del progetto (nessuna fonte di altri progetti).
        foreach (var source in c.Sources.Where(s => s.Kind == AnalysisSourceKind.Message))
        {
            if (source.MessageId is not { } id || !projectMessageIds.Contains(id))
            {
                f.Failures.Add($"fonte {source.Id}: il messaggio non appartiene al progetto");
            }
        }

        var sourceIndex = c.Sources
            .Where(s => s.Kind == AnalysisSourceKind.Message && s.MessageId is { } m && userIndex.ContainsKey(m))
            .ToDictionary(s => s.Id, s => userIndex[s.MessageId!.Value]);
        IEnumerable<int> Cited(AnalysisItem item) => item.EvidenceRefs.Where(sourceIndex.ContainsKey).Select(r => sourceIndex[r]);

        // Fatti, esclusioni, lacune: presenti nella sezione giusta, con classificazione e fonti corrette.
        foreach (var fact in script.Facts)
        {
            var candidates = fact.Accept.SelectMany(s => Items(c, s)).Where(i => Matches(Norm(i.Text), fact.Match)).ToList();
            if (candidates.Count == 0)
            {
                var elsewhere = Enum.GetValues<Sec>().Except(fact.Accept)
                    .Where(s => Items(c, s).Any(i => Matches(Norm(i.Text), fact.Match))).ToList();
                f.Failures.Add($"omissione: «{fact.Key}» non presente in {string.Join("/", fact.Accept)}"
                    + (elsewhere.Count > 0 ? $" (trovata in {string.Join("/", elsewhere)}: sezione sbagliata)" : ""));
                continue;
            }

            if (fact.Class == AnalysisClassification.Declared)
            {
                var good = candidates.Any(i => i.Classification == AnalysisClassification.Declared && Cited(i).Intersect(fact.From).Any());
                if (!good)
                {
                    var anyDeclared = candidates.Any(i => i.Classification == AnalysisClassification.Declared);
                    f.Failures.Add(anyDeclared
                        ? $"fonte errata: «{fact.Key}» non cita il messaggio PM {string.Join("/", fact.From)}"
                        : $"classificazione: «{fact.Key}» dichiarata dal PM ma non Declared");
                }
            }
        }

        // Divieti: negazioni non invertite, nessuna invenzione, nessun mescolamento fra progetti.
        foreach (var forbid in script.Forbids)
        {
            var pattern = new[] { forbid.Pattern };
            if (forbid.Mode == ForbidMode.Affirmed)
            {
                foreach (var section in Positive)
                {
                    foreach (var item in Items(c, section).Where(i => Matches(Norm(i.Text), pattern) && !Negation.IsMatch(Norm(i.Text))))
                    {
                        f.Failures.Add($"«{forbid.Key}» affermata in {section} (negazione invertita o informazione inventata)");
                    }
                }
            }
            else
            {
                foreach (var text in AllTexts(c).Where(t => Matches(Norm(t), pattern)))
                {
                    f.Failures.Add($"«{forbid.Key}» presente nell'analisi (informazione inventata o di un altro progetto)");
                    break;
                }
            }
        }

        // Conflitti: entrambe le alternative, Open, senza risoluzione, con le fonti dei rispettivi messaggi.
        foreach (var spec in script.Conflicts)
        {
            var found = c.Conflicts.FirstOrDefault(k => spec.AltMatch.Select((p, n) => k.Alternatives.Any(a => Matches(Norm(a.Text), [p]))).All(x => x));
            if (found is null)
            {
                f.Failures.Add($"conflitto «{spec.Key}»: alternative non riportate entrambe (omissione o scelta silenziosa)");
                continue;
            }

            if (found.Status != AnalysisConflictStatus.Open || found.Resolution is not null)
            {
                f.Failures.Add($"conflitto «{spec.Key}»: risolto dall'AI (deve restare Open)");
            }

            for (var n = 0; n < spec.AltMatch.Length; n++)
            {
                var alternative = found.Alternatives.First(a => Matches(Norm(a.Text), [spec.AltMatch[n]]));
                if (!Cited(alternative).Intersect(spec.AltFrom[n]).Any())
                {
                    f.Failures.Add($"conflitto «{spec.Key}»: l'alternativa {n + 1} non cita il messaggio PM {string.Join("/", spec.AltFrom[n])}");
                }
            }
        }

        if (script.Conflicts.Length == 0 && c.Conflicts.Count > 0)
        {
            f.Notes.Add($"{c.Conflicts.Count} conflitti non attesi dalle annotazioni: da valutare nella revisione umana");
        }

        // Ripetizioni letterali nella stessa sezione.
        foreach (var section in Enum.GetValues<Sec>())
        {
            var dup = Items(c, section).GroupBy(i => Norm(i.Text).Trim()).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
            {
                f.Failures.Add($"ripetizione: voce duplicata in {section}");
            }
        }

        if (AllTexts(c).Any(t => Estimates.IsMatch(Norm(t))))
        {
            f.Failures.Add("stima di costi/ore/tempi non presente nel dialogo (FR-016)");
        }

        var expected = script.Facts.Select(x => x.Key).ToHashSet();
        var unexpected = Positive.Concat([Sec.Excluded, Sec.Open, Sec.Assumptions]).Sum(s => Items(c, s)
            .Count(i => !script.Facts.Any(x => x.Accept.Contains(s) && Matches(Norm(i.Text), x.Match))));
        f.Notes.Add($"voci non corrispondenti ad alcuna attesa: {unexpected} (da valutare: possibili invenzioni o dettagli legittimi); attese={expected.Count}");
        return f;
    }

    public static IEnumerable<string> AllTexts(AnalysisContent c) =>
        Enum.GetValues<Sec>().SelectMany(s => Items(c, s)).Select(i => i.Text)
            .Concat(c.Conflicts.SelectMany(k => k.Alternatives.Select(a => a.Text).Append(k.Description).Concat(k.Resolution is null ? [] : [k.Resolution.Reason])));

    // ------------------------------------------------------------------------------------------------ bozza

    private sealed record Unit(string Text, string Raw, string Region);

    private static List<Unit> Units(string markdown)
    {
        var units = new List<Unit>();
        var heading = "";
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                var raw = current.ToString();
                var plain = Norm(CitationMarker.Replace(raw, " ")).Replace("*", "").Replace("_", "").Replace("`", "");
                var h = Norm(heading);
                units.Add(new(Regex.Replace(plain, @"\s+", " ").Trim(), raw, ExclusionHeading.IsMatch(h) ? "excl" : OpenHeading.IsMatch(h) ? "open" : "normal"));
                current.Clear();
            }
        }

        foreach (var line in markdown.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                Flush();
                continue;
            }

            if (t.StartsWith('#'))
            {
                Flush();
                heading = t.TrimStart('#').Trim();
                continue;
            }

            if (Regex.IsMatch(t, @"^([-*+]|\d+[.)])\s"))
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(t);
        }

        Flush();
        return units;
    }

    /// <param name="confirmed">Revisione confermata di input della generazione (fonti valide per le citazioni).</param>
    /// <param name="resolvedConflictKey">Conflitto risolto dal PM prima della generazione (ne va riportata scelta e motivo).</param>
    public static AcceptanceFindings CheckDocument(ProjectScript script, string markdown, AnalysisContent confirmed, bool correctionApplied)
    {
        var f = new AcceptanceFindings();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            f.Failures.Add("bozza vuota");
            return f;
        }

        var units = Units(markdown);
        var resolve = correctionApplied ? script.Correction.Resolve : null;

        // Fatti, esclusioni, lacune e informazioni aggiunte dal PM.
        var facts = script.Facts.Select(x => (x.Key, x.Section, x.Match, Declared: x.Class == AnalysisClassification.Declared)).ToList();
        if (correctionApplied)
        {
            facts.AddRange(script.Correction.Additions.Select(a => (a.Key, a.Section, a.Match, Declared: a.Class == AnalysisClassification.Declared)));
        }

        var uncited = 0;
        foreach (var (key, section, match, declared) in facts)
        {
            var hits = units.Where(u => Matches(u.Text, match)).ToList();
            var ok = section switch
            {
                Sec.Excluded => hits.Where(u => u.Region == "excl" || Negation.IsMatch(u.Text)).ToList(),
                Sec.Open => hits.Where(u => u.Region == "open" || OpenMarker.IsMatch(u.Text)).ToList(),
                _ => hits,
            };
            if (ok.Count == 0)
            {
                f.Failures.Add($"omissione nella bozza: «{key}»"
                    + (hits.Count > 0 ? section == Sec.Excluded ? " (presente ma non come esclusione: negazione invertita?)" : " (presente ma non come punto aperto: lacuna trasformata in fatto?)" : ""));
            }
            else if (declared && section != Sec.Open && !ok.Any(u => CitationMarker.IsMatch(u.Raw)))
            {
                uncited++;
            }
        }

        if (uncited > 0)
        {
            f.Notes.Add($"{uncited} informazioni dichiarate senza fonte [fonte:...] nello stesso punto (da valutare)");
        }

        // Conflitti: aperti restano aperti con tutte le alternative; risolto = scelta e motivo del PM.
        foreach (var spec in script.Conflicts)
        {
            if (resolve is not null && resolve.ConflictKey == spec.Key)
            {
                if (!units.Any(u => Matches(u.Text, [spec.AltMatch[resolve.Alternative]])))
                {
                    f.Failures.Add($"conflitto «{spec.Key}» risolto dal PM: alternativa scelta non riportata nella bozza");
                }

                if (!units.Any(u => Matches(u.Text, [resolve.ReasonMatch])))
                {
                    f.Failures.Add($"conflitto «{spec.Key}» risolto dal PM: motivo non riportato nella bozza");
                }

                continue;
            }

            var found = spec.AltMatch.Select(p => units.Where(u => Matches(u.Text, [p])).ToList()).ToList();
            if (found.Any(x => x.Count == 0))
            {
                f.Failures.Add($"conflitto «{spec.Key}» aperto: alternativa omessa nella bozza (scelta silenziosa)");
            }
            else if (!found.SelectMany(x => x).Any(u => u.Region == "open" || ConflictMarker.IsMatch(u.Text)))
            {
                f.Failures.Add($"conflitto «{spec.Key}» aperto: la bozza non lo presenta come aperto");
            }
        }

        // Divieti.
        foreach (var forbid in script.Forbids)
        {
            if (forbid.ConflictKey is not null && resolve?.ConflictKey == forbid.ConflictKey)
            {
                continue;
            }

            foreach (var unit in units.Where(u => Matches(u.Text, [forbid.Pattern])))
            {
                if (forbid.Mode == ForbidMode.Anywhere)
                {
                    f.Failures.Add($"«{forbid.Key}» presente nella bozza (informazione inventata o di un altro progetto)");
                    break;
                }

                var exempt = unit.Region == "excl" || Negation.IsMatch(unit.Text)
                    || unit.Region == "open" || OpenMarker.IsMatch(unit.Text) || (forbid.ConflictKey is not null && ConflictMarker.IsMatch(unit.Text));
                if (!exempt)
                {
                    f.Failures.Add($"«{forbid.Key}» affermata nella bozza (negazione invertita o scelta silenziosa)");
                    break;
                }
            }
        }

        // Citazioni, ripetizioni, approvazioni, stime.
        var sourceIds = confirmed.Sources.Select(s => s.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markers = CitationMarker.Matches(markdown).Select(m => m.Value[7..^1]).ToList();
        if (markers.Count == 0)
        {
            f.Failures.Add("la bozza non cita alcuna fonte");
        }

        foreach (var id in markers.Where(m => !sourceIds.Contains(m)).Distinct())
        {
            f.Failures.Add($"citazione {id}: non e' una fonte della revisione confermata");
        }

        var repeated = units.Where(u => u.Text.Length >= 40).GroupBy(u => u.Text).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
        {
            f.Failures.Add("ripetizione: lo stesso punto compare piu' volte nella bozza");
        }

        if (units.Any(u => ApprovalClaim.IsMatch(u.Text) && !Negation.IsMatch(u.Text)))
        {
            f.Failures.Add("la bozza si presenta come approvata/definitiva (conferma PM non e' approvazione cliente)");
        }

        if (units.Any(u => Estimates.IsMatch(u.Text)))
        {
            f.Failures.Add("stima di costi/ore/tempi non presente nel dialogo (FR-016)");
        }

        return f;
    }
}

// ==================================================================================================== oracolo deterministico

internal sealed class OracleOptions
{
    /// <summary>Omette la voce con questa chiave (prova che il verificatore rileva le omissioni nel percorso completo).</summary>
    public string? DropFact { get; set; }

    /// <summary>Aggiunge a ogni obiettivo del progetto un riferimento a un altro progetto (contaminazione).</summary>
    public string? Contaminate { get; set; }
}

/// <summary>
/// Costruisce l'analisi e la bozza "ideali" di un progetto dalle sole attese. E' un modello finto: dimostra provenienza, validazione,
/// isolamento e verificatore, NON la qualita' del modello reale.
/// </summary>
internal static class AcceptanceOracle
{
    private static JsonObject Item(string text, AnalysisClassification cls, IEnumerable<Guid> evidence) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["text"] = text,
        ["classification"] = cls.ToString(),
        ["evidenceRefs"] = new JsonArray(evidence.Select(e => (JsonNode)JsonValue.Create(e.ToString())!).ToArray()),
    };

    /// <param name="userMessageIds">Messaggi PM nell'ordine del dialogo (indice 0 = messaggio 1).</param>
    public static JsonObject BuildAnalysis(
        ProjectScript script, Guid projectId, Guid analysisId, long cutoff, IReadOnlyList<Guid> userMessageIds, OracleOptions? options = null)
    {
        options ??= new();
        var sources = new Dictionary<int, Guid>();
        IEnumerable<Guid> Src(IEnumerable<int> indexes) => indexes.Select(i =>
        {
            if (!sources.TryGetValue(i, out var id))
            {
                sources[i] = id = Guid.NewGuid();
            }

            return id;
        }).ToList();

        var sections = Enum.GetValues<Sec>().ToDictionary(s => s, _ => new JsonArray());
        foreach (var fact in script.Facts.Where(x => x.Key != options.DropFact))
        {
            var text = fact is { Section: Sec.Goals } && options.Contaminate is not null ? fact.Text + " (" + options.Contaminate + ")" : fact.Text;
            sections[fact.Section].Add(Item(text, fact.Class, Src(fact.From)));
        }

        var conflicts = new JsonArray();
        foreach (var spec in script.Conflicts)
        {
            conflicts.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["description"] = spec.Description,
                ["status"] = "Open",
                ["alternatives"] = new JsonArray(spec.AltText.Select((t, n) => (JsonNode)Item(t, AnalysisClassification.Declared, Src(spec.AltFrom[n]))).ToArray()),
                ["resolution"] = null,
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["analysisId"] = analysisId.ToString(),
            ["projectId"] = projectId.ToString(),
            ["sourceCutoffSequence"] = cutoff,
            ["validationState"] = "Unvalidated",
            ["sources"] = new JsonArray(sources.OrderBy(s => s.Key).Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Value.ToString(),
                ["kind"] = "Message",
                ["messageId"] = userMessageIds[s.Key - 1].ToString(),
                ["messageRole"] = "user",
                ["revisionId"] = null,
            }).ToArray()),
            ["goals"] = sections[Sec.Goals],
            ["contextAndScope"] = new JsonObject { ["overview"] = sections[Sec.Overview], ["included"] = sections[Sec.Included], ["excluded"] = sections[Sec.Excluded] },
            ["requirements"] = sections[Sec.Requirements],
            ["operationalFlows"] = sections[Sec.Flows],
            ["constraints"] = sections[Sec.Constraints],
            ["dependencies"] = sections[Sec.Dependencies],
            ["assumptions"] = sections[Sec.Assumptions],
            ["openQuestions"] = sections[Sec.Open],
            ["conflicts"] = conflicts,
        };
    }

    /// <summary>Bozza Markdown fedele alla revisione confermata: ogni voce con le proprie fonti, aperti e conflitti dichiarati tali.</summary>
    public static string RenderMarkdown(AnalysisContent c, string name)
    {
        var text = new StringBuilder($"# {name} - bozza da revisionare\n\n");

        string Cite(IEnumerable<Guid> refs) => string.Concat(refs.Select(r => $" [fonte:{r}]"));

        void Section(string title, IEnumerable<string> lines, int level = 2)
        {
            var list = lines.ToList();
            if (list.Count == 0)
            {
                return;
            }

            text.Append(new string('#', level)).Append(' ').AppendLine(title).AppendLine();
            foreach (var line in list)
            {
                text.Append("- ").AppendLine(line);
            }

            text.AppendLine();
        }

        IEnumerable<string> Lines(IEnumerable<AnalysisItem> items) => items.Select(i => i.Text + Cite(i.EvidenceRefs));

        Section("Sintesi e obiettivi", Lines(c.Goals));
        var scope = c.ContextAndScope;
        if (scope.Overview.Count + scope.Included.Count + scope.Excluded.Count > 0)
        {
            text.AppendLine("## Contesto e perimetro").AppendLine();
            Section("Panoramica", Lines(scope.Overview), 3);
            Section("Incluso", Lines(scope.Included), 3);
            Section("Esclusioni dal perimetro", Lines(scope.Excluded), 3);
        }

        Section("Requisiti", Lines(c.Requirements));
        Section("Flussi operativi", Lines(c.OperationalFlows));
        Section("Vincoli e dipendenze", Lines(c.Constraints).Concat(Lines(c.Dependencies)));
        Section("Assunzioni da verificare", c.Assumptions.Select(i => "Ipotesi da verificare: " + i.Text + Cite(i.EvidenceRefs)));
        var open = c.OpenQuestions.Select(i => "Punto aperto: " + i.Text + Cite(i.EvidenceRefs)).ToList();
        foreach (var k in c.Conflicts)
        {
            if (k.Status == AnalysisConflictStatus.Open)
            {
                open.Add($"Conflitto aperto, non risolto: {k.Description} " + string.Join(" Alternativa: ", k.Alternatives.Select(a => a.Text + Cite(a.EvidenceRefs))));
            }
            else
            {
                var chosen = k.Alternatives.Where(a => k.Resolution!.SelectedAlternativeIds.Contains(a.Id)).ToList();
                open.Add($"Conflitto risolto dal PM: {k.Description} Scelta: {string.Join("; ", chosen.Select(a => a.Text))}. Motivo: {k.Resolution!.Reason}{Cite(k.Resolution.EvidenceRefs)}");
            }
        }

        Section("Punti aperti e conflitti", open);
        return text.ToString();
    }
}

// ==================================================================================================== dati per il browser (E2E)

/// <summary>
/// Proiezione dei casi nominali per <c>tests/e2e/project-draft-success.spec.ts</c>: un solo insieme di dati, due consumatori.
/// Il file e' verificato da <c>AcceptanceFixtureTests</c> (si rigenera con BPROJECTS_ACCEPTANCE_REGENERATE=1).
/// </summary>
internal static class AcceptanceE2eData
{
    public static string Serialize()
    {
        static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        static string Key(Sec s) => s switch { Sec.Flows => "flows", Sec.Open => "questions", _ => s.ToString().ToLowerInvariant() };

        var root = new JsonObject
        {
            ["note"] = "Generato da AcceptanceFixtures.cs (AcceptanceE2eData). Non modificare a mano: BPROJECTS_ACCEPTANCE_REGENERATE=1 dotnet test --filter-method *e2e_data*",
            ["cases"] = new JsonArray(AcceptanceCases.Nominal.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["projects"] = new JsonArray(c.Projects.Select(p => (JsonNode)new JsonObject
                {
                    ["key"] = p.Key,
                    ["name"] = p.Name,
                    ["messages"] = Strings(p.Messages),
                    ["facts"] = new JsonArray(p.Facts.Select(f => (JsonNode)new JsonObject
                    {
                        ["key"] = f.Key,
                        ["section"] = Key(f.Section),
                        ["accept"] = Strings(f.Accept.Select(Key)),
                        ["declared"] = f.Class == AnalysisClassification.Declared,
                        ["from"] = new JsonArray(f.From.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
                        ["match"] = Strings(f.Match),
                    }).ToArray()),
                    ["conflicts"] = new JsonArray(p.Conflicts.Select(k => (JsonNode)new JsonObject
                    {
                        ["key"] = k.Key,
                        ["altMatch"] = Strings(k.AltMatch),
                    }).ToArray()),
                    ["forbids"] = new JsonArray(p.Forbids.Select(x => (JsonNode)new JsonObject
                    {
                        ["key"] = x.Key,
                        ["pattern"] = x.Pattern,
                        ["mode"] = x.Mode == ForbidMode.Anywhere ? "anywhere" : "affirmed",
                        ["conflictKey"] = x.ConflictKey,
                    }).ToArray()),
                    ["correction"] = new JsonObject
                    {
                        ["editKey"] = p.Correction.EditKey,
                        ["editedText"] = p.Correction.EditedText,
                        ["additions"] = new JsonArray(p.Correction.Additions.Select(a => (JsonNode)new JsonObject
                        {
                            ["key"] = a.Key,
                            ["section"] = Key(a.Section),
                            ["text"] = a.Text,
                            ["match"] = Strings(a.Match),
                        }).ToArray()),
                        ["resolve"] = p.Correction.Resolve is { } r
                            ? new JsonObject { ["conflictKey"] = r.ConflictKey, ["alternative"] = r.Alternative, ["reason"] = r.Reason, ["reasonMatch"] = r.ReasonMatch }
                            : null,
                    },
                }).ToArray()),
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
}
