namespace Cogworks.UmbracoAI.AgentMemory.Feedback;

/// <summary>
/// Editor's verdict on a single agent run.
/// </summary>
public enum FeedbackScore
{
    Neutral = 0,
    ThumbsUp = 1,
    ThumbsDown = -1,
}
