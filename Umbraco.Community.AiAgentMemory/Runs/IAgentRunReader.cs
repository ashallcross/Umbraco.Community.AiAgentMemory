namespace Cogworks.UmbracoAI.AgentMemory.Runs;

/// <summary>
/// Read-only view over agent runs, synthesised from <see cref="Umbraco.AI.Core.AuditLog.AIAuditLog"/>
/// rows grouped by <c>Metadata["Umbraco.AI.Agent.RunId"]</c>. Runs are persisted by Umbraco.AI's
/// upstream <c>AIAuditingChatMiddleware</c>; we do not write run rows ourselves.
///
/// <para>
/// <b>v0.1 scoping (DRIFT-NEW-5 + Story 0.D Branch 2, ratified at AR28).</b> In v0.1, this reader
/// surfaces ONLY chat calls that flow through the Copilot UI surface — those alone populate
/// <c>Metadata["Umbraco.AI.Agent.RunId"]</c> and <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> in
/// <c>Umbraco.AI.Agent.Core 1.9.0</c> + <c>Umbraco.AI.Automate 1.0.0--alpha1.preview.55</c>. Three
/// invocation paths empirically produce null/empty <c>Metadata</c> and are therefore filtered out
/// by this reader's GROUP BY:
/// </para>
/// <list type="number">
///   <item><description><b>Programmatic</b>: direct <c>IAIAgentService.RunAgentAsync(agentId, messages, options, ct)</c> — empty Metadata. <c>0-c-spike-outcome.md</c> § DRIFT-NEW-5 (3 rows captured).</description></item>
///   <item><description><b>Programmatic via factory</b>: <c>IAIAgentFactory.CreateAgentAsync(... additionalProperties: ...)</c> + <c>agent.RunAsync(prompt, ct)</c> — caller-supplied <c>additionalProperties</c> does NOT propagate to <c>chatOptions.AdditionalProperties</c> in v1.9.0 → empty Metadata. <c>0-c-spike-outcome.md</c> § DRIFT-NEW-5.</description></item>
///   <item><description><b>Umbraco.AI.Automate</b>: <c>RunAgentAction.ExecuteAsync</c> via Automate workflow — empty Metadata for the same structural reason (the action injects <c>IAIAgentService</c> directly without populating the runtime context keys). <c>0-d-spike-outcome.md</c> § Branch decision (1 row captured, <c>metadata: null</c>).</description></item>
/// </list>
/// <para>
/// The contract is correct: null-Metadata rows are not logical agent runs from the reader's
/// perspective — they're audit-rows whose host call path didn't thread RunId/ThreadId through.
/// The reader filters them out cleanly; the GROUP BY pipeline returns zero records for an
/// agent whose runs only flowed through these three paths.
/// </para>
/// <para>
/// <b>Adopter implications (v0.1, pre-PR-Upstream-N):</b>
/// </para>
/// <list type="bullet">
///   <item><description>Runs reaching this reader come from the Copilot UI invocation path only.</description></item>
///   <item><description>Brand Voice Audit Loop demos and other Umbraco.AI.Automate-driven flows produce successful audit-log rows but those rows are NOT visible here.</description></item>
///   <item><description>Story 5.2 README includes a callout instructing adopters to drive demo runs through the Copilot UI for memory features to activate, and notes when PR-Upstream-N is expected to land in upstream.</description></item>
///   <item><description>If Story 4.1's keynote demo can't run through Copilot, John re-scopes the demo flow per AR28's Adam's-downstream-action-#2 + the Story 0.D Fork (i) ratification (see <c>16-ar28-reconciliation-2026-05-06.md</c> § Story 0.D close-out).</description></item>
/// </list>
/// <para>
/// <b>Adopter implications (post-PR-Upstream-N):</b> when upstream's <c>RunAgentAction.ExecuteAsync</c>
/// (or <c>IAIAgentService.RunAgentAsync</c>) ships the metadata-propagation patch (Fork (i),
/// ratified at AR28 final close-out), the Automate-driven and programmatic-driven runs surface in
/// this reader without further package change. Story 1.2's GROUP BY contract is unchanged across
/// the upstream patch — only the row-volume reaching the reader changes.
/// </para>
/// </summary>
public interface IAgentRunReader
{
    /// <summary>
    /// Returns the <see cref="AgentRunRecord"/> matching the given <paramref name="runId"/>, or
    /// <see langword="null"/> if no row carrying <c>Metadata["Umbraco.AI.Agent.RunId"] == runId</c>
    /// is found within the recent-row window
    /// (<c>AgentMemoryOptions.MaxMemoryAgeDays</c>, default 90 days).
    /// </summary>
    /// <remarks>
    /// In v0.1 (DRIFT-NEW-3) each chat call produces exactly one audit-log row with a unique
    /// RunId, so this method projects a single-row group. The
    /// <see cref="AgentRunRecord"/>'s MIN/MAX/SUM/Joined fields degenerate cleanly to the single
    /// row's values. Future-proof seam for upstream multi-row-per-RunId emissions.
    /// <para>
    /// Returns <see langword="null"/> (not an exception) when the
    /// <see cref="Umbraco.AI.Core.AuditLog.IAIAuditLogService"/> dependency throws — graceful
    /// degradation per NFR-R3. The underlying exception is logged at <c>Warning</c> level.
    /// </para>
    /// </remarks>
    Task<AgentRunRecord?> GetRunAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="take"/> most-recent <see cref="AgentRunRecord"/>s for the
    /// given <paramref name="agentId"/>, ordered by <c>StartedUtc</c> descending. <paramref name="take"/>
    /// is clamped to the inclusive range <c>[0, 100]</c>; values outside the range do NOT throw
    /// (<c>0</c> or negative ⇒ empty list; <c>&gt; 100</c> ⇒ 100).
    /// </summary>
    /// <remarks>
    /// Rows whose <c>Metadata</c> is <see langword="null"/> or missing
    /// <c>Metadata["Umbraco.AI.Agent.RunId"]</c> are filtered out (v0.1 reality: three of four
    /// call paths produce null Metadata — see interface-level remarks). Those rows do NOT count
    /// toward the caller's <paramref name="take"/>.
    /// <para>
    /// Returns an empty list (not an exception) when the
    /// <see cref="Umbraco.AI.Core.AuditLog.IAIAuditLogService"/> dependency throws — graceful
    /// degradation per NFR-R3. The underlying exception is logged at <c>Warning</c> level.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AgentRunRecord>> GetRecentRunsForAgentAsync(
        Guid agentId,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns all <see cref="AgentRunRecord"/>s that share the supplied
    /// <paramref name="threadId"/> — i.e. all chat-call rows whose
    /// <c>Metadata["Umbraco.AI.Agent.ThreadId"] == threadId</c>. One Automate workflow run
    /// produces one <c>ThreadId</c> shared across every <c>RunAgentAction.ExecuteAsync</c>
    /// invocation in that workflow; each <c>ExecuteAsync</c> in turn produces a distinct
    /// <c>RunId</c>-group worth of rows. The result is grouped by <c>RunId</c> internally
    /// — one <see cref="AgentRunRecord"/> per <c>ExecuteAsync</c> invocation, ordered by
    /// <c>StartedUtc</c> descending.
    /// </summary>
    /// <remarks>
    /// Story 2.3 / Task 0.5 — bridge for the editor feedback widget. The widget receives
    /// <c>modalContext.data.runId</c> from Automate's <c>Ua.Modal.RunDetail</c> modal; that
    /// value is semantically a <c>ThreadId</c> (workflow-run-level identifier). The
    /// controller (Story 2.2 amended in Task 0.6) calls this method to resolve
    /// <c>agentId</c> server-side from the supplied thread id — the widget never sees the
    /// agent id, never has to provide it.
    /// <para>
    /// Returns an empty list (not an exception) when no rows match OR when the
    /// <see cref="Umbraco.AI.Core.AuditLog.IAIAuditLogService"/> dependency throws —
    /// graceful degradation per NFR-R3. Fast-fails to empty list on null/empty
    /// <paramref name="threadId"/>. Pre-Fork-(i) rows (<c>Metadata = null</c> on
    /// Automate-driven runs in v1.9.0 builds without Adam's PR-Upstream-N patch) do NOT
    /// match this filter — they're filtered out by the same GROUP BY contract as
    /// <see cref="GetRunAsync"/>.
    /// </para>
    /// <para>
    /// <b>v0.1 single-agent assumption (Story 2.3 Task 0.6 caveat):</b> when multiple
    /// <c>RunAgentAction</c> steps within one workflow run use different agents, the
    /// returned records carry distinct <c>AgentId</c> values. The controller picks
    /// <c>records.First().AgentId</c> for feedback attribution — sound for the
    /// Brand Voice Audit demo (single-agent) but surfaces a v0.2 disambiguation
    /// requirement for multi-agent workflows.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<AgentRunRecord>> GetRunsForThreadAsync(
        string threadId,
        CancellationToken cancellationToken);
}
