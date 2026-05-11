using Cogworks.UmbracoAI.AgentMemory.Composing;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        AssertScopedRegistered<EFCoreMemoryEntryRepository>(services);
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

    private static void AssertScopedRegistered<T>(IServiceCollection services)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        Assert.That(descriptor, Is.Not.Null, $"{typeof(T).Name} must be registered");
        Assert.That(descriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Scoped),
            $"{typeof(T).Name} must be Scoped (matches IEFCoreScopeProvider lifetime; "
            + "Singleton would be a captive-dep bug)");
    }
}
