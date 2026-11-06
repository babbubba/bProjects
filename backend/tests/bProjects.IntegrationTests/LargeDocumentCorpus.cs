using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using bProjects.App.Documents;

namespace bProjects.IntegrationTests;

/// <summary>Zona del documento (terzi del testo estratto) in cui un fatto gold deve trovarsi.</summary>
public enum CorpusZone
{
    Start,
    Middle,
    End,
}

/// <summary>
/// Fatto gold: frase letterale inserita UNA volta nel documento <see cref="DocKey"/>. <see cref="Text"/> sono i token (minuscoli, senza diacritici; «a|b» = alternative)
/// che devono comparire nel testo di una voce dell'analisi, <see cref="Quote"/> quelli della citazione. Dichiarati qui e solo qui: non si adattano alle risposte.
/// </summary>
public sealed record GoldFact(string Id, string DocKey, CorpusZone Zone, string Sentence, string[] Text, string[] Quote, bool Negative = false, string Class = "Documented");

/// <summary>Conflitto gold: ogni lato (token nella citazione) deve restare un'alternativa di un conflitto Open senza risoluzione; fuori dal conflitto nessun lato è affermato.</summary>
public sealed record GoldConflict(string Id, string[][] Sides, string[] Topic);

/// <summary>Due enunciati equivalenti con parole diverse (in documenti diversi): entrambi presenti e mai trattati come conflitto.</summary>
public sealed record GoldEquivalence(string Id, string[] QuoteA, string[] QuoteB);

public sealed record CorpusDocument(
    string Key, string FileName, DocumentFormat Format, byte[] Bytes, string Sha256, string PlainText, string PlainTextSha256,
    IReadOnlyList<string> Fillers, IReadOnlyList<string> BulkRules);

public sealed record Corpus(
    string Name, IReadOnlyList<CorpusDocument> Documents, IReadOnlyList<GoldFact> Gold, IReadOnlyList<GoldConflict> Conflicts,
    IReadOnlyList<GoldEquivalence> Equivalents, string? PmStatement)
{
    public int PlainTextChars => Documents.Sum(d => d.PlainText.Length);

    /// <summary>Scrive gli originali in <paramref name="directory"/> e un manifesto con gli SHA-256 (file e testo canonico).</summary>
    public string Materialize(string directory)
    {
        Directory.CreateDirectory(directory);
        var manifest = new StringBuilder();
        foreach (var doc in Documents)
        {
            File.WriteAllBytes(Path.Combine(directory, doc.FileName), doc.Bytes);
            manifest.AppendLine($"{doc.FileName} sha256={doc.Sha256} testo-sha256={doc.PlainTextSha256} byte={doc.Bytes.Length} caratteri={doc.PlainText.Length}");
        }

        var path = Path.Combine(directory, "hashes.txt");
        File.WriteAllText(path, manifest.ToString());
        return path;
    }
}

/// <summary>
/// Generatore DETERMINISTICO (seed fisso, PRNG proprio: nessuna dipendenza dalla versione di .NET) dei documenti grandi per T082/T083. Nessun file nel repository: i documenti si
/// producono a tempo di test. Contenuto: (1) fatti gold dichiarati qui, ciascuno una sola volta; (2) riempitivo narrativo NON applicabile (niente cifre, obblighi, termini né parole dei
/// fatti gold); (3) regole secondarie di linea, applicabili, uniche e non in conflitto (non sono gold): servono a dare all'analisi un payload di evidenze oltre la capacità di una
/// chiamata, perché la riconciliazione gerarchica si attiva sul payload delle EVIDENZE estratte e non sul testo grezzo.
/// </summary>
public static class LargeDocumentCorpus
{
    public const ulong Seed = 20261106;

    // ================================================================ gold: documento singolo (T082)

    public static readonly IReadOnlyList<GoldFact> SingleGold =
    [
        new("S01", "cap", CorpusZone.Start, "Il portale Aurora conserva i documenti di trasporto per 84 mesi dalla data di emissione.", ["84", "mesi"], ["84 mesi"]),
        new("S02", "cap", CorpusZone.Start, "L'accesso al portale Aurora non è consentito agli utenti privi di credenziale nominativa.", ["credenzial"], ["credenziale nominativa"], Negative: true),
        new("S03", "cap", CorpusZone.Start, "La fattura elettronica è emessa entro il settimo giorno lavorativo del mese successivo.", ["settimo", "lavorativ"], ["settimo giorno lavorativo"]),
        new("S04", "cap", CorpusZone.Start, "Fanno eccezione i mesi di agosto e dicembre, nei quali il termine per l'emissione della fattura elettronica è il decimo giorno lavorativo.", ["decimo", "agosto"], ["decimo giorno lavorativo"]),
        new("S05", "cap", CorpusZone.Middle, "Le segnalazioni di guasto con priorità alta sono prese in carico entro 40 minuti, esclusi i giorni festivi.", ["40", "minut"], ["40 minuti"]),
        new("S06", "cap", CorpusZone.Middle, "Per gli ordini urgenti il limite di spesa è di 1850 euro; gli importi superiori richiedono l'autorizzazione scritta del responsabile di magazzino.", ["1850|1.850", "autorizzazione"], ["1850 euro"]),
        new("S07", "cap", CorpusZone.Middle, "Il magazzino di Verona non accetta resi di merce deperibile.", ["verona", "resi"], ["non accetta resi"], Negative: true),
        new("S08", "cap", CorpusZone.Middle, "Le copie di sicurezza sono eseguite ogni notte alle 02:30 e conservate per 31 giorni.", ["31", "giorni"], ["31 giorni"]),
        new("S09", "cap", CorpusZone.End, "La penale per il ritardo nella consegna è pari allo 0,4 per cento del valore dell'ordine per ogni giorno lavorativo di ritardo, fino a un massimo del 12 per cento.", ["0,4|0.4", "12"], ["0,4 per cento"]),
        new("S10", "cap", CorpusZone.End, "Il fornitore non risponde dei ritardi causati da scioperi dei vettori comunicati almeno 48 ore prima della consegna.", ["scioper", "48"], ["48 ore"], Negative: true),
        new("S11", "cap", CorpusZone.End, "La reperibilità notturna è garantita soltanto per il sito di Padova.", ["padova", "reperibil"], ["padova"]),
        new("S12", "cap", CorpusZone.End, "Tutte le comunicazioni contrattuali sono inviate esclusivamente tramite posta elettronica certificata.", ["certificata|pec"], ["posta elettronica certificata"]),
        // Conflitto lontano 1: pagamento (inizio contro fine).
        new("S13", "cap", CorpusZone.Start, "Il termine di pagamento delle fatture è di 30 giorni dalla data di ricevimento.", ["30", "giorni"], ["30 giorni"]),
        new("S14", "cap", CorpusZone.End, "Ai fini del presente capitolato il termine di pagamento delle fatture è di 60 giorni data fattura.", ["60", "giorni"], ["60 giorni"]),
        // Conflitto lontano 2: finestra di manutenzione (metà contro fine).
        new("S15", "cap", CorpusZone.Middle, "La finestra di manutenzione programmata cade il primo sabato di ogni mese, dalle 22:00 alle 02:00.", ["primo sabato"], ["primo sabato"]),
        new("S16", "cap", CorpusZone.End, "La finestra di manutenzione programmata cade la terza domenica di ogni mese.", ["terza domenica"], ["terza domenica"]),
    ];

    public static readonly IReadOnlyList<GoldConflict> SingleConflicts =
    [
        new("pagamento", [["30 giorni"], ["60 giorni"]], ["pagament"]),
        new("manutenzione", [["primo sabato"], ["terza domenica"]], ["manutenzion"]),
    ];

    // ================================================================ gold: più documenti (T083)

    public const string PmStatementMulti = "Il cliente mi ha confermato per telefono che il limite di ordini per ciascuna sede è di 25 al giorno e che il termine di pagamento delle fatture è di 45 giorni.";

    public static readonly IReadOnlyList<GoldFact> MultiGold =
    [
        // d1 — contratto quadro (TXT)
        new("M01", "d1", CorpusZone.Start, "Il contratto quadro Zefiro ha durata di 48 mesi dalla data di firma.", ["48", "mesi"], ["48 mesi"]),
        new("M02", "d1", CorpusZone.Start, "Il rinnovo tacito del contratto non è previsto.", ["rinnov", "tacit"], ["rinnovo tacito"], Negative: true),
        new("M06", "d1", CorpusZone.Start, "Le penali per i ritardi di consegna sono determinate secondo la tabella dell'Allegato C.", ["penal", "allegato c"], ["allegato c"]),
        new("M07", "d1", CorpusZone.Start, "Il pacchetto Ambra comprende il monitoraggio degli ordini.", ["ambra", "monitoraggio"], ["pacchetto ambra"]),
        new("M03", "d1", CorpusZone.Middle, "Il termine di pagamento delle fatture è di 30 giorni dalla data di ricevimento.", ["30", "giorni"], ["30 giorni"]),
        new("M04", "d1", CorpusZone.Middle, "La garanzia sui ricambi ha durata di 24 mesi dalla consegna.", ["24", "mesi"], ["24 mesi"]),
        new("M05", "d1", CorpusZone.End, "I ricambi richiesti con la procedura ordinaria sono consegnati entro 5 giorni lavorativi.", ["5", "lavorativ"], ["5 giorni lavorativi"]),
        // d2 — allegato tecnico (Markdown)
        new("M08", "d2", CorpusZone.Start, "Il limite di ordini per ciascuna sede è di 40 al giorno.", ["40"], ["40 al giorno"]),
        new("M12", "d2", CorpusZone.Middle, "Gli ordini urgenti non sono accettati dopo le 16:00, tranne quelli dei clienti di categoria Gold.", ["gold"], ["16:00"], Negative: true),
        new("M09", "d2", CorpusZone.Middle, "I pezzi di ricambio ordinari arrivano al più tardi entro una settimana lavorativa.", ["settimana"], ["settimana lavorativa"]),
        new("M10", "d2", CorpusZone.Middle, "Nel pacchetto Ambra il monitoraggio degli ordini viene aggiornato ogni 15 minuti.", ["ambra", "15", "minut"], ["15 minuti"]),
        new("M11", "d2", CorpusZone.End, "Allegato C: per ogni giorno di ritardo nella consegna si applica una penale dell'1,5 per cento del valore dell'ordine, con un tetto del 15 per cento.", ["1,5|1.5", "15"], ["1,5 per cento"]),
        // d3 — verbale di collaudo (DOCX)
        new("M13", "d3", CorpusZone.Start, "La garanzia sui ricambi ha durata di 36 mesi dalla consegna.", ["36", "mesi"], ["36 mesi"]),
        new("M14", "d3", CorpusZone.Middle, "Il termine di pagamento delle fatture è di 60 giorni data fattura.", ["60", "giorni"], ["60 giorni"]),
        new("M15", "d3", CorpusZone.Middle, "Il collaudo finale si svolgerà il 12 marzo 2027 presso la sede di Brescia.", ["12 marzo 2027|2027-03-12|12/03/2027", "brescia"], ["12 marzo 2027"]),
        new("M16", "d3", CorpusZone.End, "Le modifiche ai listini non sono ammesse durante il periodo di collaudo.", ["listin", "collaud"], ["listini"], Negative: true),
        new("M17", "d3", CorpusZone.End, "Se il collaudo non viene superato, il pagamento della seconda rata è sospeso.", ["second", "rata", "sospes"], ["seconda rata"]),
        // dichiarazioni del PM (messaggio di chat): fonte Message, classificazione Declared
        new("P1", "pm", CorpusZone.Start, PmStatementMulti, ["25"], ["25 al giorno"], Class: "Declared"),
        new("P2", "pm", CorpusZone.Start, PmStatementMulti, ["45"], ["45 giorni"], Class: "Declared"),
    ];

    public static readonly IReadOnlyList<GoldConflict> MultiConflicts =
    [
        new("pagamento (doc/doc/PM)", [["30 giorni"], ["60 giorni"], ["45 giorni"]], ["pagament"]),
        new("garanzia (doc/doc)", [["24 mesi"], ["36 mesi"]], ["garanzia"]),
        new("limite ordini (doc/PM)", [["40 al giorno"], ["25 al giorno"]], ["limite", "sede"]),
    ];

    public static readonly IReadOnlyList<GoldEquivalence> MultiEquivalents =
    [
        new("consegna ricambi: cinque giorni lavorativi / una settimana lavorativa", ["5 giorni lavorativi"], ["settimana lavorativa"]),
    ];

    /// <summary>Radici lessicali dei fatti gold e dei loro oggetti: mai nel riempitivo né nelle regole secondarie (nessun fatto gold duplicato o spurio).</summary>
    public static readonly IReadOnlyList<string> ReservedStems =
    [
        "aurora", "verona", "padova", "ambra", "zefiro", "brescia", "gold", "listin", "collaud", "garanzi", "pagament", "fattur", "penal", "manutenzion", "reperibil", "guast",
        "scioper", "ordin", "consegn", "ricambi", "ricambio", "termine", "limite", "giorni", "giorno", "mesi", "euro", "minuti", "credenzial", "portale", "capitolato", "contratt",
        "monitoraggio", "allegato", "rinnov", "rata", "cliente", "fornitore", "resi", "copie", "sicurezza", "settimana", "sabato", "domenica", "posta", "comunicazion", "sede",
    ];

    /// <summary>Parole che un estrattore potrebbe leggere come requisito, obbligo, termine o numero: vietate nel riempitivo.</summary>
    public static readonly IReadOnlyList<string> ObligationWords =
    [
        "deve", "devono", "dovr", "obblig", "vietat", "divieto", "consentit", "ammess", "entro", "massimo", "minimo", "almeno", "esclusiv", "soltanto", "garantit", "requisit", "regola",
        "per cento", "tranne", "salvo", "eccetto", "non è", "non sono", "non può", "non possono",
    ];

    // ================================================================ costruzione

    /// <summary>Documento singolo TXT (T082): <paramref name="fillerChars"/> di riempitivo, <paramref name="bulkRules"/> regole secondarie, 16 fatti gold.</summary>
    public static Corpus Single(int fillerChars, int bulkRules) =>
        new("T082-single", [Build("cap", "capitolato-aurora.txt", DocumentFormat.Text, "Capitolato dei servizi del portale Aurora", SingleGold, fillerChars, bulkRules, 40, Seed)],
            SingleGold, SingleConflicts, [], null);

    /// <summary>Tre documenti di formati diversi (TXT, Markdown, DOCX) con fatti complementari, conflitti fra documenti e con il PM, equivalenze parafrasate (T083).</summary>
    public static Corpus Multi(int fillerChars, int bulkRulesPerDocument)
    {
        var each = fillerChars / 3;
        return new("T083-multi",
            [
                Build("d1", "contratto-quadro-zefiro.txt", DocumentFormat.Text, "Contratto quadro di fornitura Zefiro", MultiGold, each, bulkRulesPerDocument, 24, Seed + 1),
                Build("d2", "allegato-tecnico-zefiro.md", DocumentFormat.Markdown, "Allegato tecnico Zefiro", MultiGold, each, bulkRulesPerDocument, 24, Seed + 2),
                Build("d3", "verbale-collaudo-zefiro.docx", DocumentFormat.Docx, "Verbale di collaudo Zefiro", MultiGold, each, bulkRulesPerDocument, 24, Seed + 3),
            ],
            MultiGold, MultiConflicts, MultiEquivalents, PmStatementMulti);
    }

    private static CorpusDocument Build(string key, string fileName, DocumentFormat format, string title, IReadOnlyList<GoldFact> allGold, int fillerChars, int bulkRules, int sections, ulong seed)
    {
        var rng = new Prng(seed);
        var gold = allGold.Where(g => g.DocKey == key).ToList();
        var placed = new Dictionary<int, List<string>>();
        foreach (var zone in Enum.GetValues<CorpusZone>())
        {
            var anchor = zone switch { CorpusZone.Start => 1, CorpusZone.Middle => sections / 2 - 1, _ => sections - 2 - gold.Count(g => g.Zone == CorpusZone.End) };
            var k = 0;
            foreach (var fact in gold.Where(g => g.Zone == zone))
            {
                placed.GetOrAdd(anchor + k++).Add(fact.Sentence);
            }
        }

        var rules = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bulkRules; i++)
        {
            rules.Add(BulkRule(rng, names));
        }

        var produced = 0L;
        var fillers = new List<string>();
        var body = new List<(string Heading, List<string> Paragraphs)>();
        var nextRule = 0;
        for (var s = 0; s < sections; s++)
        {
            var paragraphs = new List<string>();
            while (produced < (long)fillerChars * (s + 1) / sections)
            {
                var filler = FillerParagraph(rng);
                produced += filler.Length;
                fillers.Add(filler);
                paragraphs.Add(filler);
            }

            if (placed.TryGetValue(s, out var sentences))
            {
                paragraphs.InsertRange(paragraphs.Count / 2, sentences);
            }

            var take = (bulkRules * (s + 1) / sections) - nextRule;
            for (var r = 0; r < take; r += 6)
            {
                paragraphs.Add(string.Join(' ', rules.Skip(nextRule + r).Take(Math.Min(6, take - r))));
            }

            nextRule += take;
            body.Add((Heading(s), paragraphs));
        }

        var plain = new StringBuilder(title).Append("\n\n");
        foreach (var (heading, paragraphs) in body)
        {
            plain.Append(heading).Append("\n\n").AppendJoin("\n\n", paragraphs).Append("\n\n");
        }

        var plainText = plain.ToString().TrimEnd() + "\n";
        var bytes = format switch
        {
            DocumentFormat.Markdown => Encoding.UTF8.GetBytes(Markdown(title, body)),
            DocumentFormat.Docx => Docx(title, body),
            _ => Encoding.UTF8.GetBytes(plainText),
        };
        return new CorpusDocument(key, fileName, format, bytes, Hex(bytes), plainText, Hex(Encoding.UTF8.GetBytes(plainText)), fillers, rules);
    }

    private static string Markdown(string title, List<(string Heading, List<string> Paragraphs)> body)
    {
        var text = new StringBuilder("# ").Append(title).Append("\n\n");
        foreach (var (heading, paragraphs) in body)
        {
            text.Append("## ").Append(heading).Append("\n\n").AppendJoin("\n\n", paragraphs).Append("\n\n");
        }

        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>DOCX minimale (document.xml con soli paragrafi): zip con timestamp fisso, quindi byte-identico a parità di runtime.</summary>
    private static byte[] Docx(string title, List<(string Heading, List<string> Paragraphs)> body)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        void Paragraph(string text) => xml.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(System.Security.SecurityElement.Escape(text)).Append("</w:t></w:r></w:p>");
        Paragraph(title);
        foreach (var (heading, paragraphs) in body)
        {
            Paragraph(heading);
            paragraphs.ForEach(Paragraph);
        }

        xml.Append("</w:body></w:document>");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            Entry("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Entry("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry("word/document.xml", xml.ToString());
        }

        return stream.ToArray();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ================================================================ testo

    private static readonly string[] HeadingBank =
    [
        "Cenni storici", "Il territorio", "La comunità", "Gli ambienti di lavoro", "Le stagioni", "Le persone", "Il paesaggio", "Le tradizioni", "Gli spazi comuni", "La memoria",
        "Le feste", "I sentieri", "Il quartiere", "Le botteghe", "Il giardino", "La piazza",
    ];

    private static string Heading(int index)
    {
        var round = index / HeadingBank.Length;
        return HeadingBank[index % HeadingBank.Length] + (round == 0 ? "" : " " + new string('I', round + 1));
    }

    private static readonly string[] Subjects =
    [
        "La storica palazzina dell'azienda", "Il cortile interno dello stabilimento", "La biblioteca dei dipendenti", "Il giardino sul retro", "La mensa del secondo piano",
        "L'ingresso principale dell'edificio", "Il corridoio dei laboratori", "La sala più grande dell'edificio", "La piccola cappella accanto al parcheggio",
        "Il murale dipinto dagli studenti del liceo vicino", "La fontana al centro della piazza", "Il campanile del paese", "L'orto condiviso dai colleghi",
        "La terrazza panoramica dell'ultimo piano", "Il viale di tigli davanti al cancello", "La vecchia fornace trasformata in spazio per le mostre",
    ];

    private static readonly string[] Predicates =
    [
        "è stata dipinta di un colore caldo che ricorda le sere d'estate", "si affaccia su una strada ombreggiata frequentata da ciclisti", "è stata ricostruita con pietre recuperate da una cascina abbandonata",
        "ospita da molti anni una piccola mostra di fotografie", "conserva ancora le panche di legno scelte dai primi fondatori", "è il luogo preferito dai colleghi per una pausa all'aria aperta",
        "ricorda a chi arriva la storia artigiana del territorio", "viene sistemata ogni primavera da un gruppo di volontari", "racconta con discrezione il carattere tranquillo della comunità",
        "si riempie di luce nelle mattine limpide", "è stata ripresa in un documentario girato da una giovane regista", "ha visto crescere generazioni di famiglie del posto",
    ];

    private static readonly string[] Tails =
    [
        "come accade da molte stagioni", "senza fare rumore", "tra la curiosità dei passanti", "con grande orgoglio di chi ci lavora", "nonostante il passare degli anni", "come raccontano i più anziani",
    ];

    private static readonly string[] Closers =
    [
        "Gli abitanti del quartiere ne parlano con affetto.", "Chi visita l'edificio per la prima volta se ne ricorda a lungo.", "Una vecchia fotografia in bianco e nero lo mostra quasi identico.",
        "Nelle giornate di vento si sente il profumo dei tigli.", "Un anziano giardiniere ama raccontarne gli aneddoti.", "Gli studenti in visita lo disegnano spesso sui loro quaderni.",
        "Dalle finestre si vedono le colline e i tetti rossi del borgo.", "Durante le feste di paese diventa il punto d'incontro di tutti.",
    ];

    private static string FillerParagraph(Prng rng)
    {
        var sentences = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            sentences.Add(i == 4 || rng.Next(3) == 0
                ? Closers[rng.Next(Closers.Length)]
                : $"{Subjects[rng.Next(Subjects.Length)]} {Predicates[rng.Next(Predicates.Length)]}{(rng.Next(2) == 0 ? ", " + Tails[rng.Next(Tails.Length)] : "")}.");
        }

        return string.Join(' ', sentences);
    }

    private static readonly string[] Consonants = ["b", "c", "d", "f", "g", "l", "m", "n", "p", "r", "s", "t", "v"];
    private static readonly string[] Vowels = ["a", "e", "i", "o", "u"];

    /// <summary>Regola secondaria di linea: applicabile, con nome di linea univoco e numero proprio; nessun legame con i fatti gold.</summary>
    private static string BulkRule(Prng rng, HashSet<string> names)
    {
        string name;
        do
        {
            name = string.Concat(Enumerable.Range(0, 3).Select(_ => Consonants[rng.Next(Consonants.Length)] + Vowels[rng.Next(Vowels.Length)]));
            name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        while (!names.Add(name) || ReservedStems.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)));

        var n = 12 + rng.Next(880);
        return rng.Next(3) switch
        {
            0 => $"Per la linea {name} il lotto minimo di rifornimento è di {n} pezzi.",
            1 => $"La scheda tecnica della linea {name} prevede un imballo da {n} pezzi per scatola.",
            _ => $"Nel reparto {name} la temperatura di conservazione è fissata a {n % 40 + 2} gradi.",
        };
    }

    /// <summary>PRNG LCG a 64 bit (Knuth MMIX): sequenza fissa su qualunque runtime.</summary>
    private sealed class Prng(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int exclusiveMax)
        {
            _state = unchecked(_state * 6364136223846793005UL + 1442695040888963407UL);
            return (int)((_state >> 33) % (ulong)exclusiveMax);
        }
    }

    private static List<string> GetOrAdd(this Dictionary<int, List<string>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        return list;
    }
}
