using System.Security.Cryptography;
using System.Text;
using bProjects.App.AI;

namespace bProjects.IntegrationTests;

/// <summary>
/// 002 reconciliation-hardening RH-014 (M1) — prompt unico <c>document-reconcile-v6</c> della definizione v11, organizzato per stadio. Verifica: v3/v4/v5 invariati (SHA), SHA del v6 pinnato, nessuna istruzione sugli
/// item delle foglie né di copertura, il tetto di 16 solo nella sezione dell'espansione, istruzioni di uno stadio assenti dalle sezioni degli altri e un test di diff che prova che le differenze dal v5 sono solo quelle
/// approvate (sei righe sostituite, nessun'altra riga del v5 persa o riordinata). Nessun database, nessuna inferenza.
/// </summary>
public sealed class ReconcilePromptV6Tests
{
    private static string File(string name) =>
        System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "bProjects.App", "AI", "Prompts", name)).ReplaceLineEndings("\n");

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string V5 => File("document-reconcile-v5.md");

    private static string V6 => File("document-reconcile-v6.md");

    /// <summary>Corpo di una sottosezione «## Nome»: dal titolo alla sezione successiva (o alla fine).</summary>
    private static string Section(string name)
    {
        var text = V6;
        var start = text.IndexOf($"## {name}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"sezione «{name}» assente");
        var next = text.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        var lastBlock = text.IndexOf("\n\nUn'etichetta inesistente", start, StringComparison.Ordinal);
        var end = next >= 0 ? next : lastBlock >= 0 ? lastBlock : text.Length;
        return text[start..end];
    }

    private static readonly string[] Stages = ["Foglie", "Riduzione", "Cross", "Espansione", "Confronto"];

    [Fact]
    public void The_historical_prompts_v3_v4_and_v5_are_byte_for_byte_unchanged_and_the_v6_sha_is_pinned()
    {
        Assert.Equal("0503d17360e21e8f72e357d1fcf0f0e32fbead294d970193af4eb61ff847c9e5", Sha(File("document-reconcile-v3.md")));
        Assert.Equal("d3e46528736e856eedfa43e1fa41785b0a87ab7ddba91881f57bb9c09c0da79d", Sha(File("document-reconcile-v4.md")));
        Assert.Equal("7e11e26c8e7a5865e36253504d714c82926249b7328f3e22c49de2ba8671057c", Sha(V5));
        Assert.Equal("2e68e7f2caf8ab8ccf6508fcc9d325a946a3dddcd73761cf101851684abf1beb", Sha(V6));
    }

    [Fact]
    public void InstructionsFor_serves_the_v6_embedded_resource_and_still_rejects_an_unknown_version()
    {
        Assert.Equal(V6.Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6).ReplaceLineEndings("\n"));
        Assert.Equal(V5.Trim(), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5).ReplaceLineEndings("\n"));
        Assert.NotEqual(ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV5), ReconcileAliasContracts.InstructionsFor(ReconcileAliasContracts.PromptVersionV6));
        Assert.Equal("document-reconcile-v6", ReconcileAliasContracts.PromptVersionV6);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconcileAliasContracts.InstructionsFor("document-reconcile-v7"));
    }

    [Fact]
    public void The_v6_has_one_section_per_stage_in_order_and_describes_no_item_and_no_leaf_coverage_duty()
    {
        var text = V6;
        var headings = text.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(l => l[3..]).ToList();
        Assert.Equal(Stages, headings);

        // Nella v11 gli item li compone il server: nessun elemento `{"key":"i1",...}`, nessuna sezione, nessuna classificazione, nessun obbligo di copertura.
        Assert.DoesNotContain("\"key\":\"i1\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"section\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deve essere citata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Declared` (dichiarato", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scopeOverview", text, StringComparison.Ordinal);
        Assert.DoesNotContain("openQuestions", text, StringComparison.Ordinal);
        Assert.Contains("- `items`: sempre `[]`: gli item delle evidenze li compone il server.", text, StringComparison.Ordinal);
        Assert.Contains("- `noFindings`: facoltativo: una breve spiegazione se il batch non ha esiti, altrimenti `null`. È solo informativo.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("una motivazione non vuota se `items`, `candidates` e `patches` sono tutti vuoti", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_of_16_and_the_do_not_select_the_first_16_rule_appear_only_in_the_expansion_section()
    {
        Assert.Contains("al massimo 16 elementi", Section("Espansione"), StringComparison.Ordinal);
        Assert.Contains("non selezionare i primi 16", Section("Espansione"), StringComparison.Ordinal);
        foreach (var other in Stages.Where(s => s != "Espansione"))
        {
            Assert.DoesNotContain("16", Section(other), StringComparison.Ordinal);
        }

        var outsideSections = V6[..V6.IndexOf("\n## ", StringComparison.Ordinal)];
        Assert.DoesNotContain("16", outsideSections, StringComparison.Ordinal);                   // né nella parte comune
        var all = new System.Text.RegularExpressions.Regex("16").Matches(V6).Count;                 // «al massimo 16», «più di 16», «esattamente 16» e «primi 16»
        Assert.Equal(4, all);
        Assert.Equal(all, new System.Text.RegularExpressions.Regex("16").Matches(Section("Espansione")).Count);   // tutte nella sezione dell'espansione
    }

    [Fact]
    public void Each_stage_instruction_appears_only_in_the_section_of_its_stage()
    {
        // Candidati dedotti da formulazioni incompatibili: solo foglie (l'istruzione di scansione non deve contaminare il confronto).
        Assert.Contains("proponi il candidato", Section("Foglie"), StringComparison.Ordinal);
        foreach (var other in Stages.Where(s => s != "Foglie"))
        {
            Assert.DoesNotContain("proponi il candidato", Section(other), StringComparison.Ordinal);
        }

        Assert.Contains("MAI le coppie", Section("Espansione"), StringComparison.Ordinal);
        Assert.DoesNotContain("MAI le coppie", V6.Replace(Section("Espansione"), string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Unresolved", Section("Espansione"), StringComparison.Ordinal);
        Assert.DoesNotContain("Unresolved", V6.Replace(Section("Espansione"), string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // Il confronto: candidates sempre vuoto, focus obbligatorio, Open con motivo obbligatorio e Compared con motivo facoltativo.
        var compare = Section("Confronto");
        Assert.Contains("- `candidates`: sempre `[]`.", compare, StringComparison.Ordinal);
        Assert.Contains("obbligatorio, riferito al candidato in esame", compare, StringComparison.Ordinal);
        Assert.Contains("`{\"outcome\":\"Compared\",\"reason\":null}` (nessuna relazione o relazione già chiara; il motivo è facoltativo)", compare, StringComparison.Ordinal);
        Assert.Contains("`reason` obbligatorio e non vuoto", compare, StringComparison.Ordinal);
        Assert.Contains("Non risolvere mai il dubbio.", compare, StringComparison.Ordinal);
        Assert.DoesNotContain("candidato, non decidere", compare, StringComparison.Ordinal);

        // Le foglie, la riduzione e il cross non hanno focus né patch.
        foreach (var stage in new[] { "Foglie", "Riduzione", "Cross" })
        {
            Assert.Contains("- `focus`: `null`. `patches`: `[]`.", Section(stage), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_already_proposed_items_line_carries_the_handle_and_the_evidence_but_no_text()
    {
        var line = V6.Split('\n').Single(l => l.StartsWith("- `Item già proposti:`", StringComparison.Ordinal));
        Assert.Contains("righe `[i3] (E3, E4)`", line, StringComparison.Ordinal);
        Assert.DoesNotContain("(E3, E4) testo", line, StringComparison.Ordinal);
        Assert.Contains("senza testo", line, StringComparison.Ordinal);
        Assert.Contains("`Relazioni note:` righe `[R1] E3 Contradicts E5 (base=...)`.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_differences_from_the_v5_are_only_the_approved_ones()
    {
        var v5 = V5.Split('\n');
        var v6 = V6.Split('\n');

        // 1. Le righe del v5 non più presenti nel v6 sono esattamente sei, identificate per prefisso: elementi sostituiti, mai cancellati in silenzio.
        string[] replacedPrefixes =
        [
            "- `Item già proposti:`", "Un unico oggetto JSON compatto", "- `noFindings`:", "- `items`:", "- `candidates`:", "- `focus`:",
        ];
        var removed = v5.Where(l => !v6.Contains(l)).ToList();
        Assert.Equal(replacedPrefixes.Length, removed.Count);
        Assert.All(replacedPrefixes, p => Assert.Single(removed, l => l.StartsWith(p, StringComparison.Ordinal)));

        // 2. Tutte le altre righe del v5 (etichette, anti-istruzioni, descriptor, patch, intestazioni, chiusura) restano identiche e nello stesso ordine relativo.
        var kept = v5.Where(l => v6.Contains(l)).ToList();
        var cursor = 0;
        foreach (var line in kept)
        {
            var index = Array.IndexOf(v6, line, cursor);
            Assert.True(index >= cursor, $"riga del v5 spostata o persa: {line[..Math.Min(80, line.Length)]}");
            cursor = index + 1;
        }

        // 3. Le righe nuove sono solo: le versioni sostitutive delle sei righe, il candidato comune, le intestazioni di stadio e il contenuto sotto di esse.
        var firstStage = Array.IndexOf(v6, "## Foglie");
        var added = v6.Select((l, i) => (l, i)).Where(t => !v5.Contains(t.l) && t.l.Length > 0).ToList();
        Assert.All(added.Where(t => t.i < firstStage), t => Assert.True(
            t.l.StartsWith("- `Item già proposti:`", StringComparison.Ordinal) || t.l.StartsWith("Un unico oggetto JSON compatto", StringComparison.Ordinal) || t.l.StartsWith("- `noFindings`:", StringComparison.Ordinal)
            || t.l.StartsWith("- `items`:", StringComparison.Ordinal) || t.l.StartsWith("- Un candidato, quando lo stadio ne prevede", StringComparison.Ordinal), $"riga nuova non prevista: {t.l}"));
        Assert.True(added.Count(t => t.i >= firstStage) >= Stages.Length);                       // le cinque sezioni di stadio
    }
}
