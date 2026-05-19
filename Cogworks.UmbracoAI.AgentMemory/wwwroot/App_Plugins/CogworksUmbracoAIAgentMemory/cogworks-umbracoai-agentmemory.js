import { css as f, state as h, customElement as y, nothing as u, html as s } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement as v } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT as p } from "@umbraco-cms/backoffice/auth";
class d extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function b(t, e, a) {
  const o = await t();
  if (!o)
    throw new d("Auth context unavailable");
  const r = o.getOpenApiConfiguration();
  let n;
  try {
    n = await r.token();
  } catch {
    throw new d("Token acquisition failed");
  }
  if (!n || n.trim() === "")
    throw new d("Token acquisition returned empty");
  const c = a.body !== void 0, m = {
    Accept: "application/json",
    Authorization: `Bearer ${n}`,
    ...c ? { "Content-Type": "application/json" } : {}
  }, g = { ...a.headers ?? {} };
  delete g.Authorization, delete g.authorization;
  const _ = {
    ...m,
    ...g
  };
  return fetch(`${r.base}${e}`, {
    method: a.method ?? "GET",
    credentials: r.credentials,
    signal: a.signal,
    headers: _,
    body: c ? JSON.stringify(a.body) : void 0
  });
}
var k = Object.defineProperty, w = Object.getOwnPropertyDescriptor, l = (t, e, a, o) => {
  for (var r = o > 1 ? void 0 : o ? w(e, a) : e, n = t.length - 1, c; n >= 0; n--)
    (c = t[n]) && (r = (o ? c(e, a, r) : c(r)) || r);
  return o && r && k(e, a, r), r;
};
let i = class extends v {
  constructor() {
    super(...arguments), this._score = null, this._comment = "", this._state = "idle", this._errorMessage = "", this._runDetailState = "loading", this._runDetail = null, this._abortController = null, this._runDetailAbortController = null, this._dismiss = () => {
      this._abortController?.abort(), this._rejectModal();
    }, this._onCommentInput = (t) => {
      this._comment = t.target.value;
    }, this._submit = async () => {
      if (!this._score)
        return;
      const t = this.data?.runId ?? "";
      if (t.length === 0) {
        this._state = "error", this._errorMessage = "Couldn't load this run's details. Refresh the page and try again.";
        return;
      }
      this._abortController?.abort(), this._abortController = new AbortController(), this._state = "submitting", this._errorMessage = "";
      try {
        const e = await b(
          () => this.getContext(p),
          "/umbraco/management/api/v1/cogworks-agent-memory/feedback",
          {
            method: "POST",
            body: {
              runId: t,
              score: this._score,
              comment: this._comment.length > 0 ? this._comment : null
            },
            signal: this._abortController.signal
          }
        );
        if (e.ok) {
          this._state = "success";
          return;
        }
        await this._handleHttpError(e);
      } catch (e) {
        if (e instanceof DOMException && e.name === "AbortError")
          return;
        if (e instanceof d) {
          this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
          return;
        }
        this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
      }
    };
  }
  connectedCallback() {
    super.connectedCallback(), this.closest("uui-modal-sidebar")?.setAttribute("size", "small"), this._loadRunDetail();
  }
  disconnectedCallback() {
    this._abortController?.abort(), this._runDetailAbortController?.abort(), super.disconnectedCallback();
  }
  async _loadRunDetail() {
    const t = this.data?.runId ?? "";
    if (t.length === 0) {
      this._runDetailState = "unavailable";
      return;
    }
    this._runDetailAbortController?.abort();
    const e = new AbortController();
    this._runDetailAbortController = e, this._runDetailState = "loading";
    try {
      const a = await b(
        () => this.getContext(p),
        `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(t)}`,
        { signal: e.signal }
      );
      if (e.signal.aborted)
        return;
      if (!a.ok) {
        this._runDetailState = "unavailable";
        return;
      }
      const o = await a.json();
      if (e.signal.aborted)
        return;
      if (!Array.isArray(o.issues) || !Array.isArray(o.suggestions)) {
        this._runDetailState = "unavailable";
        return;
      }
      this._runDetail = o, this._runDetailState = "loaded";
    } catch (a) {
      if (a instanceof DOMException && a.name === "AbortError" || e.signal.aborted)
        return;
      this._runDetailState = "unavailable";
    }
  }
  render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }
  _renderForm() {
    const t = this._score !== null, e = !t || this._state === "submitting", a = this._state === "submitting" ? "waiting" : void 0;
    return s`
      <umb-body-layout headline="Run Feedback">
        ${this._renderAgentOutput()}
        <uui-box headline="How was this run?">
          <div class="row">
            <uui-button
              look=${this._score === "ThumbsUp" ? "primary" : "secondary"}
              label="Helpful"
              aria-pressed=${this._score === "ThumbsUp"}
              @click=${() => this._selectScore("ThumbsUp")}
            >
              👍 Helpful
            </uui-button>
            <uui-button
              look=${this._score === "ThumbsDown" ? "primary" : "secondary"}
              label="Not helpful"
              aria-pressed=${this._score === "ThumbsDown"}
              @click=${() => this._selectScore("ThumbsDown")}
            >
              👎 Not helpful
            </uui-button>
          </div>

          ${t ? s`<uui-textarea
                auto-height
                label="Optional comment"
                placeholder="Optional — explain why (helps the agent learn)"
                maxlength="4000"
                .value=${this._comment}
                @input=${this._onCommentInput}
              ></uui-textarea>` : u}

          ${this._state === "error" ? this._renderError() : u}
        </uui-box>

        <div slot="actions">
          <uui-button
            look="secondary"
            label="Cancel"
            ?disabled=${this._state === "submitting"}
            @click=${this._dismiss}
          >
            Cancel
          </uui-button>
          <uui-button
            look="primary"
            color="positive"
            label="Submit feedback"
            ?disabled=${e}
            state=${a ?? u}
            @click=${this._submit}
          >
            Submit feedback
          </uui-button>
        </div>
      </umb-body-layout>
    `;
  }
  _renderSuccess() {
    return s`
      <umb-body-layout headline="Run Feedback">
        ${this._renderAgentOutput()}
        <uui-box headline="Feedback recorded">
          <p role="status" class="success">Thanks — your feedback was recorded.</p>
        </uui-box>

        <div slot="actions">
          <uui-button
            look="primary"
            color="positive"
            label="Close"
            @click=${this._dismiss}
          >
            Close
          </uui-button>
        </div>
      </umb-body-layout>
    `;
  }
  _renderError() {
    return s`<p role="alert" class="error">${this._errorMessage}</p>`;
  }
  /**
   * Story 4.2 — DRIFT-4.1-12 closure. Renders the agent's score / flagged
   * issues / suggestions ABOVE the existing feedback form so the editor sees
   * what they're rating. Three states:
   *
   * - `loading` → `<uui-loader>` while the GET completes
   * - `loaded`  → score + issues + suggestions rendered via Lit interpolation
   * - `unavailable` → graceful-degradation notice ("you can still submit
   *   feedback below"); feedback form remains usable
   *
   * **XSS defence pin (Story 4.2 AC6 + Story 2.3 AC9):** all agent-derived
   * fields render via Lit's automatic template-literal HTML-encoding. The
   * `unsafeHTML` symbol is NEVER imported in this file — static grep gate at
   * `grep -r 'unsafeHTML' Client/src/feedback-widget/` returns zero matches.
   */
  _renderAgentOutput() {
    if (this._runDetailState === "loading")
      return s`
        <uui-box headline="Agent output" class="agent-output-box">
          <p class="agent-output-loading">
            <uui-loader></uui-loader>
            Loading agent output…
          </p>
        </uui-box>
      `;
    if (this._runDetailState === "unavailable" || this._runDetail === null)
      return s`
        <uui-box headline="Agent output" class="agent-output-box">
          <p class="agent-output-unavailable">
            Agent output unavailable; you can still submit feedback below.
          </p>
        </uui-box>
      `;
    const t = this._runDetail, e = t.issues.length > 0, a = t.suggestions.length > 0, o = t.score !== null || e || a;
    return s`
      <uui-box headline="Agent output" class="agent-output-box">
        ${t.score !== null ? s`<p class="agent-output-score">
              Score: <strong>${t.score}</strong>
            </p>` : u}
        ${e ? s`
              <h5 class="agent-output-section-heading">Flagged issues</h5>
              <ul class="agent-output-issues">
                ${t.issues.map(
      (r) => s`
                    <li>
                      <span class="agent-output-issue-text">${r.text}</span>
                      ${r.reason ? s`<span class="agent-output-issue-reason">
                            — ${r.reason}
                          </span>` : u}
                    </li>
                  `
    )}
              </ul>
            ` : u}
        ${a ? s`
              <h5 class="agent-output-section-heading">Suggestions</h5>
              <ul class="agent-output-suggestions">
                ${t.suggestions.map(
      (r) => s`<li>${r}</li>`
    )}
              </ul>
            ` : u}
        ${o ? u : s`<p class="agent-output-empty-note">
              (no structured output captured for this run)
            </p>`}
      </uui-box>
    `;
  }
  _selectScore(t) {
    this._score = t, this._state === "error" && (this._state = "idle", this._errorMessage = "");
  }
  async _handleHttpError(t) {
    if (t.status === 401) {
      this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
      return;
    }
    if (t.status === 404) {
      this._state = "error", this._errorMessage = "This run hasn't been audit-logged yet. Wait a moment and try again — or refresh the page if it keeps failing.";
      return;
    }
    if (t.status === 400) {
      try {
        const e = await t.json();
        if (typeof e?.detail == "string" && e.detail.length > 0) {
          this._state = "error", this._errorMessage = e.detail;
          return;
        }
        if (e?.errors && typeof e.errors == "object") {
          const a = Object.values(e.errors)[0];
          if (Array.isArray(a) && typeof a[0] == "string") {
            this._state = "error", this._errorMessage = a[0];
            return;
          }
        }
      } catch {
      }
      this._state = "error", this._errorMessage = "Your submission was rejected. Refresh and try again — if it keeps failing, the run may no longer accept feedback.";
      return;
    }
    this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
  }
};
i.styles = f`
    :host {
      display: block;
      font-family: var(--uui-font-family);
      color: var(--uui-color-text);
    }

    /* Thumbs row — flex layout for the side-by-side 👍 / 👎 buttons. */
    .row {
      display: flex;
      gap: var(--uui-size-space-2);
      margin-bottom: var(--uui-size-space-3);
    }

    /* Actions slot — umb-footer-layout's <slot name="actions"> wraps this in
       its own flex container, so we only need to space the buttons apart. */
    [slot="actions"] {
      display: flex;
      gap: var(--uui-size-space-2);
    }

    /* Comment textarea — full width within the body-layout main area; UUI's
       auto-height handles vertical growth. */
    uui-textarea {
      display: block;
      width: 100%;
      margin-bottom: var(--uui-size-space-3);
      --uui-textarea-min-height: 5rem;
    }

    /* Use the same positive/danger token pairs that uui-button consumes for
       its color="positive"/color="danger" looks — guaranteed paired (contrast
       text always reads against the background colour). The "-standalone-..."
       variants used at Story 3.4 first cut produced an unreadable dark-green
       on light-green combo because the contrast token didn't resolve cleanly
       in our shadow scope. Captured as DRIFT-3.4-impl-4 at manual gate
       2026-05-14. */
    .success {
      padding: var(--uui-size-space-3);
      background: var(--uui-color-positive);
      color: var(--uui-color-positive-contrast);
      border-radius: var(--uui-border-radius);
      margin: 0;
    }

    .error {
      padding: var(--uui-size-space-3);
      background: var(--uui-color-danger);
      color: var(--uui-color-danger-contrast);
      border-radius: var(--uui-border-radius);
      margin: var(--uui-size-space-3) 0 0;
    }

    /* Story 4.2 — Agent output panel (DRIFT-4.1-12 closure).
       Sits ABOVE the feedback form so the editor sees what they're rating. */
    .agent-output-box {
      display: block;
      margin-bottom: var(--uui-size-space-4);
    }

    .agent-output-loading,
    .agent-output-unavailable,
    .agent-output-empty-note {
      margin: 0;
      color: var(--uui-color-text-alt);
      font-style: italic;
    }

    .agent-output-loading {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-2);
    }

    .agent-output-score {
      margin: 0 0 var(--uui-size-space-3) 0;
    }

    .agent-output-section-heading {
      margin: var(--uui-size-space-3) 0 var(--uui-size-space-2) 0;
      font-size: var(--uui-type-h5-size, 1rem);
      font-weight: 600;
    }

    .agent-output-issues,
    .agent-output-suggestions {
      margin: 0 0 var(--uui-size-space-2) 0;
      padding-left: var(--uui-size-space-5);
    }

    .agent-output-issues li,
    .agent-output-suggestions li {
      margin-bottom: var(--uui-size-space-1);
    }

    .agent-output-issue-reason {
      color: var(--uui-color-text-alt);
    }
  `;
l([
  h()
], i.prototype, "_score", 2);
l([
  h()
], i.prototype, "_comment", 2);
l([
  h()
], i.prototype, "_state", 2);
l([
  h()
], i.prototype, "_errorMessage", 2);
l([
  h()
], i.prototype, "_runDetailState", 2);
l([
  h()
], i.prototype, "_runDetail", 2);
i = l([
  y("cogworks-agent-feedback")
], i);
const $ = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 1e4
  }
];
export {
  $ as manifests
};
//# sourceMappingURL=cogworks-umbracoai-agentmemory.js.map
