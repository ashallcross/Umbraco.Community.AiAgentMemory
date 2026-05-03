namespace Cogworks.UmbracoAI.AgentMemory.Tests.Composing;

/// <summary>
/// Smoke tests for the composition root. Real DI integration tests come once
/// the EF-backed implementations land in Week 1.
/// </summary>
[TestFixture]
public class AgentMemoryComposerTests
{
    [Test]
    public void Smoke_Build_DoesNotThrow()
    {
        // Placeholder smoke test so the suite is not empty.
        // Replace with real composition root tests in Week 1.
        Assert.That(true, Is.True);
    }
}
