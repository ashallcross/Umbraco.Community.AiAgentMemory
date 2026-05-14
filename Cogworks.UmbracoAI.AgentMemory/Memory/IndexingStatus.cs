namespace Cogworks.UmbracoAI.AgentMemory.Memory;

/// <summary>
/// Lifecycle state of a <see cref="Persistence.Entities.MemoryEntryEntity"/>'s
/// embed-and-upsert pipeline.
/// </summary>
/// <remarks>
/// Ordinal values are persistence-locked at first ship — reordering after
/// release silently flips persisted-row meaning. Pinned values:
/// <c>Pending = 0</c>, <c>Embedded = 1</c>, <c>Failed = 2</c>. Mirrors Story
/// 2.1's <c>FeedbackScore</c> ordinal-stability pattern.
/// </remarks>
public enum IndexingStatus
{
    Pending = 0,
    Embedded = 1,
    Failed = 2,
}
