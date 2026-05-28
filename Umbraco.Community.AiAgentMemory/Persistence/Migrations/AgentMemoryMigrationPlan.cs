using Umbraco.Cms.Core.Packaging;

namespace Umbraco.Community.AiAgentMemory.Persistence.Migrations;

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
        => From(string.Empty)
            // Story 1.1 (2026-05-10) — single batched step creates both package
            // tables (cogworks_agent_memory_feedback + cogworks_agent_memory_entries).
            // GUID is stable forever — once shipped, future schema changes append
            // a new step with a new GUID, NEVER mutate this one.
            .To<AddAgentMemorySchema>("8B3A4D6E-1F92-4C5B-A7D8-9E0F1B2C3D4A");
}
