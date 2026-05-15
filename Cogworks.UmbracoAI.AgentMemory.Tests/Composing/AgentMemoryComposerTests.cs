using Cogworks.UmbracoAI.AgentMemory.Composing;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Middleware;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.AI.Core.Chat;
using Umbraco.Cms.Api.Common.OpenApi;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Composing;

/// <summary>
/// AR4 startup-validation gate. Catches captive-dependency / lifetime mismatch
/// at unit-test time rather than first-request time.
///
/// Future stories adding new DI-resolved types MUST extend this fixture (or
/// add stub-driven siblings per the LlmsTxt 3.2 / 4.1 / 4.2 / 5.1 precedent).
/// </summary>
[TestFixture]
public class AgentMemoryComposerStartupValidationTests
{
    private static (IUmbracoBuilder builder, IServiceCollection services) CreateBuilder(
        IConfiguration? configuration = null)
    {
        var (builder, services, _) = CreateBuilderWithChatMiddleware(configuration);
        return (builder, services);
    }

    // Story 3.3 — AgentMemoryComposer now calls builder.AIChatMiddleware()
    // which routes through builder.WithCollectionBuilder<AIChatMiddlewareCollectionBuilder>().
    // Substitute.For<IUmbracoBuilder>() returns null for generic methods by default,
    // so the call would NRE without an explicit Returns(). Wiring a real
    // AIChatMiddlewareCollectionBuilder lets the composer's Append<T>() land in a
    // real type list that tests can introspect via RegisterWith(IServiceCollection).
    private static (IUmbracoBuilder builder, IServiceCollection services, AIChatMiddlewareCollectionBuilder chatMiddlewareBuilder) CreateBuilderWithChatMiddleware(
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        var config = configuration ?? new ConfigurationBuilder().Build();

        var chatMiddlewareBuilder = new AIChatMiddlewareCollectionBuilder();

        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(config);
        builder.WithCollectionBuilder<AIChatMiddlewareCollectionBuilder>().Returns(chatMiddlewareBuilder);
        return (builder, services, chatMiddlewareBuilder);
    }

    [Test]
    public void Compose_RegistersAgentMemoryOptions()
    {
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Some.Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IConfigureOptions<AgentMemoryOptions>)),
            "AgentMemoryComposer must bind AgentMemoryOptions from IConfiguration");
    }

    [Test]
    public void Compose_RegistersAgentMemoryDbContext()
    {
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Some.Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(AgentMemoryDbContext)
            || d.ServiceType == typeof(DbContextOptions<AgentMemoryDbContext>)),
            "AgentMemoryComposer must register AgentMemoryDbContext via AddUmbracoDbContext<>");
    }

    [Test]
    public void Compose_RegistersBothRepositoriesAsScoped()
    {
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        AssertScopedRegistered<EFCoreAgentRunFeedbackRepository>(services);
        // Story 3.1 — IMemoryEntryRepository → EFCoreMemoryEntryRepository
        // (interface-based registration; the indexer mocks the interface in
        // its unit tests since EFCoreMemoryEntryRepository is sealed).
        var memoryRepoDescriptor = services.SingleOrDefault(d =>
            d.ServiceType == typeof(IMemoryEntryRepository));
        Assert.That(memoryRepoDescriptor, Is.Not.Null,
            "IMemoryEntryRepository must be registered (Story 3.1).");
        Assert.That(memoryRepoDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Scoped));
        Assert.That(memoryRepoDescriptor.ImplementationType, Is.EqualTo(typeof(EFCoreMemoryEntryRepository)));
    }

    [Test]
    public void Compose_RegistersAgentMemoryOptionsValidator()
    {
        // Story 1.3 — IValidateOptions<AgentMemoryOptions> → AgentMemoryOptionsValidator
        // at Singleton lifetime, via TryAddEnumerable (documented MS pattern;
        // preserves IEnumerable<IValidateOptions<>> contract so adopters can
        // layer additional validators without colliding).
        // Calling Compose twice pins TryAddEnumerable's idempotency: a
        // regression to plain AddSingleton would produce a second descriptor
        // on the second call and trip the Exactly(1) assertion.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);
        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IValidateOptions<AgentMemoryOptions>)
            && d.ImplementationType == typeof(AgentMemoryOptionsValidator)
            && d.Lifetime == ServiceLifetime.Singleton),
            "AgentMemoryComposer must register AgentMemoryOptionsValidator as the "
            + "(exactly one) IValidateOptions<AgentMemoryOptions> at Singleton lifetime "
            + "via TryAddEnumerable — idempotent under repeated Compose() calls.");
    }

    [Test]
    public void Compose_RegistersAgentRunReader()
    {
        // Story 1.2 — IAgentRunReader → AgentRunReader at Singleton lifetime.
        // Mirrors the existing Compose_RegistersAgentMemoryDbContext shape.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IAgentRunReader)
            && d.ImplementationType == typeof(AgentRunReader)
            && d.Lifetime == ServiceLifetime.Singleton),
            "AgentMemoryComposer must register IAgentRunReader → AgentRunReader at Singleton lifetime "
            + "(matches upstream IAIAuditLogService's Singleton lifetime per 0-c § AC1.a). "
            + "Exactly(1) catches accidental duplicate registrations.");
    }

    [Test]
    public void Compose_StartupValidation_AgentRunReader_NoCaptiveDependency()
    {
        // Story 1.2 captive-dep check, mirroring Story 1.1 NOTE-0.5 (1-1-outcome.md
        // lines 172-178): descriptor-inspection instead of a real
        // BuildServiceProvider(ValidateOnBuild=true, ValidateScopes=true). Rationale:
        // AgentRunReader's dep graph is Singleton → Singleton (IAIAuditLogService) +
        // Singleton (IOptions<>) + Singleton (ILogger<>) — no Scoped dep, no graph-
        // resolution risk. A real provider build pulls in Umbraco's full service
        // graph (~hundreds of deps) — over-engineering for this narrow surface.
        //
        // Forward contract carried from Story 1.1 NOTE-0.5: if a future story adds a
        // Singleton consuming a Scoped repository, the descriptor check still fires
        // AND that future story SHOULD add a real-provider-build assertion. Story
        // 1.2 does NOT cross that threshold.

        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        // AgentRunReader is registered at Singleton.
        var readerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAgentRunReader));
        Assert.That(readerDescriptor, Is.Not.Null, "IAgentRunReader must be registered");
        Assert.That(readerDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
            "IAgentRunReader must be Singleton (matches upstream IAIAuditLogService lifetime)");
        Assert.That(readerDescriptor.ImplementationType, Is.EqualTo(typeof(AgentRunReader)));

        // AgentRunReader's ctor parameters take NO Scoped repository / no
        // IEFCoreScopeProvider<> — those belong to Stories 2.1 / 3.1 (AC10).
        var readerCtorParams = typeof(AgentRunReader).GetConstructors()
            .Single()
            .GetParameters();
        Assert.That(readerCtorParams, Has.None.Matches<System.Reflection.ParameterInfo>(p =>
            p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
            || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
            || (p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>))),
            "AC10: AgentRunReader must NOT depend on the repository shells or IEFCoreScopeProvider<> "
            + "— those are Stories 2.1 / 3.1 surface.");

        // Captive-dep guard (carry-forward from Story 1.1 fixture): no Singleton in
        // the package's surface depends on a Scoped repository. Story 1.2 doesn't
        // add such a Singleton, so this re-asserts the contract is intact.
        var ourSingletonsTakingScopedDeps = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == typeof(AgentMemoryComposer).Assembly)
            .Where(d => d.ImplementationType!.GetConstructors()
                .Any(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
                    || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)))))
            .ToArray();

        Assert.That(ourSingletonsTakingScopedDeps, Is.Empty,
            "Story 1.2 must not introduce a Singleton consuming a Scoped repository — "
            + "if a future story does, this check fires and the new story owes a real-"
            + "provider-build assertion per Story 1.1 NOTE-0.5.");
    }

    [Test]
    public void Compose_StartupValidation_AgentMemoryDbContext_NoCaptiveDependency()
    {
        // Captive-dependency check for Story 1.1's surface: the two repositories
        // are Scoped and depend on IEFCoreScopeProvider<AgentMemoryDbContext>
        // (also Scoped, registered via AddUmbracoDbContext<>). Resolving them
        // inside a scope with ValidateScopes = true would throw if a Singleton
        // anywhere in the dep-graph was holding onto a Scoped dependency.
        //
        // Story 1.1 doesn't add any Singletons depending on Scoped types — the
        // captive-dep risk surfaces only when later stories register Singletons
        // that take repository constructors. For now we assert the lifetime
        // contract directly off the descriptor table; future stories extend this
        // by attempting a real .GetRequiredService<>() against a built provider.

        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        // No singleton in the package's surface depends on a scoped service.
        var ourSingletonsTakingScopedDeps = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == typeof(AgentMemoryComposer).Assembly)
            .Where(d => d.ImplementationType!.GetConstructors()
                .Any(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
                    || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)))))
            .ToArray();

        Assert.That(ourSingletonsTakingScopedDeps, Is.Empty,
            "Captive-dep guard: no Singleton may depend on a Scoped repository or "
            + "IEFCoreScopeProvider<>. Future stories adding such a Singleton "
            + "introduce a captive-dep bug that surfaces only at first request.");
    }

    [Test]
    public void Compose_RegistersAgentFeedbackService()
    {
        // Story 2.1 — IAgentFeedbackService → AgentFeedbackService at Scoped
        // lifetime (Scoped-on-Scoped: service → repository → IEFCoreScopeProvider).
        // Mirrors the existing Compose_RegistersAgentRunReader shape (Story 1.2)
        // and Compose_RegistersAgentMemoryOptionsValidator (Story 1.3).
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IAgentFeedbackService)
            && d.ImplementationType == typeof(AgentFeedbackService)
            && d.Lifetime == ServiceLifetime.Scoped),
            "AgentMemoryComposer must register IAgentFeedbackService → AgentFeedbackService "
            + "at Scoped lifetime (Story 2.1). Scoped is required because the service "
            + "composes on EFCoreAgentRunFeedbackRepository which composes on "
            + "IEFCoreScopeProvider<AgentMemoryDbContext> (Scoped). Exactly(1) catches "
            + "accidental duplicate registrations.");
    }

    [Test]
    public void Compose_StartupValidation_AgentFeedbackService_NoCaptiveDependency()
    {
        // Story 2.1 captive-dep check, mirroring Story 1.2 pattern (lines 116-174).
        // Descriptor inspection only — AgentFeedbackService is Scoped (NOT Singleton),
        // so the Singleton-on-Scoped forward contract (Story 1.1 NOTE-0.5) is NOT
        // triggered. A real BuildServiceProvider(ValidateScopes=true, ValidateOnBuild=true)
        // would pull in Umbraco's full service graph (~hundreds of deps); over-
        // engineering for this narrow surface.

        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        var serviceDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAgentFeedbackService));
        Assert.That(serviceDescriptor, Is.Not.Null, "IAgentFeedbackService must be registered");
        Assert.That(serviceDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Scoped),
            "IAgentFeedbackService must be Scoped (composes on Scoped repository; "
            + "Singleton-on-Scoped would trip the Story 1.1 NOTE-0.5 captive-dep forward contract)");
        Assert.That(serviceDescriptor.ImplementationType, Is.EqualTo(typeof(AgentFeedbackService)));

        // AgentFeedbackService's ctor parameters: EFCoreAgentRunFeedbackRepository (Scoped),
        // ILogger<>, TimeProvider. No direct IEFCoreScopeProvider<> dep — the repository
        // is the indirection layer.
        var serviceCtorParams = typeof(AgentFeedbackService).GetConstructors()
            .Single()
            .GetParameters();
        Assert.That(serviceCtorParams, Has.None.Matches<System.Reflection.ParameterInfo>(p =>
            p.ParameterType.IsGenericType
            && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)),
            "AgentFeedbackService composes on the repository, NOT directly on IEFCoreScopeProvider<> "
            + "— the repository owns the scope-usage pattern.");

        // Captive-dep guard (carry-forward from Stories 1.1 / 1.2 / 1.3): no Singleton in
        // the package's surface depends on a Scoped repository or IEFCoreScopeProvider<>.
        // Story 2.1 doesn't introduce a new Singleton, so this re-asserts the contract is intact.
        var ourSingletonsTakingScopedDeps = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == typeof(AgentMemoryComposer).Assembly)
            .Where(d => d.ImplementationType!.GetConstructors()
                .Any(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
                    || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)))))
            .ToArray();

        Assert.That(ourSingletonsTakingScopedDeps, Is.Empty,
            "Story 2.1 must not introduce a Singleton consuming a Scoped repository — "
            + "if a future story does, this check fires and the new story owes a real-"
            + "provider-build assertion per Story 1.1 NOTE-0.5.");
    }

    [Test]
    public void Compose_RegistersAgentMemoryBackofficeApiComposer_SwaggerDocAndOperationFilter()
    {
        // Story 2.2 — AgentMemoryBackofficeApiComposer wires the Management-API
        // controllers into Umbraco's Swagger doc generation + auth requirement
        // pipeline. Descriptor inspection only.
        //
        // Calling Compose twice pins TryAddEnumerable's idempotency (matches
        // Compose_RegistersAgentMemoryOptionsValidator's double-compose
        // contract — a regression to plain AddSingleton or Configure<>-lambda
        // produces second descriptors on the second call and trips Exactly(1).
        var (builder, services) = CreateBuilder();

        new AgentMemoryBackofficeApiComposer().Compose(builder);
        new AgentMemoryBackofficeApiComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IOperationIdHandler)
            && d.Lifetime == ServiceLifetime.Singleton),
            "AgentMemoryBackofficeApiComposer must register IOperationIdHandler at Singleton "
            + "lifetime via TryAddEnumerable — idempotent under repeated Compose() calls.");

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IConfigureOptions<SwaggerGenOptions>)
            && d.ImplementationType is not null
            && d.ImplementationType.Assembly == typeof(AgentMemoryBackofficeApiComposer).Assembly),
            "AgentMemoryBackofficeApiComposer must register exactly one typed "
            + "IConfigureOptions<SwaggerGenOptions> from this assembly via TryAddEnumerable "
            + "(SwaggerDoc + OperationFilter<AgentMemoryOperationSecurityFilter>). Other "
            + "framework IConfigureOptions<SwaggerGenOptions> from Umbraco / Swashbuckle "
            + "live in other assemblies and are filtered out by the assembly check.");
    }

    [Test]
    public void Compose_StartupValidation_BackofficeApi_NoCaptiveDependency()
    {
        // Story 2.2 captive-dep check after composing both composers together.
        // IOperationIdHandler is Singleton with only Singleton deps
        // (IOptions<ApiVersioningOptions>). No new captive-dep introduced.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);
        new AgentMemoryBackofficeApiComposer().Compose(builder);

        var ourSingletonsTakingScopedDeps = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == typeof(AgentMemoryComposer).Assembly)
            .Where(d => d.ImplementationType!.GetConstructors()
                .Any(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
                    || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)))))
            .ToArray();

        Assert.That(ourSingletonsTakingScopedDeps, Is.Empty,
            "Story 2.2 must not introduce a Singleton consuming a Scoped repository — "
            + "AgentMemoryBackofficeApiComposer's IOperationIdHandler is Singleton with "
            + "only Singleton deps (IOptions<ApiVersioningOptions>).");
    }

    [Test]
    public void Compose_StartupValidation_FeedbackIndexer_NoCaptiveDependency()
    {
        // Story 3.1 AC5 — IFeedbackIndexer → FeedbackIndexer at Singleton
        // lifetime; the Singleton consumes IServiceScopeFactory + framework
        // singletons only. The indexer's per-call IServiceScope is the
        // canonical Microsoft pattern for Singleton-services-consuming-Scoped
        // — captive-dep risk is structurally zero.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        var indexerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IFeedbackIndexer));
        Assert.That(indexerDescriptor, Is.Not.Null, "IFeedbackIndexer must be registered");
        Assert.That(indexerDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
            "IFeedbackIndexer must be Singleton — the indexer creates per-work-item scopes "
            + "via IServiceScopeFactory.CreateScope(); a Scoped lifetime would defeat the queue model.");
        Assert.That(indexerDescriptor.ImplementationType, Is.EqualTo(typeof(FeedbackIndexer)));

        // FeedbackIndexer's ctor deps: framework singletons + our options
        // monitor + TimeProvider — no direct Scoped repository / no direct
        // IEFCoreScopeProvider<>. The Singleton-on-Scoped indirection is
        // managed via IServiceScopeFactory (the per-call scope is created
        // inside IndexAsync, NOT injected through the ctor).
        var ctorParams = typeof(FeedbackIndexer).GetConstructors()
            .Single()
            .GetParameters();
        Assert.That(ctorParams, Has.None.Matches<System.Reflection.ParameterInfo>(p =>
            p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
            || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
            || p.ParameterType == typeof(IMemoryEntryRepository)
            || (p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(Umbraco.Cms.Persistence.EFCore.Scoping.IEFCoreScopeProvider<>))),
            "FeedbackIndexer must NOT directly depend on Scoped repository or IEFCoreScopeProvider<> — "
            + "those are resolved per-call inside IndexAsync via IServiceScopeFactory.");
    }

    [Test]
    public void Compose_DoubleCompose_FeedbackIndexerStillRegisteredExactlyOnce()
    {
        // Story 3.1 AC5 — TryAddSingleton idempotency pin (matches Story 1.3 / 2.2 patterns).
        // A regression to plain AddSingleton would duplicate on the second call and trip Exactly(1).
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);
        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IFeedbackIndexer)
            && d.ImplementationType == typeof(FeedbackIndexer)
            && d.Lifetime == ServiceLifetime.Singleton),
            "IFeedbackIndexer must be registered exactly once under repeated Compose() calls.");
        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IMemoryEntryRepository)
            && d.ImplementationType == typeof(EFCoreMemoryEntryRepository)
            && d.Lifetime == ServiceLifetime.Scoped),
            "IMemoryEntryRepository must be registered exactly once under repeated Compose() calls.");
    }

    [Test]
    public void Compose_StartupValidation_SemanticMemoryRetriever_NoCaptiveDependency()
    {
        // Story 3.2 AC7 — IMemoryRetriever → SemanticMemoryRetriever at Singleton
        // lifetime; the Singleton consumes IServiceScopeFactory + framework
        // singletons only. The retriever's per-call IServiceScope is the
        // canonical Microsoft pattern for Singleton-services-consuming-Scoped
        // — captive-dep risk is structurally zero. Mirrors the Story 3.1
        // FeedbackIndexer test verbatim.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);

        var retrieverDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMemoryRetriever));
        Assert.That(retrieverDescriptor, Is.Not.Null, "IMemoryRetriever must be registered");
        Assert.That(retrieverDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Singleton),
            "IMemoryRetriever must be Singleton — the retriever creates per-call scopes "
            + "via IServiceScopeFactory.CreateScope(); a Scoped lifetime would conflict with "
            + "Story 3.3's middleware composition (chat-client pipeline binds the retriever "
            + "at agent-pipeline-construction time, before the chat-call scope exists).");
        Assert.That(retrieverDescriptor.ImplementationType, Is.EqualTo(typeof(SemanticMemoryRetriever)),
            "Story 3.2 replaces the Story 1.3 NullMemoryRetriever placeholder with SemanticMemoryRetriever.");

        // SemanticMemoryRetriever's ctor deps: framework singletons + our options
        // monitor + TimeProvider — NO direct upstream Umbraco.AI surface, NO
        // direct Scoped package dep. Mirrors the Story 3.1 FeedbackIndexer
        // assertion shape verbatim. Captive-dep risk structurally zero regardless
        // of upstream lifetimes (resolved per-call inside RetrieveSimilarAsync).
        var ctorParams = typeof(SemanticMemoryRetriever).GetConstructors()
            .Single()
            .GetParameters();
        Assert.That(ctorParams, Has.None.Matches<System.Reflection.ParameterInfo>(p =>
            p.ParameterType == typeof(IMemoryEntryRepository)
            || p.ParameterType == typeof(IAgentFeedbackService)
            || p.ParameterType == typeof(Umbraco.AI.Search.Core.VectorStore.IAIVectorStore)
            || p.ParameterType == typeof(Umbraco.AI.Core.Embeddings.IAIEmbeddingService)
            || p.ParameterType == typeof(Umbraco.AI.Core.Profiles.IAIProfileService)
            || (p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(Umbraco.Cms.Persistence.EFCore.Scoping.IEFCoreScopeProvider<>))),
            "SemanticMemoryRetriever must NOT directly depend on any Scoped or upstream service — "
            + "those are resolved per-call inside RetrieveSimilarAsync via IServiceScopeFactory.");
    }

    [Test]
    public void Compose_DoubleCompose_SemanticMemoryRetrieverStillRegisteredExactlyOnce()
    {
        // Story 3.2 AC7 — TryAddSingleton idempotency pin (matches Story 1.3 / 2.2 / 3.1 patterns).
        // A regression to plain AddSingleton would duplicate on the second call and trip Exactly(1).
        // Also asserts NullMemoryRetriever is NOT in the descriptor list — verifies the
        // composer surgery successfully DELETED the prior AddSingleton<IMemoryRetriever, NullMemoryRetriever>()
        // line; if both lines accidentally land, descriptor count would be 2.
        var (builder, services) = CreateBuilder();

        new AgentMemoryComposer().Compose(builder);
        new AgentMemoryComposer().Compose(builder);

        Assert.That(services, Has.Exactly(1).Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IMemoryRetriever)
            && d.ImplementationType == typeof(SemanticMemoryRetriever)
            && d.Lifetime == ServiceLifetime.Singleton),
            "IMemoryRetriever must be registered exactly once under repeated Compose() calls.");
        Assert.That(services, Has.None.Matches<ServiceDescriptor>(d =>
            d.ServiceType == typeof(IMemoryRetriever)
            && d.ImplementationType == typeof(NullMemoryRetriever)),
            "The Story 1.3 NullMemoryRetriever registration must be DELETED at Story 3.2 composer surgery — "
            + "a stray duplicate would cause last-wins resolution drift.");
    }

    [Test]
    public void Compose_StartupValidation_MemoryInjectionChatMiddleware_NoCaptiveDependency()
    {
        // Story 3.3 AC7 / AC8.13 — MemoryInjectionChatMiddleware ctor takes
        // only framework + package Singletons (IMemoryRetriever,
        // IAIRuntimeContextAccessor, IOptionsMonitor<>, ILogger<>). No direct
        // Scoped repository, no IAIVectorStore / IAIEmbeddingService /
        // IAIProfileService — those are resolved per-call INSIDE
        // SemanticMemoryRetriever's IServiceScope (Story 3.2). Mirrors
        // Compose_StartupValidation_SemanticMemoryRetriever_NoCaptiveDependency
        // verbatim.
        var (builder, services, chatMiddlewareBuilder) = CreateBuilderWithChatMiddleware();

        new AgentMemoryComposer().Compose(builder);

        // The middleware type lands in the AIChatMiddlewareCollectionBuilder's
        // internal type list via .Append<T>(); GetTypes() exposes it as the
        // descriptor introspection surface (the IServiceCollection descriptors
        // are populated later at IUmbracoBuilder.Build() time, NOT at .Append<T>()
        // call time — DRIFT-3.3-4 resolution).
        var middlewareTypes = chatMiddlewareBuilder.GetTypes().ToArray();
        Assert.That(middlewareTypes, Has.Some.EqualTo(typeof(MemoryInjectionChatMiddleware)),
            "MemoryInjectionChatMiddleware must be registered into the "
            + "AIChatMiddlewareCollectionBuilder via builder.AIChatMiddleware().Append<T>().");

        var ctorParams = typeof(MemoryInjectionChatMiddleware).GetConstructors()
            .Single()
            .GetParameters();
        Assert.That(ctorParams, Has.None.Matches<System.Reflection.ParameterInfo>(p =>
            p.ParameterType == typeof(IMemoryEntryRepository)
            || p.ParameterType == typeof(IAgentFeedbackService)
            || p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
            || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
            || p.ParameterType == typeof(Umbraco.AI.Search.Core.VectorStore.IAIVectorStore)
            || p.ParameterType == typeof(Umbraco.AI.Core.Embeddings.IAIEmbeddingService)
            || p.ParameterType == typeof(Umbraco.AI.Core.Profiles.IAIProfileService)
            || (p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>))),
            "MemoryInjectionChatMiddleware must NOT directly depend on any Scoped "
            + "repository or upstream Umbraco.AI service — IMemoryRetriever is the "
            + "Singleton seam (Story 3.2) that internalises per-call scope discipline.");

        // Captive-dep guard (carry-forward): no Singleton in the package's
        // surface depends on a Scoped repository or IEFCoreScopeProvider<>.
        var ourSingletonsTakingScopedDeps = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton
                        && d.ImplementationType is not null
                        && d.ImplementationType.Assembly == typeof(AgentMemoryComposer).Assembly)
            .Where(d => d.ImplementationType!.GetConstructors()
                .Any(c => c.GetParameters().Any(p =>
                    p.ParameterType == typeof(EFCoreAgentRunFeedbackRepository)
                    || p.ParameterType == typeof(EFCoreMemoryEntryRepository)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEFCoreScopeProvider<>)))))
            .ToArray();

        Assert.That(ourSingletonsTakingScopedDeps, Is.Empty,
            "Story 3.3 must not introduce a Singleton consuming a Scoped repository — "
            + "the captive-dep guard carries from Stories 1.1 / 1.2 / 1.3 / 2.1 / 2.2 / 3.1 / 3.2.");
    }

    [Test]
    public void Compose_DoubleCompose_MemoryInjectionChatMiddleware_StillRegisteredExactlyOnce()
    {
        // Story 3.3 AC7 / AC8.14 — AIChatMiddlewareCollectionBuilder.Append<T>()
        // routes through OrderedCollectionBuilderBase<>.Append<T> which is
        // de-dup-by-move-to-end (REMOVES any existing entry for T before adding it).
        // Decompile-verified 2026-05-14 (DRIFT-3.3-4 resolution). The double-Compose
        // therefore lands exactly 1 entry; no composer-side guard required.
        var (builder, _, chatMiddlewareBuilder) = CreateBuilderWithChatMiddleware();

        new AgentMemoryComposer().Compose(builder);
        new AgentMemoryComposer().Compose(builder);

        var middlewareTypes = chatMiddlewareBuilder.GetTypes().ToArray();
        var count = middlewareTypes.Count(t => t == typeof(MemoryInjectionChatMiddleware));
        Assert.That(count, Is.EqualTo(1),
            "MemoryInjectionChatMiddleware must be registered exactly once under "
            + "repeated Compose() calls. OrderedCollectionBuilderBase<>.Append<T> is "
            + "de-dup-by-move-to-end (REMOVES then re-adds), so a regression that "
            + "switches to a non-de-duping append would trip this assertion.");
    }

    [Test]
    public void Compose_HasComposeAfterAttribute_TargetingUmbracoAIAgentComposer()
    {
        // Story 3.3 AC7 / AC8.15 — LOAD-BEARING DRIFT-3.3-9 attribute-reflection pin.
        // Without [ComposeAfter(typeof(UmbracoAIAgentComposer))] on AgentMemoryComposer,
        // Umbraco's composer-orchestration may schedule us BEFORE upstream — at which
        // point builder.AIChatMiddleware().Append<MemoryInjectionChatMiddleware>() lands
        // on an empty collection; upstream then Append's AIAuditingChatMiddleware LATER,
        // making Auditing the OUTER wrapper at runtime; our enrichment runs INSIDE
        // auditing; the injected system message is NOT captured in PromptSnapshot;
        // FR25 + FR43 silently fail.
        //
        // Pure attribute reflection — cheap, deterministic. The behavioural
        // verification (boot-time AIChatMiddlewareCollection ordering) lives at the
        // AC9.b manual-gate probe (h). This test protects against the attribute
        // being silently dropped during a future refactor.
        var attributes = typeof(AgentMemoryComposer)
            .GetCustomAttributes(typeof(ComposeAfterAttribute), inherit: false)
            .Cast<ComposeAfterAttribute>()
            .ToArray();

        Assert.That(attributes, Has.Some.Matches<ComposeAfterAttribute>(a =>
            a.RequiredType == typeof(Umbraco.AI.Agent.Startup.Configuration.UmbracoAIAgentComposer)),
            "AgentMemoryComposer must carry [ComposeAfter(typeof(UmbracoAIAgentComposer))] "
            + "so Umbraco's composer-orchestration runs upstream's UmbracoAIComposer + "
            + "UmbracoAIAgentComposer chain BEFORE us — guaranteeing upstream's "
            + "Append<AIAuditingChatMiddleware>() lands in AIChatMiddlewareCollection "
            + "BEFORE our Append<MemoryInjectionChatMiddleware>(). At runtime our "
            + "middleware then wraps OUTSIDE Auditing, capturing enriched PromptSnapshot "
            + "per FR25 + FR43 (DRIFT-3.3-9 LOAD-BEARING contract).");
    }

    private static void AssertScopedRegistered<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        Assert.That(descriptor, Is.Not.Null, $"{typeof(T).Name} must be registered");
        Assert.That(descriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Scoped),
            $"{typeof(T).Name} must be Scoped (matches IEFCoreScopeProvider lifetime; "
            + "Singleton would be a captive-dep bug)");
    }
}
