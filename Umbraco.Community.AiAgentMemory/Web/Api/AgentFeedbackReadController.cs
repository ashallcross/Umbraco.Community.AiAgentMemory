using Asp.Versioning;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api;

/// <summary>
/// Story 4.5 — Backoffice Management API for reading editor feedback rows
/// against an agent run. Composes on
/// <see cref="IAgentFeedbackService.GetFeedbackForRunAsync"/>; no service
/// change. Routes to
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/feedback/{runId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sibling endpoint:</b> the existing <see cref="AgentFeedbackController"/>
/// POST endpoint lives at the same route prefix (<c>feedback</c>) but a
/// different HTTP verb — ASP.NET routing disambiguates without cross-talk.
/// </para>
/// <para>
/// <b>Empty-array (not 404) contract:</b> when the service returns zero rows
/// (run had no feedback), this endpoint returns 200 OK with
/// <c>existing: []</c>. NEVER 404. The widget renders no Previous-feedback
/// block + opens the form fresh. Matches Story 2.1's
/// <c>GetFeedbackForRunAsync</c> empty-list contract per NFR-R3.
/// </para>
/// <para>
/// <b>Auth + Swagger:</b> identical pattern to
/// <see cref="AgentRunReadController"/> /
/// <see cref="AgentFeedbackController"/> —
/// <see cref="AuthorizationPolicies.BackOfficeAccess"/> policy, bearer token
/// via OpenIddict, no manual 401/403 <c>ProducesResponseType</c> (the
/// <c>BackOfficeSecurityRequirementsOperationFilterBase</c> filter subclassed
/// by <c>AgentMemoryBackofficeApiComposer</c> auto-adds those). This
/// controller is registered in
/// <c>AgentMemoryOperationIdHandler.AllowedControllers</c> — missing the
/// allow-list entry causes a Swagger duplicate-operation-id boot crash.
/// </para>
/// <para>
/// <b>Multi-agent disambiguation:</b> the endpoint returns ALL feedback rows
/// for the runId regardless of agent — one row per
/// <c>(RunId, CreatedBy)</c> per Story 2.1 supersede contract. Multi-agent
/// workflows (where a single run carries rows for multiple agents) surface a
/// v0.2 disambiguation requirement; tracked in <c>deferred-work.md</c>.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("cogworks-agent-memory/feedback")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[MapToApi(Constants.ApiName)]
public sealed class AgentFeedbackReadController : ManagementApiControllerBase
{
    /// <summary>
    /// Server-side cap on the <c>runId</c> route parameter. Mirrors
    /// <see cref="AgentFeedbackController.RunIdMaxChars"/> (256) for parity
    /// with the POST endpoint + <see cref="AgentRunReadController.RunIdMaxChars"/>
    /// — pre-empts resource-exhaustion vectors via oversized route segments.
    /// </summary>
    internal const int RunIdMaxChars = 256;

    private readonly IAgentFeedbackService _feedbackService;
    private readonly IUserService _userService;
    private readonly ILogger<AgentFeedbackReadController> _logger;

    public AgentFeedbackReadController(
        IAgentFeedbackService feedbackService,
        IUserService userService,
        ILogger<AgentFeedbackReadController> logger)
    {
        ArgumentNullException.ThrowIfNull(feedbackService);
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(logger);
        _feedbackService = feedbackService;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the editor feedback rows recorded against the supplied
    /// <paramref name="runId"/>. Used by the editor feedback widget to render
    /// the Previous-feedback block above the form so editors see their (and
    /// other editors') prior feedback + an Edit affordance for supersede.
    /// </summary>
    [HttpGet("{runId}")]
    [ProducesResponseType<AgentRunFeedbackListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAsync(
        string runId,
        CancellationToken cancellationToken)
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

        var rows = await _feedbackService
            .GetFeedbackForRunAsync(runId, cancellationToken)
            .ConfigureAwait(false);

        // 200 OK with empty `existing: []` array on zero rows per Story 4.5 Q1
        // contract — matches GetFeedbackForRunAsync empty-list NFR-R3. NEVER 404.
        if (rows.Count == 0)
        {
            return Ok(new AgentRunFeedbackListResponse(runId, Array.Empty<AgentRunFeedbackEntry>()));
        }

        // DRIFT-4.5-impl-2 (mid-gate fast-follow 2026-05-19) — batch-resolve
        // user display names via IUserService for the rows' distinct CreatedBy
        // GUIDs. Closes the Task 0e fallback ("CreatedByDisplayName=null,
        // widget shows 'An editor'") that we deferred for v0.1; the manual
        // gate at Step 7 surfaced an architectural confirmation that Umbraco's
        // upstream runtime already knows the user's name (visible in the
        // agent's PromptSnapshot under [system] ## Current User block),
        // making this a cheap one-call wire-up. NFR-R3 graceful degradation:
        // any throw → all rows fall back to null display name.
        IReadOnlyDictionary<Guid, string?> displayNameByKey = await ResolveDisplayNamesAsync(
            rows.Select(r => r.CreatedBy).Distinct().ToList(),
            cancellationToken).ConfigureAwait(false);

        var entries = rows.Select(r => MapToEntry(r, displayNameByKey)).ToArray();

        return Ok(new AgentRunFeedbackListResponse(runId, entries));
    }

    /// <summary>
    /// Batch-resolve user display names from <see cref="IUserService.GetAsync(System.Collections.Generic.IEnumerable{Guid})"/>.
    /// One round trip for all distinct CreatedBy GUIDs in the response — cheap
    /// (Umbraco caches user reads internally; typical row counts in v0.1 are
    /// 1-3 per run). Returns an empty dictionary on any exception so the
    /// caller's <see cref="MapToEntry"/> falls back to null display names
    /// uniformly — NFR-R3 graceful degradation.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string?>> ResolveDisplayNamesAsync(
        IReadOnlyList<Guid> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        try
        {
            var users = await _userService.GetAsync(keys).ConfigureAwait(false);
            // Defensive: GetAsync may return fewer users than supplied keys
            // (deleted users, never-existed GUIDs from manual SQL etc.). Build
            // a dictionary keyed by Key so missing rows fall back to null.
            // GroupBy + First defends against duplicate-Key returns from
            // stale-cache / merged-account corner cases — a straight
            // ToDictionary would throw ArgumentException and the surrounding
            // catch would misattribute the throw to GetAsync itself.
            return users
                .GroupBy(u => u.Key)
                .ToDictionary(g => g.Key, g => (string?)g.First().Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentFeedbackReadController.ResolveDisplayNamesAsync — IUserService.GetAsync threw for {KeyCount} keys. " +
                "All rows fall back to null display name (widget renders 'An editor'). NFR-R3 graceful degradation.",
                keys.Count);
            return new Dictionary<Guid, string?>();
        }
    }

    /// <summary>
    /// Project an <see cref="AgentRunFeedback"/> service-layer record into the
    /// wire DTO. <see cref="AgentRunFeedbackEntry.CreatedByDisplayName"/> is
    /// resolved from the supplied dictionary; falls back to <see langword="null"/>
    /// when the user GUID isn't in the dictionary (deleted user, lookup error,
    /// etc.) — the widget then renders "An editor".
    /// </summary>
    private static AgentRunFeedbackEntry MapToEntry(
        AgentRunFeedback row,
        IReadOnlyDictionary<Guid, string?> displayNameByKey) => new(
        Score: row.Score,
        Comment: row.Comment,
        CreatedBy: row.CreatedBy,
        CreatedByDisplayName: displayNameByKey.TryGetValue(row.CreatedBy, out var name) ? name : null,
        CreatedUtc: row.CreatedUtc);
}
