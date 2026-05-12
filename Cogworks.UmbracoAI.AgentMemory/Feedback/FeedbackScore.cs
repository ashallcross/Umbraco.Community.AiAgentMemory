namespace Cogworks.UmbracoAI.AgentMemory.Feedback;

/// <summary>
/// Editor's verdict on a single agent run.
/// </summary>
/// <remarks>
/// Ordinal values are persistence-locked at first ship — reordering after
/// release silently flips persisted-row meaning. Pinned values:
/// <c>ThumbsUp = 0</c>, <c>ThumbsDown = 1</c>, <c>Neutral = 2</c>.
/// </remarks>
public enum FeedbackScore
{
    ThumbsUp = 0,
    ThumbsDown = 1,
    Neutral = 2,
}
