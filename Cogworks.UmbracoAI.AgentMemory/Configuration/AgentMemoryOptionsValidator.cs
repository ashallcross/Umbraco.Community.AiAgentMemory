using System.Globalization;
using Microsoft.Extensions.Options;

namespace Cogworks.UmbracoAI.AgentMemory.Configuration;

/// <summary>
/// Validates <see cref="AgentMemoryOptions"/> at first read per AR3.
/// Registered via
/// <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IValidateOptions&lt;AgentMemoryOptions&gt;,
/// AgentMemoryOptionsValidator&gt;())</c> in
/// <see cref="Composing.AgentMemoryComposer"/>. Accumulates ALL failures
/// into a single <see cref="OptionsValidationException"/> so adopters see
/// every misconfiguration in one boot cycle, not iteratively.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.Options invokes this validator the first time
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> (or
/// <see cref="IOptions{TOptions}.Value"/>) is dereferenced. v0.1 does
/// NOT wire <c>ValidateOnStart</c> — first-read failure is sufficient
/// for the AR3 contract and avoids coupling the package's boot to
/// Umbraco's host startup lifecycle. Story 5.1 NFR-S8 audit may revisit.
/// </para>
/// <para>
/// Named-options support: this validator handles ONLY the default-named
/// options instance (v0.1 binds the default name only). Calls with a
/// non-default name return <see cref="ValidateOptionsResult.Skip"/> for
/// forward-compat with adopters who might layer named options later.
/// </para>
/// </remarks>
internal sealed class AgentMemoryOptionsValidator : IValidateOptions<AgentMemoryOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentMemoryOptions options)
    {
        // Skip named-options instances we don't own (forward-compat). Null
        // name is the framework's default in some paths — treat it as the
        // default-name bucket rather than skipping.
        if (!string.IsNullOrEmpty(name) && name != Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        // FR20 — TopKMemories ∈ [1, 10].
        if (options.TopKMemories < 1 || options.TopKMemories > 10)
        {
            failures.Add(
                $"AgentMemoryOptions.TopKMemories must be in [1, 10]; got '{options.TopKMemories.ToString(CultureInfo.InvariantCulture)}'");
        }

        // AR3 — MaxMemoryAgeDays >= 1.
        if (options.MaxMemoryAgeDays < 1)
        {
            failures.Add(
                $"AgentMemoryOptions.MaxMemoryAgeDays must be >= 1; got '{options.MaxMemoryAgeDays.ToString(CultureInfo.InvariantCulture)}'");
        }

        // AR3 — DigestMaxChars >= 1.
        if (options.DigestMaxChars < 1)
        {
            failures.Add(
                $"AgentMemoryOptions.DigestMaxChars must be >= 1; got '{options.DigestMaxChars.ToString(CultureInfo.InvariantCulture)}'");
        }

        // FR24 — EligibilityThreshold ∈ [0.0, 1.0]. NaN < 0.0 and NaN > 1.0
        // both return false, so the explicit double.IsNaN guard is required
        // to fail closed.
        if (double.IsNaN(options.EligibilityThreshold)
            || options.EligibilityThreshold < 0.0
            || options.EligibilityThreshold > 1.0)
        {
            failures.Add(
                $"AgentMemoryOptions.EligibilityThreshold must be in [0.0, 1.0]; got '{options.EligibilityThreshold.ToString(CultureInfo.InvariantCulture)}'");
        }

        // FR27 / FR38 — EnabledAgents invariants. Three distinct contracts:
        //   (a) Collection must not be null (programmatic mutation guard —
        //       JSON binding produces an empty list when the key is unset).
        //   (b) No Guid.Empty entries (caller error — placeholder value).
        //   (c) No duplicate GUIDs (duplicate-enable signals adopter error).
        // All three surface independently — adopters see every failure in one
        // pass when multiple apply.
        if (options.EnabledAgents is null)
        {
            failures.Add(
                "AgentMemoryOptions.EnabledAgents must not be null");
        }
        else
        {
            if (options.EnabledAgents.Any(g => g == Guid.Empty))
            {
                failures.Add(
                    "AgentMemoryOptions.EnabledAgents must not contain Guid.Empty");
            }

            if (options.EnabledAgents.Count != options.EnabledAgents.Distinct().Count())
            {
                failures.Add(
                    "AgentMemoryOptions.EnabledAgents must not contain duplicate GUIDs");
            }
        }

        // VectorIndex must not be null. JSON binding always allocates a fresh
        // VectorIndexOptions instance when the key is unset; this guard
        // catches programmatic-mutation abuse only.
        if (options.VectorIndex is null)
        {
            failures.Add(
                "AgentMemoryOptions.VectorIndex must not be null");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
