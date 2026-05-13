using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.AuditLog;

namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Default <see cref="IAgentRunReader"/> implementation. Composes on
/// <see cref="IAIAuditLogService"/> (host-owned, upstream-persistence) — we do NOT
/// own a parallel runs table (AR8 / AR9).
/// </summary>
internal sealed class AgentRunReader : IAgentRunReader
{
    // Upstream-owned context-key literals, verified at 0-c § AC1.c + 0-d § (c) against
    // Umbraco.AI.Agent.Core 1.9.0 (Constants.ContextKeys.RunId / .ThreadId). Sourced as
    // named constants for grep-ability and to make the dependency surface explicit
    // (these are NOT brand-rename targets — AR20 / AR21).
    private const string RunIdMetadataKey = "Umbraco.AI.Agent.RunId";
    private const string ThreadIdMetadataKey = "Umbraco.AI.Agent.ThreadId";

    // Empirically pinned at 0-c § AC1.d + 0-d § (d): every agent chat row has
    // featureType="agent" (independent of Copilot / programmatic / Automate path).
    private const string AgentFeatureType = "agent";

    // Underlying audit-log page-size for the recent-row fallback envelope (per
    // 0-c § AC1.g lines 189-194: "12 rows in a 30-min window for one agent;
    // pull take ≈ 200 over 7 days → expect ~20-40 RunId-bearing groups under v0.1
    // demo scale"). Scales with caller's take to absorb null-Metadata orphans
    // (DRIFT-NEW-5: 3-of-4 call paths produce null Metadata in v0.1).
    private const int MaxAuditLogPageSize = 200;

    // GetRunsForThreadAsync paging cap — upstream AIAuditLogFilter has no ThreadId
    // filter, so we read pages and filter in-memory. 10 pages × 200 = 2000 rows
    // covers a busy host's MaxMemoryAgeDays window with comfortable headroom for
    // the v0.1 demo + brand-audit feedback flow. Cap exists to bound IO; loud
    // log breadcrumb if hit so ops can re-tune.
    private const int MaxAuditLogPagesForThreadLookup = 10;

    // Joined-snapshot separator per architecture v1 § Run reading + 0-c §
    // Synthesis findings table (line 228).
    private const string SnapshotJoinSeparator = "\n\n---\n\n";

    private readonly IAIAuditLogService _auditLogService;
    private readonly IOptions<AgentMemoryOptions> _options;
    private readonly ILogger<AgentRunReader> _logger;

    public AgentRunReader(
        IAIAuditLogService auditLogService,
        IOptions<AgentMemoryOptions> options,
        ILogger<AgentRunReader> logger)
    {
        _auditLogService = auditLogService;
        _options = options;
        _logger = logger;
    }

    public async Task<AgentRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return null;
        }

        try
        {
            var filter = new AIAuditLogFilter
            {
                FromDate = DateTime.UtcNow.AddDays(-_options.Value.MaxMemoryAgeDays),
                FeatureType = AgentFeatureType,
                // No FeatureId — GetRunAsync is RunId-keyed, not agent-keyed.
            };

            var (rows, _) = await _auditLogService.GetAuditLogsPagedAsync(
                filter,
                skip: 0,
                take: MaxAuditLogPageSize,
                ct: cancellationToken);

            var matching = rows
                .Where(r => r.Metadata is not null
                            && r.Metadata.TryGetValue(RunIdMetadataKey, out var keyValue)
                            && keyValue == runId
                            && r.FeatureId.HasValue
                            && r.FeatureId.Value != Guid.Empty)
                .ToList();

            if (matching.Count == 0)
            {
                return null;
            }

            return ProjectGroupToRecord(runId, matching);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read audit log for {Operation} runId={RunId}",
                nameof(GetRunAsync),
                runId);
            return null;
        }
    }

    public async Task<IReadOnlyList<AgentRunRecord>> GetRunsForThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return Array.Empty<AgentRunRecord>();
        }

        try
        {
            var filter = new AIAuditLogFilter
            {
                FromDate = DateTime.UtcNow.AddDays(-_options.Value.MaxMemoryAgeDays),
                FeatureType = AgentFeatureType,
                // No FeatureId — GetRunsForThreadAsync is ThreadId-keyed, not agent-keyed.
            };

            // Upstream AIAuditLogFilter has no ThreadId predicate, so we page through
            // the FeatureType-scoped result and in-memory-filter on Metadata. Cap at
            // MaxAuditLogPagesForThreadLookup to bound IO; loud-log if hit so ops can
            // tune MaxMemoryAgeDays or escalate to an upstream filter-API request.
            var aggregated = new List<AIAuditLog>();
            for (var page = 0; page < MaxAuditLogPagesForThreadLookup; page++)
            {
                var (rows, _) = await _auditLogService.GetAuditLogsPagedAsync(
                    filter,
                    skip: page * MaxAuditLogPageSize,
                    take: MaxAuditLogPageSize,
                    ct: cancellationToken);
                var rowsList = rows.ToList();
                aggregated.AddRange(rowsList);
                if (rowsList.Count < MaxAuditLogPageSize)
                {
                    break;
                }
                if (page == MaxAuditLogPagesForThreadLookup - 1)
                {
                    _logger.LogWarning(
                        "GetRunsForThreadAsync hit the page-scan cap ({Cap} pages × {PageSize}) for ThreadId={ThreadId}. Older audit rows beyond {MaxRows} were not scanned; consider lowering MaxMemoryAgeDays or requesting an upstream ThreadId filter.",
                        MaxAuditLogPagesForThreadLookup,
                        MaxAuditLogPageSize,
                        threadId,
                        MaxAuditLogPagesForThreadLookup * MaxAuditLogPageSize);
                }
            }

            // Filter to rows carrying the matching ThreadId metadata + a RunId we can group by
            // + a non-empty agent FeatureId. Pre-Fork-(i) rows (Metadata = null on
            // Automate-driven runs in v1.9.0 builds without Adam's PR-Upstream-N patch) drop
            // out at this filter — they're not visible to thread-keyed lookups, same as the
            // RunId-keyed methods.
            var matching = aggregated
                .Where(r => r.Metadata is not null
                            && r.Metadata.TryGetValue(ThreadIdMetadataKey, out var rowThreadId)
                            && rowThreadId == threadId
                            && r.Metadata.TryGetValue(RunIdMetadataKey, out var keyValue)
                            && !string.IsNullOrEmpty(keyValue)
                            && r.FeatureId.HasValue
                            && r.FeatureId.Value != Guid.Empty)
                .GroupBy(r => r.Metadata![RunIdMetadataKey])
                .Select(g => ProjectGroupToRecord(g.Key, g))
                // ThenByDescending(RunId) pins a deterministic ordering when multiple
                // groups share an identical StartedUtc (clock-granularity collisions).
                // The controller picks records.First().AgentId, so determinism here
                // matters for feedback attribution reproducibility.
                .OrderByDescending(r => r.StartedUtc)
                .ThenByDescending(r => r.RunId, StringComparer.Ordinal)
                .ToList();

            return matching;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read audit log for {Operation} threadId={ThreadId}",
                nameof(GetRunsForThreadAsync),
                threadId);
            return Array.Empty<AgentRunRecord>();
        }
    }

    public async Task<IReadOnlyList<AgentRunRecord>> GetRecentRunsForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken)
    {
        // Clamp take to [0, 100] per AC7 / FR7. Non-throwing on out-of-range values.
        if (take <= 0)
        {
            return Array.Empty<AgentRunRecord>();
        }
        if (take > 100)
        {
            take = 100;
        }

        try
        {
            // Scale the underlying page-size with caller's take to absorb null-Metadata
            // orphan rows (DRIFT-NEW-5 — 3-of-4 v0.1 call paths produce null Metadata).
            // Cap at MaxAuditLogPageSize.
            var pageSize = Math.Min(MaxAuditLogPageSize, take * 10);

            var filter = new AIAuditLogFilter
            {
                FromDate = DateTime.UtcNow.AddDays(-_options.Value.MaxMemoryAgeDays),
                FeatureType = AgentFeatureType,
                FeatureId = agentId,
            };

            var (rows, _) = await _auditLogService.GetAuditLogsPagedAsync(
                filter,
                skip: 0,
                take: pageSize,
                ct: cancellationToken);

            var groups = rows
                .Where(r => r.Metadata is not null
                            && r.Metadata.TryGetValue(RunIdMetadataKey, out var keyValue)
                            && !string.IsNullOrEmpty(keyValue)
                            && r.FeatureId.HasValue)
                .GroupBy(r => r.Metadata![RunIdMetadataKey]);

            // Project each group → AgentRunRecord per architecture v1 lines 162-166
            // and 0-c § Synthesis findings table.
            //
            // The MIN/MAX/SUM/Joined aggregation rules degenerate cleanly to single-row
            // groups in v0.1 (DRIFT-NEW-3 — 1 chat call = 1 RunId = 1 row) AND remain
            // coded as a future-proof seam for the day upstream emits multi-row-per-RunId
            // groups (PR-Upstream-3 candidate — architecture v1 line 164).
            var records = groups
                .Select(g => ProjectGroupToRecord(g.Key, g))
                .OrderByDescending(r => r.StartedUtc)
                .Take(take)
                .ToList();

            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read audit log for {Operation} agentId={AgentId}",
                nameof(GetRecentRunsForAgentAsync),
                agentId);
            return Array.Empty<AgentRunRecord>();
        }
    }

    // The MIN/MAX/SUM/Joined aggregation rules below remain in code as a future-proof seam:
    // they degenerate cleanly to single-row groups today, and continue to work if upstream later
    // emits multi-row-per-RunId groups (e.g., a future "agent run rollup" row — PR-Upstream-3
    // candidate).
    private static AgentRunRecord ProjectGroupToRecord(string runId, IEnumerable<AIAuditLog> group)
    {
        // Order by StartTime ascending so "first non-null X" picks the chronologically-first row.
        var ordered = group.OrderBy(r => r.StartTime).ToList();
        var first = ordered[0];

        var startedUtc = ordered.Min(r => r.StartTime);
        var completedUtc = ordered.Any(r => r.EndTime.HasValue)
            ? ordered.Where(r => r.EndTime.HasValue).Max(r => r.EndTime)
            : null;

        var aggregateStatus = MapStatus(WorstStatus(ordered.Select(r => r.Status)));
        var error = ordered.Select(r => r.ErrorMessage).FirstOrDefault(e => !string.IsNullOrEmpty(e));

        var promptSnapshots = ordered
            .Select(r => r.PromptSnapshot)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        var prompt = promptSnapshots.Count == 0 ? null : string.Join(SnapshotJoinSeparator, promptSnapshots);

        var responseSnapshots = ordered
            .Select(r => r.ResponseSnapshot)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        var response = responseSnapshots.Count == 0 ? null : string.Join(SnapshotJoinSeparator, responseSnapshots);

        var tokensIn = ordered.Any(r => r.InputTokens.HasValue)
            ? ordered.Where(r => r.InputTokens.HasValue).Sum(r => r.InputTokens!.Value)
            : (int?)null;
        var tokensOut = ordered.Any(r => r.OutputTokens.HasValue)
            ? ordered.Where(r => r.OutputTokens.HasValue).Sum(r => r.OutputTokens!.Value)
            : (int?)null;

        // ThreadId — first row's value (null when key absent). All rows in a Copilot
        // ThreadId-RunId group share the same ThreadId by definition, so picking from
        // the first is equivalent to picking from any.
        string? threadId = null;
        if (first.Metadata is not null && first.Metadata.TryGetValue(ThreadIdMetadataKey, out var threadIdValue))
        {
            threadId = threadIdValue;
        }

        var userId = ordered.Select(r => r.UserId).FirstOrDefault(u => !string.IsNullOrEmpty(u));
        var traceId = ordered.Select(r => r.TraceId).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        var agentVersion = first.FeatureVersion;

        return new AgentRunRecord(
            RunId: runId,
            AgentId: first.FeatureId!.Value,
            AgentVersion: agentVersion,
            StartedUtc: startedUtc,
            CompletedUtc: completedUtc,
            AggregateStatus: aggregateStatus,
            Error: error,
            PromptSnapshotJoined: prompt,
            ResponseSnapshotJoined: response,
            TokenCountInput: tokensIn,
            TokenCountOutput: tokensOut,
            ThreadId: threadId,
            UserId: userId,
            TraceId: traceId);
    }

    // Worst-status precedence — CONTRACT DECISION by us, NOT a runtime-proven invariant.
    //
    //     Blocked > Failed > Cancelled > PartialSuccess > Running > Succeeded
    //
    // Empirical evidence at AR28 (Story 0.C, 12 rows):
    //   - Failed > Succeeded — verified by row 782f5a9d-... (SIGINT-killed) vs the 11 Succeeded rows.
    //   - Succeeded — verified across 11 rows.
    //   - Blocked, Cancelled, PartialSuccess, Running — NOT exercised against real data.
    //
    // The full ordering is Story 1.2's mapping contract, ratified at AR28 to match upstream's
    // AIAuditLogStatus authoring intent (Blocked = guardrail violation = strongest negative;
    // Failed = unhandled error; Cancelled = explicit caller-driven cancel; PartialSuccess =
    // recoverable degradation; Running = in-flight; Succeeded = clean). Story 1.2 inherits this
    // precedence as a deliberate decision; Story 1.2's tests cover all six values via mocked
    // AIAuditLog rows, not via runtime proof. Re-litigate if upstream's enum semantics change.
    //
    // DRIFT-NEW-6 clarification: SIGINT-killed in-flight chat work surfaces upstream as
    // Status=Failed with ErrorMessage="The operation was canceled." and ErrorCategory=Unknown —
    // NOT as Cancelled. Cancelled is reserved for explicit caller-driven cancellation paths the
    // v1.9.0 runtime does not exercise from process shutdown.
    private static AIAuditLogStatus WorstStatus(IEnumerable<AIAuditLogStatus> statuses)
    {
        var worst = AIAuditLogStatus.Succeeded;
        var worstRank = StatusRank(worst);

        foreach (var status in statuses)
        {
            var rank = StatusRank(status);
            if (rank > worstRank)
            {
                worst = status;
                worstRank = rank;
            }
        }

        return worst;
    }

    // Unknown values escalate to the worst rank so MapStatus's defensive "_ => Failed"
    // fallback carries the loud signal end-to-end (a future Umbraco.AI version adding a
    // new AIAuditLogStatus value our type doesn't carry yet should NOT silently project
    // as Succeeded — adopter mental model is "unknown upstream state = treat as failed").
    private static int StatusRank(AIAuditLogStatus status) => status switch
    {
        AIAuditLogStatus.Succeeded => 0,
        AIAuditLogStatus.Running => 1,
        AIAuditLogStatus.PartialSuccess => 2,
        AIAuditLogStatus.Cancelled => 3,
        AIAuditLogStatus.Failed => 4,
        AIAuditLogStatus.Blocked => 5,
        _ => int.MaxValue,
    };

    // Map upstream AIAuditLogStatus → our AgentRunStatus (1:1 by name, ordinal-aligned —
    // DRIFT-NEW-1 ratified at AR28 Option A). Defensive cast in case a future Umbraco.AI
    // version adds an enum value our type doesn't carry yet — fall back to Failed (loud
    // signal at adopter mental model: "unknown upstream state = treat as failed").
    private static AgentRunStatus MapStatus(AIAuditLogStatus upstream) => upstream switch
    {
        AIAuditLogStatus.Running => AgentRunStatus.Running,
        AIAuditLogStatus.Succeeded => AgentRunStatus.Succeeded,
        AIAuditLogStatus.Failed => AgentRunStatus.Failed,
        AIAuditLogStatus.Cancelled => AgentRunStatus.Cancelled,
        AIAuditLogStatus.PartialSuccess => AgentRunStatus.PartialSuccess,
        AIAuditLogStatus.Blocked => AgentRunStatus.Blocked,
        _ => AgentRunStatus.Failed,
    };
}
