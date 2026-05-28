using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Community.AiAgentMemory.Configuration;
using Umbraco.Community.AiAgentMemory.Feedback;
using Umbraco.Community.AiAgentMemory.Memory;
using Umbraco.Community.AiAgentMemory.Middleware;
using Umbraco.Community.AiAgentMemory.Persistence;
using Umbraco.Community.AiAgentMemory.Persistence.Repositories;
using Umbraco.Community.AiAgentMemory.Runs;
using Umbraco.AI.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace Umbraco.Community.AiAgentMemory.Composing;

/// <summary>
/// Single composition root for the package (AR1). Auto-discovered by Umbraco
/// at startup. All DI registration goes through here — never through
/// <c>Program.cs</c> or extension methods.
/// </summary>
/// <remarks>
/// <para>
/// <c>[ComposeAfter(typeof(UmbracoAIAgentComposer))]</c> (DRIFT-3.3-9
/// LOAD-BEARING) guarantees Umbraco's composer-orchestration runs upstream's
/// <c>UmbracoAIComposer</c> + <c>UmbracoAIAgentComposer</c> chain BEFORE this
/// composer. That ordering matters because Story 3.3 appends
/// <c>MemoryInjectionChatMiddleware</c> to <c>AIChatMiddlewareCollection</c>;
/// upstream's <c>Append&lt;AIAuditingChatMiddleware&gt;()</c> must land first so
/// our middleware ends up at a LATER collection position. At runtime
/// <c>AIChatClientFactory.ApplyMiddleware</c> folds via
/// <c>client = middleware.Apply(client)</c> in collection order, so later
/// position = OUTER wrapper. Our middleware therefore wraps OUTSIDE Auditing;
/// we enrich the prompt first, then delegate inward into the Auditing-wrapped
/// client, which captures the enriched <c>PromptSnapshot</c> per FR25 + FR43.
/// </para>
/// <para>
/// <c>UmbracoAIAgentComposer</c> is the correct anchor because it is itself
/// <c>[ComposeAfter(typeof(UmbracoAIComposer))]</c>; depending on the leaf
/// gives deterministic placement after both.
/// </para>
/// </remarks>
[ComposeAfter(typeof(Umbraco.AI.Agent.Startup.Configuration.UmbracoAIAgentComposer))]
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
        // Story 3.1 — interface-based registration replaces the Story 1.1
        // concrete-type AddScoped<EFCoreMemoryEntryRepository>. The indexer
        // (Singleton) needs IMemoryEntryRepository to be a mockable seam in
        // unit tests because EFCoreMemoryEntryRepository is sealed.
        builder.Services.TryAddScoped<IMemoryEntryRepository, EFCoreMemoryEntryRepository>();

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
        // Story 3.2 — SemanticMemoryRetriever replaces the Story 1.3 NullMemoryRetriever
        // placeholder. Singleton lifetime; per-call IServiceScopeFactory.CreateScope()
        // for upstream Umbraco.AI surfaces + Scoped package deps (mirrors Story 3.1
        // FeedbackIndexer pattern verbatim — captive-dep risk structurally zero).
        // TryAddSingleton (NOT AddSingleton) honours the package-wide idempotency
        // contract used since Story 1.3 D1 — double-Compose() in adopter hosts must
        // not silently duplicate.
        builder.Services.TryAddSingleton<IMemoryRetriever, SemanticMemoryRetriever>();

        // Background indexer (Story 3.1) — Singleton; creates per-work-item
        // Scopes via IServiceScopeFactory. Enqueued by AgentFeedbackController
        // after a successful POST. TryAddSingleton (NOT AddSingleton) honours
        // the package-wide idempotency contract — double-Compose() in adopter
        // hosts must not silently duplicate the registration.
        builder.Services.TryAddSingleton<IFeedbackIndexer, FeedbackIndexer>();

        // Story 3.3 — register MemoryInjectionChatMiddleware in the chat pipeline.
        // builder.AIChatMiddleware().Append<T>() applies our middleware via
        // Apply(IChatClient inner) each time the chat pipeline is constructed.
        // Composes WITH upstream's AIAuditingChatMiddleware such that the injected
        // "Lessons from past runs" system message is captured in PromptSnapshot
        // per FR25 + FR43 (outcome-pinned by AC9.b manual gate).
        //
        // Append<T>() is de-dup-by-move-to-end per
        // OrderedCollectionBuilderBase<>.Append<T> (DRIFT-3.3-4 resolution,
        // decompile-verified 2026-05-14) — 2× Compose() leaves exactly one
        // registration without a composer-side guard.
        //
        // ORDERING (DRIFT-3.3-9 LOAD-BEARING): the
        // [ComposeAfter(typeof(UmbracoAIAgentComposer))] attribute on this class
        // ensures upstream registers AIAuditingChatMiddleware FIRST so our Append
        // lands AFTER. AIChatClientFactory.ApplyMiddleware folds via
        // client = middleware.Apply(client) in collection order; later position
        // = OUTER wrapper at runtime. Our middleware wraps OUTSIDE Auditing; we
        // enrich, delegate inward, Auditing captures the enriched PromptSnapshot.
        builder.AIChatMiddleware().Append<MemoryInjectionChatMiddleware>();
    }
}
