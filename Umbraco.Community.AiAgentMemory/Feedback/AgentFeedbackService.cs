using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Umbraco.Community.AiAgentMemory.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.AiAgentMemory.Feedback;

/// <summary>
/// Default <see cref="IAgentFeedbackService"/> implementation. Composes on
/// <see cref="EFCoreAgentRunFeedbackRepository"/> (Scoped) which composes on
/// <c>IEFCoreScopeProvider&lt;AgentMemoryDbContext&gt;</c>. Supersede semantics
/// implemented at the service layer: <c>FindByRunIdAndCreatedByAsync</c> →
/// mutate-or-insert. Race-safe under Umbraco backoffice single-user-session
/// model; concurrent same-(RunId, CreatedBy) writes from a multi-tab race
/// would produce two rows, caught at the GET side — Story 5.x adds a unique
/// index if production data surfaces the race.
/// </summary>
internal sealed class AgentFeedbackService : IAgentFeedbackService
{
    private readonly EFCoreAgentRunFeedbackRepository _repository;
    private readonly ILogger<AgentFeedbackService> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentFeedbackService(
        EFCoreAgentRunFeedbackRepository repository,
        ILogger<AgentFeedbackService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task RecordFeedbackAsync(
        string runId,
        Guid agentId,
        FeedbackScore score,
        string? comment,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        // Write-path failures propagate to the caller (Story 2.2 controller
        // returns HTTP 500). OperationCanceledException always propagates
        // unwrapped — never swallowed.
        var existing = await _repository
            .FindByRunIdAndCreatedByAsync(runId, createdBy, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (existing is null)
        {
            var entity = new AgentRunFeedbackEntity
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                AgentId = agentId,
                Score = (int)score,
                Comment = comment,
                CreatedBy = createdBy,
                CreatedUtc = now,
                // v0.1: FR33 / FR36 — workspace context not yet host-surfaced.
                // Populated when Umbraco exposes a workspace identity (v0.2+).
                WorkspaceId = null,
            };
            await _repository.AddAsync(entity, cancellationToken);
            return;
        }

        // Supersede: mutate the same tracked instance — preserves Id, RunId,
        // AgentId, CreatedBy, WorkspaceId; updates Score, Comment, CreatedUtc.
        // CreatedUtc moves to supersede time so the row surfaces at the top
        // of GetRecentForAgentAsync retrieval (AC3.a locked decision).
        existing.Score = (int)score;
        existing.Comment = comment;
        existing.CreatedUtc = now;
        await _repository.UpdateAsync(existing, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRunFeedback>> GetFeedbackForRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _repository.GetByRunIdAsync(runId, cancellationToken);
            return rows.Select(ToRecord).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read feedback rows for {Operation} runId={RunId}",
                nameof(GetFeedbackForRunAsync),
                runId);
            return Array.Empty<AgentRunFeedback>();
        }
    }

    public async Task<IReadOnlyList<AgentRunFeedback>> GetRecentForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
    {
        // Clamp take to [0, 100] (mirror IAgentRunReader.GetRecentRunsForAgentAsync
        // per Story 1.2 contract). Non-throwing on out-of-range values.
        if (take <= 0)
        {
            return Array.Empty<AgentRunFeedback>();
        }
        if (take > 100)
        {
            take = 100;
        }

        try
        {
            var rows = await _repository.GetRecentByAgentIdAsync(agentId, take, cancellationToken);
            return rows.Select(ToRecord).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read feedback rows for {Operation} agentId={AgentId}",
                nameof(GetRecentForAgentAsync),
                agentId);
            return Array.Empty<AgentRunFeedback>();
        }
    }

    private AgentRunFeedback ToRecord(AgentRunFeedbackEntity row) => new(
        Id: row.Id,
        RunId: row.RunId,
        AgentId: row.AgentId,
        Score: MapScore(row.Score, row.Id),
        Comment: row.Comment,
        CreatedBy: row.CreatedBy,
        CreatedUtc: row.CreatedUtc);

    // Mirrors AgentRunReader.MapStatus — defensive cast for an int column with
    // no DB-level CHECK constraint. An unknown ordinal (manual SQL tampering,
    // future schema evolution) maps to Neutral and logs a warning so the
    // anomaly is loud in operator dashboards.
    private FeedbackScore MapScore(int raw, Guid rowId) => raw switch
    {
        0 => FeedbackScore.ThumbsUp,
        1 => FeedbackScore.ThumbsDown,
        2 => FeedbackScore.Neutral,
        _ => LogAndDefaultScore(raw, rowId),
    };

    private FeedbackScore LogAndDefaultScore(int raw, Guid rowId)
    {
        _logger.LogWarning(
            "Unknown FeedbackScore ordinal {Raw} on feedback row {RowId} — mapped to Neutral. " +
            "Persistence-locked values are ThumbsUp=0, ThumbsDown=1, Neutral=2.",
            raw,
            rowId);
        return FeedbackScore.Neutral;
    }
}
