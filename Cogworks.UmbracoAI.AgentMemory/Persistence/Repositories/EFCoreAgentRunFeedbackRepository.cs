using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;

/// <summary>
/// EF Core repository for <see cref="Entities.AgentRunFeedbackEntity"/>. The
/// read/write surface is added in Story 2.1 (<c>IAgentFeedbackService</c>);
/// Story 1.1 only constructs the type so the DI startup-validation gate can
/// resolve it (AR4).
/// </summary>
internal sealed class EFCoreAgentRunFeedbackRepository
{
    private readonly IEFCoreScopeProvider<AgentMemoryDbContext> _scopeProvider;

    public EFCoreAgentRunFeedbackRepository(IEFCoreScopeProvider<AgentMemoryDbContext> scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }
}
