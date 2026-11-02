using bProjects.App.Analysis;
using bProjects.App.Conversations;
using bProjects.App.Documents;
using Microsoft.Extensions.Options;

namespace bProjects.App.AI;

/// <summary>
/// Composizione DI dei workflow AI nel dispatcher esistente (T123): i grafi 001 (project-analysis-v3, project-document-v1, dialogo) e i tre
/// grafi documentali (chat, analisi, generazione) con lo stesso client, lo stesso monitor e lo stesso dispatcher. Non è un registro di engine né
/// un coordinatore: ogni workflow conserva il proprio guard di definizione e ha una sola dipendenza opzionale verso il proprio gemello documentale,
/// scelto soltanto dalla definizione congelata alla richiesta (run) o dallo snapshot congelato (turno). Usata da Program.cs e dai test di routing.
/// </summary>
public static class AiWorkflowRegistration
{
    public static IServiceCollection AddAiWorkflows(this IServiceCollection services)
    {
        // Grafi documentali (002): stesso client e monitor; il conteggio del payload usa la calibrazione identificata di Documents:Calibration
        // (come le unita di estrazione), senza la quale il ramo resta runtime_unverified. Nessuna dipendenza dal tipo di run: la scelta e dei guard.
        services.AddSingleton(sp => new DocumentChatWorkflow(
            sp.GetRequiredService<IServiceScopeFactory>(), DocumentFactory(sp), sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<IOptions<DocumentOptions>>()));
        services.AddSingleton(sp => new DocumentAnalysisWorkflow(
            sp.GetRequiredService<IServiceScopeFactory>(), DocumentFactory(sp), sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<IOptions<DocumentOptions>>()));
        services.AddSingleton(sp => new DocumentSourceDraftWorkflow(
            sp.GetRequiredService<IServiceScopeFactory>(), DocumentFactory(sp), sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<IOptions<DocumentOptions>>()));
        services.AddSingleton(sp => new DocumentChatTurnExecutor(sp.GetRequiredService<DocumentChatWorkflow>(), sp.GetService<ChatTurnEvents>()));

        services.AddSingleton(sp => new AnalysisWorkflow(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ConversationalAgent>(),
            sp.GetRequiredService<InternalModelClient>().CreateChatOptions(AnalysisSchema.Element, AnalysisSchema.Name, temperature: 0, phase: AiPhase.Analysis),
            sp.GetRequiredService<DocumentAnalysisWorkflow>()));
        services.AddSingleton(sp =>
        {
            var model = sp.GetRequiredService<InternalModelClient>();
            return new DocumentAgent(
                AgentFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value, model, sp.GetRequiredService<ModelRuntimeMonitor>()),
                model.CreateChatOptions(temperature: 0.2f, phase: AiPhase.Document));
        });
        services.AddSingleton(sp => new DocumentWorkflow(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<DocumentAgent>(), sp.GetRequiredService<DocumentSourceDraftWorkflow>()));
        services.AddSingleton<IChatTurnExecutor, ConversationalTurnExecutor>();
        return services;
    }

    private static AgentFactory DocumentFactory(IServiceProvider sp) => AgentFactory.Create(
        sp.GetRequiredService<IOptions<AiOptions>>().Value, sp.GetRequiredService<InternalModelClient>(), sp.GetRequiredService<ModelRuntimeMonitor>(),
        sp.GetRequiredService<IOptions<DocumentOptions>>().Value.Calibration.ToCalibration());
}
