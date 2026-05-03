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
    /// Prefix used for our database tables to avoid collision with Umbraco core
    /// and Umbraco.AI tables.
    /// </summary>
    public const string TablePrefix = "cogworksAgentMemory";

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
