# Umbraco.Community.AiAgentMemory

> Persistent memory + learning layer for Umbraco's AI agent stack — agents that get better every time editors give them feedback.

[![NuGet](https://img.shields.io/nuget/v/Umbraco.Community.AiAgentMemory.svg)](https://www.nuget.org/packages/Umbraco.Community.AiAgentMemory/)
[![Umbraco](https://img.shields.io/badge/Umbraco-17.3+-blue.svg)](https://umbraco.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

When an editor thumbs-down an AI agent's output and explains why, the package writes that feedback as searchable memory and injects it into the agent's next similar run — suppressing false positives and reusing validated patterns without prompt edits or fine-tuning.

> **v0.1.0 — initial release.** Built for Codegarden 2026. See [CHANGELOG.md](CHANGELOG.md) for what's in the box.

---

## Prerequisites

Install and configure these **before** adding the package — the package composes on top of them and silently no-ops if the embedding profile or vector store isn't reachable.

| Package | Version | Notes |
|---|---|---|
| `Umbraco.Cms` | **17.3.2** | Lower bound. .NET 10 TFM (`net10.0`). |
| `Umbraco.AI` | **1.10.0** | Façade meta-package — pulls in `Umbraco.AI.Core` + provider transitives. |
| `Umbraco.AI.Agent` | **1.9.0** | Façade meta-package — pulls in agent runtime + chat-client middleware contracts. |
| `Umbraco.AI.Search` | **1.0.0-beta3** | ⚠ **Prerelease — must NOT float.** Vector store + embedding generator. |
| `Microsoft.EntityFrameworkCore` | **10.0.7+** | Floor pin propagated by the package nupkg. |

**One-time backoffice setup** before installing the package:

1. **AIConnection** — create at least one provider connection (OpenAI / Anthropic) via the Umbraco backoffice **AI section → Connections**. API keys live in the host DB, never in `appsettings.json`.
2. **Embedding AIProfile** — create one profile with **capability = Embedding** (e.g. backed by OpenAI's `text-embedding-3-small`). This is what the retriever uses to compare past runs against new inputs.

> Without a configured embedding profile, the memory retriever silently no-ops — agents still run, but no memory is injected or learned.

---

## Quick start: Brand Voice Audit Template

The fastest way to see the memory loop work end-to-end is the **Brand Voice Audit Template** that ships in the [`templates/`](templates/) folder. It's a complete Umbraco.Automate workflow + agent + seed content that demonstrates the "agents get better with feedback" core promise.

1. **Import the template into Umbraco.Automate** — full step-by-step in [`templates/brand-audit/README.md`](templates/brand-audit/README.md).
2. **Run the agent** against the seeded Northwind Trails articles. It flags 12+ brand-voice issues, some of which are actually intentional brand idioms (false positives).
3. **Submit 👎 + an explanatory comment** on 2–3 flagged idioms via the AI Agent Feedback widget on the Run Detail modal.
4. **Run the agent again** against the same articles. The agent now sees your feedback as injected memory bullets and suppresses the taught idioms — flag count drops, brand voice preserved.

For batched multi-article workflows, see the sibling [`templates/brand-audit-batch/README.md`](templates/brand-audit-batch/README.md) — same agent, demonstrates the compounding effect across a corpus.

---

## ⚠ Memory is opt-in — read this before enabling

> **Memory enables per-agent, never globally.** There is no `MemoryEnabledByDefault`-style switch. The only way to turn memory on for an agent is to add that agent's GUID to the `EnabledAgents` list in `appsettings.json` (or set it via configuration override).
>
> **Why this matters.** Memory entries are keyed by `agentId` and retrieved by semantic similarity. If you enabled memory globally, an agent for "Brand Voice Audit" could surface memory written for a "SEO Meta Description" agent — different intent, different correctness criteria, polluted output. v0.1 makes that footgun structurally impossible.
>
> **Action:** know the GUID of every agent you want to enable. Add them explicitly. An empty `EnabledAgents` list (the default) means memory is off for every agent, and the package is a runtime no-op.

---

## Install

```bash
dotnet add package Umbraco.Community.AiAgentMemory
```

The package auto-registers via `IComposer` — no `Program.cs` changes needed. Database tables (`cogworks_agent_memory_feedback`, `cogworks_agent_memory_entries`) are created on first boot via a `PackageMigrationPlan` and supported on both **SQLite** and **SQL Server**.

### `appsettings.json` — full example

Drop this block into your host's `appsettings.json` and replace the GUID placeholder with your agent's GUID (find it in the Umbraco backoffice **AI section → Agents** — column "Key"). Every field shown is optional; defaults are sensible for the Brand Voice Audit template and most starter workloads.

```jsonc
{
  "AiAgentMemory": {
    // Number of past-run memories injected per agent run. Range [1, 10].
    // Higher = more context, higher prompt cost. Default: 5.
    "TopKMemories": 5,

    // Memories older than this are excluded from retrieval. Days. >= 1.
    // ⚠ Adopter footgun: if you set Umbraco:AI:AuditLog:RetentionDays (default 14)
    // shorter than MaxMemoryAgeDays, memory entries outlive their source audit
    // rows. Memory still works, but you lose the back-link to the original run.
    "MaxMemoryAgeDays": 90,

    // Per-memory digest length cap (chars). >= 1. Bounds prompt size when
    // memories are injected. The indexer truncates joined run text before
    // embedding. Segment order is comment → response → prompt so the
    // highest-information segment (editor's teaching comment) always survives.
    "DigestMaxChars": 500,

    // Cosine-similarity threshold for retrieval. Range [0.0, 1.0]. Memories
    // below this score are not injected. Default 0.35 is tuned for OpenAI
    // text-embedding-3-small. If you use text-embedding-3-large, override
    // upward toward ~0.6 — its similarity band sits higher.
    "EligibilityThreshold": 0.35,

    // Pointer to an Embedding AIProfile configured via the backoffice AI
    // section UI. If null (default), falls back to the host's
    // DefaultEmbeddingProfileAlias. If neither resolves at runtime, memory
    // features silently no-op.
    "EmbeddingProfileAlias": null,

    // 🔑 THE ONLY WAY TO ENABLE MEMORY.
    // List of agent GUIDs the package will record feedback and inject memory
    // for. Empty (default) ⇒ memory off for every agent. Add the GUID of each
    // agent you've vetted for memory injection. Duplicate GUIDs, Guid.Empty,
    // and null collection all surface as OptionsValidationException at first
    // read — not silently ignored.
    "EnabledAgents": [
      "00000000-0000-0000-0000-000000000000"  // ← replace with your agent's GUID
    ],

    "VectorIndex": {
      // Index alias the package uses inside Umbraco.AI.Search's vector store.
      // Default fits ~all adopters; override only for multi-tenant partitioning.
      "Alias": "cogworks-agent-memory"
    }
  }
}
```

**What validation enforces at first read** (via `IValidateOptions<AgentMemoryOptions>` — all failures accumulate into one `OptionsValidationException`, so you fix them in one boot cycle, not one-at-a-time):

- `TopKMemories ∈ [1, 10]`
- `MaxMemoryAgeDays ≥ 1`
- `DigestMaxChars ≥ 1`
- `EligibilityThreshold ∈ [0.0, 1.0]`
- `EnabledAgents` is non-null, contains no duplicates, no `Guid.Empty`
- `VectorIndex` is non-null

---

## Architecture — where memory sits in the pipeline

The package composes on top of Umbraco.AI's `IChatClient` pipeline. The memory injection step runs **before** the real LLM call; the host's audit logging runs **after**. This ordering is the auditability promise: every memory-injected run is captured in upstream `AIAuditLog` rows the same as any other agent run — no parallel introspection surface, no shadow log to keep in sync.

```
┌──────────────────────────────────────────────────────────────┐
│  Editor surface                                              │
│  Umbraco backoffice • Umbraco.Automate canvas                │
└────────────────────────┬─────────────────────────────────────┘
                         │ triggers agent run
                         ▼
┌──────────────────────────────────────────────────────────────┐
│  Umbraco.AI agent runtime  —  IChatClient middleware chain   │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  MemoryInjectionMiddleware  (this package)             │  │
│  │  • Resolves agent GUID against EnabledAgents           │  │
│  │  • Embeds the incoming prompt                          │  │
│  │  • IMemoryRetriever → top-K cosine match               │  │
│  │  • Injects "Lessons from past runs" system message     │  │
│  └────────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  ⟶  Real LLM call (OpenAI / Anthropic / …)             │  │
│  └────────────────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  AIAuditingChatMiddleware  (upstream Umbraco.AI)       │  │
│  │  • Writes prompt + response + injected context to      │  │
│  │    Umbraco's AIAuditLog (single source of truth)       │  │
│  └────────────────────────────────────────────────────────┘  │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│  Editor opens Run Detail modal → AI Agent Feedback widget    │
│  Submits 👍 / 👎 + optional comment                          │
│                                                              │
│  IAgentFeedbackService → cogworks_agent_memory_feedback      │
│                                                              │
│  Background FeedbackIndexer:                                 │
│   1. Reads run via IAgentRunReader (composes on upstream     │
│      IAIAuditLogService — no parallel runs table)            │
│   2. Builds digest: comment → response → prompt              │
│      (truncated to DigestMaxChars; comment survives first)   │
│   3. Embeds via configured AIProfile                         │
│   4. Writes cogworks_agent_memory_entries + vector ref       │
└──────────────────────────────────────────────────────────────┘
```

**Key architectural decisions** (the load-bearing ones):

- **No parallel runs table.** The package reads run history via `IAgentRunReader` composed on Umbraco.AI's `IAIAuditLogService`. The upstream `AIAuditingChatMiddleware` is the single writer to `AIAuditLog` — we never duplicate that surface.
- **Vector storage owned by `Umbraco.AI.Search`.** The package is a tenant of that store under the index alias `cogworks-agent-memory`. We don't implement or operate vector infrastructure ourselves.
- **Truncation segment order** (`comment → response → prompt`) is deliberate — under realistic ~6KB joined run text, only the high-signal editor teaching comment is guaranteed to survive the `DigestMaxChars` cap and reach the embedding model.

---

## Memory Learning Wall dashboard

Once the package is installed and at least one agent has feedback indexed, adopters find a new tab under the Umbraco backoffice **AI section** named **"Memory Learning Wall"**:

```
/umbraco/section/ai/memory-learning-wall
```

The wall renders every memory entry the package has learned, grouped by agent, with:
- Editor's teaching comment (verbatim)
- Score / timestamp
- Truncated digest snippet
- Agent + run back-reference

Read-only at v0.1 — there's no delete UI yet. Re-index by submitting new feedback; old entries age out via `MaxMemoryAgeDays`.

The wall only renders if the host has Umbraco.AI installed (the section gate is satisfied by Umbraco.AI's section registration). Adopters running the package without Umbraco.AI installed are an unsupported configuration — memory injection has nothing to compose on.

---

## Backoffice API surface

The widget POSTs and dashboard reads are served from a versioned backoffice Management API:

```
/umbraco/management/api/v1/cogworks-agent-memory/
```

This route prefix is rename-stable across the package's brand boundary — the `cogworks-agent-memory` segment is the package-owned anchor. Hosts already running Umbraco.AI surfaces will see the package's Swagger document registered alongside the standard Management API docs.

---

## Repo layout

```
Umbraco.Community.AiAgentMemory/        # Package source (ships to NuGet)
├── Composing/                          # IComposer DI registration
├── Configuration/                      # AgentMemoryOptions POCO + validator
├── Persistence/                        # EF Core entities + NPoco migrations
├── Runs/                               # IAgentRunReader (composes on IAIAuditLogService)
├── Feedback/                           # IAgentFeedbackService + FeedbackIndexer
├── Memory/                             # IMemoryRetriever + IMemoryDigestService
├── Middleware/                         # MemoryInjectionMiddleware (IChatClient)
├── Web/Api/                            # Backoffice Management API endpoints
└── Client/                             # Lit + Vite frontend (widget + dashboard)

Umbraco.Community.AiAgentMemory.TestSite/   # Local Umbraco host (Clean starter kit)
Umbraco.Community.AiAgentMemory.Tests/      # NUnit unit + integration tests
templates/
├── brand-audit/                            # Single-article workflow + agent + seed content
└── brand-audit-batch/                      # Multi-article For Each variant
```

---

## Development

```bash
# Build everything
dotnet build Umbraco.Community.AiAgentMemory.slnx

# Run tests (always specify .slnx — bare `dotnet test` fails with MSB1011)
dotnet test Umbraco.Community.AiAgentMemory.slnx

# Run the TestSite (Clean starter kit, seeded Northwind content)
dotnet run --project Umbraco.Community.AiAgentMemory.TestSite

# Frontend (Lit + Vite — outputs to wwwroot/App_Plugins/)
cd Umbraco.Community.AiAgentMemory/Client
npm install
npm run watch    # rebuilds on changes
npm run build    # production build
npm test         # web-test-runner + axe-core
```

The `wwwroot/App_Plugins/` bundle is committed alongside `.ts` source — run `npm run build` before committing any frontend change.

---

## v0.1 scope and known limitations

v0.1 is the Codegarden 2026 demo release. The core memory loop (feedback → index → retrieve → inject) ships and is exercised end-to-end by the Brand Voice Audit Template. Known limitations:

- **Run Detail modal opens at only one iteration per batched run.** Affects the batch template's editorial UX. Workaround documented in [`templates/brand-audit-batch/README.md`](templates/brand-audit-batch/README.md#known-limitations-v01). Iteration picker tracked for v0.1.x.
- **No delete UI on the Memory Learning Wall.** Read-only at v0.1; entries age out via `MaxMemoryAgeDays`. Delete UI deferred pending adopter signal.
- **Per-iteration `RunId` semantic.** The `RunId` column on the feedback + memory entries tables carries dual semantics depending on submission origin (legacy ThreadId rows vs picker-selected per-iteration RunId rows). Transparent to adopters; relevant only for direct DB inspection. v0.2 will rename the column.

For the full deferred-work queue, see the planning repo (private — Cogworks internal).

---

## Telemetry and data

The package collects **no telemetry**. All data — feedback rows, memory entries, embeddings, audit-log references — stays in the adopter's own Umbraco database and the adopter's own `Umbraco.AI.Search` vector store. Embedding API calls go directly from the host to the configured `AIConnection` provider (OpenAI / Anthropic / etc.); the package never proxies them.

API keys are read from the Umbraco-managed `AIConnection` records, never from `appsettings.json` or environment variables this package owns.

---

## Support and contributing

- **Issues + feature requests:** [GitHub Issues](https://github.com/ashallcross/Umbraco.Community.AiAgentMemory/issues). Best-effort response within one week.
- **Commercial support / bespoke implementations:** [Cogworks](https://www.wearecogworks.com).
- **PRs welcome.** Open an issue first if the change is non-trivial so we can sanity-check direction before you spend the time.

## License

[MIT](LICENSE). Fork, contribute, ship.

## Acknowledgements

- **Umbraco HQ** — for shipping [Umbraco.AI](https://github.com/umbraco/Umbraco.AI) and [Umbraco.Automate](https://umbraco.com/products/umbraco-automate/) and creating a credible upstream surface for community packages to compose on.
- **[AgentRun](https://github.com/ashallcross/AgentRun)** — predecessor experiment that proved the editor-feedback-as-training-signal pattern was worth productising. Now in maintenance; this package is its successor.

## Related projects

- [Umbraco.AI](https://github.com/umbraco/Umbraco.AI) — official AI integration layer (chat clients, profiles, audit logging, vector store)
- [Umbraco.Automate](https://umbraco.com/products/umbraco-automate/) — official workflow + automation platform
- [Umbraco.Community.AiVisibility](https://github.com/ashallcross/Umbraco.Community.AiVisibility) — sibling community package, AI-search visibility audit + llms.txt generation
