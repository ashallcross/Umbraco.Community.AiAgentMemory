using Microsoft.Extensions.DependencyInjection;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace Cogworks.UmbracoAI.AgentMemory.Composing;

/// <summary>
/// Single composition root for the package (AR1). Auto-discovered by Umbraco
/// at startup. All DI registration goes through here — never through
/// <c>Program.cs</c> or extension methods.
/// </summary>
public sealed class AgentMemoryComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // Bind options
        builder.Services.Configure<AgentMemoryOptions>(
            builder.Config.GetSection(Constants.ConfigSection));

        // Persistence (Story 1.1) — DbContext maps onto the schema created by
        // AgentMemoryMigrationPlan / AddAgentMemorySchema. Repositories own the
        // EF Core scope-provider handle; their read/write surface lands in
        // Stories 2.1 (feedback) and 3.1 (memory entries).
        builder.Services.AddUmbracoDbContext<AgentMemoryDbContext>(
            (options, connectionString, providerName, _) =>
                options.UseDatabaseProvider(providerName!, connectionString!));
        builder.Services.AddScoped<EFCoreAgentRunFeedbackRepository>();
        builder.Services.AddScoped<EFCoreMemoryEntryRepository>();

        // Feedback collection (Story 2.1 replaces the Null* placeholder)
        builder.Services.AddSingleton<IAgentFeedbackService, NullAgentFeedbackService>();

        // Memory retrieval (Phase 2 — depends on Umbraco.AI.Search vector store)
        builder.Services.AddSingleton<IMemoryDigestService, NullMemoryDigestService>();
        builder.Services.AddSingleton<IMemoryRetriever, NullMemoryRetriever>();

        // Middleware wiring is left to the implementer in Week 3 — see
        // 06-architecture-v1.md and 11-week-by-week-plan.md in the planning repo.
    }
}
