using Cogworks.UmbracoAI.AgentMemory.Feedback;

namespace Cogworks.UmbracoAI.AgentMemory.Web.Api.Models;

/// <summary>
/// POST body for
/// <c>/umbraco/management/api/v1/cogworks-agent-memory/feedback</c>.
/// </summary>
/// <remarks>
/// All fields are required except <see cref="Comment"/>. The host user identity
/// is resolved server-side via <c>IBackOfficeSecurityAccessor</c> and is NOT
/// part of this payload — never trust a client-supplied <c>createdBy</c>.
/// </remarks>
public sealed record AgentFeedbackPostRequest(
    string RunId,
    Guid AgentId,
    FeedbackScore Score,
    string? Comment);
