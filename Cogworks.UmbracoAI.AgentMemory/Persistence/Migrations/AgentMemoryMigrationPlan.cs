using Umbraco.Cms.Core.Packaging;

namespace Cogworks.UmbracoAI.AgentMemory.Persistence.Migrations;

/// <summary>
/// Package migration plan. Auto-discovered by Umbraco's assembly scanning —
/// no explicit registration in <see cref="Composing.AgentMemoryComposer"/> needed.
/// </summary>
/// <remarks>
/// Steps are added one-at-a-time in the sprint plan. Each step gets a fresh GUID;
/// once a GUID has shipped to adopters it MUST NOT change (Umbraco tracks executed
/// migrations by GUID, not by class name).
/// </remarks>
public sealed class AgentMemoryMigrationPlan : PackageMigrationPlan
{
    public AgentMemoryMigrationPlan()
        : base(Constants.MigrationPlanName)
    {
    }

    protected override void DefinePlan()
    {
        // Week 1 — first migration adds run history table.
        // Replace this empty plan with .To<AddAgentRunsTable>("...GUID...") once the
        // migration class is implemented.
        From(string.Empty);

        // Future:
        // .To<AddAgentRunsTable>("12345678-1234-1234-1234-123456789012")
        // .To<AddAgentRunFeedbackTable>("23456789-2345-2345-2345-234567890123")
    }
}
