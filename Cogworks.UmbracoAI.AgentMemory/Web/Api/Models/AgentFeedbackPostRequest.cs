using Cogworks.UmbracoAI.AgentMemory.Feedback;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// POST body for
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/feedback</c>.
/// </summary>
/// <remarks>
/// <para>
/// All fields are required except <see cref="Comment"/>. The host user identity
/// (<c>CreatedBy</c>) is resolved server-side via
/// <c>IBackOfficeSecurityAccessor</c> and is NOT part of this payload — never
/// trust a client-supplied <c>createdBy</c>.
/// </para>
/// <para>
/// <b>Agent identity (<c>AgentId</c>) is also resolved server-side</b> via
/// <c>IAgentRunReader.GetRunsForThreadAsync(RunId)</c> — the widget's mount
/// surface (Automate's <c>Ua.Modal.RunDetail</c>) supplies only the workflow
/// run identifier in <c>modalContext.data.runId</c>, never an agent id. Story
/// 2.3 Task 0 spike empirically established that the original 4-field contract
/// (<c>runId, agentId, score, comment</c>) was over-specified; the controller
/// now derives <c>agentId</c> from the run.
/// </para>
/// </remarks>
public sealed record AgentFeedbackPostRequest(
    /// <summary>
    /// The workflow run identifier from the editor's feedback surface — i.e.
    /// the value the Bellissima modal hands the widget in
    /// <c>modalContext.data.runId</c>. Semantically this is the upstream
    /// <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> (workflow-run-level
    /// conversation grouping key; 1 per Automate workflow run, shared across
    /// all <c>RunAgentAction.ExecuteAsync</c> invocations within). The package
    /// stores this in its <c>RunId</c> column for Path (b) naming continuity
    /// per Story 2.3 § Locked decisions; a future post-keynote story may
    /// rename the column to <c>ThreadId</c> (Path a) per AR8.
    /// </summary>
    string RunId,
    FeedbackScore Score,
    string? Comment);
