using System.Text.RegularExpressions;

namespace bProjects.IntegrationTests;

/// <summary>
/// Protezione fail-closed dei test che possono fare inferenza reale sul DGX: endpoint, modello e chiave nell'ambiente NON bastano,
/// serve l'opt-in esplicito <c>BPROJECTS_TEST_DGX_ALLOW=1</c>. Prove deterministiche, senza rete e senza mai contattare il DGX.
/// </summary>
public sealed class DgxGuardTests
{
    private static readonly string[] GuardTokens = ["RequireDgx(", "RequireDgxOptions(", "RequireProductionLikeOptions(", "DocumentTestEnvironment.RequireDgx("];

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // ---------------------------------------------------------------- opt-in

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("yes", false)]
    [InlineData(" 1", false)]
    [InlineData("1", true)]
    public void Only_the_exact_value_1_is_an_opt_in(string? value, bool expected)
    {
        var read = value is null ? Env() : Env((TestEnvironment.DgxAllowVariable, value));

        Assert.Equal(expected, TestEnvironment.DgxAllowedFrom(read));
    }

    [Fact]
    public void Endpoint_model_and_key_alone_never_make_the_DGX_available()
    {
        var read = Env(
            (TestEnvironment.DgxEndpointVariable, "http://dgx.invalid/v1"),
            (TestEnvironment.DgxModelVariable, "modello"),
            (TestEnvironment.DgxApiKeyVariable, "chiave"));

        var reason = TestEnvironment.DgxUnavailableReason(read);

        Assert.NotNull(reason);
        Assert.Contains(TestEnvironment.DgxAllowVariable, reason);
        Assert.Contains("nessuna inferenza", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non è un PASS", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_opt_in_a_missing_endpoint_is_reported_by_name_and_a_complete_setup_is_available()
    {
        var allow = (TestEnvironment.DgxAllowVariable, "1");

        Assert.Contains(TestEnvironment.DgxEndpointVariable, TestEnvironment.DgxUnavailableReason(Env(allow)));
        Assert.Null(TestEnvironment.DgxUnavailableReason(Env(allow, (TestEnvironment.DgxEndpointVariable, "http://dgx.invalid/v1"))));
    }

    [Fact]
    public void The_real_process_getters_are_null_whenever_the_opt_in_is_absent_even_if_the_variables_exist()
    {
        if (TestEnvironment.DgxAllowed)
        {
            return;   // opt-in dato esplicitamente per questa esecuzione: nulla da escludere
        }

        Assert.Null(TestEnvironment.DgxEndpoint);
        Assert.Null(TestEnvironment.DgxApiKey);
        Assert.Null(TestEnvironment.DgxModel);
        Assert.NotNull(TestEnvironment.DgxUnavailableReason());
    }

    // ---------------------------------------------------------------- punto di accesso unico

    [Fact]
    public void No_test_source_reads_the_DGX_variables_outside_TestEnvironment()
    {
        var offenders = new List<string>();
        foreach (var file in TestSources().Where(f => Path.GetFileName(f) is not ("TestEnvironment.cs" or "DgxGuardTests.cs")))
        {
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Regex.IsMatch(code, @"GetEnvironmentVariable\w*\s*\([^)]*(DGX|Dgx)")
                    || Regex.IsMatch(code, @"""BPROJECTS_TEST_DGX_[A-Z_]*""")
                    || Regex.IsMatch(code, @"GetEnvironmentVariables\s*\("))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{lineNo}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Lettura diretta delle variabili DGX fuori da TestEnvironment: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_source_with_MafReal_tests_goes_through_a_DGX_guard_helper()
    {
        var unguarded = new List<string>();
        foreach (var file in TestSources())
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "DgxGuardTests.cs" || !Regex.IsMatch(text, @"Trait\(\s*""Category""\s*,\s*(TestCategories\.MafReal|""MafReal"")\s*\)"))
            {
                continue;
            }

            // DocumentSmallFlowMafRealTests decide con un flag costruito solo dai getter di TestEnvironment (null senza opt-in).
            var guarded = GuardTokens.Any(t => text.Contains(t, StringComparison.Ordinal))
                || text.Contains("TestEnvironment.DgxEndpoint", StringComparison.Ordinal);
            if (!guarded)
            {
                unguarded.Add(Path.GetFileName(file));
            }
        }

        Assert.True(unguarded.Count == 0, "File con test MafReal senza guardia DGX: " + string.Join(", ", unguarded));
    }

    private static IEnumerable<string> TestSources()
    {
        var anchor = Locate("bProjects.IntegrationTests/TestEnvironment.cs");
        return Directory.EnumerateFiles(Path.GetDirectoryName(anchor)!, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string Locate(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var prefix in new[] { "", "tests", Path.Combine("backend", "tests") })
            {
                var path = Path.Combine(dir.FullName, prefix, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        throw new FileNotFoundException($"{relative} non trovato risalendo da {AppContext.BaseDirectory}: la guardia non può verificare i sorgenti.");
    }
}
