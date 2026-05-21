using Asp.Versioning;
using Cogworks.UmbracoAI.AgentMemory.Feedback;
using Cogworks.UmbracoAI.AgentMemory.Persistence.Repositories;
using Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.AI.Agent.Core.Agents;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api;

/// <summary>
/// Story 4.9 — Backoffice Management API for the Memory Learning Wall
/// dashboard. Returns a flat list of every memory entry the package has
/// learned (across all agents); the wall widget groups client-side by
/// <c>AgentId</c> per architect direction 2026-05-20.
/// </summary>
/// <remarks>
/// <para>
/// Routes to
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/memory-entries</c> via
/// the canonical Umbraco 17.3.2 pattern (same shape as
/// <see cref="AgentRunReadController"/> /
/// <see cref="AgentFeedbackReadController"/>). Composes on
/// <see cref="IMemoryEntryRepository"/> for the row read,
/// <see cref="IAgentFeedbackService"/> for the per-row latest-feedback
/// hydration, <see cref="IUserService"/> for the editor display-name batch
/// (Story 4.5 DRIFT-4.5-impl-2 pattern), and <see cref="IAIAgentService"/>
/// for the agent display-name batch (Story 4.8 single-row pattern lifted to
/// batch).
/// </para>
/// <para>
/// <b>Auth + Swagger:</b> identical pattern to the sibling read controllers
/// — <see cref="AuthorizationPolicies.BackOfficeAccess"/> policy, bearer
/// token via OpenIddict, no manual 401/403 <c>ProducesResponseType</c> (the
/// <c>BackOfficeSecurityRequirementsOperationFilterBase</c> filter
/// subclassed by <c>AgentMemoryBackofficeApiComposer</c> auto-adds those).
/// This controller is registered in
/// <c>AgentMemoryOperationIdHandler.AllowedControllers</c> — missing the
/// allow-list entry causes a Swagger duplicate-operation-id boot crash.
/// </para>
/// <para>
/// <b>Empty-array (NOT 404) on zero entries:</b> a fresh adopter install
/// with no memories yet returns 200 OK with <c>Entries: []</c>. NEVER 404.
/// The wall widget renders the empty-state copy.
/// </para>
/// <para>
/// <b>NFR-R3 graceful degradation:</b> any of the three downstream display-
/// name / hydration calls throwing causes per-row null fallback — the
/// endpoint always returns 200 OK with whatever it could project. See AC5.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("cogworks-agent-memory/memory-entries")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[MapToApi(Constants.ApiName)]
public sealed class MemoryEntriesReadController : ManagementApiControllerBase
{
    /// <summary>
    /// Server-side default for the <c>take</c> query param when the caller
    /// omits it. Matches the upstream <see cref="IMemoryEntryRepository"/>
    /// clamp ceiling (100); v0.1 demo scale is well under this.
    /// </summary>
    internal const int DefaultTake = 100;

    /// <summary>
    /// Hard upper bound enforced at the controller edge (defence in depth —
    /// the repo also clamps). Matches <see cref="DefaultTake"/>.
    /// </summary>
    internal const int MaxTake = 100;

    private readonly IMemoryEntryRepository _repository;
    private readonly IAgentFeedbackService _feedbackService;
    private readonly IUserService _userService;
    private readonly IAIAgentService _agentService;
    private readonly ILogger<MemoryEntriesReadController> _logger;

    public MemoryEntriesReadController(
        IMemoryEntryRepository repository,
        IAgentFeedbackService feedbackService,
        IUserService userService,
        IAIAgentService agentService,
        ILogger<MemoryEntriesReadController> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(feedbackService);
        ArgumentNullException.ThrowIfNull(userService);
        ArgumentNullException.ThrowIfNull(agentService);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _feedbackService = feedbackService;
        _userService = userService;
        _agentService = agentService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the flat list of memory entries (across all agents), each
    /// projected with row identity + latest-feedback hydration + agent +
    /// editor display names. Used by the Memory Learning Wall dashboard.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MemoryWallListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        // Controller-edge clamp — defence in depth. The repo also clamps to
        // [0, 100], but clamping here means the bytes over the wire never
        // claim `take=10000` and the adopter's expectation aligns with
        // `?take=N` reading directly.
        var requested = take ?? DefaultTake;
        var effectiveTake = requested <= 0 ? 0 : requested > MaxTake ? MaxTake : requested;

        var entries = await _repository
            .GetRecentAcrossAgentsAsync(effectiveTake, cancellationToken)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return Ok(new MemoryWallListResponse(Array.Empty<MemoryWallEntry>()));
        }

        // Resolve agent display names in ONE round-trip — single GetAgentsAsync
        // call + dictionary projection. Story 4.8's per-call shape is the
        // single-row variant; the wall is N-row so we batch.
        // Filter Guid.Empty (legacy/corrupt row defence) — mirrors Story 4.8's
        // single-row short-circuit at AgentRunReadController.ResolveAgentDisplayNameAsync.
        var distinctAgentIds = entries
            .Select(e => e.AgentId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var agentDisplayNameByKey = await ResolveAgentDisplayNamesAsync(
            distinctAgentIds, cancellationToken).ConfigureAwait(false);

        // Sequential per-row feedback hydration — see Dev Notes "Why
        // sequential, not parallel Task.WhenAll": v0.1 demo scale is <50
        // entries; sequential is trivially within p95 budget and avoids the
        // EFCoreScope concurrency surface area documented at deferred-work.md
        // line 372 (the wall doesn't touch the vector store but the
        // belt-and-braces approach keeps the wall path off any unverified
        // concurrent-await complexity).
        var hydrated = new List<(MemoryEntryEntityProjection Row, AgentRunFeedback? Latest)>(entries.Count);
        foreach (var entry in entries)
        {
            AgentRunFeedback? latest = null;
            try
            {
                var feedback = await _feedbackService
                    .GetFeedbackForRunAsync(entry.RunId, cancellationToken)
                    .ConfigureAwait(false);
                latest = feedback.Count > 0 ? feedback[0] : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MemoryEntriesReadController.GetAsync — IAgentFeedbackService.GetFeedbackForRunAsync threw for RunId={RunId}. " +
                    "Row falls back to null Score/FeedbackComment/CreatedBy. NFR-R3 graceful degradation.",
                    entry.RunId);
                latest = null;
            }

            hydrated.Add((
                new MemoryEntryEntityProjection(
                    entry.RunId,
                    entry.AgentId,
                    entry.DigestText,
                    entry.CreatedUtc),
                latest));
        }

        // Resolve user display names ONCE across distinct CreatedBy GUIDs.
        // Filter Guid.Empty (corrupt feedback row defence) — IUserService.GetAsync
        // may throw on empty Guid, which would collapse ALL rows' display names
        // via the NFR-R3 catch.
        var distinctCreatedByKeys = hydrated
            .Where(h => h.Latest is not null && h.Latest.CreatedBy != Guid.Empty)
            .Select(h => h.Latest!.CreatedBy)
            .Distinct()
            .ToList();
        var userDisplayNameByKey = await ResolveUserDisplayNamesAsync(
            distinctCreatedByKeys, cancellationToken).ConfigureAwait(false);

        // Project per-row to the wire DTO.
        var projected = hydrated.Select(h => new MemoryWallEntry(
            RunId: h.Row.RunId,
            AgentId: h.Row.AgentId,
            AgentDisplayName: agentDisplayNameByKey.TryGetValue(h.Row.AgentId, out var aName) ? aName : null,
            DigestText: h.Row.DigestText,
            Score: h.Latest?.Score,
            FeedbackComment: h.Latest?.Comment,
            CreatedBy: h.Latest?.CreatedBy,
            CreatedByDisplayName: h.Latest is not null
                && userDisplayNameByKey.TryGetValue(h.Latest.CreatedBy, out var uName)
                ? uName
                : null,
            CreatedUtc: h.Row.CreatedUtc)).ToArray();

        return Ok(new MemoryWallListResponse(projected));
    }

    /// <summary>
    /// Story 4.9 — batch agent-display-name resolver. Lifts Story 4.8's
    /// single-row <c>ResolveAgentDisplayNameAsync</c> pattern to a batch
    /// shape: ONE call to <see cref="IAIAgentService.GetAgentsAsync"/>
    /// (returns ALL agents — cheap for v0.1 adopter scale of &lt;50 agents)
    /// + dictionary projection filtered to the supplied
    /// <paramref name="distinctAgentIds"/>. Whitespace-to-null normalisation
    /// + <c>GroupBy + First</c> duplicate-key defence carry forward from
    /// Story 4.8 LD + Story 4.5 review-patch #3.
    /// </summary>
    /// <remarks>
    /// NFR-R3 graceful degradation: any non-cancellation throw is caught,
    /// logged at Warning, and the caller receives an empty dictionary so
    /// every row falls through to <see langword="null"/> AgentDisplayName
    /// (widget renders <c>"Agent {agentId-first-8}"</c>).
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, string?>> ResolveAgentDisplayNamesAsync(
        IReadOnlyList<Guid> distinctAgentIds,
        CancellationToken cancellationToken)
    {
        if (distinctAgentIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        try
        {
            var agents = (await _agentService
                .GetAgentsAsync(cancellationToken)
                .ConfigureAwait(false)).ToList();

            var requested = new HashSet<Guid>(distinctAgentIds);
            return agents
                .Where(a => requested.Contains(a.Id))
                .GroupBy(a => a.Id)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var name = g.First().Name?.Trim();
                        return string.IsNullOrEmpty(name) ? null : (string?)name;
                    });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MemoryEntriesReadController.ResolveAgentDisplayNamesAsync — IAIAgentService.GetAgentsAsync threw for {KeyCount} keys. " +
                "All rows fall back to null AgentDisplayName (widget renders 'Agent <agentId-first-8>'). NFR-R3 graceful degradation.",
                distinctAgentIds.Count);
            return new Dictionary<Guid, string?>();
        }
    }

    /// <summary>
    /// Story 4.9 — batch user-display-name resolver. Verbatim shape mirror
    /// of <c>AgentFeedbackReadController.ResolveDisplayNamesAsync</c>
    /// (DRIFT-4.5-impl-2 lineage). Duplicate the shape rather than calling
    /// into the other controller's instance — rule-of-three forward-pointed
    /// to v0.2 (see Dev Notes "Why no shared IDisplayNameResolver helper").
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string?>> ResolveUserDisplayNamesAsync(
        IReadOnlyList<Guid> distinctCreatedByKeys,
        CancellationToken cancellationToken)
    {
        if (distinctCreatedByKeys.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        try
        {
            var users = await _userService.GetAsync(distinctCreatedByKeys).ConfigureAwait(false);
            return users
                .GroupBy(u => u.Key)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        // Whitespace-to-null normalisation mirrors Story 4.8's
                        // single-row ResolveAgentDisplayNameAsync — without this,
                        // an IUser.Name of "   " surfaces three blank spaces to
                        // the widget instead of falling through to the
                        // "An editor" fallback.
                        var name = g.First().Name?.Trim();
                        return string.IsNullOrEmpty(name) ? null : (string?)name;
                    });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MemoryEntriesReadController.ResolveUserDisplayNamesAsync — IUserService.GetAsync threw for {KeyCount} keys. " +
                "All rows fall back to null CreatedByDisplayName (widget renders 'An editor'). NFR-R3 graceful degradation.",
                distinctCreatedByKeys.Count);
            return new Dictionary<Guid, string?>();
        }
    }

    /// <summary>
    /// Internal projection of <c>MemoryEntryEntity</c> fields we surface to
    /// the wire — narrowed to the read-only subset the wall needs. Keeps the
    /// per-row hydration loop's tuple shape readable without exposing
    /// EF-internal fields (<c>IndexingStatus</c>, <c>EmbeddingRef</c>,
    /// <c>WorkspaceId</c>, etc.) to the projection logic.
    /// </summary>
    private sealed record MemoryEntryEntityProjection(
        string RunId,
        Guid AgentId,
        string DigestText,
        DateTime CreatedUtc);
}
