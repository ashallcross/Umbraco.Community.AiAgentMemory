using Umbraco.Community.AiAgentMemory.Memory;

namespace Umbraco.Community.AiAgentMemory.Tests.Memory;

/// <summary>
/// Pins the persistence contract on <see cref="IndexingStatus"/> ordinals.
/// Reordering values after first ship silently flips persisted-row meaning —
/// mirrors Story 2.1's <c>FeedbackScoreEnumTests</c>.
/// </summary>
[TestFixture]
public class IndexingStatusEnumTests
{
    [TestCase(IndexingStatus.Pending, 0)]
    [TestCase(IndexingStatus.Embedded, 1)]
    [TestCase(IndexingStatus.Failed, 2)]
    public void IndexingStatusOrdinals_PinContract(IndexingStatus value, int expected)
    {
        Assert.That((int)value, Is.EqualTo(expected),
            $"{value} must persist as ordinal {expected} — reordering silently flips persisted-row meaning.");
    }
}
