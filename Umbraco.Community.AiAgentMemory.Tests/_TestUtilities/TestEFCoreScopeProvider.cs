using Umbraco.Community.AiAgentMemory.Persistence;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Persistence.EFCore.Scoping;

namespace Umbraco.Community.AiAgentMemory.Tests._TestUtilities;

/// <summary>
/// Test-only <see cref="IEFCoreScopeProvider{AgentMemoryDbContext}"/> that wraps
/// a real <see cref="AgentMemoryDbContext"/> (Sqlite in-memory or similar) so
/// repository tests can exercise the canonical scope-usage pattern without a
/// full Umbraco host. Only <see cref="CreateScope"/> is implemented; other
/// members throw <see cref="NotSupportedException"/> to fail loudly on
/// accidental use.
/// </summary>
internal sealed class TestEFCoreScopeProvider : IEFCoreScopeProvider<AgentMemoryDbContext>
{
    private readonly AgentMemoryDbContext _ctx;

    public TestEFCoreScopeProvider(AgentMemoryDbContext ctx) => _ctx = ctx;

    public IScopeContext? AmbientScopeContext => null;

    public IEfCoreScope<AgentMemoryDbContext> CreateScope(
        RepositoryCacheMode repositoryCacheMode = RepositoryCacheMode.Default,
        bool? scopeFileSystems = null)
        => new TestEFCoreScope(_ctx);

    public IEfCoreScope<AgentMemoryDbContext> CreateDetachedScope(
        RepositoryCacheMode repositoryCacheMode = RepositoryCacheMode.Default,
        bool? scopeFileSystems = null)
        => throw new NotSupportedException("Test fake — only CreateScope is implemented");

    public void AttachScope(IEfCoreScope<AgentMemoryDbContext> other)
        => throw new NotSupportedException("Test fake — only CreateScope is implemented");

    public IEfCoreScope<AgentMemoryDbContext> DetachScope()
        => throw new NotSupportedException("Test fake — only CreateScope is implemented");
}

/// <summary>
/// Test-only <see cref="IEfCoreScope{AgentMemoryDbContext}"/> that executes the
/// caller's lambda against the wrapped context. <c>Complete()</c> is a no-op
/// (returns <c>true</c>); <c>Dispose()</c> is a no-op (the wrapping test
/// fixture owns the context lifetime).
/// </summary>
internal sealed class TestEFCoreScope : IEfCoreScope<AgentMemoryDbContext>
{
    private readonly AgentMemoryDbContext _ctx;

    public TestEFCoreScope(AgentMemoryDbContext ctx) => _ctx = ctx;

    public Task<T> ExecuteWithContextAsync<T>(Func<AgentMemoryDbContext, Task<T>> method)
        => method(_ctx);

    public Task ExecuteWithContextAsync<T>(Func<AgentMemoryDbContext, Task> method)
        => method(_ctx);

    public bool Complete() => true;

    public void Dispose() { /* fixture owns context lifetime */ }

    public IScopeContext? ScopeContext { get; set; }

    public IScopedNotificationPublisher Notifications
        => throw new NotSupportedException("Test fake — Notifications not implemented");

    public int Depth => 0;

    public ILockingMechanism Locks
        => throw new NotSupportedException("Test fake — Locks not implemented");

    public RepositoryCacheMode RepositoryCacheMode => RepositoryCacheMode.Default;

    public IsolatedCaches IsolatedCaches
        => throw new NotSupportedException("Test fake — IsolatedCaches not implemented");

    public void ReadLock(params int[] lockIds)
        => throw new NotSupportedException("Test fake — ReadLock not implemented");

    public void WriteLock(params int[] lockIds)
        => throw new NotSupportedException("Test fake — WriteLock not implemented");

    public void WriteLock(TimeSpan timeout, int lockId)
        => throw new NotSupportedException("Test fake — WriteLock not implemented");

    public void ReadLock(TimeSpan timeout, int lockId)
        => throw new NotSupportedException("Test fake — ReadLock not implemented");

    public void EagerWriteLock(params int[] lockIds)
        => throw new NotSupportedException("Test fake — EagerWriteLock not implemented");

    public void EagerWriteLock(TimeSpan timeout, int lockId)
        => throw new NotSupportedException("Test fake — EagerWriteLock not implemented");

    public void EagerReadLock(TimeSpan timeout, int lockId)
        => throw new NotSupportedException("Test fake — EagerReadLock not implemented");

    public void EagerReadLock(params int[] lockIds)
        => throw new NotSupportedException("Test fake — EagerReadLock not implemented");

    public Guid InstanceId { get; } = Guid.NewGuid();

    public int CreatedThreadId => Environment.CurrentManagedThreadId;
}
