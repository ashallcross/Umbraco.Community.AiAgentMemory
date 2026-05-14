using Cogworks.UmbracoAI.AgentMemory.Composing;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;
using Umbraco.Cms.Api.Common.OpenApi;
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
        var services = new ServiceCollection();
        var config = configuration ?? new ConfigurationBuilder().Build();

        var builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(config);
        return (builder, services);
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

    private static void AssertScopedRegistered<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        Assert.That(descriptor, Is.Not.Null, $"{typeof(T).Name} must be registered");
        Assert.That(descriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Scoped),
            $"{typeof(T).Name} must be Scoped (matches IEFCoreScopeProvider lifetime; "
            + "Singleton would be a captive-dep bug)");
    }
}
