using Cogworks.UmbracoAI.AgentMemory.Composing;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Persistence;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
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
