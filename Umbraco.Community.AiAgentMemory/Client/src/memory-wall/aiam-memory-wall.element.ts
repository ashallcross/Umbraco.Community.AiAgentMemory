import { html, css, customElement, state, nothing, LitElement } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import {
  authenticatedFetch,
  AuthContextUnavailableError,
} from "../util/authenticated-fetch.js";

/**
 * Score wire format — matches `FeedbackScore` per Umbraco Management-API's
 * `JsonStringEnumConverter` (Story 4.5 review-patch #2 lineage; pinned at
 * the server DTO via `[property: JsonConverter(typeof(JsonStringEnumConverter))]`).
 */
type ScoreWire = "ThumbsUp" | "ThumbsDown" | "Neutral";

/**
 * One memory entry projected by the Learning Wall endpoint. Mirrors the
 * server DTO `MemoryWallEntry` field-for-field.
 */
type MemoryWallEntry = {
  runId: string;
  agentId: string;
  agentDisplayName: string | null;
  digestText: string;
  score: ScoreWire | null;
  feedbackComment: string | null;
  createdBy: string | null;
  createdByDisplayName: string | null;
  createdUtc: string;
};

/**
 * Shape of the GET /umbraco/management/api/v1/cogworks-agent-memory/memory-entries
 * response. Story 4.9.
 */
type MemoryWallListResponse = {
  entries: MemoryWallEntry[];
};

type WallState = "loading" | "loaded" | "error";

const DIGEST_SNIPPET_MAX = 200;

/**
 * Story 4.9 — Memory Learning Wall dashboard.
 *
 * Renders every memory entry the package has learned, grouped client-side by
 * agent. Read-only at v0.1 — delete UI defers to v0.1.x pending adopter
 * signal (see `deferred-work.md § Deferred from: story 4-9`).
 *
 * Mounts as a `type: "dashboard"` extension under the Umbraco.AI section
 * (alias `'ai'`) per Task 0a empirical contract probe — the canonical
 * Bellissima dashboard manifest shape verified against
 * `Umbraco.AI/.../Client/src/section/dashboard/manifests.ts`.
 *
 * NFR-A1 (axe-core WCAG 2.1 AA): score column shows BOTH emoji + text label
 * (colour-not-sole-signifier per NFR-A2); empty / error / loading states
 * carry semantic role attributes (`role="status"` / `role="alert"`); all
 * agent + editor display names null-fallback to `Agent {first-8}` /
 * "An editor" per Story 4.5 + 4.8 widget copy carry-forward.
 *
 * XSS defence per AR14 / Story 2.3 AC9: all rendered content goes through
 * Lit's auto-encoding template interpolation; the `unsafeHTML` directive is
 * NEVER imported in this file — static grep gate verifies.
 */
@customElement("aiam-memory-wall")
export class AiamMemoryWallElement extends UmbElementMixin(LitElement) {
  @state() private _state: WallState = "loading";
  @state() private _entries: MemoryWallEntry[] = [];
  @state() private _errorMessage: string = "";

  private _abortController: AbortController | null = null;

  override connectedCallback() {
    super.connectedCallback();
    void this._loadEntries();
  }

  override disconnectedCallback() {
    this._abortController?.abort();
    super.disconnectedCallback();
  }

  private async _loadEntries() {
    this._abortController?.abort();
    const controller = new AbortController();
    this._abortController = controller;
    this._state = "loading";
    this._errorMessage = "";

    try {
      const response = await authenticatedFetch(
        () => this.getContext(UMB_AUTH_CONTEXT),
        "/umbraco/management/api/v1/cogworks-agent-memory/memory-entries?take=100",
        { signal: controller.signal },
      );
      if (controller.signal.aborted) {
        return;
      }
      if (!response.ok) {
        this._errorMessage = `Failed to load memory entries (HTTP ${response.status}).`;
        this._state = "error";
        return;
      }
      const body = (await response.json()) as MemoryWallListResponse;
      if (controller.signal.aborted) {
        return;
      }
      this._entries = Array.isArray(body?.entries) ? body.entries : [];
      this._state = "loaded";
    } catch (err) {
      // Re-check abort BEFORE every state write — a fresh _loadEntries()
      // invocation may have aborted this controller while we awaited the
      // response/JSON, in which case committing state here would clobber the
      // newer request's "loading" state.
      if (controller.signal.aborted) {
        return;
      }
      if ((err as { name?: string })?.name === "AbortError") {
        return;
      }
      if (err instanceof AuthContextUnavailableError) {
        this._errorMessage =
          "Authentication unavailable. Sign in to the backoffice and reload this page.";
      } else {
        this._errorMessage =
          "Couldn't load the Memory Learning Wall. Refresh the page and try again.";
      }
      if (controller.signal.aborted) {
        return;
      }
      this._state = "error";
    }
  }

  private _groupedByAgent(): Array<{ agentId: string; displayName: string | null; entries: MemoryWallEntry[] }> {
    const groups = new Map<string, { displayName: string | null; entries: MemoryWallEntry[] }>();
    for (const entry of this._entries) {
      const existing = groups.get(entry.agentId);
      if (existing) {
        existing.entries.push(entry);
        // Prefer the first non-null displayName seen for this agent. Defends
        // against a partial-resolution wire where one row carries a populated
        // name and another carries null (e.g., split-batch race upstream).
        if (existing.displayName === null && entry.agentDisplayName !== null) {
          existing.displayName = entry.agentDisplayName;
        }
      } else {
        groups.set(entry.agentId, {
          displayName: entry.agentDisplayName,
          entries: [entry],
        });
      }
    }
    return Array.from(groups, ([agentId, { displayName, entries }]) => ({
      agentId,
      displayName,
      entries,
    }));
  }

  private _scoreLabel(score: ScoreWire | null): { emoji: string; label: string } {
    switch (score) {
      case "ThumbsUp":
        return { emoji: "👍", label: "Helpful" };
      case "ThumbsDown":
        return { emoji: "👎", label: "Not helpful" };
      case "Neutral":
        return { emoji: "•", label: "Neutral" };
      default:
        return { emoji: "•", label: "No verdict" };
    }
  }

  private _truncate(text: string, max: number): string {
    if (typeof text !== "string") {
      return "";
    }
    return text.length > max ? `${text.slice(0, max)}…` : text;
  }

  /**
   * Story 4.9 DRIFT-4.9-6 — wall-side display filter for the `DigestText`
   * field. The underlying digest is written by `FeedbackIndexer.BuildDigest`
   * per AR35's segment-order contract (`Comment → ResponseSnapshotJoined →
   * PromptSnapshotJoined`, joined + truncated to 500 chars). The
   * `ResponseSnapshotJoined` portion legitimately carries the agent's
   * tool-call transcript fragments — `[tool_call:TOKEN] toolname(...)` and
   * `[tool:TOKEN] -> result` markers — which are load-bearing context for
   * Run 2's memory injection but render as noise in editorial chrome.
   *
   * This filter is RENDER-ONLY: the wire shape stays AR35-faithful, the
   * agent receives the full transcript at injection time, only the wall's
   * editor-facing view drops the markers.
   *
   * Iteration 2 (manual-gate Iteration 3) — switched from line-based filter
   * to regex-based pattern matching. The line-based approach missed the
   * markers because the transcript fragments are interleaved on the SAME
   * line as adjacent segments in the wire shape (or whitespace-collapsed by
   * CSS at render time). The regex matches `[tool_call:TOKEN] toolname(...)`
   * + `[tool:TOKEN] -> {...}` and consumes their trailing args/result up to
   * the next bracket marker or end-of-line, so the surrounding semantic
   * content (editor comment, `[assistant]` turn, `[user]` turn, prompt
   * prose) survives intact.
   */
  private _cleanDigestForDisplay(digest: string): string {
    if (typeof digest !== "string" || digest.length === 0) {
      return "";
    }
    // [tool_call:TOKEN] toolname(json-args)
    // Consume the marker + identifier + balanced-ish parenthesised args by
    // greedy-matching everything up to the NEXT bracket marker or newline.
    // `[^\[\n]*` excludes `[` so we stop before the next `[tool:...]` /
    // `[assistant]` / `[user]` segment.
    const toolCallPattern = /\[tool_call:[^\]]*\][^\[\n]*/g;
    // [tool:TOKEN] -> {json-result}
    const toolResultPattern = /\[tool:[^\]]*\][^\[\n]*/g;
    return digest
      .replace(toolCallPattern, "")
      .replace(toolResultPattern, "")
      // Collapse runs of whitespace introduced by the deletions into single
      // spaces; trim leading/trailing whitespace so the snippet reads cleanly.
      .replace(/[ \t]+/g, " ")
      .replace(/\n[ \t]+/g, "\n")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  private _formatDate(iso: string): string {
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) {
        return iso;
      }
      return new Intl.DateTimeFormat(undefined, {
        dateStyle: "medium",
        timeStyle: "short",
      }).format(d);
    } catch {
      return iso;
    }
  }

  override render() {
    if (this._state === "loading") {
      return html`
        <uui-box headline="Memory Learning Wall" class="memory-wall-box">
          <p class="loading-line" role="status">
            <uui-loader></uui-loader>
            Loading memory entries…
          </p>
        </uui-box>
      `;
    }

    if (this._state === "error") {
      return html`
        <uui-box headline="Memory Learning Wall" class="memory-wall-box">
          <p class="error-line" role="alert">${this._errorMessage}</p>
          <uui-button
            look="primary"
            color="default"
            label="Retry"
            @click=${() => this._loadEntries()}
          >
            Retry
          </uui-button>
        </uui-box>
      `;
    }

    if (this._entries.length === 0) {
      return html`
        <uui-box headline="Memory Learning Wall" class="memory-wall-box">
          <p class="empty-line" role="status">
            No memories learned yet — submit feedback on agent runs to teach the agent.
          </p>
        </uui-box>
      `;
    }

    const groups = this._groupedByAgent();
    return html`
      <uui-box headline="Memory Learning Wall" class="memory-wall-box">
        <p class="summary-line">
          ${this._entries.length === 1
            ? "1 memory captured across 1 agent."
            : `${this._entries.length} memories captured across ${groups.length} ${
                groups.length === 1 ? "agent" : "agents"
              }.`}
        </p>
      </uui-box>
      ${groups.map(
        (group) => html`
          <uui-box
            class="agent-group-box"
            headline=${group.displayName ?? (group.agentId ? `Agent ${group.agentId.slice(0, 8)}` : "Unknown agent")}
          >
            <p class="group-meta">
              ${group.entries.length === 1
                ? "1 memory"
                : `${group.entries.length} memories`}
            </p>
            <uui-table class="memory-table">
              <uui-table-head>
                <uui-table-head-cell>When</uui-table-head-cell>
                <uui-table-head-cell>Score</uui-table-head-cell>
                <uui-table-head-cell>Editor</uui-table-head-cell>
                <uui-table-head-cell>Digest</uui-table-head-cell>
              </uui-table-head>
              ${group.entries.map((entry) => {
                const score = this._scoreLabel(entry.score);
                return html`
                  <uui-table-row>
                    <uui-table-cell class="when-cell">
                      ${this._formatDate(entry.createdUtc)}
                    </uui-table-cell>
                    <uui-table-cell class="score-cell">
                      <span aria-hidden="true">${score.emoji}</span>
                      <span class="score-label">${score.label}</span>
                    </uui-table-cell>
                    <uui-table-cell class="editor-cell">
                      ${entry.createdByDisplayName ?? "An editor"}
                    </uui-table-cell>
                    <uui-table-cell class="digest-cell">
                      ${this._truncate(
                        this._cleanDigestForDisplay(entry.digestText ?? ""),
                        DIGEST_SNIPPET_MAX,
                      )}
                    </uui-table-cell>
                  </uui-table-row>
                `;
              })}
            </uui-table>
          </uui-box>
        `,
      )}
      ${nothing}
    `;
  }

  static override styles = css`
    :host {
      display: block;
      font-family: var(--uui-font-family);
      color: var(--uui-color-text);
      padding: var(--uui-size-space-4);
    }

    .memory-wall-box,
    .agent-group-box {
      display: block;
      margin-bottom: var(--uui-size-space-4);
    }

    .loading-line,
    .empty-line {
      margin: 0;
      color: var(--uui-color-text-alt);
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-2);
    }

    .error-line {
      margin: 0 0 var(--uui-size-space-3) 0;
      padding: var(--uui-size-space-3);
      background: var(--uui-color-danger);
      color: var(--uui-color-danger-contrast);
      border-radius: var(--uui-border-radius);
    }

    .summary-line {
      margin: 0;
      color: var(--uui-color-text-alt);
    }

    .group-meta {
      margin: 0 0 var(--uui-size-space-2) 0;
      color: var(--uui-color-text-alt);
      font-size: var(--uui-type-small-size, 0.875rem);
    }

    .memory-table {
      width: 100%;
    }

    .when-cell {
      white-space: nowrap;
      color: var(--uui-color-text-alt);
    }

    .score-cell {
      white-space: nowrap;
    }

    .score-label {
      margin-left: var(--uui-size-space-1);
    }

    .editor-cell {
      white-space: nowrap;
      color: var(--uui-color-text-alt);
    }

    .digest-cell {
      color: var(--uui-color-text);
    }
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    "aiam-memory-wall": AiamMemoryWallElement;
  }
}

// DRIFT-4.9-5 (manual-gate iteration 1) — Bellissima's
// `createExtensionElement` resolves the dynamic-import module by looking for
// `default` OR `element` exports. Without one of these, the dashboard mount
// surfaces the "did not export a 'element' or 'default'" warning and the
// router falls through to a 'Cannot read properties of undefined' crash.
// Mirrors Umbraco.AI's own canonical pattern at
// `Umbraco.AI/.../section/dashboard/ai-dashboard.element.ts:67`.
export default AiamMemoryWallElement;
