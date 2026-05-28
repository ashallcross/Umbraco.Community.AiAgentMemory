namespace Umbraco.Community.AiAgentMemory;

/// <summary>
/// Shared constants for the package.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Configuration section root: <c>AiAgentMemory</c>.
    /// </summary>
    public const string ConfigSection = "AiAgentMemory";

    /// <summary>
    /// Swagger document name + Management-API operation-grouping identifier
    /// for this package's controllers. Used by
    /// <c>AgentMemoryBackofficeApiComposer</c>'s <c>SwaggerDoc</c> registration
    /// and the controllers' <c>[MapToApi(Constants.ApiName)]</c> attribute.
    /// </summary>
    /// <remarks>
    /// The resolved Management API prefix is
    /// <c>/umbraco/management/api/v1/cogworks-agent-memory/</c> via
    /// <c>[VersionedApiBackOfficeRoute("cogworks-agent-memory/...")]</c> (the
    /// framework prepends <c>/management/api/v{version:apiVersion}/</c>).
    /// AR12/AR20 brand-rename boundary: this value is RENAME-IMMUTABLE; the
    /// <c>cogworks-agent-memory</c> package-scope segment stays stable through
    /// the brand rename (only the <c>/management/api/v{version}/</c> segment is
    /// framework-enforced via <c>VersionedApiBackOfficeRouteAttribute</c>; the
    /// <c>cogworks-agent-memory</c> slug is the package-owned rename-stable
    /// anchor reconciled at Story 2.2 2026-05-12).
    /// </remarks>
    public const string ApiName = "cogworks-agent-memory";

    /// <summary>
    /// Database table holding editor feedback against agent runs. The
    /// <c>cogworks_agent_memory_*</c> prefix is a runtime contract for adopter
    /// sites and does not participate in the brand rename pass (AR6 + AR20).
    /// </summary>
    public const string FeedbackTableName = "cogworks_agent_memory_feedback";

    /// <summary>
    /// Database table holding memory entries (digest text + vector reference).
    /// Same rename-safe contract as <see cref="FeedbackTableName"/> (AR6 + AR20).
    /// </summary>
    public const string MemoryEntriesTableName = "cogworks_agent_memory_entries";

    /// <summary>
    /// Migration plan name (also used as the migration history key in
    /// <c>umbracoKeyValue</c>). PINNED to the legacy value through the
    /// Story 5.3 brand rename per LD#R3 — renaming this without a
    /// key-value seed migration would cause Umbraco to think the renamed
    /// plan has never executed and re-run all package migrations on
    /// adopter upgrade. v0.2 candidate: rename + seed-migration step.
    /// </summary>
    public const string MigrationPlanName = "Cogworks.UmbracoAI.AgentMemory";

    /// <summary>
    /// Vector index alias used when composing against <c>Umbraco.AI.Search</c>'s
    /// <c>IAIVectorStore</c> for memory retrieval.
    /// </summary>
    public const string MemoryVectorIndexAlias = "cogworks-agent-memory";
}
