using bProjects.App.AI;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace bProjects.IntegrationTests;

/// <summary>Classificazione degli errori (contracts/ai-workflow.md §2): i guasti transitori dell'infrastruttura non sono errori permanenti del modello.</summary>
public sealed class ModelFailureTests
{
    [Fact]
    public void A_transient_Npgsql_exception_is_transient()
    {
        var failure = ModelFailure.Classify(new NpgsqlException("connessione persa", new IOException()));

        Assert.True(failure.IsTransient);
    }

    [Fact]
    public void A_non_transient_Npgsql_exception_is_permanent()
    {
        var failure = ModelFailure.Classify(new PostgresException("errore", "ERROR", "ERROR", "23505"));

        Assert.False(failure.IsTransient);
        Assert.Equal("model_error", failure.Code);
    }

    [Fact]
    public void A_DbUpdateException_with_a_transient_inner_cause_is_transient_and_with_a_permanent_one_is_not()
    {
        Assert.True(ModelFailure.Classify(new DbUpdateException("salvataggio", new NpgsqlException("giù", new TimeoutException()))).IsTransient);
        Assert.False(ModelFailure.Classify(new DbUpdateException("salvataggio", new PostgresException("dup", "ERROR", "ERROR", "23505"))).IsTransient);
        Assert.False(ModelFailure.Classify(new DbUpdateException("salvataggio")).IsTransient);
    }

    [Fact]
    public void A_TimeoutException_is_transient_also_when_wrapped()
    {
        Assert.True(ModelFailure.Classify(new TimeoutException()).IsTransient);
        Assert.True(ModelFailure.Classify(new InvalidOperationException("incapsulato", new TimeoutException())).IsTransient);
        Assert.False(ModelFailure.Classify(new InvalidOperationException("altro", new ArgumentException())).IsTransient);
    }
}
