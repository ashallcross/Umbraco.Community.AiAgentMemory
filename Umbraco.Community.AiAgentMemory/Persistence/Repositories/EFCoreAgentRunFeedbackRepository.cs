using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Umbraco.Community.AiAgentMemory.Persistence.Repositories;

/// <summary>
/// EF Core repository for <see cref="Entities.AgentRunFeedbackEntity"/>. Composes
/// on <see cref="IEFCoreScopeProvider{TDbContext}"/>; every method follows the
/// canonical <c>CreateScope → ExecuteWithContextAsync → ((ICoreScope)scope).Complete()</c>
/// pattern (mirror upstream <c>Umbraco.AI.Persistence.AuditLog.EFCoreAIAuditLogRepository</c>).
/// </summary>
internal sealed class EFCoreAgentRunFeedbackRepository
{
    private readonly IEFCoreScopeProvider<AgentMemoryDbContext> _scopeProvider;

    public EFCoreAgentRunFeedbackRepository(IEFCoreScopeProvider<AgentMemoryDbContext> scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public async Task<AgentRunFeedbackEntity?> FindByRunIdAndCreatedByAsync(
        string runId,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var result = await scope.ExecuteWithContextAsync<AgentRunFeedbackEntity?>(
            async db => await db.Feedback
                .FirstOrDefaultAsync(f => f.RunId == runId && f.CreatedBy == createdBy, cancellationToken));
        // ((ICoreScope)scope).Complete() commits the underlying transaction;
        // without it the scope's transaction rolls back at Dispose.
        ((ICoreScope)scope).Complete();
        return result;
    }

    public async Task AddAsync(AgentRunFeedbackEntity entity, CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        // Typed overload (return-value-bearing): the lambda yields SaveChangesAsync's
        // rowcount so the compiler can infer T. The void-returning overload of
        // ExecuteWithContextAsync has a phantom generic T that can't be inferred
        // from a Task-without-T lambda.
        await scope.ExecuteWithContextAsync<int>(async db =>
        {
            db.Feedback.Add(entity);
            return await db.SaveChangesAsync(cancellationToken);
        });
        ((ICoreScope)scope).Complete();
    }

    public async Task UpdateAsync(AgentRunFeedbackEntity entity, CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        await scope.ExecuteWithContextAsync<int>(async db =>
        {
            db.Feedback.Update(entity);
            return await db.SaveChangesAsync(cancellationToken);
        });
        ((ICoreScope)scope).Complete();
    }

    public async Task<IReadOnlyList<AgentRunFeedbackEntity>> GetByRunIdAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var rows = await scope.ExecuteWithContextAsync<List<AgentRunFeedbackEntity>>(
            async db => await db.Feedback
                .Where(f => f.RunId == runId)
                .OrderByDescending(f => f.CreatedUtc)
                .ThenByDescending(f => f.Id)
                .ToListAsync(cancellationToken));
        ((ICoreScope)scope).Complete();
        return rows;
    }

    public async Task<IReadOnlyList<AgentRunFeedbackEntity>> GetRecentByAgentIdAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
    {
        // take is pre-clamped by the service layer to [0, 100]. Defensive guard
        // here for direct repo callers; service-layer clamping is the contract
        // surface, but the repo doesn't trust callers blindly.
        if (take <= 0)
        {
            return Array.Empty<AgentRunFeedbackEntity>();
        }

        using var scope = _scopeProvider.CreateScope(RepositoryCacheMode.Default, null);
        var rows = await scope.ExecuteWithContextAsync<List<AgentRunFeedbackEntity>>(
            async db => await db.Feedback
                .Where(f => f.AgentId == agentId)
                .OrderByDescending(f => f.CreatedUtc)
                .ThenByDescending(f => f.Id)
                .Take(take)
                .ToListAsync(cancellationToken));
        ((ICoreScope)scope).Complete();
        return rows;
    }
}
