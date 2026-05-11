namespace Cogworks.UmbracoAI.AgentMemory.Configuration;

/// <summary>
/// Bound configuration for <see cref="Cogworks.UmbracoAI.AgentMemory"/>.
/// Maps to the <c>Cogworks:AgentMemory</c> section of <c>appsettings.json</c>
/// (sourced via <see cref="Constants.ConfigSection"/>).
/// </summary>
/// <remarks>
/// Invariants on these fields are enforced at first read of
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}.CurrentValue"/>
/// by <see cref="AgentMemoryOptionsValidator"/> — a misconfigured value
/// surfaces as <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
/// the first time the agent-memory pipeline reads options, rather than
/// degrading silently to zero-results-from-retrieval downstream.
/// </remarks>
public sealed class AgentMemoryOptions
{
    /// <summary>
    /// Number of past-run memories to retrieve and inject per agent run.
    /// Default: 5. Range: <c>[1, 10]</c>. Values outside the range surface
    /// as <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
    /// at first read.
    /// </summary>
    public int TopKMemories { get; set; } = 5;

    /// <summary>
    /// Maximum age of memories considered for retrieval. Default: 90 days.
    /// Must be <c>&gt;= 1</c>. No upper bound is code-enforced — the
    /// adopter footgun against <c>Umbraco:AI:AuditLog:RetentionDays</c>
    /// (default 14) is README-documented.
    /// </summary>
    public int MaxMemoryAgeDays { get; set; } = 90;

    /// <summary>
    /// Maximum length of each memory's digest text in characters. Default:
    /// 500. Must be <c>&gt;= 1</c>. Bounds prompt size when memories are
    /// injected — the background indexer reads this to truncate the joined
    /// run text before embedding.
    /// </summary>
    public int DigestMaxChars { get; set; } = 500;

    /// <summary>
    /// Profile alias used by the digest service if/when a digest-generation
    /// model is wired (Phase 2+). Default: <c>anthropic-haiku-4-5</c>. Not
    /// validated at the options layer — alias existence is determined at
    /// runtime via <c>IAIProfileService.GetProfileByAliasAsync</c> with
    /// graceful no-op per NFR-R1.
    /// </summary>
    /// <remarks>
    /// The current build ships <see cref="DigestMaxChars"/>-truncated raw
    /// joined run text as the digest content (no LLM summarisation). This
    /// alias is reserved for a future digest-service revival.
    /// </remarks>
    public string DigestProfile { get; set; } = "anthropic-haiku-4-5";

    /// <summary>
    /// Pointer to an <c>AIProfile</c> configured by the adopter via the
    /// backoffice AI section UI. The package looks the profile up at runtime
    /// via <c>IAIProfileService.GetProfileByAliasAsync(alias, ct)</c>. If
    /// <c>null</c> (default), the retriever falls back to
    /// <c>IOptions&lt;AIOptions&gt;.Value.DefaultEmbeddingProfileAlias</c>
    /// (the host's default). If neither resolves, memory features silently
    /// no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a pointer, not a model literal:</b> the embedding
    /// model is determined by the <c>AIProfile</c>'s <c>Model</c> reference
    /// (DB-persisted, UI-managed via the backoffice AI section UI), NOT an
    /// <c>appsettings.json</c> POCO field.
    /// </para>
    /// <para>
    /// <b>Adopter prerequisite:</b> before installing the package, adopters
    /// MUST configure (a) an <c>AIConnection</c> (provider + API key) and
    /// (b) an embedding <c>AIProfile</c> (capability=Embedding) via the
    /// backoffice AI section UI. The package never reads API keys from
    /// <c>appsettings.json</c> — they live in the host's <c>AIConnection</c>
    /// DB rows.
    /// </para>
    /// </remarks>
    public string? EmbeddingProfileAlias { get; set; }

    /// <summary>
    /// Cosine-similarity threshold for memory-retrieval eligibility.
    /// Memories whose vector similarity to the current input is below this
    /// threshold are NOT injected. Range: <c>[0.0, 1.0]</c>; values outside
    /// the range surface as
    /// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
    /// at first read. Default: <c>0.7</c>.
    /// </summary>
    /// <remarks>
    /// The retriever reads this value at runtime to filter
    /// <c>IAIVectorStore.SearchAsync</c> results in C# post-fetch (the
    /// upstream vector store has no native eligibility-threshold filter).
    /// </remarks>
    public double EligibilityThreshold { get; set; } = 0.7;

    /// <summary>
    /// Agent GUIDs for which memory injection is enabled. <b>The ONLY way
    /// to enable memory.</b> Empty by default ⇒ memory disabled for every
    /// agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No global on-switch exists.</b> Per-agent enrolment is the only
    /// way to enable memory — cross-agent memory pollution from a global
    /// default is structurally impossible.
    /// </para>
    /// <para>
    /// <b>Validation:</b> a <c>null</c> collection, duplicate GUIDs, and any
    /// <see cref="System.Guid.Empty"/> entry surface as
    /// <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
    /// at first read. Duplicate-enable signals adopter error, not
    /// idempotency-tolerated behaviour.
    /// </para>
    /// <para>
    /// <c>MemoryInjectionMiddleware</c> reads this collection at runtime to
    /// gate per-agent memory injection.
    /// </para>
    /// </remarks>
    public IList<Guid> EnabledAgents { get; set; } = new List<Guid>();

    /// <summary>
    /// Vector index settings for memory retrieval.
    /// </summary>
    public VectorIndexOptions VectorIndex { get; set; } = new();

    /// <summary>
    /// Nested options block for the <c>IAIVectorStore</c> integration.
    /// </summary>
    public sealed class VectorIndexOptions
    {
        /// <summary>
        /// Index alias used when composing against
        /// <c>Umbraco.AI.Search</c>'s <c>IAIVectorStore</c>. Default:
        /// <c>cogworks-agent-memory</c>. The default value is a runtime
        /// contract for adopter sites and does NOT participate in the
        /// brand-rename pass.
        /// </summary>
        /// <remarks>
        /// Adopters CAN override the alias in rare cases (e.g. multi-tenant
        /// site partitioning), but the default fits ~all adopters. Not
        /// validated for string content at the options layer — adopter
        /// footgun against typos surfaces as empty retrieval, not a
        /// security or data-integrity violation.
        /// </remarks>
        public string Alias { get; set; } = Constants.MemoryVectorIndexAlias;
    }
}
