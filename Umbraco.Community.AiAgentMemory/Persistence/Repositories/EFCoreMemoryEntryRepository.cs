using Cogworks.UmbracoAI.AgentMemory.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;

/// <summary>
/// EF Core repository for <see cref="MemoryEntryEntity"/>. Composes on
/// <see cref="IEFCoreScopeProvider{TDbContext}"/>; every method follows the
/// canonical <c>CreateScope → ExecuteWithContextAsync → ((ICoreScope)scope).Complete()</c>
/// pattern (mirror Story 2.1's <c>EFCoreAgentRunFeedbackRepository</c>). Surface
/// fill lands in Story 3.1 (background indexer + Story 3.2 retriever consumer).
/// </summary>
internal sealed class EFCoreMemoryEntryRepository : IMemoryEntryRepository
{
    private readonly IEFCoreScopeProvider<AgentMemoryDbContext> _scopeProvider;

    public EFCoreMemoryEntryRepository(IEFCoreScopeProvider<AgentMemoryDbContext> scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public async Task<MemoryEntryEntity?> FindByRunIdAndAgentIdAsync(
        string runId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var result = await scope.ExecuteWithContextAsync<MemoryEntryEntity?>(
            async db => await db.MemoryEntries
                .FirstOrDefaultAsync(e => e.RunId == runId && e.AgentId == agentId, cancellationToken));
        ((ICoreScope)scope).Complete();
        return result;
    }

    public async Task AddAsync(MemoryEntryEntity entity, CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        await scope.ExecuteWithContextAsync<int>(async db =>
        {
            db.MemoryEntries.Add(entity);
            return await db.SaveChangesAsync(cancellationToken);
        });
        ((ICoreScope)scope).Complete();
    }

    public async Task UpdateAsync(MemoryEntryEntity entity, CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        await scope.ExecuteWithContextAsync<int>(async db =>
        {
            db.MemoryEntries.Update(entity);
            return await db.SaveChangesAsync(cancellationToken);
        });
        ((ICoreScope)scope).Complete();
    }

    public async Task<IReadOnlyList<MemoryEntryEntity>> GetByRunIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var rows = await scope.ExecuteWithContextAsync<List<MemoryEntryEntity>>(
            async db => await db.MemoryEntries
                .Where(e => e.RunId == runId)
                .OrderByDescending(e => e.CreatedUtc)
                .ThenByDescending(e => e.Id)
                .ToListAsync(cancellationToken));
        ((ICoreScope)scope).Complete();
        return rows;
    }

    public async Task<IReadOnlyList<MemoryEntryEntity>> GetRecentByAgentIdAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
    {
        // take is the contract surface; defensive clamp here mirrors
        // EFCoreAgentRunFeedbackRepository (Story 2.1 carry-forward).
        if (take <= 0)
        {
            return Array.Empty<MemoryEntryEntity>();
        }
        var effectiveTake = take > 100 ? 100 : take;

        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var rows = await scope.ExecuteWithContextAsync<List<MemoryEntryEntity>>(
            async db => await db.MemoryEntries
                .Where(e => e.AgentId == agentId)
                .OrderByDescending(e => e.CreatedUtc)
                .ThenByDescending(e => e.Id)
                .Take(effectiveTake)
                .ToListAsync(cancellationToken));
        ((ICoreScope)scope).Complete();
        return rows;
    }

    public async Task<IReadOnlyList<MemoryEntryEntity>> GetRecentAcrossAgentsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        // Story 4.9 — Learning Wall consumer. Mirrors GetRecentByAgentIdAsync
        // verbatim minus the per-agent Where filter.
        if (take <= 0)
        {
            return Array.Empty<MemoryEntryEntity>();
        }
        var effectiveTake = take > 100 ? 100 : take;

        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var rows = await scope.ExecuteWithContextAsync<List<MemoryEntryEntity>>(
            async db => await db.MemoryEntries
                .OrderByDescending(e => e.CreatedUtc)
                .ThenByDescending(e => e.Id)
                .Take(effectiveTake)
                .ToListAsync(cancellationToken));
        ((ICoreScope)scope).Complete();
        return rows;
    }
}
