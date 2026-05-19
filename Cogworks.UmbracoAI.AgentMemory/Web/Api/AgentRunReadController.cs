using System.Text.Json;
using Asp.Versioning;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api;

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
/// 404 response copy is verbatim from <see cref="AgentFeedbackController"/>'s
/// equivalent branch for adopter-error-message parity.
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

    private readonly IAgentRunReader _runReader;
    private readonly ILogger<AgentRunReadController> _logger;

    public AgentRunReadController(
        IAgentRunReader runReader,
        ILogger<AgentRunReadController> logger)
    {
        ArgumentNullException.ThrowIfNull(runReader);
        ArgumentNullException.ThrowIfNull(logger);
        _runReader = runReader;
        _logger = logger;
    }

    /// <summary>
    /// Returns the run's identity + parsed structured output (score, flagged
    /// issues, suggestions). Used by the editor feedback widget to render
    /// agent-output context above the feedback form.
    /// </summary>
    [HttpGet("{runId}")]
    [ProducesResponseType<AgentRunDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        if (runId is null || runId.Length == 0)
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

        // v0.1 single-agent attribution assumption — Brand Voice Audit demo's
        // workflow is single-agent (one Run AI Agent step). Mirrors
        // AgentFeedbackController.PostAsync line 172. Multi-agent
        // disambiguation is a v0.2 candidate.
        var run = runs[0];

        var (score, issues, suggestions) = TryParseStructuredOutput(run.ResponseSnapshotJoined);

        var response = new AgentRunDetailResponse(
            RunId: runId,
            AgentId: run.AgentId,
            // v0.1 — agent display name + content node name not surfaced
            // cheaply by IAgentRunReader; widget falls back to "Agent {agentId}".
            AgentDisplayName: null,
            ContentNodeName: null,
            RanAtUtc: run.StartedUtc,
            Score: score,
            Issues: issues,
            Suggestions: suggestions);

        return Ok(response);
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
    private const string AssistantTagWithBrace = "[assistant] {";

    private static (int? score, IReadOnlyList<AgentRunDetailIssue> issues, IReadOnlyList<string> suggestions)
        TryParseStructuredOutput(string? responseSnapshotJoined)
    {
        if (string.IsNullOrWhiteSpace(responseSnapshotJoined))
        {
            return (null, Array.Empty<AgentRunDetailIssue>(), Array.Empty<string>());
        }

        // DRIFT-4.2-impl-2: locate the last [assistant] marker and parse the
        // JSON payload that follows. Tool-using turns interleave
        // [tool_call:...] and [tool:...] lines BEFORE the final [assistant]
        // line; we want the structured output from the final turn only.
        //
        // Code-review hardening (Story 4.2 § Review Findings patch #1): anchor
        // on the literal "[assistant] {" boundary (tag + space + opening
        // brace) rather than the bare tag. The bare-tag form was vulnerable
        // to false matches when the agent's own JSON string content quoted
        // "[assistant]" (e.g., the agent describing its role in an `issues`
        // entry) — `LastIndexOf` would land on the inner occurrence, leaving
        // `jsonStart` mid-string-literal and the parse would throw. The
        // tag-plus-brace form is the unique transcript-to-JSON boundary
        // shape; collisions inside JSON string values would require the
        // agent to literally emit "[assistant] {" inside a quoted string,
        // which is sufficiently improbable to be acceptable for v0.1.
        var assistantIdx = responseSnapshotJoined.LastIndexOf(AssistantTagWithBrace, StringComparison.Ordinal);
        var jsonStart = assistantIdx >= 0
            ? assistantIdx + AssistantTag.Length // advance past "[assistant]" but keep the "{" for the parser
            : 0; // fall back to whole-document parse if no marker (preserves
                 // future-proof seam for non-transcript ResponseSnapshot shapes
                 // adopter agents might produce).
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
                    score = (int)Math.Round(d);
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
}
