namespace Umbraco.Community.AiAgentMemory.Runs;

/// <summary>
/// Aliased to upstream <c>AIAuditLogStatus</c> (six values; verified at runtime via
/// <c>Enum.GetNames(typeof(AIAuditLogStatus))</c> against <c>Umbraco.AI.Core 1.10.0</c>; see
/// <c>0-c-spike-outcome.md</c> § Locked decisions (e)). DRIFT-NEW-1 ratified at AR28 — Option A
/// (full 6-value enum). Exposing <c>Blocked</c> and <c>PartialSuccess</c> as first-class is
/// editorially valuable for the "agents that learn" demo (an agent's previous run being blocked
/// by a guardrail is exactly the kind of signal memory should carry forward).
/// </summary>
public enum AgentRunStatus
{
    /// <summary>Operation currently executing.</summary>
    Running,

    /// <summary>Operation completed successfully.</summary>
    Succeeded,

    /// <summary>
    /// Operation failed with an error. Includes SIGINT-cancelled in-flight chat
    /// (DRIFT-NEW-6: process shutdown surfaces as <c>Failed</c> with
    /// <c>ErrorMessage = "The operation was canceled."</c>, NOT <c>Cancelled</c>).
    /// </summary>
    Failed,

    /// <summary>
    /// Explicit caller-driven cancellation (rare; reserved for explicit
    /// <see cref="System.Threading.CancellationToken"/> usage from a caller,
    /// distinct from process kill which maps to <see cref="Failed"/>).
    /// </summary>
    Cancelled,

    /// <summary>Operation completed with partial success.</summary>
    PartialSuccess,

    /// <summary>Operation blocked by a guardrail policy.</summary>
    Blocked,
}
