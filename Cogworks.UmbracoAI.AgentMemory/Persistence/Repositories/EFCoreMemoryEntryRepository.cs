using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;

/// <summary>
/// EF Core repository for <see cref="Entities.MemoryEntryEntity"/>. The
/// read/write surface is added in Story 3.1 (background indexer); Story 1.1
/// only constructs the type so the DI startup-validation gate can resolve it
/// (AR4).
/// </summary>
internal sealed class EFCoreMemoryEntryRepository
{
    private readonly IEFCoreScopeProvider<AgentMemoryDbContext> _scopeProvider;

    public EFCoreMemoryEntryRepository(IEFCoreScopeProvider<AgentMemoryDbContext> scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }
}
