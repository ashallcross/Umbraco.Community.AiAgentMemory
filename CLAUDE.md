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
- **TFM**: net10.0
- **Umbraco**: 17.3.2 (pinned via Directory.Packages.props)
- **Central Package Management**: yes — all package versions live in Directory.Packages.props
- **Composer-only DI registration** — never Program.cs. Single `AgentMemoryComposer : IComposer` is the entry point
- **API route prefix**: `/umbraco/cogworks-agent-memory/api/` for backoffice endpoints
- **Database tables**: prefix `cogworks_agent_memory_` for our tables to avoid collision with Umbraco core / Umbraco.AI tables
- **Migration pattern**: `PackageMigrationPlan` with named GUID steps, mirroring AgentRun's `AgentRunMigrationPlan`

## Architecture

The package adds plumbing on top of Umbraco.AI's runtime — never replaces it.

| Layer | Owner |
|---|---|
| Agent runtime, tool loop | Umbraco.AI |
| Workflow canvas | Umbraco.Automate |
| Vector store for content | Umbraco.AI.Search |
| Run history persistence | **Us (`IAgentRunStore`)** |
| Editor feedback | **Us (`IAgentFeedbackService`)** |
| Memory retrieval & injection | **Us (`IMemoryRetriever`, `MemoryInjectionMiddleware`)** |

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
