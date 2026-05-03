using Microsoft.Extensions.DependencyInjection;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Cogworks.UmbracoAI.AgentMemory.Composing;

/// <summary>
/// Single composition root for the package. Auto-discovered by Umbraco at startup.
/// All DI registration goes through here — never through <c>Program.cs</c> or extension methods.
/// </summary>
public sealed class AgentMemoryComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Bind options
        builder.Services.Configure<AgentMemoryOptions>(
            builder.Config.GetSection(Constants.ConfigSection));

        // Run persistence (Phase 1 — replace Null* with EF-backed implementations)
        builder.Services.AddSingleton<IAgentRunStore, NullAgentRunStore>();

        // Feedback collection (Phase 1)
        builder.Services.AddSingleton<IAgentFeedbackService, NullAgentFeedbackService>();

        // Memory retrieval (Phase 2 — depends on Umbraco.AI.Search vector store)
        builder.Services.AddSingleton<IMemoryDigestService, NullMemoryDigestService>();
        builder.Services.AddSingleton<IMemoryRetriever, NullMemoryRetriever>();

        // Middleware wiring is left to the implementer in Week 3 — see
        // 06-architecture-v1.md and 11-week-by-week-plan.md in the planning repo.
    }
}
