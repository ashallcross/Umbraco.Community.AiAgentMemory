import { LitElement as m, html as i, nothing as d, css as g, state as c, customElement as p } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin as b } from "@umbraco-cms/backoffice/element-api";
import { UMB_AUTH_CONTEXT as h } from "@umbraco-cms/backoffice/auth";
import { a as y, A as _ } from "./index-BO7BtkZC.js";
var f = Object.defineProperty, w = Object.getOwnPropertyDescriptor, s = (t, e, a, r) => {
  for (var l = r > 1 ? void 0 : r ? w(e, a) : e, n = t.length - 1, u; n >= 0; n--)
    (u = t[n]) && (l = (r ? u(e, a, l) : u(l)) || l);
  return r && l && f(e, a, l), l;
};
const v = 200;
let o = class extends b(m) {
  constructor() {
    super(...arguments), this._state = "loading", this._entries = [], this._errorMessage = "", this._abortController = null;
  }
  connectedCallback() {
    super.connectedCallback(), this._loadEntries();
  }
  disconnectedCallback() {
    this._abortController?.abort(), super.disconnectedCallback();
  }
  async _loadEntries() {
    this._abortController?.abort();
    const t = new AbortController();
    this._abortController = t, this._state = "loading", this._errorMessage = "";
    try {
      const e = await y(
        () => this.getContext(h),
        "/umbraco/management/api/v1/cogworks-agent-memory/memory-entries?take=100",
        { signal: t.signal }
      );
      if (t.signal.aborted)
        return;
      if (!e.ok) {
        this._errorMessage = `Failed to load memory entries (HTTP ${e.status}).`, this._state = "error";
        return;
      }
      const a = await e.json();
      if (t.signal.aborted)
        return;
      this._entries = Array.isArray(a?.entries) ? a.entries : [], this._state = "loaded";
    } catch (e) {
      if (t.signal.aborted || e?.name === "AbortError" || (e instanceof _ ? this._errorMessage = "Authentication unavailable. Sign in to the backoffice and reload this page." : this._errorMessage = "Couldn't load the Memory Learning Wall. Refresh the page and try again.", t.signal.aborted))
        return;
      this._state = "error";
    }
  }
  _groupedByAgent() {
    const t = /* @__PURE__ */ new Map();
    for (const e of this._entries) {
      const a = t.get(e.agentId);
      a ? (a.entries.push(e), a.displayName === null && e.agentDisplayName !== null && (a.displayName = e.agentDisplayName)) : t.set(e.agentId, {
        displayName: e.agentDisplayName,
        entries: [e]
      });
    }
    return Array.from(t, ([e, { displayName: a, entries: r }]) => ({
      agentId: e,
      displayName: a,
      entries: r
    }));
  }
  _scoreLabel(t) {
    switch (t) {
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
  _truncate(t, e) {
    return typeof t != "string" ? "" : t.length > e ? `${t.slice(0, e)}…` : t;
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
  _cleanDigestForDisplay(t) {
    if (typeof t != "string" || t.length === 0)
      return "";
    const e = /\[tool_call:[^\]]*\][^\[\n]*/g, a = /\[tool:[^\]]*\][^\[\n]*/g;
    return t.replace(e, "").replace(a, "").replace(/[ \t]+/g, " ").replace(/\n[ \t]+/g, `
`).replace(/\n{3,}/g, `

`).trim();
  }
  _formatDate(t) {
    try {
      const e = new Date(t);
      return Number.isNaN(e.getTime()) ? t : new Intl.DateTimeFormat(void 0, {
        dateStyle: "medium",
        timeStyle: "short"
      }).format(e);
    } catch {
      return t;
    }
  }
  render() {
    if (this._state === "loading")
      return i`
        <uui-box headline="Memory Learning Wall" class="memory-wall-box">
          <p class="loading-line" role="status">
            <uui-loader></uui-loader>
            Loading memory entries…
          </p>
        </uui-box>
      `;
    if (this._state === "error")
      return i`
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
    if (this._entries.length === 0)
      return i`
        <uui-box headline="Memory Learning Wall" class="memory-wall-box">
          <p class="empty-line" role="status">
            No memories learned yet — submit feedback on agent runs to teach the agent.
          </p>
        </uui-box>
      `;
    const t = this._groupedByAgent();
    return i`
      <uui-box headline="Memory Learning Wall" class="memory-wall-box">
        <p class="summary-line">
          ${this._entries.length === 1 ? "1 memory captured across 1 agent." : `${this._entries.length} memories captured across ${t.length} ${t.length === 1 ? "agent" : "agents"}.`}
        </p>
      </uui-box>
      ${t.map(
      (e) => i`
          <uui-box
            class="agent-group-box"
            headline=${e.displayName ?? (e.agentId ? `Agent ${e.agentId.slice(0, 8)}` : "Unknown agent")}
          >
            <p class="group-meta">
              ${e.entries.length === 1 ? "1 memory" : `${e.entries.length} memories`}
            </p>
            <uui-table class="memory-table">
              <uui-table-head>
                <uui-table-head-cell>When</uui-table-head-cell>
                <uui-table-head-cell>Score</uui-table-head-cell>
                <uui-table-head-cell>Editor</uui-table-head-cell>
                <uui-table-head-cell>Digest</uui-table-head-cell>
              </uui-table-head>
              ${e.entries.map((a) => {
        const r = this._scoreLabel(a.score);
        return i`
                  <uui-table-row>
                    <uui-table-cell class="when-cell">
                      ${this._formatDate(a.createdUtc)}
                    </uui-table-cell>
                    <uui-table-cell class="score-cell">
                      <span aria-hidden="true">${r.emoji}</span>
                      <span class="score-label">${r.label}</span>
                    </uui-table-cell>
                    <uui-table-cell class="editor-cell">
                      ${a.createdByDisplayName ?? "An editor"}
                    </uui-table-cell>
                    <uui-table-cell class="digest-cell">
                      ${this._truncate(
          this._cleanDigestForDisplay(a.digestText ?? ""),
          v
        )}
                    </uui-table-cell>
                  </uui-table-row>
                `;
      })}
            </uui-table>
          </uui-box>
        `
    )}
      ${d}
    `;
  }
};
o.styles = g`
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
s([
  c()
], o.prototype, "_state", 2);
s([
  c()
], o.prototype, "_entries", 2);
s([
  c()
], o.prototype, "_errorMessage", 2);
o = s([
  p("cogworks-memory-wall")
], o);
const C = o;
export {
  o as CogworksMemoryWallElement,
  C as default
};
//# sourceMappingURL=cogworks-memory-wall.element-sf_vyVTe.js.map
