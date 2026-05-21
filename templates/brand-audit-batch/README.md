# Brand Voice Audit Batch Demo

A multi-article variant of the Brand Voice Audit Template. Iterates over every article matching a configured name prefix and audits each one in sequence against the Brand Voice Auditor agent. Demonstrates the **memory loop's compounding effect across a batch** — teach the agent via feedback on 2-3 articles, then run the batch again and watch flag counts drop across the same articles.

> **Sibling template:** This batch workflow reuses the Brand Voice Auditor agent + Northwind Brand Voice Context resource shipped in `templates/brand-audit/`. Install both folders side-by-side.

## What this ships

| File | Purpose |
|---|---|
| `workflow.json` | Automate workflow definition: Find Content → For Each → Get Content → Run AI Agent → Notify Editor |
| `README.md` | This file |

**REUSES (do not modify):**
- `templates/brand-audit/agent.json` — Brand Voice Auditor agent (Context-driven per Story 4.7)
- `templates/brand-audit/brand-voice-context.json` — Northwind Brand Voice Context resource

## Import flow

1. **Install sibling template first.** If you haven't already, follow `templates/brand-audit/README.md` to import the Brand Voice Auditor agent + Northwind Brand Voice Context resource. The batch workflow won't work without them.
2. **Import this workflow.** Drop `workflow.json` into your Automate workflows folder OR use Automate's Import action via the backoffice.
3. **Verify the workflow appears in Automate** as `Brand Voice Audit Batch Demo` with alias `brand-voice-audit-batch-demo`.
4. **Trigger via Automate UI** → click Run on the workflow row.

## Two-batches demo arc

The memory loop requires editorial feedback between iterations to actually learn. The honest demo arc is **two batches with teaching between**:

1. **Batch 1 — baseline.** Trigger the workflow. The agent audits all matching articles (6 by default, per the `name="idiomatic"` prefix matching the sibling template's seed corpus). Each iteration produces an audit-log row with a score, flagged issues, and suggestions. Note the per-article flag counts.

2. **Submit feedback on 2-3 articles.** Open the Run Detail modal for the articles you want to teach (see § Known limitations below — only one iteration is reachable per modal at v0.1). Submit 👎 + canonical brand-voice comment template:

   > *"These are intentional Northwind Trails brand idioms, do not flag: '\<idiom-1\>', '\<idiom-2\>', '\<idiom-3\>'. Brand guideline: regional idioms like these are part of the voice, not breaches."*

3. **Wait ~30 seconds** for the FeedbackIndexer to write memory entries into the package's `cogworks_agent_memory_entries` table. Optionally check the Memory Learning Wall dashboard (`/umbraco/section/ai/memory-learning-wall`) to confirm the new memory entries surfaced.

4. **Batch 2 — same articles.** Trigger the workflow again. The agent now sees the prior feedback as injected memory bullets and **suppresses the taught idioms** while still flagging genuine off-voice issues. Flag counts visibly drop across the batch.

The keynote narrative: *"Watch the agent's flag count drop across 6 articles after 3 editorial corrections — site-wide learning from a handful of edits."*

## Filter customisation

The workflow filters articles by **name prefix** + **content type alias**:

```jsonc
// In workflow.json, Find Content step settings:
{
  "name": "idiomatic",                                    // ← change this prefix
  "contentTypes": "0f63b49a-5423-46bd-91fa-0e78bbd2f6d6", // ← change this doctype GUID
  "matchMode": "StartsWith",
  "limit": 50
}
```

- **`name`**: prefix to match. Set to `""` to match all content (the trick is empty-string is `StartsWith`-prefix-of-everything).
- **`contentTypes`**: GUID of the doctype you're auditing. Find your own doctype GUIDs in the Umbraco backoffice → Settings → Document Types.
- **`matchMode`**: `Exact` / `StartsWith` / `Contains` — token-based matching against content names.
- **`limit`**: max number of matches (1-500).

The Northwind seed ships 6 idiomatic articles named `idiomatic-01..06`, hence the default prefix.

## Known limitations (v0.1)

These limitations are documented as forward-pointers for v0.1.x patches. None block the core demo arc; they affect editorial UX at scale.

### 1. Run Detail modal opens at only one iteration per batch run

**Symptom:** clicking a batch workflow run in Automate's Runs table opens our Run Detail modal showing ONE iteration's audit data — not a picker for all N iterations.

**Why:** Automate's Runs table treats each workflow invocation as a single row. The For Each iterations don't surface as sub-rows in the UI; their audit data lives in `umbracoAIAuditLog` but lacks a per-iteration drill-down UI surface.

**Workaround at v0.1:** to teach feedback on multiple articles in a batch, you'll need to invoke the single-article workflow (`templates/brand-audit/workflow.json`) per article, OR navigate to each article's most-recent agent run via the Memory Learning Wall (after submitting at least one feedback row to seed memory entries).

**Coming in v0.1.x:** an iteration picker in the Run Detail modal (next/previous arrows OR dropdown) lets editors flip through all N iterations of a single batch run without leaving the modal. Tracked as Story 4.12 in the package roadmap.

### 2. Notify Editor toasts only fire for users actively editing each article

**Symptom:** the Notify Editor step at the end of each iteration sends a toast notification, but you may not see all N toasts during a batch run.

**Why:** by design, Umbraco.AI.Automate's Notify Editor step targets the realtime backoffice session of any user **currently editing that specific content item**. If no editor has the article open at the moment the iteration fires, the toast has no audience.

**Workaround:** open the article(s) you care about in the content editor before triggering the batch. Or remove the Notify Editor step entirely if it's noisy for your batch workflows (it's optional; the workflow runs fine without it).

### 3. Run all workflows one at a time during demos

**Symptom:** if you have multiple Automate workflows registered with the Manual Trigger (`umbracoAutomate.manual`) — including the sibling single-article `brand-voice-audit-demo` workflow — clicking the Run button on ANY of them fires ALL of them concurrently. This is an [upstream Umbraco.AI.Automate trigger dispatch bug](https://github.com/umbraco/Umbraco.AI/issues) under investigation.

**Why:** the per-workflow trigger endpoint `POST /umbraco/automate/management/api/v1/automations/{workflowId}/trigger` broadcasts to all subscribers of the manual trigger alias instead of scoping to the workflow id in the URL.

**Workaround:** unpublish (or change-trigger-alias on) all sibling Manual-triggered workflows before triggering the batch. Concurrent agent invocations also hit a pre-existing EFCoreScope concurrency limit in `IAIVectorStore.SearchAsync` and can fail — sequential triggering is the safe path at v0.1.

### 4. Memory loop is agent-keyed, not batch-keyed

**Symptom:** feedback on one article in batch 1 surfaces as injected memory for ALL agent runs in batch 2 — including articles that didn't receive feedback.

**Why (and why this is correct):** memory entries are keyed by `(agentId, threadId)`, not by content node. Vector retrieval returns semantically-similar memory entries regardless of which article triggered the original feedback. This is **the cross-article generalisation effect that the demo arc relies on** — teach on article 1, the lesson propagates to articles 2-N when they share semantic content.

**Implication:** you do NOT need to teach the agent on every article in a batch. Teaching on 2-3 representative articles propagates across the batch. Bulk-comment surfaces (apply one comment to N articles) are NOT necessary — feedback compounds automatically through vector similarity.

## Architectural notes for adopters

This batch template is a **template-only ship** — zero changes to the package's C# / TypeScript / test code. The memory pipeline (indexer + retriever + middleware) is **for-each-agnostic**: it doesn't know or care whether the agent was invoked from a single-article workflow or a batched For Each iteration. Each iteration is a separate agent run with its own ThreadId, audit-log row, and memory injection lookup.

This is the empirical proof of the package's architectural composability: the memory loop layers cleanly on top of whatever workflow shape Umbraco.AI.Automate supports.

## Reference

- **Sibling template:** [`templates/brand-audit/README.md`](../brand-audit/README.md) — single-article workflow + agent + Context resource that this batch reuses.
- **Memory Learning Wall:** `/umbraco/section/ai/memory-learning-wall` — view all memory entries learned by the agent, grouped by agent name, across all workflow runs.
- **Story 4.10 spec** (private repo): `_bmad-output/implementation-artifacts/4-10-multi-article-batch-workflow-template.md` — full architectural reasoning, locked decisions, and manual gate trace.
- **Story 4.12 spec** (planned v0.1.x): adds the iteration picker that closes limitation #1.
