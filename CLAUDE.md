# Project Instructions for Claude Code

Cogworks.UmbracoAI.AgentMemory — persistent memory + learning layer for Umbraco's AI agent stack.

## Two-Repo Development Split (Non-Negotiable)

This project is developed across two repositories:

- **Public repo** at `~/Documents/Cogworks.UmbracoAI.AgentMemory/` — package source, tests, TestSite, docs, ships to NuGet
- **Private repo** at `~/Documents/Cogworks.UmbracoAI.AgentMemory-planning/` — BMAD planning artefacts, story specs, retrospectives, sprint status, deferred work, Claude skills

The folders `_bmad-output/`, `_bmad/`, `.agents/`, and `.claude/` inside the public repo are symlinks pointing at the private repo. The public repo's `.gitignore` excludes those paths.

### Commit routing

| What changed | Which repo to commit in |
|---|---|
| Files under `Cogworks.UmbracoAI.AgentMemory/`, `.TestSite/`, `.Tests/` | Public |
| `docs/`, README.md, LICENSE, NOTICE, csproj files, .gitignore | Public |
| Anything under `_bmad-output/`, `_bmad/`, `.agents/`, `.claude/` (reached via symlink) | **Private** — cd to `~/Documents/Cogworks.UmbracoAI.AgentMemory-planning/` to commit |

A typical story completion produces two separate commits across the two repos. This is correct and expected. Never combine them.

### Red flags

- `git status` in the public repo showing any `_bmad-output/`, `_bmad/`, `.agents/`, or `.claude/` content → symlinks or `.gitignore` are broken; stop and investigate
- An agent proposing to copy files between the repos "for simplicity" → violates the split

## Other Rules

- **Tests**: `dotnet test Cogworks.UmbracoAI.AgentMemory.slnx` — never bare `dotnet test` (multi-project repo; bare call fails with MSB1011)
- **Frontend tests**: `npm test` from `Cogworks.UmbracoAI.AgentMemory/Client/`
- **Frontend build before commit**: `npm run build` from `Cogworks.UmbracoAI.AgentMemory/Client/` so `wwwroot/App_Plugins/` is current
- **TestSite**: uses Cogworks Clean starter kit (Clean + Clean.Core 7.0.5) — both packages required, version-locked
- **TestSite scaffolding**: always start from `dotnet new umbraco`, never hand-roll. Delete the template's nested `Directory.Packages.props` (it shadows the repo root CPM and causes silent version-pin drift). Lesson: Story 1.1 hand-roll caused boot crash; the re-scaffold-from-template fix is the canonical recipe (DRIFT-1.1-3 in `1-1-outcome.md`).
- **TFM**: net10.0
- **Umbraco**: 17.3.2 (pinned via Directory.Packages.props)
- **Central Package Management**: yes — all package versions live in Directory.Packages.props
- **Composer-only DI registration** — never Program.cs. Single `AgentMemoryComposer : IComposer` is the entry point
- **API route prefix**: `/umbraco/cogworks-agent-memory/api/` for backoffice endpoints
- **Database tables**: prefix `cogworks_agent_memory_` for our tables to avoid collision with Umbraco core / Umbraco.AI tables
- **Migration pattern**: `PackageMigrationPlan` with named GUID steps, mirroring AgentRun's `AgentRunMigrationPlan`
- **Spike harness lifetime**: throwaway harnesses outside the repo's accountability boundary (e.g. `~/Documents/Spike0A-TestSite/` for Epic 0) are NOT subject to the public-repo "zero diff" cleanup contract. Keep them alive until the next mass-context-loss boundary (epic retro), not at story cleanup. Lesson: Story 0.A 2026-05-05 prematurely deleted the harness; Story 0.B reversed that policy.
- **Test-density principle (AR30)**: match test density to actual risk — happy path + 1 override path + 1-2 genuine edge cases per surface. Skip mirror-pattern tests when underlying helper is already pinned.

## Architecture

The package adds plumbing on top of Umbraco.AI's runtime — never replaces it.

| Layer | Owner |
|---|---|
| Agent runtime, tool loop | Umbraco.AI |
| Workflow canvas | Umbraco.Automate |
| Vector store + embedding generator | Umbraco.AI.Search (we are a tenant under index alias `cogworks-agent-memory`) |
| Run history **persistence** | **Upstream — `AIAuditingChatMiddleware` writes to `AIAuditLog`. We do NOT own a runs table.** |
| Run history **reads** | **Us (`IAgentRunReader`, composes on `IAIAuditLogService`, groups by `Metadata["Umbraco.AI.Agent.RunId"]`)** |
| Editor feedback | **Us (`IAgentFeedbackService` + `cogworks_agent_memory_feedback` table)** |
| Memory entries (digest + embedding) | **Us (`cogworks_agent_memory_entries` table, decoupled from audit-log retention)** |
| Memory retrieval & injection | **Us (`IMemoryRetriever`, `MemoryInjectionMiddleware`)** |

**Architectural pivot (2026-05-03, locked in `15-upstream-reuse-investigation.md`):** earlier drafts proposed an `IAgentRunStore` write path and AGUI subscriber. Both are gone. Upstream's auditing middleware already captures everything we need; we compose, we don't duplicate. Any downstream agent or document still referencing `IAgentRunStore` ownership should be treated as pre-pivot and corrected.

## Key planning docs

In `~/Documents/Cogworks.UmbracoAI.AgentMemory-planning/_bmad-output/planning-artifacts/`:

- `00-vision.md` — the thesis
- `06-architecture-v1.md` — interface signatures, flow diagrams
- `11-week-by-week-plan.md` — 4-week sprint to Codegarden
- `13-demo-use-case-brainstorm.md` — selected demo (Brand Audit Loop with Memory)

## Inherited project conventions (carried from AgentRun)

- Always specify slnx in `dotnet test`
- Stories must include "Failure & Edge Cases" section
- Prefer GUIDs over integer node IDs in tool contracts
- Comment limitations inline (XML doc + adopter docs, not just story spec)
- Verbatim invariant lock during Phase 1
- Manual E2E testing finds seam bugs that unit suites miss
- Research before fix (search Umbraco docs/forums/GitHub)
- Context efficiency (write intermediate results to disk, read selectively)
- Integration mindset (this is a CMS integration, not greenfield)
- Follow existing conventions (don't invent new tracking sections on the fly)
