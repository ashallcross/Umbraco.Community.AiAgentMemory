using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Cogworks.UmbracoAI.AgentMemory.Memory;

namespace Cogworks.UmbracoAI.AgentMemory.Middleware;

/// <summary>
/// Wraps an agent's <see cref="IChatClient"/> and prepends a system message
/// summarising relevant past runs ("Lessons from past runs: ...") before
/// delegating to the inner chat client.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing middleware that closes the learning loop. Per-agent
/// opt-in via <see cref="AgentMemoryOptions.EnabledAgents"/> (FR27 / FR28 / FR29 /
/// FR38 — the only enable surface in v0.1; no global on-switch exists).
/// </para>
/// <para>
/// Implementation is placeholder for v0.1 scaffold. Week 3 of the sprint plan
/// completes the retrieval + injection logic.
/// </para>
/// </remarks>
public sealed class MemoryInjectionMiddleware : DelegatingChatClient
{
    private readonly IMemoryRetriever _retriever;
    private readonly AgentMemoryOptions _options;
    private readonly Guid _agentId;

    public MemoryInjectionMiddleware(
        IChatClient innerClient,
        IMemoryRetriever retriever,
        IOptions<AgentMemoryOptions> options,
        Guid agentId)
        : base(innerClient)
    {
        _retriever = retriever;
        _options = options.Value;
        _agentId = agentId;
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

    private async Task<List<ChatMessage>> EnrichWithMemoriesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var list = messages.ToList();

        var memories = await _retriever.RetrieveSimilarAsync(
            _agentId,
            list,
            _options.TopKMemories,
            cancellationToken).ConfigureAwait(false);

        if (memories.Count == 0)
        {
            return list;
        }

        var systemMessage = BuildMemorySystemMessage(memories);
        list.Insert(0, new ChatMessage(ChatRole.System, systemMessage));
        return list;
    }

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
                : $" — \"{memory.FeedbackComment}\"";

            sb.Append("• Run ");
            sb.Append(memory.RunId.ToString("N").AsSpan(0, 8));
            sb.Append(' ');
            sb.Append(marker);
            sb.Append(": ");
            sb.Append(memory.Summary);
            sb.AppendLine(feedbackSuffix);
        }

        return sb.ToString();
    }
}
