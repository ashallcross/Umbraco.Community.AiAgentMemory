using Umbraco.Community.AiAgentMemory.Configuration;
using Umbraco.Community.AiAgentMemory.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Chat;
using Umbraco.AI.Core.RuntimeContext;

namespace Umbraco.Community.AiAgentMemory.Middleware;

/// <summary>
/// Registration class for the memory-injection chat middleware. Implements
/// <see cref="IAIChatMiddleware"/> — Umbraco.AI's single-instance global
/// chat-pipeline seam. <see cref="Apply"/> is invoked by
/// <c>AIChatClientFactory</c> each time the chat pipeline is constructed,
/// producing a per-call <see cref="MemoryInjectionMiddleware"/> wrapper around
/// the inbound <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two-class shape (DRIFT-3.3-1): the upstream <see cref="IAIChatMiddleware"/>
/// registration seam is global (one instance per registration; no per-agent
/// constructor binding). The per-call wrapper
/// <see cref="MemoryInjectionMiddleware"/> resolves <c>agentId</c> from
/// <see cref="IAIRuntimeContextAccessor"/>. Mirrors upstream's
/// <c>AIToolReorderingChatMiddleware</c> / <c>AIToolReorderingChatClient</c>
/// pattern verbatim.
/// </para>
/// <para>
/// Registered by <c>AgentMemoryComposer</c> via
/// <c>builder.AIChatMiddleware().Append&lt;MemoryInjectionChatMiddleware&gt;()</c>.
/// </para>
/// </remarks>
internal sealed class MemoryInjectionChatMiddleware : IAIChatMiddleware
{
    private readonly IMemoryRetriever _retriever;
    private readonly IAIRuntimeContextAccessor _runtimeContextAccessor;
    private readonly IOptionsMonitor<AgentMemoryOptions> _options;
    private readonly ILogger<MemoryInjectionMiddleware> _innerLogger;

    public MemoryInjectionChatMiddleware(
        IMemoryRetriever retriever,
        IAIRuntimeContextAccessor runtimeContextAccessor,
        IOptionsMonitor<AgentMemoryOptions> options,
        ILogger<MemoryInjectionMiddleware> innerLogger)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(runtimeContextAccessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(innerLogger);
        _retriever = retriever;
        _runtimeContextAccessor = runtimeContextAccessor;
        _options = options;
        _innerLogger = innerLogger;
    }

    public IChatClient Apply(IChatClient inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new MemoryInjectionMiddleware(inner, _retriever, _runtimeContextAccessor, _options, _innerLogger);
    }
}
