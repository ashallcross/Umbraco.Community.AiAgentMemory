# Cogworks.UmbracoAI.AgentMemory

> Persistent memory and learning layer for Umbraco's AI agent stack — agents that get better every time they run.

[![NuGet](https://img.shields.io/nuget/v/Cogworks.UmbracoAI.AgentMemory.svg)](https://www.nuget.org/packages/Cogworks.UmbracoAI.AgentMemory/)
[![Umbraco](https://img.shields.io/badge/Umbraco-17-blue.svg)](https://umbraco.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## What it does

Adds a memory and learning layer on top of Umbraco's existing AI agent runtime. Agents can:

- Remember what they did in past runs
- Learn from editor feedback (👍 / 👎 with optional comments)
- Retrieve relevant past runs when handling similar inputs
- Get visibly better over time without prompt edits

Built as middleware on Umbraco.AI's chat client pipeline. Composes with Umbraco.Automate's agent action — no changes to how editors author flows.

## Status

> ⚠ **Pre-release.** v0.1 in active development for Codegarden 2026 launch.

## Install

```bash
dotnet add package Cogworks.UmbracoAI.AgentMemory --prerelease
```

Requires:
- Umbraco CMS 17.3+
- Umbraco.AI.Agent 1.7+
- Umbraco.AI.Search (optional, for vector retrieval)
- .NET 10.0

## Quick start

```csharp
// In your Composer or Program.cs (the package auto-registers via IComposer)
// No additional configuration needed beyond appsettings.

// appsettings.json
{
  "Cogworks": {
    "AgentMemory": {
      "MemoryEnabledByDefault": false,
      "TopKMemories": 5,
      "MaxMemoryAgeDays": 90
    }
  }
}
```

Then opt-in per-agent in the backoffice (or via inline agent config).

## Architecture

See [docs](docs/) for full details. High-level shape:

```
┌────────────────────────────────────────────┐
│  Umbraco backoffice / Automate canvas      │
│  Editor configures agents, runs flows      │
└──────────┬─────────────────────────────────┘
           │
           ▼
┌────────────────────────────────────────────┐
│  Umbraco.AI agent runtime                  │
│  IChatClient pipeline                      │
│  ┌──────────────────────────────────────┐  │
│  │  MemoryInjectionMiddleware (ours)    │  │
│  │  • Retrieves relevant past runs      │  │
│  │  • Injects "Lessons from past runs"  │  │
│  │    as system message                 │  │
│  └──────────────────────────────────────┘  │
│  Real LLM call                             │
└──────────┬─────────────────────────────────┘
           │
           ▼
┌────────────────────────────────────────────┐
│  Cogworks.UmbracoAI.AgentMemory            │
│  • IAgentRunStore (run history)            │
│  • IAgentFeedbackService (👍/👎 + comment) │
│  • IMemoryRetriever (vector search)        │
└────────────────────────────────────────────┘
```

## Project structure

```
Cogworks.UmbracoAI.AgentMemory/        # The package (ships to NuGet)
├── Composing/                          # IComposer for DI registration
├── Configuration/                      # Bound options POCO
├── Persistence/                        # EF entities + migrations
├── Runs/                               # IAgentRunStore + DTOs
├── Feedback/                           # IAgentFeedbackService + DTOs
├── Memory/                             # IMemoryRetriever + IMemoryDigestService
├── Middleware/                         # IChatClient memory injection
├── Web/Api/                            # Backoffice API endpoints
└── Client/                             # Lit frontend (Vite + TypeScript)

Cogworks.UmbracoAI.AgentMemory.TestSite/   # Local Umbraco host with Clean starter kit
Cogworks.UmbracoAI.AgentMemory.Tests/      # NUnit unit tests
```

## Development

```bash
# Build everything
dotnet build Cogworks.UmbracoAI.AgentMemory.slnx

# Run tests (always specify .slnx — NEVER bare `dotnet test`)
dotnet test Cogworks.UmbracoAI.AgentMemory.slnx

# Run the TestSite (with Clean starter kit)
dotnet run --project Cogworks.UmbracoAI.AgentMemory.TestSite

# Frontend (Lit + Vite)
cd Cogworks.UmbracoAI.AgentMemory/Client
npm install
npm run watch    # rebuilds on changes
npm run build    # production build to ../wwwroot/App_Plugins/CogworksUmbracoAIAgentMemory/
npm test         # web-test-runner
```

## Repo split

This is the **public** repo — package source, tests, TestSite, docs.

Planning artifacts (BMAD docs, story specs, retrospectives) live in the **private** sibling repo:

```
~/Documents/Cogworks.UmbracoAI.AgentMemory/             # public, this repo
~/Documents/Cogworks.UmbracoAI.AgentMemory-planning/    # private, planning
```

`_bmad-output/`, `_bmad/`, `.agents/`, `.claude/` are symlinks from the public repo into the private repo. The public `.gitignore` excludes those paths so planning never accidentally commits to the public repo.

## License

[MIT](LICENSE) — fork, contribute, ship.

## Contributing

PRs welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) (TBC).

## Related projects

- [Umbraco.AI](https://github.com/umbraco/Umbraco.AI) — official AI integration layer
- [Umbraco.Automate](https://github.com/umbraco/Umbraco.Automate) — official automation platform (private)
- [AgentRun.Umbraco](https://github.com/ashallcross/AgentRun) — predecessor experiment, now in maintenance

## Support

- GitHub Issues: bugs and feature requests
- [Cogworks](https://www.wearecogworks.com) — commercial support and bespoke implementations
