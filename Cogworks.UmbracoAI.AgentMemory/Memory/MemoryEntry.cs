using Cogworks.UmbracoAI.AgentMemory.Feedback;

namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// A single past-run memory considered for injection into a future agent run.
/// </summary>
public sealed record MemoryEntry(
    Guid RunId,
    string Summary,
    FeedbackScore? Score,
    string? FeedbackComment,
    DateTime When,
    double SimilarityScore);
