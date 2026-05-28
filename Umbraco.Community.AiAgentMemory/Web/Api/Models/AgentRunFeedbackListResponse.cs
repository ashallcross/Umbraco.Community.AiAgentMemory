using System.Text.Json.Serialization;
using Cogworks.UmbracoAI.AgentMemory.Feedback;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// Read-only projection of editor feedback rows for one agent run. Returned by
/// <c>GET /umbraco/management/api/v1/cogworks-agent-memory/feedback/{runId}</c>
/// to the editor feedback widget so the editor sees their own (and other
/// editors') prior thumbs-up/down + comments on this run, with an Edit
/// affordance that pre-populates the supersede form (Story 4.5 Q1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty-array (not 404) on zero feedback:</b> matches
/// <see cref="IAgentFeedbackService.GetFeedbackForRunAsync"/>'s empty-list
/// contract per NFR-R3. The widget renders no Previous-feedback block when
/// <see cref="Existing"/> is empty; the form opens fresh.
/// </para>
/// <para>
/// <b>Multi-user disposition:</b> the response carries ALL rows for the runId
/// — one row per <c>(RunId, CreatedBy)</c> per Story 2.1's supersede contract.
/// The widget filters client-side: the current user's row carries the Edit
/// button; other editors' rows render in display-only mode (collegial
/// collaboration framing).
/// </para>
/// </remarks>
public sealed record AgentRunFeedbackListResponse(
    string RunId,
    IReadOnlyList<AgentRunFeedbackEntry> Existing);

/// <summary>
/// One editor's feedback row on an agent run. Surfaced to the widget as a
/// display-only line (or as an Edit-able row when the row's
/// <see cref="CreatedBy"/> matches the currently-authenticated backoffice
/// user).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format:</b> <see cref="Score"/> ships as a string name
/// (<c>"ThumbsUp"</c>, <c>"ThumbsDown"</c>, <c>"Neutral"</c>) — NOT numeric
/// ordinal. Pinned via the property-level <see cref="JsonConverterAttribute"/>
/// rather than relying on Umbraco Management-API's host MvcOptions
/// configuration; adopter MvcOptions overrides or future Umbraco serializer
/// changes would otherwise silently break the widget's score-string compare
/// (Edit-button gating + Submit-disable-on-no-change derivation both depend
/// on the string-name shape).
/// </para>
/// <para>
/// <b>CreatedByDisplayName:</b> resolved server-side via
/// <see cref="Umbraco.Cms.Core.Services.IUserService.GetAsync(System.Collections.Generic.IEnumerable{Guid})"/>
/// — one batch call per response for the distinct <c>CreatedBy</c> GUIDs in
/// the rows (DRIFT-4.5-impl-2 mid-gate fast-follow 2026-05-19). Falls back to
/// <see langword="null"/> when the user GUID isn't in the lookup (deleted
/// user, lookup throw — NFR-R3 graceful degradation); the widget then renders
/// the literal "An editor" copy. Story 4.5 manual gate Step 7 confirmed the
/// real-user-name surfacing end-to-end (see Manual Gate Captures § Step 5).
/// </para>
/// </remarks>
public sealed record AgentRunFeedbackEntry(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FeedbackScore Score,
    string? Comment,
    Guid CreatedBy,
    string? CreatedByDisplayName,
    DateTime CreatedUtc);
