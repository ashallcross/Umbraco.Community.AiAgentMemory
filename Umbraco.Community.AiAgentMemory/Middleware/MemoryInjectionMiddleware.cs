using Umbraco.Community.AiAgentMemory.Configuration;
using Umbraco.Community.AiAgentMemory.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.RuntimeContext;

namespace Umbraco.Community.AiAgentMemory.Middleware;

/// <summary>
/// Per-call wrapper produced by <see cref="MemoryInjectionChatMiddleware.Apply"/>.
/// Prepends a system message summarising relevant past runs ("Lessons from
/// past runs: ...") before delegating to the inner <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two-class shape (DRIFT-3.3-1): the upstream <see cref="Umbraco.AI.Core.Chat.IAIChatMiddleware"/>
/// registration seam is global (no per-agent ctor binding), so this wrapper
/// resolves <c>agentId</c> per call from
/// <see cref="IAIRuntimeContextAccessor"/> using
/// <c>ContextKeys.AgentId = "Umbraco.AI.Agent.AgentId"</c> (Spike 0.C verified
/// literal). <c>TryGetValue</c> (not <c>GetValue</c>) is used so the no-key
/// gate is distinguishable from the defensive <see cref="Guid.Empty"/> gate.
/// </para>
/// <para>
/// Per-call hot-reload (FR30): <c>IOptionsMonitor&lt;AgentMemoryOptions&gt;</c>
/// is read via <c>CurrentValue</c> at the top of each invocation; cross-call
/// hot-reload IS honoured, mid-call hot-reload is structurally impossible by
/// design (one snapshot per call).
/// </para>
/// <para>
/// Per-agent opt-in gate (NFR-R4 LOAD-BEARING): opted-out agents see ZERO
/// <see cref="IMemoryRetriever.RetrieveSimilarAsync"/> calls AND zero
/// injected messages. <c>EnabledAgents</c> is the only enable surface in v0.1.
/// </para>
/// <para>
/// Cancellation discipline: <see cref="OperationCanceledException"/> from the
/// retriever propagates unwrapped to the caller (mirrors Story 1.2 / 2.1 /
/// 3.1 / 3.2). Other retriever exceptions are caught + Warning-logged +
/// pass-through unenriched (NFR-R3 graceful degradation; defensive
/// depth-in-defence beyond Story 3.2's never-throws-except-OCE contract).
/// </para>
/// <para>
/// MemoryEntry.RunId is non-nullable <see cref="string"/> per Story 3.2
/// amendment + Story 1.1 schema NOT NULL constraint, so
/// <see cref="BuildMemorySystemMessage"/> trusts the contract end-to-end
/// (no defensive null guard). The <c>Math.Min(8, RunId.Length)</c> clamp
/// covers the short-string edge case if a non-GUID RunId ever lands.
/// </para>
/// </remarks>
internal sealed class MemoryInjectionMiddleware : DelegatingChatClient
{
    private readonly IMemoryRetriever _retriever;
    private readonly IAIRuntimeContextAccessor _runtimeContextAccessor;
    private readonly IOptionsMonitor<AgentMemoryOptions> _options;
    private readonly ILogger<MemoryInjectionMiddleware> _logger;

    public MemoryInjectionMiddleware(
        IChatClient innerClient,
        IMemoryRetriever retriever,
        IAIRuntimeContextAccessor runtimeContextAccessor,
        IOptionsMonitor<AgentMemoryOptions> options,
        ILogger<MemoryInjectionMiddleware> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(runtimeContextAccessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _retriever = retriever;
        _runtimeContextAccessor = runtimeContextAccessor;
        _options = options;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var enriched = await EnrichWithMemoriesAsync(messages, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(enriched, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enriched = await EnrichWithMemoriesAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(enriched, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task<IEnumerable<ChatMessage>> EnrichWithMemoriesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        // P8 — gates run BEFORE materialising the inbound enumerable. NFR-R4
        // LOAD-BEARING: pass-through paths (no-context / no-agentId /
        // Guid.Empty / EnabledAgents-null / AgentNotOptedIn) pay zero
        // materialisation cost. The v0.1 default (empty EnabledAgents) routes
        // every chat call through AgentNotOptedIn — keeping that path cheap is
        // the whole point.

        // AC2.a defensive — runtime context absent (no active scope).
        var context = _runtimeContextAccessor.Context;
        if (context is null)
        {
            _logger.LogDebug(
                "MemoryInjectionMiddleware — no AIRuntimeContext active; passing through. Reason={Reason}.",
                "NoRuntimeContext");
            return messages;
        }

        // AC2.a — TryGetValue distinguishes no-key vs Guid.Empty cleanly.
        // Programmatic + Automate paths produce empty Metadata in upstream 1.10.0
        // baseline (DRIFT-NEW-5); Adam's local fork Fork (i) patch closes the gap.
        if (!context.TryGetValue<Guid>(Umbraco.AI.Agent.Core.Constants.ContextKeys.AgentId, out var agentId))
        {
            _logger.LogDebug(
                "MemoryInjectionMiddleware — no AgentId in runtime context (likely programmatic / Automate path pre Fork (i)); passing through. Reason={Reason}.",
                "NoAgentIdInRuntimeContext");
            return messages;
        }

        // AC2.c — defensive Guid.Empty depth-in-defence. Validator (Story 1.3)
        // already pins EnabledAgents against Guid.Empty; named-options bypass is
        // theoretically possible.
        if (agentId == Guid.Empty)
        {
            _logger.LogWarning(
                "MemoryInjectionMiddleware — agent identity is Guid.Empty (validator-bypass scenario); passing through. Reason={Reason}.",
                "AgentIdGuidEmpty");
            return messages;
        }

        // AC2.b LOAD-BEARING NFR-R4 — opt-in gate BEFORE retriever call.
        // CurrentValue re-read per call honours FR30 hot-reload (mirrors
        // SemanticMemoryRetriever.cs:96).
        var options = _options.CurrentValue;

        // P6 — symmetric defensive guard mirroring the Guid.Empty branch above.
        // Validator (Story 1.3) pins EnabledAgents non-null for default-bound
        // options; named-options bypass could theoretically produce null.
        if (options.EnabledAgents is null)
        {
            _logger.LogWarning(
                "MemoryInjectionMiddleware — EnabledAgents collection is null (validator-bypass scenario); passing through. Reason={Reason}.",
                "EnabledAgentsNull");
            return messages;
        }

        if (!options.EnabledAgents.Contains(agentId))
        {
            _logger.LogDebug(
                "MemoryInjectionMiddleware — agent {AgentId} not opted in; passing through (zero retriever call). Reason={Reason}.",
                agentId, "AgentNotOptedIn");
            return messages;
        }

        // Inject path — materialise once. Retriever requires IReadOnlyList<>
        // (Story 3.2 contract); we reuse the same list for the inner chat call.
        var list = messages.ToList();

        // Story 4.5 DRIFT-4.5-impl-1 — FeatureType preservation patch (architect
        // ratification 2026-05-19 mid-gate). The retriever's embedding pipeline
        // (Umbraco.AI's ScopedProfileEmbeddingGenerator + ScopedInlineEmbedding-
        // Generator) MUTATES the agent's runtime context scope's profile +
        // feature metadata without snapshot/restore — see Story 4.5 § Pre-flight
        // notes Step 1 for the upstream-source-trace. Consequence pre-patch:
        // the agent's outer chat call's audit-log row gets attributed as
        // FeatureType="inline-embedding" + ProfileAlias="openai-embedding" etc.,
        // and the cascade through AgentFeedbackController.PostAsync (Story 2.2)
        // — which filters on FeatureType="agent" — returns 404 on submit;
        // downstream the feedback row + memory entry would index under the
        // wrong AgentId, and Run 2's retriever would find zero entries (demo
        // loop wouldn't close). The fix below snapshots all 9 profile + feature
        // keys before the retriever call + restores them in a finally so the
        // wrapped chat call's audit row captures the agent's identity correctly.
        //
        // This is the v0.1 in-our-code workaround for an upstream defect; the
        // architecturally correct fix would be in upstream ScopedProfileEmbedd-
        // ingGenerator (snapshot/restore in its PopulateProfileMetadata when
        // scopeExisted=true). Deferred to a future upstream PR; the in-our-
        // code fix is sufficient for v0.1.
        // P12: snapshot via TryGetValue so we can distinguish "key set" from
        // "key absent". Restoring `default(T)` for an originally-absent key
        // would actively corrupt the post-condition the snapshot is meant to
        // preserve — writing Guid.Empty / null / 0 into a context that didn't
        // carry the key on entry. Only keys present pre-snapshot are restored;
        // any key the retriever adds that wasn't there before is left alone
        // (no false-positive overwrites of downstream contributions).
        var hadProfileId = context.TryGetValue<Guid>(Umbraco.AI.Core.Constants.ContextKeys.ProfileId, out var savedProfileId);
        var hadProfileAlias = context.TryGetValue<string>(Umbraco.AI.Core.Constants.ContextKeys.ProfileAlias, out var savedProfileAlias);
        var hadProfileVersion = context.TryGetValue<int>(Umbraco.AI.Core.Constants.ContextKeys.ProfileVersion, out var savedProfileVersion);
        var hadProviderId = context.TryGetValue<string>(Umbraco.AI.Core.Constants.ContextKeys.ProviderId, out var savedProviderId);
        var hadModelId = context.TryGetValue<string>(Umbraco.AI.Core.Constants.ContextKeys.ModelId, out var savedModelId);
        var hadFeatureType = context.TryGetValue<string>(Umbraco.AI.Core.Constants.ContextKeys.FeatureType, out var savedFeatureType);
        var hadFeatureId = context.TryGetValue<Guid>(Umbraco.AI.Core.Constants.ContextKeys.FeatureId, out var savedFeatureId);
        var hadFeatureAlias = context.TryGetValue<string>(Umbraco.AI.Core.Constants.ContextKeys.FeatureAlias, out var savedFeatureAlias);
        var hadFeatureVersion = context.TryGetValue<int>(Umbraco.AI.Core.Constants.ContextKeys.FeatureVersion, out var savedFeatureVersion);

        // AC3 — retrieve. workspaceId: null is the v0.1 production default per
        // Story 3.1 Locked decision #9 (FR33/FR34/FR36 v0.2 candidate).
        IReadOnlyList<MemoryEntry> memories;
        try
        {
            memories = await _retriever.RetrieveSimilarAsync(
                agentId,
                workspaceId: null,
                list,
                options.TopKMemories,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // AC8.9 — cancellation propagates unwrapped.
        }
        catch (Exception ex)
        {
            // AC8.8 — NFR-R3 graceful degradation; defensive depth-in-defence
            // beyond Story 3.2's never-throws-except-OCE contract.
            _logger.LogWarning(ex,
                "MemoryInjectionMiddleware — retriever threw (NFR-R3 graceful degradation; defensive depth-in-defence beyond Story 3.2 contract). " +
                "Agent {AgentId} chat call proceeds without memory injection.",
                agentId);
            return list;
        }
        finally
        {
            // Story 4.5 DRIFT-4.5-impl-1 — restore profile + feature context
            // keys regardless of retriever outcome (success / NFR-R3 caught
            // exception / cancellation rethrow). The wrapped chat call's audit
            // row (captured by Umbraco.AI's AIAuditingChatClient at AC5 of
            // Story 3.3) thereby reads the agent's identity correctly:
            // FeatureType="agent", FeatureId={agent's GUID}, ProfileAlias=
            // {chat profile alias}, etc.
            if (hadProfileId) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.ProfileId, savedProfileId);
            if (hadProfileAlias) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.ProfileAlias, savedProfileAlias!);
            if (hadProfileVersion) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.ProfileVersion, savedProfileVersion);
            if (hadProviderId) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.ProviderId, savedProviderId!);
            if (hadModelId) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.ModelId, savedModelId!);
            if (hadFeatureType) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.FeatureType, savedFeatureType!);
            if (hadFeatureId) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.FeatureId, savedFeatureId);
            if (hadFeatureAlias) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.FeatureAlias, savedFeatureAlias!);
            if (hadFeatureVersion) context.SetValue(Umbraco.AI.Core.Constants.ContextKeys.FeatureVersion, savedFeatureVersion);
        }

        // AC5 — FR26 empty-result pass-through.
        if (memories.Count == 0)
        {
            _logger.LogDebug(
                "MemoryInjectionMiddleware — retriever returned empty for agent {AgentId}; passing through. Reason={Reason}.",
                agentId, "RetrieverReturnedEmpty");
            return list;
        }

        // AC3 happy path — prepend "Lessons from past runs" system message.
        var systemMessage = BuildMemorySystemMessage(memories);
        list.Insert(0, new ChatMessage(ChatRole.System, systemMessage));
        _logger.LogDebug(
            "MemoryInjectionMiddleware — injected {Count} memories for agent {AgentId}. Reason={Reason}.",
            memories.Count, agentId, "MemoriesInjected");
        return list;
    }

    // AR21 brand-anchor: "Lessons from past runs" literal first-line is the
    // LOAD-BEARING audit-log surface for FR43; rename only with Adam approval.
    //
    // P11: writes "\n" literals (NOT StringBuilder.AppendLine, which emits
    // Environment.NewLine and therefore "\r\n" on Windows hosts). The
    // downstream parser at AgentRunReadController.ParseMemoryInjection
    // anchors on the LF-only literal "[system] Lessons from past runs:\n";
    // a CRLF emit would flip MemoryUsed false + null every commentSnippet on
    // Windows adopters. Pin LF here so the wire shape is OS-independent.
    private static string BuildMemorySystemMessage(IReadOnlyList<MemoryEntry> memories)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Lessons from past runs:\n");
        foreach (var memory in memories)
        {
            var marker = memory.Score switch
            {
                Feedback.FeedbackScore.ThumbsUp => "👍",
                Feedback.FeedbackScore.ThumbsDown => "👎",
                _ => "•",
            };

            var feedbackSuffix = string.IsNullOrWhiteSpace(memory.FeedbackComment)
                ? string.Empty
                : $" — \"{FlattenForBullet(memory.FeedbackComment)}\"";

            sb.Append("• Run ");
            // MemoryEntry.RunId is string (post Story 3.2 DRIFT-3.2-1 — was Guid;
            // semantically holds the upstream ThreadId per project-context § Schema
            // RunId Column). Defensive Math.Min handles the edge case of <8-char
            // strings (theoretically possible if a non-GUID RunId ever lands).
            sb.Append(memory.RunId.AsSpan(0, Math.Min(8, memory.RunId.Length)));
            sb.Append(' ');
            sb.Append(marker);
            sb.Append(": ");
            // P10: Summary is the entries-row DigestText — real chat content routinely
            // contains newlines / markdown / JSON. Strip newlines to keep each memory on
            // one bullet line; preserves audit-log parseability for FR43 + LLM
            // bullet-list interpretation.
            sb.Append(FlattenForBullet(memory.Summary));
            sb.Append(feedbackSuffix);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string FlattenForBullet(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
