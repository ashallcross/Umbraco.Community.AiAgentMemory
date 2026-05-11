using Cogworks.UmbracoAI.AgentMemory.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cogworks.UmbracoAI.AgentMemory.Tests.Configuration;

/// <summary>
/// AR3 contract pin for <see cref="AgentMemoryOptionsValidator"/>:
///   - happy path returns Success on default-initialised options
///   - parameterised 6-case invariant pin (one per AC3 table row)
///   - EnabledAgents accumulates BOTH duplicate + Guid.Empty failures
///   - end-to-end real-BuildServiceProvider asserts
///     OptionsValidationException fires on first IOptionsMonitor read.
/// </summary>
[TestFixture]
public class AgentMemoryOptionsValidatorTests
{
    [Test]
    public void Validate_DefaultOptions_ReturnsSuccess()
    {
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions();

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Succeeded, Is.True,
            "Default-initialised AgentMemoryOptions must satisfy every invariant; "
            + $"got failures: {string.Join(" | ", result.Failures ?? new List<string>())}");
        Assert.That(result.Failed, Is.False);
    }

    [Test]
    public void Validate_NonDefaultName_ReturnsSkip()
    {
        // v0.1 binds the default name only; non-default named-options
        // instances are not ours to validate (forward-compat for adopters
        // who might layer named options later).
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { TopKMemories = 0 }; // would fail if not skipped

        var result = validator.Validate("some-other-instance", options);

        Assert.That(result.Skipped, Is.True);
    }

    [TestCase(0, "AgentMemoryOptions.TopKMemories must be in [1, 10]; got '0'", TestName = "TopKMemories below range fails")]
    [TestCase(99, "AgentMemoryOptions.TopKMemories must be in [1, 10]; got '99'", TestName = "TopKMemories above range fails")]
    public void Validate_TopKMemoriesOutOfRange_ReturnsFailWithExpectedMessage(int mutatedValue, string expectedMessage)
    {
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { TopKMemories = mutatedValue };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Exactly(1).EqualTo(expectedMessage));
    }

    [Test]
    public void Validate_MaxMemoryAgeDaysBelowOne_ReturnsFailWithExpectedMessage()
    {
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { MaxMemoryAgeDays = 0 };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures,
            Has.Exactly(1).EqualTo("AgentMemoryOptions.MaxMemoryAgeDays must be >= 1; got '0'"));
    }

    [Test]
    public void Validate_DigestMaxCharsBelowOne_ReturnsFailWithExpectedMessage()
    {
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { DigestMaxChars = 0 };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures,
            Has.Exactly(1).EqualTo("AgentMemoryOptions.DigestMaxChars must be >= 1; got '0'"));
    }

    [TestCase(-0.1, "AgentMemoryOptions.EligibilityThreshold must be in [0.0, 1.0]; got '-0.1'", TestName = "EligibilityThreshold below 0.0 fails")]
    [TestCase(1.5, "AgentMemoryOptions.EligibilityThreshold must be in [0.0, 1.0]; got '1.5'", TestName = "EligibilityThreshold above 1.0 fails")]
    public void Validate_EligibilityThresholdOutOfRange_ReturnsFailWithExpectedMessage(double mutatedValue, string expectedMessage)
    {
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { EligibilityThreshold = mutatedValue };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Exactly(1).EqualTo(expectedMessage));
    }

    [Test]
    public void Validate_EnabledAgentsNull_ReturnsFailWithExpectedMessage()
    {
        // Programmatic-mutation guard: JSON binding produces an empty list
        // when the key is unset, but `services.PostConfigure(o => o.EnabledAgents = null!)`
        // would create a third state. Validator surfaces it as a failure so
        // downstream consumers cannot NRE on iterate.
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { EnabledAgents = null! };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures,
            Has.Exactly(1).EqualTo("AgentMemoryOptions.EnabledAgents must not be null"));
    }

    [Test]
    public void Validate_VectorIndexNull_ReturnsFailWithExpectedMessage()
    {
        // Symmetric to the EnabledAgents null guard: JSON binder always
        // allocates a fresh VectorIndexOptions instance, but programmatic
        // mutation could null it. Validator surfaces it as a failure.
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions { VectorIndex = null! };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures,
            Has.Exactly(1).EqualTo("AgentMemoryOptions.VectorIndex must not be null"));
    }

    [Test]
    public void Validate_EnabledAgentsInvariants_SurfacesBothFailures()
    {
        // Mutate so EnabledAgents = [ Guid.Empty, agentA, agentA ]: one empty
        // GUID + one duplicate of agentA. The validator MUST accumulate (not
        // short-circuit) so adopters see both problems in one boot cycle.
        var agentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions
        {
            EnabledAgents = new List<Guid> { Guid.Empty, agentA, agentA },
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures!.Count(), Is.EqualTo(2),
            "Both Guid.Empty and duplicate-GUID failures must accumulate into one OptionsValidationException");
        Assert.That(result.Failures, Has.Some.Matches<string>(s =>
            s.Contains("must not contain Guid.Empty")));
        Assert.That(result.Failures, Has.Some.Matches<string>(s =>
            s.Contains("must not contain duplicate GUIDs")));
    }

    [Test]
    public void Validate_MultipleInvariantViolations_AccumulatesAllFailures()
    {
        // Three distinct fields broken; validator must NOT short-circuit on
        // first failure. Adopter sees all three in one exception.
        var validator = new AgentMemoryOptionsValidator();
        var options = new AgentMemoryOptions
        {
            TopKMemories = 99,
            MaxMemoryAgeDays = 0,
            EligibilityThreshold = 1.5,
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures!.Count(), Is.EqualTo(3));
    }

    [Test]
    public void OptionsValidator_OnInvalidConfig_ThrowsOptionsValidationExceptionAtFirstRead()
    {
        // End-to-end pin: real IServiceCollection + real Configure<> +
        // TryAddEnumerable + real BuildServiceProvider, then dereference
        // IOptionsMonitor<>.CurrentValue and assert OptionsValidationException
        // fires through the OptionsFactory pipeline (NOT at registration time,
        // NOT at provider build — at first read). This is the carve-out per
        // AC7 forward contract: graph-resolution risk distinct from descriptor
        // inspection, so a real provider is warranted.

        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["Cogworks:AgentMemory:MaxMemoryAgeDays"] = "0",
            ["Cogworks:AgentMemory:TopKMemories"] = "99",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AgentMemoryOptions>(
            configuration.GetSection(Cogworks.UmbracoAI.AgentMemory.Constants.ConfigSection));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AgentMemoryOptions>, AgentMemoryOptionsValidator>());

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AgentMemoryOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = monitor.CurrentValue;
        });

        Assert.That(ex!.OptionsType, Is.EqualTo(typeof(AgentMemoryOptions)));
        Assert.That(ex.Failures, Has.Exactly(1).EqualTo(
            "AgentMemoryOptions.MaxMemoryAgeDays must be >= 1; got '0'"));
        Assert.That(ex.Failures, Has.Exactly(1).EqualTo(
            "AgentMemoryOptions.TopKMemories must be in [1, 10]; got '99'"));
    }
}
