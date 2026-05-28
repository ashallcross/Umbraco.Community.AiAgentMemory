using Umbraco.Community.AiAgentMemory.Configuration;
using Umbraco.Community.AiAgentMemory.Feedback;
using Umbraco.Community.AiAgentMemory.Memory;
using Umbraco.Community.AiAgentMemory.Persistence.Entities;
using Umbraco.Community.AiAgentMemory.Persistence.Repositories;
using Umbraco.Community.AiAgentMemory.Runs;
using Umbraco.Community.AiAgentMemory.Web.Api;
using Umbraco.Community.AiAgentMemory.Web.Api.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.HostedServices;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;

namespace Umbraco.Community.AiAgentMemory.Tests.Memory;

/// <summary>
/// Story 3.1 Task 6 — integration spine. Drives controller → indexer →
/// mocked upstream via a real <see cref="IServiceProvider"/>. Bypasses the
/// composer (which depends on Umbraco's full host) and instead registers a
/// minimal substitute graph; this test pins the wiring between the layers
/// without spinning up a real <c>QueuedHostedService</c>.
/// </summary>
[TestFixture]
public class FeedbackIndexerEnqueueIntegrationTests
{
    [Test]
    public async Task EnqueueIndex_FromController_RoutesToBackgroundTaskQueue_WhichInvokesIndexAsync()
    {
        // 1. Mocks for the per-call resolution path.
        var repository = Substitute.For<IMemoryEntryRepository>();
        repository.FindByRunIdAndAgentIdAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntryEntity?>(null));
        repository.AddAsync(Arg.Any<MemoryEntryEntity>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var runReader = Substitute.For<IAgentRunReader>();
        var agentId = Guid.NewGuid();
        runReader.GetRunsForThreadAsync("run-int", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>(new[]
            {
                new AgentRunRecord(
                    "run-int", agentId, 1,
                    DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow,
                    AgentRunStatus.Succeeded, null,
                    "[user] hello", "[assistant] hi", 10, 5,
                    "run-int", "user-1", "trace-1"),
            }));

        var feedbackService = Substitute.For<IAgentFeedbackService>();
        feedbackService.GetFeedbackForRunAsync("run-int", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunFeedback>>(new[]
            {
                new AgentRunFeedback(
                    Guid.NewGuid(), "run-int", agentId, FeedbackScore.ThumbsUp,
                    "looks good", Guid.NewGuid(), DateTime.UtcNow),
            }));

        var profileService = Substitute.For<IAIProfileService>();
        var profileId = Guid.NewGuid();
        profileService.GetProfileByAliasAsync("openai-embedding", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AIProfile?>(MakeProfile(profileId, "openai-embedding")));

        var embeddingService = Substitute.For<IAIEmbeddingService>();
        embeddingService.GenerateEmbeddingAsync(
                Arg.Any<Action<AIEmbeddingBuilder>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Embedding<float>(new float[] { 0.1f, 0.2f })));

        var vectorStore = Substitute.For<IAIVectorStore>();
        vectorStore.UpsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<IDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // 2. Fake IBackgroundTaskQueue captures the queued lambda + invokes
        //    it synchronously. The real QueuedHostedService would require a
        //    full host; this shortcut is enough to pin the wiring.
        Func<CancellationToken, Task>? captured = null;
        var queue = Substitute.For<IBackgroundTaskQueue>();
        queue.When(q => q.QueueBackgroundWorkItem(Arg.Any<Func<CancellationToken, Task>>()))
            .Do(call => captured = (Func<CancellationToken, Task>)call.Args()[0]);

        // 3. Service collection — minimal graph. Skip the composer entirely:
        //    AddUmbracoDbContext requires Umbraco's full host. We register
        //    exactly what FeedbackIndexer.IndexAsync resolves per-call.
        var services = new ServiceCollection();
        services.AddSingleton(runReader);
        services.AddSingleton(feedbackService);
        services.AddSingleton(repository);
        services.AddSingleton(profileService);
        services.AddSingleton(embeddingService);
        services.AddSingleton(vectorStore);
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new AgentMemoryOptions
        {
            EmbeddingProfileAlias = "openai-embedding",
            DigestMaxChars = 500,
        });
        var optionsMonitor = new StaticOptionsMonitor<AgentMemoryOptions>(options.Value);
        var aiOptions = Options.Create(new AIOptions());
        var indexer = new FeedbackIndexer(
            queue,
            sp.GetRequiredService<IServiceScopeFactory>(),
            optionsMonitor,
            aiOptions,
            TimeProvider.System,
            NullLogger<FeedbackIndexer>.Instance);

        // 4. Authenticated user resolves to a known Key.
        var securityAccessor = Substitute.For<IBackOfficeSecurityAccessor>();
        var security = Substitute.For<IBackOfficeSecurity>();
        var user = Substitute.For<IUser>();
        user.Key.Returns(Guid.NewGuid());
        security.CurrentUser.Returns(user);
        securityAccessor.BackOfficeSecurity.Returns(security);

        var controller = new AgentFeedbackController(
            feedbackService,
            indexer,
            securityAccessor,
            runReader,
            NullLogger<AgentFeedbackController>.Instance);

        // 5. POST → controller calls service.RecordFeedbackAsync → calls
        //    indexer.EnqueueIndex → captures the lambda on the fake queue.
        var result = await controller.PostAsync(
            new AgentFeedbackPostRequest("run-int", FeedbackScore.ThumbsUp, "looks good"),
            CancellationToken.None);

        Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Mvc.OkResult>(),
            "Controller must still return 200 OK after enqueueing.");
        Assert.That(captured, Is.Not.Null,
            "Controller must enqueue exactly one background work item via IBackgroundTaskQueue.");

        // 6. Invoke the captured lambda — simulates QueuedHostedService.
        await captured!(CancellationToken.None);

        // 7. Pin the wiring: repository.AddAsync called once; vector-store
        //    UpsertAsync called once with the cogworks-agent-memory index.
        await repository.Received(1).AddAsync(
            Arg.Is<MemoryEntryEntity>(e =>
                e.RunId == "run-int"
                && e.AgentId == agentId
                && e.IndexingStatus == (int)IndexingStatus.Embedded),
            Arg.Any<CancellationToken>());
        await vectorStore.Received(1).UpsertAsync(
            "cogworks-agent-memory",
            Arg.Any<string>(),
            Arg.Is<string?>(c => c == null),
            0,
            Arg.Any<ReadOnlyMemory<float>>(),
            Arg.Any<IDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    private static AIProfile MakeProfile(Guid id, string alias)
    {
        var profile = new AIProfile
        {
            Alias = alias,
            Name = "test-profile",
            ConnectionId = Guid.NewGuid(),
            Capability = AICapability.Embedding,
        };
        typeof(AIProfile).GetProperty(nameof(AIProfile.Id))!.SetValue(profile, id);
        return profile;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        private readonly T _value;
        public StaticOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
