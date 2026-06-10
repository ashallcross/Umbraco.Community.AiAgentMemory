# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Planned for v0.1.x patch releases

- Iteration picker in the Run Detail modal for batched workflow runs (closes batch-template limitation #1).
- Delete UI on the Memory Learning Wall dashboard.
- Rename `RunId` column → `ThreadId` + add explicit `IterationRunId` column (gated on migration up-then-down test infrastructure).
- Drop EF Core 10.0.7 floor pin once `Umbraco.AI` nuspec declares it upstream.
- Drop local-fork preview pins on `Umbraco.AI*` once the Metadata-propagation patch lands upstream.

---

## [0.1.0] — 2026-05-29

Initial public release. Built for Codegarden 2026.

### Added

**Core memory loop**
- `IAgentFeedbackService` + `cogworks_agent_memory_feedback` table — editors submit 👍/👎 + optional comment via the AI Agent Feedback widget on the Run Detail modal.
- `FeedbackIndexer` — background indexer reads feedback, builds a digest (segment order `comment → response → prompt` so the editor's teaching comment survives `DigestMaxChars` truncation), embeds via the configured `AIProfile`, and writes `cogworks_agent_memory_entries`.
- `IMemoryRetriever` + `MemoryInjectionMiddleware` — `IChatClient` middleware that retrieves top-K cosine-similar memories per agent run and injects them as a "Lessons from past runs" system message before the LLM call.
- `IAgentRunReader` — composes on upstream `IAIAuditLogService`; groups audit-log rows by `Metadata["Umbraco.AI.Agent.RunId"]`. **No parallel runs table** — the package never duplicates upstream's audit surface.

**Configuration**
- `AgentMemoryOptions` bound to the `AiAgentMemory` section of `appsettings.json`.
- `IValidateOptions<AgentMemoryOptions>` — validates at first read; accumulates ALL failures into one `OptionsValidationException` so adopters fix every misconfiguration in one boot cycle.
- Per-agent opt-in via `EnabledAgents` GUID list — **the only way to enable memory.** No global on-switch; cross-agent memory pollution is structurally impossible.
- Sensible defaults: `TopKMemories=5`, `MaxMemoryAgeDays=90`, `DigestMaxChars=500`, `EligibilityThreshold=0.35` (tuned for OpenAI `text-embedding-3-small`).

**Backoffice surfaces**
- AI Agent Feedback widget — Lit element mounted on the Run Detail modal. Renders agent score + issues + suggestions in a `<uui-box>`, captures editor 👍/👎 + comment, POSTs to the package API.
- Memory Learning Wall dashboard — new tab under the Umbraco AI section at `/umbraco/section/ai/memory-learning-wall`. Lists every memory entry the package has learned, grouped by agent, with teaching comment + score + digest snippet.
- Versioned Management API at `/umbraco/management/api/v1/cogworks-agent-memory/` — rename-stable across the package's brand boundary.

**Templates**
- `templates/brand-audit/` — single-article Brand Voice Audit workflow + Northwind Brand Voice Auditor agent + brand-voice Context resource + seeded Northwind Trails content (`seed-content.zip`). Reproduces the FR44 demo arc end-to-end.
- `templates/brand-audit-batch/` — multi-article For Each variant of the same workflow. Demonstrates the cross-article generalisation effect: teach the agent on 2-3 articles, watch flag counts drop across an N-article batch.

**Persistence**
- `cogworks_agent_memory_feedback` + `cogworks_agent_memory_entries` tables (prefix is rename-stable; never touched by brand passes).
- `PackageMigrationPlan` with stable named GUIDs for idempotent migration execution. Per-step `TableExists` guards for partial-failure recovery.
- Cross-provider DDL via Umbraco's NPoco migration DSL — runs on **SQLite** and **SQL Server**.

**Composition**
- `AgentMemoryComposer : IComposer` — single composer wires the core service surface. Sibling `AgentMemoryBackofficeApiComposer` registers the Web/API + Swagger transport concerns.
- Captive-dependency avoidance — all DI registrations covered by a startup-validation fixture using descriptor inspection (or real `BuildServiceProvider(ValidateOnBuild=true, ValidateScopes=true)` carve-outs when graph resolution risk warrants).
- Lifetimes locked: `IAgentRunReader` = Singleton (matches upstream `IAIAuditLogService`); validators = Singleton via `TryAddEnumerable`; EF Core repositories = Scoped via `IEFCoreScopeProvider<AgentMemoryDbContext>`.

### Compatibility

- Target framework: `net10.0`
- Tested against `Umbraco.Cms` **17.3.2**, `Umbraco.AI` **1.10.0**, `Umbraco.AI.Agent` **1.9.0**, `Umbraco.AI.Search` **1.0.0-beta3**.
- `Microsoft.EntityFrameworkCore` floor pinned at **10.0.7** in the package nupkg (DRIFT-5.3-5 — addresses the upstream nuspec gap where the package would otherwise resolve to 10.0.4 via the `Umbraco.Cms` transitive graph and runtime-fail with `FileNotFoundException` on the strong-named 10.0.7.0 assembly).
- Requires a pre-configured `AIConnection` (provider + API key) and embedding `AIProfile` (capability=Embedding) in the host's Umbraco AI section. Without them, memory features silently no-op rather than throwing.

### Known limitations

- Run Detail modal opens at only one iteration per batched run (For Each iterations don't surface as sub-rows in Automate's Runs table — workaround in [`templates/brand-audit-batch/README.md`](templates/brand-audit-batch/README.md#known-limitations-v01)).
- Memory Learning Wall is read-only — no delete UI.
- Memory loop is agent-keyed, not content-keyed — feedback on one article surfaces as injected memory for any semantically-similar agent run, including different articles. This is the cross-article generalisation effect the demo relies on, but worth knowing if you expect per-content isolation.

### Security

- No third-party telemetry. All feedback rows, memory entries, embeddings, and audit-log references stay in the adopter's own Umbraco database and `Umbraco.AI.Search` vector store.
- API keys read from Umbraco-managed `AIConnection` records — never from `appsettings.json` or environment variables this package owns.
- Editor-typed comment text is HTML-encoded on render (server-side `WebUtility.HtmlEncode` + Lit's automatic interpolation); the package never uses `unsafeHTML()` on user-typed or run-record-derived content.

---

[Unreleased]: https://github.com/ashallcross/Umbraco.Community.AiAgentMemory/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ashallcross/Umbraco.Community.AiAgentMemory/releases/tag/v0.1.0
