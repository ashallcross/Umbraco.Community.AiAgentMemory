using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
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

        // Validate options at first read (AR3). Surfaces invariant violations
        // as OptionsValidationException when the agent-memory pipeline first
        // reads IOptionsMonitor<AgentMemoryOptions>.CurrentValue — not boot,
        // not silently downstream. TryAddEnumerable is the documented
        // Microsoft pattern for IValidateOptions<> registration: it preserves
        // the IEnumerable<IValidateOptions<TOptions>> contract that
        // Microsoft.Extensions.Options resolves at first read, allowing
        // adopters to layer additional validators without colliding.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AgentMemoryOptions>, AgentMemoryOptionsValidator>());

        // Persistence (Story 1.1) — DbContext maps onto the schema created by
        // AgentMemoryMigrationPlan / AddAgentMemorySchema. Repositories own the
        // EF Core scope-provider handle; their read/write surface lands in
        // Stories 2.1 (feedback) and 3.1 (memory entries).
        builder.Services.AddUmbracoDbContext<AgentMemoryDbContext>(
            (options, connectionString, providerName, _) =>
                options.UseDatabaseProvider(providerName!, connectionString!));
        builder.Services.AddScoped<EFCoreAgentRunFeedbackRepository>();
        builder.Services.AddScoped<EFCoreMemoryEntryRepository>();

        // Run reading (Story 1.2 — composes on upstream IAIAuditLogService;
        // we do NOT own a runs table, AR8/AR9).
        builder.Services.AddSingleton<IAgentRunReader, AgentRunReader>();

        // Clock dependency for AgentFeedbackService supersede-CreatedUtc
        // determinism in tests + explicit clock-dependency in production.
        // TryAddSingleton ensures host-supplied TimeProvider (if any) wins.
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Feedback collection (Story 2.1) — Scoped to match the EF Core
        // repository's scope-provider lifetime; no captive-dep risk
        // introduced (Scoped-on-Scoped).
        builder.Services.AddScoped<IAgentFeedbackService, AgentFeedbackService>();

        // Memory retrieval (Phase 2 — depends on Umbraco.AI.Search vector store)
        builder.Services.AddSingleton<IMemoryDigestService, NullMemoryDigestService>();
        builder.Services.AddSingleton<IMemoryRetriever, NullMemoryRetriever>();

        // Middleware wiring is left to the implementer in Week 3 — see
        // 06-architecture-v1.md and 11-week-by-week-plan.md in the planning repo.
    }
}
