using Asp.Versioning;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Runs;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api;

/// <summary>
/// Story 2.2 — Backoffice Management API for recording editor feedback against
/// agent runs. Routes to
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/feedback</c> via the
/// canonical Umbraco 17.3.2 pattern:
/// <c>[VersionedApiBackOfficeRoute("cogworks-agent-memory/feedback")]</c> from
/// <c>Umbraco.Cms.Api.Management.Routing</c> — the framework prepends
/// <c>/umbraco/management/api/v{version}/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth pattern:</b> the Management API enforces bearer-token auth via
/// OpenIddict, NOT cookies. Cookie-only
/// <c>fetch(..., { credentials: "include" })</c> calls return HTTP 401. The
/// Story 2.3 widget MUST use <c>UMB_AUTH_CONTEXT.getOpenApiConfiguration()</c>
/// to obtain a bearer token per call.
/// </para>
/// <para>
/// <b>Authorization:</b> the endpoint is gated by
/// <see cref="AuthorizationPolicies.BackOfficeAccess"/> — any authenticated
/// backoffice user. Section-specific policies are intentionally NOT used in
/// v0.1 because the widget's mount surface is still in flight at Story 2.3.
/// </para>
/// <para>
/// <b>Swagger 500 collision avoidance:</b> the framework filter
/// <c>BackOfficeSecurityRequirementsOperationFilterBase</c> (subclassed by
/// <c>AgentMemoryBackofficeApiComposer</c>) auto-adds 401 + 403 response
/// schemas. This controller deliberately omits manual
/// <c>[ProducesResponseType(401)]</c> / <c>[ProducesResponseType(403)]</c>
/// attributes to prevent dual-schema generation crashes at boot.
/// </para>
/// <para>
/// <b>CreatedBy provenance:</b> the persisted <c>CreatedBy</c> column carries
/// the authenticated backoffice user GUID resolved server-side via
/// <see cref="IBackOfficeSecurityAccessor"/>; the request body does NOT carry
/// a <c>createdBy</c> field. A client cannot impersonate another user.
/// </para>
/// <para>
/// <b>Agent identity (<c>AgentId</c>) is also resolved server-side</b> via
/// <see cref="IAgentRunReader.GetRunsForThreadAsync"/> using the supplied
/// <see cref="AgentFeedbackPostRequest.RunId"/> (= upstream
/// <c>Metadata["Umbraco.AI.Agent.ThreadId"]</c> — the workflow-run-level
/// conversation identifier surfaced by the editor's modal). Empirical Story
/// 2.3 Task 0 spike disproved the original 4-field contract premise that the
/// widget's mount surface could supply an agent id; the controller now
/// derives it. Returns 404 when no audit-log rows match the supplied run id
/// (e.g. the audit-log row hasn't been written yet, OR the upstream Fork (i)
/// metadata propagation isn't deployed). v0.1 single-agent attribution
/// assumption: when a workflow has multiple <c>RunAgentAction</c> steps with
/// different agents, the first matched record's <c>AgentId</c> is used —
/// Brand Voice Audit demo is single-agent so the assumption holds. Multi-
/// agent disambiguation is a v0.2 candidate.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("cogworks-agent-memory/feedback")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[MapToApi(Constants.ApiName)]
public sealed class AgentFeedbackController : ManagementApiControllerBase
{
    /// <summary>
    /// Server-side cap on <see cref="AgentFeedbackPostRequest.Comment"/>.
    /// Matches the widget's client-side max-length (Story 2.3) for parity;
    /// 4000 chars balances editor expressiveness with payload hygiene.
    /// </summary>
    internal const int CommentMaxChars = 4000;

    /// <summary>
    /// Server-side cap on <see cref="AgentFeedbackPostRequest.RunId"/>. Matches
    /// the schema column's <c>HasMaxLength(256)</c> from Story 1.1.
    /// </summary>
    internal const int RunIdMaxChars = 256;

    private readonly IAgentFeedbackService _feedbackService;
    private readonly IBackOfficeSecurityAccessor _securityAccessor;
    private readonly IAgentRunReader _runReader;
    private readonly ILogger<AgentFeedbackController> _logger;

    public AgentFeedbackController(
        IAgentFeedbackService feedbackService,
        IBackOfficeSecurityAccessor securityAccessor,
        IAgentRunReader runReader,
        ILogger<AgentFeedbackController> logger)
    {
        ArgumentNullException.ThrowIfNull(feedbackService);
        ArgumentNullException.ThrowIfNull(securityAccessor);
        ArgumentNullException.ThrowIfNull(runReader);
        ArgumentNullException.ThrowIfNull(logger);
        _feedbackService = feedbackService;
        _securityAccessor = securityAccessor;
        _runReader = runReader;
        _logger = logger;
    }

    /// <summary>
    /// Records (or supersedes) a single feedback signal against an agent run.
    /// Idempotent on <c>(runId, createdBy)</c> at the service layer (Story 2.1).
    /// </summary>
    [HttpPost("")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostAsync(
        [FromBody] AgentFeedbackPostRequest? request,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePostRequest(request);
        if (validation.IsInvalid)
        {
            return Problem(
                title: "Invalid agent feedback payload",
                detail: validation.Detail,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Resolve the authenticated host user server-side. The [Authorize]
        // filter rejects unauthenticated requests at 401 before reaching this
        // method; the defense-in-depth null/empty guard catches config drift.
        var resolvedKey = _securityAccessor.BackOfficeSecurity?.CurrentUser?.Key;
        if (resolvedKey is null || resolvedKey.Value == Guid.Empty)
        {
            _logger.LogWarning(
                "AgentFeedbackController.PostAsync — authenticated host user "
                + "identity could not be resolved despite passing the BackOfficeAccess "
                + "authorization filter. Returning 401; check Management-API auth "
                + "configuration for drift.");
            return Problem(
                title: "Authenticated host user identity could not be resolved.",
                detail: null,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Story 2.3 Task 0.6 — modal hands us a thread-level run identifier, not an
        // agent id. Resolve agentId server-side so the widget contract stays minimal
        // (3 fields, no agent guessing on the client).
        var runs = await _runReader.GetRunsForThreadAsync(request!.RunId, cancellationToken).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            _logger.LogWarning(
                "AgentFeedbackController.PostAsync — IAgentRunReader.GetRunsForThreadAsync returned zero records for RunId={RunId}. Returning 404; the audit-log row may not yet be written, or upstream Metadata propagation (PR-Upstream-N / Fork (i)) is not deployed.",
                request.RunId);
            return Problem(
                title: "Run not found.",
                detail: $"No agent runs found for the supplied runId ('{request.RunId}'). The run may not yet be audit-logged, or the upstream Metadata propagation (PR-Upstream-N / Fork (i)) is not deployed on this host.",
                statusCode: StatusCodes.Status404NotFound);
        }
        // v0.1 single-agent attribution assumption — Brand Voice Audit demo's
        // workflow is single-agent (one Run AI Agent step). Multi-agent workflows
        // surface a v0.2 disambiguation requirement (Story 5.x candidate).
        var agentId = runs[0].AgentId;

        await _feedbackService.RecordFeedbackAsync(
            request.RunId,
            agentId,
            request.Score,
            request.Comment,
            resolvedKey.Value,
            cancellationToken).ConfigureAwait(false);

        return Ok();
    }

    private static (bool IsInvalid, string? Detail) ValidatePostRequest(AgentFeedbackPostRequest? request)
    {
        if (request is null)
        {
            return (true, "Request body is required.");
        }
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            return (true, "runId is required and cannot be empty.");
        }
        if (request.RunId.Length > RunIdMaxChars)
        {
            return (true, $"runId cannot exceed {RunIdMaxChars} characters (received {request.RunId.Length}).");
        }
        if (!Enum.IsDefined<FeedbackScore>(request.Score))
        {
            return (true, $"score must be 0 (ThumbsUp), 1 (ThumbsDown), or 2 (Neutral); received {(int)request.Score}.");
        }
        if (request.Comment is { Length: > CommentMaxChars })
        {
            return (true, $"comment cannot exceed {CommentMaxChars} characters (received {request.Comment.Length}).");
        }
        return (false, null);
    }
}
