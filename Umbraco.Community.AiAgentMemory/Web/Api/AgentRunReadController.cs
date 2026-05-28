using System.Text.Json;
using Asp.Versioning;
using Umbraco.Community.AiAgentMemory.Runs;
using Umbraco.Community.AiAgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.AI.Agent.Core.Agents;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace Umbraco.Community.AiAgentMemory.Web.Api;

/// <summary>
/// Story 4.2 — Backoffice Management API for reading an agent run's identity +
/// parsed structured output for the editor feedback widget's "Agent output"
/// panel. Routes to
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/runs/{runId}</c> via the
/// canonical Umbraco 17.3.2 pattern (same shape as
/// <see cref="AgentFeedbackController"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Closes DRIFT-4.1-12:</b> the Run Detail modal previously rendered ONLY the
/// thumbs-up/down + comment form (Story 2.3 Strategy B modal-replacement
/// dropped the upstream agent-output chrome). This endpoint provides the data
/// the widget needs to render score + flagged issues + suggestions in a
/// <c>&lt;uui-box&gt;</c> above the feedback form so editors see what they're
/// rating.
/// </para>
/// <para>
/// <b>Auth + Swagger:</b> identical pattern to
/// <see cref="AgentFeedbackController"/> — <c>BackOfficeAccess</c> policy,
/// bearer token via OpenIddict, no manual 401/403 <c>ProducesResponseType</c>
/// (the <c>BackOfficeSecurityRequirementsOperationFilterBase</c> filter
/// subclassed by <c>AgentMemoryBackofficeApiComposer</c> auto-adds those).
/// </para>
/// <para>
/// <b>Run identity resolution:</b> the supplied <c>runId</c> route parameter
/// is semantically the upstream <c>Metadata.Umbraco.AI.Agent.ThreadId</c> per
/// the Story 2.3 schema amendment 2026-05-13 (the editor's modal hands the
/// widget <c>modalContext.data.runId</c>, which maps to ThreadId, not a
/// per-call RunId). The controller invokes
/// <see cref="IAgentRunReader.GetRunsForThreadAsync"/> + picks the first
/// record (most-recent per <c>StartedUtc DESC</c> convention — matches
    /// <see cref="AgentFeedbackController"/>'s v0.1 single-agent attribution). The
    /// 404 response copy keeps the same adopter-facing recovery instruction as
    /// <see cref="AgentFeedbackController"/>'s equivalent branch.
/// </para>
/// <para>
/// <b>Graceful degradation (NFR-R1):</b> if the matched record's
/// <c>ResponseSnapshotJoined</c> is null, empty, or fails JSON parsing, the
/// endpoint returns 200 OK with <c>Score = null</c> + empty issues/suggestions
/// arrays + the run-identity fields populated. The widget surfaces an "Agent
/// output unavailable" notice but still allows feedback submission.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("cogworks-agent-memory/runs")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[MapToApi(Constants.ApiName)]
public sealed class AgentRunReadController : ManagementApiControllerBase
{
    /// <summary>
    /// Server-side cap on the <c>runId</c> route parameter. Mirrors
    /// <see cref="AgentFeedbackController.RunIdMaxChars"/> (256) for parity
    /// with the POST endpoint's validation contract — pre-empts resource-
    /// exhaustion vectors via oversized route segments.
    /// </summary>
    internal const int RunIdMaxChars = 256;

    /// <summary>
    /// Cap on the number of <c>issues</c> / <c>suggestions</c> entries surfaced
    /// to the widget. Adopter agents with longer arrays get truncated; v0.1
    /// demo expectations are well under this cap. Pre-allocation size for the
    /// list builders also clamps to this cap so an attacker-controlled
    /// <c>GetArrayLength()</c> can't drive an OOM via runaway capacity hint.
    /// </summary>
    internal const int MaxStructuredOutputItems = 100;

    /// <summary>
    /// Cap on the number of memory-injection bullets surfaced to the widget
    /// via <see cref="AgentRunDetailResponse.CitedMemories"/>. Defensive
    /// against runaway-emission shapes mirroring
    /// <see cref="MaxStructuredOutputItems"/>; v0.1 TopK is well under this
    /// cap (Story 3.2's <c>AgentMemoryOptions.MemoryTopK</c> default 5).
    /// Story 4.5 Q2a.
    /// </summary>
    internal const int MaxCitedMemories = 10;

    /// <summary>
    /// Cap on each <see cref="AgentRunCitedMemory.CommentSnippet"/> in chars.
    /// Trimmed at the controller layer (NOT the widget) with <c>"…"</c>
    /// ellipsis appended. Editor comments can run to 4000 chars (the
    /// <see cref="AgentFeedbackController.CommentMaxChars"/> cap) but the
    /// widget's cited-memory list cell would overflow at that length.
    /// Story 4.5 Q2a.
    /// </summary>
    internal const int CommentSnippetMaxChars = 300;

    /// <summary>
    /// Anchor literal matched at the start of
    /// <see cref="Runs.AgentRunRecord.PromptSnapshotJoined"/> to detect memory
    /// injection. Story 3.3 outer-wrapping invariant — the upstream chat
    /// composer prepends <c>"[system] "</c> to the system-role message body
    /// emitted by
    /// <c>MemoryInjectionMiddleware.BuildMemorySystemMessage</c>.
    /// </summary>
    private const string MemoryInjectionAnchor = "[system] Lessons from past runs:\n";

    private readonly IAgentRunReader _runReader;
    private readonly IAIAgentService _agentService;
    private readonly ILogger<AgentRunReadController> _logger;

    public AgentRunReadController(
        IAgentRunReader runReader,
        IAIAgentService agentService,
        ILogger<AgentRunReadController> logger)
    {
        ArgumentNullException.ThrowIfNull(runReader);
        ArgumentNullException.ThrowIfNull(agentService);
        ArgumentNullException.ThrowIfNull(logger);
        _runReader = runReader;
        _agentService = agentService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the run's identity + parsed structured output (score, flagged
    /// issues, suggestions). Used by the editor feedback widget to render
    /// agent-output context above the feedback form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Optional <paramref name="selectedRunId"/> (Story 4.12 picker):</b>
    /// when supplied, the controller selects the single sibling within the
    /// ThreadId group whose <c>Metadata.Umbraco.AI.Agent.RunId</c> equals
    /// <paramref name="selectedRunId"/> — surfacing a specific For Each
    /// iteration's prompt/response/memory-injection state. Unknown
    /// <paramref name="selectedRunId"/> returns 404. When omitted, behaviour
    /// is byte-compatible with Story 4.5: selects <c>runs[0]</c> from
    /// <see cref="IAgentRunReader.GetRunsForThreadAsync"/>'s DESC-ordered
    /// list (most-recent iteration).
    /// </para>
    /// </remarks>
    [HttpGet("{runId}")]
    [ProducesResponseType<AgentRunDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string runId,
        CancellationToken cancellationToken,
        [FromQuery] string? selectedRunId = null)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return Problem(
                title: "Invalid run identifier.",
                detail: "runId is required and cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (runId.Length > RunIdMaxChars)
        {
            return Problem(
                title: "Invalid run identifier.",
                detail: $"runId cannot exceed {RunIdMaxChars} characters (received {runId.Length}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Story 4.12 — selectedRunId is opt-in. When supplied as a non-null
        // string it must be non-whitespace (symmetric with the runId rule
        // above); silently treating whitespace-only as "legacy mode" would
        // mask client contract drift.
        if (selectedRunId is not null && string.IsNullOrWhiteSpace(selectedRunId))
        {
            return Problem(
                title: "Invalid selected run identifier.",
                detail: "selectedRunId cannot be whitespace; omit the query parameter for the legacy ThreadId-keyed path.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (selectedRunId is not null && selectedRunId.Length > RunIdMaxChars)
        {
            return Problem(
                title: "Invalid selected run identifier.",
                detail: $"selectedRunId cannot exceed {RunIdMaxChars} characters (received {selectedRunId.Length}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Story 4.5 — canonical Story 1.2 reader contract. DRIFT-4.5-impl-1
        // middleware FeatureType-preservation patch (architect ratification
        // 2026-05-19 mid-gate) ensures the agent's chat-call audit row is
        // attributed `FeatureType="agent"` — so GetRunsForThreadAsync's
        // FeatureType=agent filter surfaces it correctly. The Story 4.5 spec
        // originally proposed a reader-side workaround (GetChatRunForThreadAsync)
        // but architect ratification reverted that mechanism in favour of the
        // cause-fix at the middleware. See Story 4.5 § Architect ratification
        // 2026-05-19.
        var runs = await _runReader
            .GetRunsForThreadAsync(runId, cancellationToken)
            .ConfigureAwait(false);

        if (runs.Count == 0)
        {
            _logger.LogDebug(
                "AgentRunReadController.GetAsync — IAgentRunReader.GetRunsForThreadAsync returned zero records for RunId={RunId}.",
                runId);
            return Problem(
                title: "Run not found.",
                detail: $"No agent runs found for the supplied runId ('{runId}'). The run may not be audit-logged yet — try refreshing the page in a moment, or contact your administrator if the issue persists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        AgentRunRecord run;
        string? resolvedSelectedRunId;
        if (!string.IsNullOrWhiteSpace(selectedRunId))
        {
            // Story 4.12 picker — pick the named iteration from the ThreadId
            // group. FirstOrDefault + duplicate-detection log preserves the
            // v0.1 single-row-per-RunId contract (DRIFT-NEW-3 / Story 1.2
            // § Remarks) without throwing 500 on contract violation; a future
            // tool-call follow-up retry that re-uses a RunId surfaces as
            // Warning. Unknown selectedRunId surfaces as 404.
            var matches = runs.Where(r =>
                string.Equals(r.RunId, selectedRunId, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
            {
                _logger.LogWarning(
                    "AgentRunReadController.GetAsync — selectedRunId={SelectedRunId} matched {MatchCount} rows within ThreadId={ThreadId} group; single-row-per-RunId contract violated. Returning first match.",
                    selectedRunId, matches.Count, runId);
            }
            var match = matches.Count > 0 ? matches[0] : null;
            if (match is null)
            {
                _logger.LogDebug(
                    "AgentRunReadController.GetAsync — selectedRunId={SelectedRunId} not found within ThreadId={ThreadId} group ({Count} siblings).",
                    selectedRunId, runId, runs.Count);
                return Problem(
                    title: "Selected iteration not found.",
                    detail: $"No iteration with runId '{selectedRunId}' was found within this workflow run. The iteration may have been pruned, or it belongs to a different workflow.",
                    statusCode: StatusCodes.Status404NotFound);
            }
            run = match;
            resolvedSelectedRunId = match.RunId;
        }
        else
        {
            // Pre-Story-4.12 byte-compatible behaviour — most-recent iteration
            // (DESC ordering from GetRunsForThreadAsync). v0.1 single-agent
            // attribution assumption — Brand Voice Audit demo's workflow is
            // single-agent (one Run AI Agent step). Mirrors
            // AgentFeedbackController.PostAsync. Multi-agent disambiguation is
            // a v0.2 candidate.
            run = runs[0];
            resolvedSelectedRunId = null;
        }

        var (score, issues, suggestions) = TryParseStructuredOutput(run.ResponseSnapshotJoined);
        var (memoryUsed, citedMemories) = ParseMemoryInjection(run.PromptSnapshotJoined);

        // Story 4.8 — resolve the agent's display name via IAIAgentService so
        // the Run Detail modal shows e.g. "Brand Voice Auditor" instead of the
        // pre-Story-4.8 fallback "Agent {agentId-first-8}". Any throw / null /
        // cancellation degrades to AgentDisplayName = null (widget keeps its
        // existing fallback). ContentNodeName remains null in v0.1 — separate
        // concern not in Story 4.8 scope.
        var agentDisplayName = await ResolveAgentDisplayNameAsync(run.AgentId, cancellationToken).ConfigureAwait(false);

        var response = new AgentRunDetailResponse(
            RunId: runId,
            AgentId: run.AgentId,
            AgentDisplayName: agentDisplayName,
            ContentNodeName: null,
            RanAtUtc: run.StartedUtc,
            Score: score,
            Issues: issues,
            Suggestions: suggestions,
            MemoryUsed: memoryUsed,
            CitedMemories: citedMemories,
            SelectedRunId: resolvedSelectedRunId);

        return Ok(response);
    }

    /// <summary>
    /// Story 4.12 — returns all sibling agent-run iterations sharing the
    /// supplied <paramref name="threadId"/> (workflow-run grouping key from
    /// <c>Metadata.Umbraco.AI.Agent.ThreadId</c>). Used by the Run Detail
    /// modal's per-iteration picker so editors can flip between For Each
    /// iterations without leaving the modal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sort order:</b> ASC by <see cref="AgentRunRecord.StartedUtc"/> —
    /// natural sequential-walk-through order for an editor reviewing an
    /// N-item batch. Note that
    /// <see cref="IAgentRunReader.GetRunsForThreadAsync"/> returns DESC; this
    /// endpoint re-sorts ASC explicitly per Story 4.12 LD#3a.
    /// </para>
    /// <para>
    /// <b>Graceful degradation (NFR-R3):</b> empty list for unknown
    /// ThreadIds; reader throws are caught at Warning + surface as empty
    /// list. <see cref="OperationCanceledException"/> propagates per
    /// ASP.NET Core convention.
    /// </para>
    /// </remarks>
    [HttpGet("{threadId}/siblings")]
    [ProducesResponseType<IReadOnlyList<AgentRunSiblingResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSiblingsAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return Problem(
                title: "Invalid thread identifier.",
                detail: "threadId is required and cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (threadId.Length > RunIdMaxChars)
        {
            return Problem(
                title: "Invalid thread identifier.",
                detail: $"threadId cannot exceed {RunIdMaxChars} characters (received {threadId.Length}).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        IReadOnlyList<AgentRunRecord> runs;
        try
        {
            runs = await _runReader
                .GetRunsForThreadAsync(threadId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentRunReadController.GetSiblingsAsync — IAgentRunReader.GetRunsForThreadAsync threw for ThreadId={ThreadId}; returning empty list. NFR-R3 graceful degradation.",
                threadId);
            return Ok(Array.Empty<AgentRunSiblingResponse>());
        }

        // ASC sort with RunId tiebreaker (deterministic order when parallel-
        // fork iterations share microsecond-identical StartedUtc) + project.
        // The response's ThreadId is sourced from the row (not the route
        // parameter) so any reader-layer filter bug surfaces as a visible
        // mismatch rather than being masked by client-input echo. Rows with
        // null ThreadId are filtered out + warn-logged; that shape indicates
        // a reader-layer bug and shouldn't reach the widget. Empty list is
        // the legitimate response for unknown ThreadIds (not 404 — the
        // picker is the natural place to surface "no iterations" copy; the
        // modal stays usable).
        var siblings = runs
            .Where(r =>
            {
                if (r.ThreadId is null)
                {
                    _logger.LogWarning(
                        "AgentRunReadController.GetSiblingsAsync — IAgentRunReader returned a row with null ThreadId for query ThreadId={ThreadId}, RunId={RunId}; dropping. Indicates reader-layer filter bug.",
                        threadId, r.RunId);
                    return false;
                }
                return true;
            })
            .OrderBy(r => r.StartedUtc)
            .ThenBy(r => r.RunId, StringComparer.Ordinal)
            .Select(r => new AgentRunSiblingResponse(
                ThreadId: r.ThreadId!,
                RunId: r.RunId,
                StartedUtc: r.StartedUtc))
            .ToList();

        return Ok((IReadOnlyList<AgentRunSiblingResponse>)siblings);
    }

    /// <summary>
    /// Story 4.8 — resolves the agent's display name via
    /// <see cref="IAIAgentService.GetAgentAsync"/> for the supplied
    /// <paramref name="agentId"/>. Returns <see cref="AIAgent.Name"/> on the
    /// happy path; <see langword="null"/> if the agent is unknown / deleted or
    /// the upstream call throws. Cancellation rethrows per ASP.NET Core
    /// convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single-row lookup, NOT a batch.</b> Spec-locked decision #1 — the
    /// caller's <see cref="GetAsync"/> projects ONE record (<c>runs[0]</c>),
    /// so the GroupBy + First dictionary-build pattern from
    /// <c>AgentFeedbackReadController.ResolveDisplayNamesAsync</c>
    /// (DRIFT-4.5-impl-2) doesn't apply. Try/catch + OCE-rethrow + Warning-log
    /// shape is the verbatim adaptation though.
    /// </para>
    /// <para>
    /// <b>NFR-R1 graceful degradation:</b> any non-cancellation throw is
    /// caught, logged at Warning, and surfaces as <see langword="null"/> so
    /// the widget renders the pre-Story-4.8 fallback ("Agent {agentId-first-8}").
    /// Null returns from <see cref="IAIAgentService.GetAgentAsync"/> (agent
    /// deleted between run-time audit-logging and read-time modal opening) are
    /// also passed through as <see langword="null"/> without logging — a
    /// legitimate v0.2 multi-week-adopter signal, not an error.
    /// </para>
    /// <para>
    /// <b>Input/output normalisation:</b> <see cref="Guid.Empty"/> short-circuits
    /// to <see langword="null"/> without calling upstream (defensive against
    /// audit-log records with an unresolved AgentId — avoids per-modal-open log
    /// noise from upstream throws on empty Guid). Whitespace-only / empty
    /// <see cref="AIAgent.Name"/> values are normalised to <see langword="null"/>
    /// so the widget falls through to its GUID-prefix fallback rather than
    /// rendering a blank attribution line.
    /// </para>
    /// </remarks>
    private async Task<string?> ResolveAgentDisplayNameAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (agentId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var agent = await _agentService.GetAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
            var name = agent?.Name?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentRunReadController.ResolveAgentDisplayNameAsync — IAIAgentService.GetAgentAsync threw for AgentId={AgentId}. " +
                "Row falls back to null display name (widget renders 'Agent <agentId-first-8>'). NFR-R1 graceful degradation.",
                agentId);
            return null;
        }
    }

    /// <summary>
    /// Defensive parse of the agent's structured-output response from the
    /// upstream <see cref="AgentRunRecord.ResponseSnapshotJoined"/>. Returns
    /// <c>(null, empty, empty)</c> on null/empty/malformed input (NFR-R1
    /// graceful degradation — the widget renders an "Agent output unavailable"
    /// notice but the feedback form remains usable).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ResponseSnapshotJoined is NOT raw JSON</b> — it's a multi-line
    /// transcript that embeds the structured output after the final
    /// <c>[assistant]</c> marker, alongside <c>[tool_call:...]</c> and
    /// <c>[tool:...]</c> rounds from any tool-using turns. Example shape:
    /// <code>
    ///   [tool_call:toolu_01KThSMy...] list_context_resources({"args":{}})
    /// [tool:toolu_01KThSMy...] -> {"resources":[],"message":"..."}
    /// [assistant] {"score":7,"issues":[{"text":"...","reason":"..."}],"suggestions":[...]}
    /// </code>
    /// Captured as DRIFT-4.2-impl-2 at the manual gate dry-run 2026-05-19 —
    /// initial spec assumed raw-JSON shape; empirical truth is transcript-with-
    /// embedded-JSON. The parser locates the LAST <c>[assistant]</c> marker so
    /// it picks the final-turn structured output even after intermediate
    /// tool-call rounds.
    /// </para>
    /// <para>
    /// Expected JSON payload shape (Brand Voice Auditor agent schema per Story
    /// 4.1 AC3):
    /// <code>
    /// {
    ///   "score": &lt;integer 1-10&gt;,
    ///   "issues": [{ "text": "...", "reason": "..." }],
    ///   "suggestions": ["...", "..."]
    /// }
    /// </code>
    /// Adopter agents with different schemas degrade gracefully — missing
    /// fields yield null/empty defaults; unparseable JSON yields the same.
    /// </para>
    /// </remarks>
    private const string AssistantTag = "[assistant]";
    private const char JsonObjectStart = '{';

    private static (int? score, IReadOnlyList<AgentRunDetailIssue> issues, IReadOnlyList<string> suggestions)
        TryParseStructuredOutput(string? responseSnapshotJoined)
    {
        if (string.IsNullOrWhiteSpace(responseSnapshotJoined))
        {
            return (null, Array.Empty<AgentRunDetailIssue>(), Array.Empty<string>());
        }

        // DRIFT-4.2-impl-2: locate the final transcript line whose assistant
        // marker is followed by optional whitespace and a JSON object. Tool-using
        // turns interleave [tool_call:...] and [tool:...] lines before the final
        // [assistant] line; we want the structured output from the final turn
        // only. Matching the marker only at a line boundary avoids false matches
        // inside JSON string values; accepting whitespace/newline before the
        // opening brace handles valid transcript formatting variants.
        var jsonStart = FindAssistantJsonStart(responseSnapshotJoined);
        var jsonPayload = responseSnapshotJoined.Substring(jsonStart).TrimStart();

        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return (null, Array.Empty<AgentRunDetailIssue>(), Array.Empty<string>());
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, Array.Empty<AgentRunDetailIssue>(), Array.Empty<string>());
            }

            int? score = null;
            if (root.TryGetProperty("score", out var scoreEl)
                && scoreEl.ValueKind == JsonValueKind.Number)
            {
                // Some LLMs emit integer-valued scores with a `.0` decimal
                // suffix (Anthropic / OpenAI under certain decoding paths)
                // which `TryGetInt32` rejects. Fall back to a double parse
                // and round so the widget surfaces the score regardless of
                // emission shape — Story 4.2 § Review Findings patch #3.
                if (scoreEl.TryGetInt32(out var s))
                {
                    score = s;
                }
                else if (scoreEl.TryGetDouble(out var d))
                {
                    score = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                }

                if (score is < 1 or > 10)
                {
                    score = null;
                }
            }

            var issues = ParseIssues(root);
            var suggestions = ParseSuggestions(root);

            return (score, issues, suggestions);
        }
        catch (JsonException)
        {
            // Malformed JSON — surface the run identity + empty structured
            // fields. The widget treats this as "Agent output unavailable".
            return (null, Array.Empty<AgentRunDetailIssue>(), Array.Empty<string>());
        }
    }

    private static int FindAssistantJsonStart(string responseSnapshotJoined)
    {
        var searchStart = responseSnapshotJoined.Length - 1;
        while (searchStart >= 0)
        {
            var assistantIdx = responseSnapshotJoined.LastIndexOf(
                AssistantTag,
                searchStart,
                StringComparison.Ordinal);
            if (assistantIdx < 0)
            {
                break;
            }

            if (IsTranscriptLineBoundary(responseSnapshotJoined, assistantIdx))
            {
                var scan = assistantIdx + AssistantTag.Length;
                while (scan < responseSnapshotJoined.Length
                       && char.IsWhiteSpace(responseSnapshotJoined[scan]))
                {
                    scan++;
                }

                if (scan < responseSnapshotJoined.Length
                    && responseSnapshotJoined[scan] == JsonObjectStart)
                {
                    return scan;
                }
            }

            searchStart = assistantIdx - 1;
        }

        // Fall back to whole-document parse if no transcript marker is found.
        // This preserves the future-proof seam for non-transcript
        // ResponseSnapshot shapes adopter agents might produce.
        return 0;
    }

    private static bool IsTranscriptLineBoundary(string value, int markerIndex)
    {
        for (var i = markerIndex - 1; i >= 0; i--)
        {
            var c = value[i];
            if (c is '\n' or '\r')
            {
                return true;
            }
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<AgentRunDetailIssue> ParseIssues(JsonElement root)
    {
        if (!root.TryGetProperty("issues", out var issuesEl)
            || issuesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentRunDetailIssue>();
        }

        // Cap pre-allocation capacity so an attacker-controlled
        // `GetArrayLength()` (e.g., a malicious / runaway agent emitting a
        // 100k-element array) can't drive an OOM via runaway capacity hint.
        // Iteration also bails at the cap. Story 4.2 § Review Findings patch #4.
        var capacity = Math.Min(issuesEl.GetArrayLength(), MaxStructuredOutputItems);
        var list = new List<AgentRunDetailIssue>(capacity);
        foreach (var item in issuesEl.EnumerateArray())
        {
            if (list.Count >= MaxStructuredOutputItems)
            {
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var text = item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString()
                : null;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : null;
            list.Add(new AgentRunDetailIssue(text, reason));
        }
        return list;
    }

    private static IReadOnlyList<string> ParseSuggestions(JsonElement root)
    {
        if (!root.TryGetProperty("suggestions", out var suggestionsEl)
            || suggestionsEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        // Capacity cap mirrors `ParseIssues` per Story 4.2 § Review Findings
        // patch #4. Iteration also bails at the cap.
        var capacity = Math.Min(suggestionsEl.GetArrayLength(), MaxStructuredOutputItems);
        var list = new List<string>(capacity);
        foreach (var item in suggestionsEl.EnumerateArray())
        {
            if (list.Count >= MaxStructuredOutputItems)
            {
                break;
            }
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Parses the Story 3.3 memory-injection block out of the upstream
    /// <see cref="Runs.AgentRunRecord.PromptSnapshotJoined"/>. Returns
    /// <c>(false, [])</c> when the anchor doesn't match (Run 1 baseline; common
    /// case) and <c>(true, [...])</c> when one or more bullet lines parse
    /// successfully. Story 4.5 Q2a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anchor:</b> the literal <c>"[system] Lessons from past runs:\n"</c>
    /// at the start of <paramref name="promptSnapshotJoined"/> (Story 3.3
    /// outer-wrapping invariant — the upstream chat composer prepends
    /// <c>"[system] "</c> to the system-role message body).
    /// </para>
    /// <para>
    /// <b>Bullet shape:</b>
    /// <code>• Run {first8} {emoji}: {summary} — "{comment}"</code>
    /// where <c>{first8}</c> is the memory's RunId truncated to its first 8
    /// chars (or shorter if the RunId itself is shorter — defensive Math.Min
    /// per BuildMemorySystemMessage line 237); <c>{emoji}</c> is one of
    /// <c>👍 👎 •</c>; the <c> — "{comment}"</c> suffix is OMITTED when the
    /// memory had no editor comment (BuildMemorySystemMessage line 228-230).
    /// </para>
    /// <para>
    /// <b>Truncation:</b> each parsed comment is truncated at
    /// <see cref="CommentSnippetMaxChars"/> chars with <c>"…"</c> appended.
    /// </para>
    /// <para>
    /// <b>Cap:</b> the bullet count is capped at <see cref="MaxCitedMemories"/>
    /// — defensive against runaway-emission shapes.
    /// </para>
    /// <para>
    /// <b>Drift detection:</b> if the anchor matches but no bullets parse (i.e.
    /// Story 3.3 format drift), the method emits a Warning log and returns
    /// <c>(false, [])</c> — graceful degradation per NFR-R1.
    /// </para>
    /// </remarks>
    private (bool memoryUsed, IReadOnlyList<AgentRunCitedMemory> citedMemories)
        ParseMemoryInjection(string? promptSnapshotJoined)
    {
        if (string.IsNullOrWhiteSpace(promptSnapshotJoined))
        {
            _logger.LogDebug(
                "AgentRunReadController.ParseMemoryInjection — empty PromptSnapshotJoined; no memory-injection block.");
            return (false, Array.Empty<AgentRunCitedMemory>());
        }

        if (!promptSnapshotJoined.StartsWith(MemoryInjectionAnchor, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "AgentRunReadController.ParseMemoryInjection — PromptSnapshotJoined does not start with memory-injection anchor; treating as Run-1 baseline (no injection).");
            return (false, Array.Empty<AgentRunCitedMemory>());
        }

        // Body region starts immediately after the anchor and runs through to
        // the first occurrence of a non-bullet line (typically a blank line
        // before "[user] ...") or end-of-string.
        var bodyStart = MemoryInjectionAnchor.Length;
        var list = new List<AgentRunCitedMemory>(MaxCitedMemories);

        var cursor = bodyStart;
        while (cursor < promptSnapshotJoined.Length && list.Count < MaxCitedMemories)
        {
            // Find next newline (or end of string).
            var newlineIdx = promptSnapshotJoined.IndexOf('\n', cursor);
            var lineEnd = newlineIdx < 0 ? promptSnapshotJoined.Length : newlineIdx;
            var line = promptSnapshotJoined.AsSpan(cursor, lineEnd - cursor);

            // Stop at the first non-bullet line — the injection block has
            // ended (typically the next system/user/assistant message).
            if (!line.StartsWith("• Run ", StringComparison.Ordinal))
            {
                break;
            }

            var parsed = TryParseBulletLine(line);
            if (parsed is not null)
            {
                list.Add(parsed);
            }

            // Advance past newline (or to end).
            cursor = newlineIdx < 0 ? promptSnapshotJoined.Length : newlineIdx + 1;
        }

        if (list.Count == 0)
        {
            _logger.LogWarning(
                "AgentRunReadController.ParseMemoryInjection — anchor matched but zero bullets parsed; Story 3.3 PromptSnapshot format drift suspected. PromptSnapshotJoined prefix={Prefix}",
                promptSnapshotJoined.Length > 200 ? promptSnapshotJoined[..200] + "…" : promptSnapshotJoined);
            return (false, Array.Empty<AgentRunCitedMemory>());
        }

        return (true, list);
    }

    /// <summary>
    /// Parses a single bullet line. Returns <see langword="null"/> on
    /// malformed shape (caller decides drift-detection semantics). Bullet
    /// shape: <c>"• Run {first8} {emoji}: {summary} — \"{comment}\""</c>.
    /// </summary>
    private static AgentRunCitedMemory? TryParseBulletLine(ReadOnlySpan<char> line)
    {
        // "• Run " prefix already established by caller.
        var afterPrefix = line["• Run ".Length..];

        // {first8} runs until next space.
        var spaceIdx = afterPrefix.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            return null;
        }
        var runIdPrefix = afterPrefix[..spaceIdx].ToString();

        var afterRunId = afterPrefix[(spaceIdx + 1)..];

        // {emoji} runs until ": " separator. BuildMemorySystemMessage emits
        // the emoji as a single codepoint glyph (👍 / 👎 / •) but in UTF-16
        // these are surrogate pairs (2 chars) for 👍 / 👎 and 1 char for •.
        // We grab everything up to ": " conservatively.
        var colonSepIdx = afterRunId.IndexOf(": ", StringComparison.Ordinal);
        if (colonSepIdx <= 0)
        {
            return null;
        }
        var emoji = afterRunId[..colonSepIdx].ToString();

        var afterEmoji = afterRunId[(colonSepIdx + 2)..];

        // Comment suffix is optional. Format: ` — "{comment}"` (em-dash, NOT
        // ASCII hyphen). When absent, the whole remainder is the summary.
        // Locate the suffix by searching for ` — "` (space + em-dash + space +
        // double-quote). The summary itself can contain the em-dash on its own,
        // so the trailing double-quote at end-of-line is the anchor for "the
        // suffix is present and well-formed".
        string? commentSnippet = null;
        const string CommentSuffixOpen = " — \"";
        if (afterEmoji.Length > 0
            && afterEmoji[^1] == '"'
            && afterEmoji.LastIndexOf(CommentSuffixOpen, StringComparison.Ordinal) is int openIdx
            && openIdx >= 0)
        {
            var commentSpan = afterEmoji[(openIdx + CommentSuffixOpen.Length)..^1];
            var commentStr = commentSpan.ToString();
            commentSnippet = commentStr.Length > CommentSnippetMaxChars
                ? commentStr[..CommentSnippetMaxChars] + "…"
                : commentStr;
        }

        return new AgentRunCitedMemory(runIdPrefix, emoji, commentSnippet);
    }
}
