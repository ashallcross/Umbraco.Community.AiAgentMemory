namespace Cogworks.UmbracoAI.AgentMemory.Configuration;

/// <summary>
/// Bound configuration for <see cref="Cogworks.UmbracoAI.AgentMemory"/>.
/// Maps to the <c>Cogworks:AgentMemory</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class AgentMemoryOptions
{
    /// <summary>
    /// Number of past-run memories to retrieve and inject per agent run.
    /// Default: 5.
    /// </summary>
    public int TopKMemories { get; set; } = 5;

    /// <summary>
    /// Maximum age of memories considered for retrieval. Default: 90 days.
    /// </summary>
    public int MaxMemoryAgeDays { get; set; } = 90;

    /// <summary>
    /// Maximum length of each memory's digest text in characters. Default: 500.
    /// Bounds prompt size when memories are injected.
    /// </summary>
    public int DigestMaxChars { get; set; } = 500;

    /// <summary>
    /// Profile alias used by the digest service to summarise runs.
    /// Cheap-and-fast model recommended (e.g. Claude Haiku, GPT-4o-mini).
    /// </summary>
    public string DigestProfile { get; set; } = "anthropic-haiku-4-5";

    /// <summary>
    /// Whether new agents have memory enabled by default. Default: false (opt-in).
    /// Per-agent override via the agent's settings.
    /// </summary>
    public bool MemoryEnabledByDefault { get; set; } = false;

    /// <summary>
    /// Vector index settings for memory retrieval.
    /// </summary>
    public VectorIndexOptions VectorIndex { get; set; } = new();

    public sealed class VectorIndexOptions
    {
        /// <summary>
        /// Index alias used when composing against <c>IAIVectorStore</c>.
        /// </summary>
        public string Alias { get; set; } = Constants.MemoryVectorIndexAlias;

        /// <summary>
        /// Embedding model to use. Default: <c>text-embedding-3-small</c>.
        /// </summary>
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    }
}
