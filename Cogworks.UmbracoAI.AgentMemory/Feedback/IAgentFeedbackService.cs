namespace Cogworks.UmbracoAI.AgentMemory.Feedback;

/// <summary>
/// Records and queries editor feedback against agent runs. Supersede semantics
/// are keyed on <c>(RunId, CreatedBy)</c> — re-submission by the same user on
/// the same run UPDATEs the existing row in place (preserves <c>Id</c>; updates
/// <c>Score</c>, <c>Comment</c>, <c>CreatedUtc</c>). Different users on the
/// same run produce distinct rows (FR15).
/// </summary>
public interface IAgentFeedbackService
{
    /// <summary>
    /// Records or supersedes a single feedback signal. Idempotent on
    /// <c>(runId, createdBy)</c>: a second call for the same pair UPDATEs the
    /// existing row in place rather than INSERTing a duplicate (FR15).
    /// <c>CreatedUtc</c> is updated to the supersede time so the row surfaces
    /// at the top of <see cref="GetRecentForAgentAsync"/> retrieval.
    /// <c>WorkspaceId</c> is persisted as <c>null</c> in v0.1
    /// (FR33 / FR36 — populated when the host surfaces a workspace context,
    /// expected v0.2).
    /// </summary>
    /// <param name="runId">
    /// Run identifier — matches the upstream
    /// <c>AIAuditLog.Metadata["Umbraco.AI.Agent.RunId"]</c> value
    /// (<see cref="string"/>, not <see cref="System.Guid"/>; Copilot path only
    /// in v0.1 per DRIFT-NEW-5).
    /// </param>
    /// <param name="agentId">Agent identifier the feedback is recorded against.</param>
    /// <param name="score">Editor's verdict (see <see cref="FeedbackScore"/>).</param>
    /// <param name="comment">Optional free-text comment; nullable.</param>
    /// <param name="createdBy">
    /// Authenticated host-user GUID (NFR-S7 — never a service-account
    /// substitute; resolved upstream of this call by the controller layer).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordFeedbackAsync(
        string runId,
        Guid agentId,
        FeedbackScore score,
        string? comment,
        Guid createdBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all feedback rows for the given run, one per
    /// <c>(RunId, CreatedBy)</c> pair. Ordering is <c>CreatedUtc DESC</c>.
    /// Empty list (not exception) when the run has no feedback.
    /// </summary>
    /// <remarks>
    /// Returns an empty list (not an exception) when the underlying repository
    /// throws on the read path — graceful degradation per NFR-R3. The
    /// underlying exception is logged at <c>Warning</c> level.
    /// <c>OperationCanceledException</c> always propagates unwrapped.
    /// </remarks>
    Task<IReadOnlyList<AgentRunFeedback>> GetFeedbackForRunAsync(
        string runId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="take"/> most-recent rows for
    /// <paramref name="agentId"/>, ordered by <c>CreatedUtc DESC</c>.
    /// <paramref name="take"/> is clamped to the inclusive range
    /// <c>[0, 100]</c> non-throwingly (matches
    /// <c>IAgentRunReader.GetRecentRunsForAgentAsync</c> per Story 1.2
    /// contract); <paramref name="take"/> &lt;= 0 returns empty list;
    /// &gt; 100 clamps to 100.
    /// </summary>
    /// <remarks>
    /// Returns an empty list (not an exception) when the underlying repository
    /// throws — graceful degradation per NFR-R3. The underlying exception is
    /// logged at <c>Warning</c> level.
    /// <c>OperationCanceledException</c> always propagates unwrapped.
    /// </remarks>
    Task<IReadOnlyList<AgentRunFeedback>> GetRecentForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken);
}
