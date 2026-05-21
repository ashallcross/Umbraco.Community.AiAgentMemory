using System.Text.Json.Serialization;
using Cogworks.UmbracoAI.AgentMemory.Feedback;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// Story 4.9 — Read-only projection of every memory entry the package has
/// learned, returned by
/// <c>GET /umbraco/management/api/v1/cogworks-agent-memory/memory-entries</c>
/// to the Memory Learning Wall dashboard. Single flat list; the wall widget
/// groups by <see cref="MemoryWallEntry.AgentId"/> client-side per
/// architect direction 2026-05-20 (locked decision #2 — backend stays simple,
/// widget owns presentation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Empty array (NOT 404) on zero entries:</b> a fresh adopter install with
/// no memories yet returns 200 OK with <c>Entries: []</c>. The wall renders
/// the empty-state copy ("No memories learned yet — submit feedback on agent
/// runs to teach the agent.") rather than an error notice.
/// </para>
/// </remarks>
public sealed record MemoryWallListResponse(
    IReadOnlyList<MemoryWallEntry> Entries);

/// <summary>
/// One memory entry projected for the Learning Wall — row identity + agent
/// identity + digest payload + editorial signal + timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>Row-side fields (from <c>MemoryEntryEntity</c>):</b> <see cref="RunId"/>,
/// <see cref="AgentId"/>, <see cref="DigestText"/>, <see cref="CreatedUtc"/>
/// project directly from the entry row.
/// </para>
/// <para>
/// <b>Hydration fields (from <c>AgentRunFeedback</c>):</b> <see cref="Score"/>,
/// <see cref="FeedbackComment"/>, <see cref="CreatedBy"/> are joined per-row
/// via <see cref="IAgentFeedbackService.GetFeedbackForRunAsync"/> at request
/// time. When no feedback row exists for the run (race — purged between
/// indexing + retrieval), all three collapse to <see langword="null"/>
/// (mirrors <c>SemanticMemoryRetriever</c>'s contract).
/// </para>
/// <para>
/// <b>Display-name fields:</b> <see cref="AgentDisplayName"/> resolves via
/// <c>IAIAgentService.GetAgentsAsync</c> batch lookup against
/// <see cref="Agents.AIAgent.Name"/> (Story 4.8 Task 0a empirical lock — the
/// canonical backoffice display field). <see cref="CreatedByDisplayName"/>
/// resolves via <c>IUserService.GetAsync(IEnumerable{Guid})</c> (Story 4.5
/// DRIFT-4.5-impl-2 batch pattern). Both fall back to <see langword="null"/>
/// on upstream throw — the wall widget then renders
/// <c>"Agent {agentId-first-8}"</c> / <c>"An editor"</c> per NFR-R3 graceful
/// degradation.
/// </para>
/// <para>
/// <b>Wire format:</b> <see cref="Score"/> ships as a string name
/// (<c>"ThumbsUp"</c>, <c>"ThumbsDown"</c>, <c>"Neutral"</c>) — NOT numeric
/// ordinal. Pinned via the property-level <see cref="JsonConverterAttribute"/>
/// (Story 4.5 review-patch #2 lineage) rather than relying on Umbraco
/// Management-API's host MvcOptions configuration; adopter MvcOptions
/// overrides or future Umbraco serializer changes would otherwise silently
/// break the widget's score-string compare.
/// </para>
/// </remarks>
public sealed record MemoryWallEntry(
    string RunId,
    Guid AgentId,
    string? AgentDisplayName,
    string DigestText,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FeedbackScore? Score,
    string? FeedbackComment,
    Guid? CreatedBy,
    string? CreatedByDisplayName,
    DateTime CreatedUtc);
