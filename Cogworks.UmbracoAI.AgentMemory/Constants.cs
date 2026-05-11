namespace Cogworks.UmbracoAI.AgentMemory;

/// <summary>
/// Shared constants for the package.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Configuration section root: <c>Cogworks:AgentMemory</c>.
    /// </summary>
    public const string ConfigSection = "Cogworks:AgentMemory";

    /// <summary>
    /// Backoffice API route prefix.
    /// </summary>
    public const string ApiRoutePrefix = "umbraco/cogworks-agent-memory/api";

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
    /// Migration plan name (also used as the migration history key).
    /// </summary>
    public const string MigrationPlanName = "Cogworks.UmbracoAI.AgentMemory";

    /// <summary>
    /// Vector index alias used when composing against <c>Umbraco.AI.Search</c>'s
    /// <c>IAIVectorStore</c> for memory retrieval.
    /// </summary>
    public const string MemoryVectorIndexAlias = "cogworks-agent-memory";
}
