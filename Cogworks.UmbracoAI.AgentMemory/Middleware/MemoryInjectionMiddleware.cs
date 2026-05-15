using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.RuntimeContext;

namespace Cogworks.UmbracoAI.AgentMemory.Middleware;

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
    private static string BuildMemorySystemMessage(IReadOnlyList<MemoryEntry> memories)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Lessons from past runs:");
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
            sb.AppendLine(feedbackSuffix);
        }

        return sb.ToString();
    }

    private static string FlattenForBullet(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
