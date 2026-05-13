import { css as f, state as d, customElement as p, nothing as g, html as u } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement as _ } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT as v } from "@umbraco-cms/backoffice/auth";
class l extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function y(t, e, r) {
  const a = await t();
  if (!a)
    throw new l("Auth context unavailable");
  const s = a.getOpenApiConfiguration();
  let o;
  try {
    o = await s.token();
  } catch {
    throw new l("Token acquisition failed");
  }
  if (!o || o.trim() === "")
    throw new l("Token acquisition returned empty");
  const n = r.body !== void 0, b = {
    Accept: "application/json",
    Authorization: `Bearer ${o}`,
    ...n ? { "Content-Type": "application/json" } : {}
  }, h = { ...r.headers ?? {} };
  delete h.Authorization, delete h.authorization;
  const m = {
    ...b,
    ...h
  };
  return fetch(`${s.base}${e}`, {
    method: r.method,
    credentials: s.credentials,
    signal: r.signal,
    headers: m,
    body: n ? JSON.stringify(r.body) : void 0
  });
}
var k = Object.defineProperty, w = Object.getOwnPropertyDescriptor, c = (t, e, r, a) => {
  for (var s = a > 1 ? void 0 : a ? w(e, r) : e, o = t.length - 1, n; o >= 0; o--)
    (n = t[o]) && (s = (a ? n(e, r, s) : n(s)) || s);
  return a && s && k(e, r, s), s;
};
let i = class extends _ {
  constructor() {
    super(...arguments), this._score = null, this._comment = "", this._state = "idle", this._errorMessage = "", this._abortController = null, this._dismiss = () => {
      this._abortController?.abort(), this._rejectModal();
    };
  }
  disconnectedCallback() {
    this._abortController?.abort(), super.disconnectedCallback();
  }
  render() {
    return u`
      ${this._state === "success" ? this._renderSuccess() : this._renderForm()}
      ${this._state === "error" ? this._renderError() : g}
    `;
  }
  _renderForm() {
    const t = this._score !== null, e = !t || this._state === "submitting";
    return u`
      <header class="modal-head">
        <h3>How was this run?</h3>
        <button
          type="button"
          class="close-icon"
          aria-label="Close"
          title="Close"
          @click=${this._dismiss}
        >
          ✕
        </button>
      </header>

      <div class="row">
        <button
          type="button"
          class="thumb ${this._score === "ThumbsUp" ? "active" : ""}"
          aria-label="Helpful"
          aria-pressed="${this._score === "ThumbsUp"}"
          @click=${() => this._selectScore("ThumbsUp")}
        >
          👍 Helpful
        </button>
        <button
          type="button"
          class="thumb ${this._score === "ThumbsDown" ? "active" : ""}"
          aria-label="Not helpful"
          aria-pressed="${this._score === "ThumbsDown"}"
          @click=${() => this._selectScore("ThumbsDown")}
        >
          👎 Not helpful
        </button>
      </div>

      <textarea
        ?hidden=${!t}
        aria-label="Optional comment"
        placeholder="Optional — explain why (helps the agent learn)"
        maxlength="4000"
        .value=${this._comment}
        @input=${(r) => this._comment = r.target.value}
      ></textarea>

      <div class="actions">
        <button
          type="button"
          class="cancel"
          ?disabled=${this._state === "submitting"}
          @click=${this._dismiss}
        >
          Cancel
        </button>
        <button
          type="button"
          class="submit"
          ?disabled=${e}
          @click=${this._submit}
        >
          ${this._state === "submitting" ? "Submitting..." : "Submit feedback"}
        </button>
      </div>
    `;
  }
  _renderSuccess() {
    return u`
      <header class="modal-head">
        <h3>Feedback recorded</h3>
        <button
          type="button"
          class="close-icon"
          aria-label="Close"
          title="Close"
          @click=${this._dismiss}
        >
          ✕
        </button>
      </header>
      <p role="status" class="success">Thanks — your feedback was recorded.</p>
      <div class="actions">
        <button type="button" class="submit" @click=${this._dismiss}>Close</button>
      </div>
    `;
  }
  _renderError() {
    return u`<p role="alert" class="error">${this._errorMessage}</p>`;
  }
  _selectScore(t) {
    this._score = t, this._state === "error" && (this._state = "idle", this._errorMessage = "");
  }
  async _submit() {
    const t = this.data?.runId ?? "";
    if (!this._score || t.length === 0) {
      this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
      return;
    }
    this._abortController?.abort(), this._abortController = new AbortController(), this._state = "submitting", this._errorMessage = "";
    try {
      const e = await y(
        () => this.getContext(v),
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
      if (e instanceof l) {
        this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
        return;
      }
      this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
    }
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
    if (t.status === 400)
      try {
        const e = await t.json();
        if (typeof e?.detail == "string" && e.detail.length > 0) {
          this._state = "error", this._errorMessage = e.detail;
          return;
        }
        if (e?.errors && typeof e.errors == "object") {
          const r = Object.values(e.errors)[0];
          if (Array.isArray(r) && typeof r[0] == "string") {
            this._state = "error", this._errorMessage = r[0];
            return;
          }
        }
      } catch {
      }
    this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
  }
};
i.styles = f`
    :host {
      display: block;
      padding: var(--uui-size-space-5, 1.5rem);
      max-width: 540px;
      margin: 0 auto;
      font-family: var(--uui-font-family, system-ui, sans-serif);
      color: var(--uui-color-text, #1a1a1a);
    }

    .modal-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--uui-size-space-3, 0.75rem);
      margin-bottom: var(--uui-size-space-3, 0.75rem);
    }

    h3 {
      margin: 0;
      font-size: 1.15rem;
      font-weight: 600;
    }

    .close-icon {
      padding: 0.25rem 0.5rem;
      font-size: 1.1rem;
      line-height: 1;
      background: transparent;
      border: 1px solid transparent;
    }

    .close-icon:hover {
      background: var(--uui-color-surface-alt, #f0f0f0);
      border-color: var(--uui-color-border, #c0c0c0);
    }

    .actions {
      display: flex;
      gap: var(--uui-size-space-2, 0.5rem);
      justify-content: flex-end;
      margin-top: var(--uui-size-space-3, 0.75rem);
    }

    .cancel {
      background: var(--uui-color-surface, #ffffff);
      color: inherit;
    }

    .row {
      display: flex;
      gap: var(--uui-size-space-2, 0.5rem);
      margin-bottom: var(--uui-size-space-3, 0.75rem);
    }

    button {
      cursor: pointer;
      font: inherit;
      padding: var(--uui-size-space-2, 0.5rem) var(--uui-size-space-4, 1rem);
      border: 1px solid var(--uui-color-border, #c0c0c0);
      background: var(--uui-color-surface, #ffffff);
      color: inherit;
      border-radius: var(--uui-border-radius, 4px);
      transition: background-color 0.12s ease, border-color 0.12s ease;
    }

    button:hover:not(:disabled) {
      background: var(--uui-color-surface-alt, #f0f0f0);
    }

    button:focus-visible {
      outline: 2px solid var(--uui-color-focus, #3544b1);
      outline-offset: 2px;
    }

    button:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }

    .thumb.active {
      background: var(--uui-color-selected, #3544b1);
      border-color: var(--uui-color-selected, #3544b1);
      color: var(--uui-color-selected-contrast, #ffffff);
    }

    textarea {
      display: block;
      width: 100%;
      min-height: 5rem;
      padding: var(--uui-size-space-2, 0.5rem);
      margin-bottom: var(--uui-size-space-3, 0.75rem);
      font: inherit;
      border: 1px solid var(--uui-color-border, #c0c0c0);
      border-radius: var(--uui-border-radius, 4px);
      box-sizing: border-box;
      resize: vertical;
    }

    textarea:focus-visible {
      outline: 2px solid var(--uui-color-focus, #3544b1);
      outline-offset: 2px;
    }

    textarea[hidden] {
      display: none;
    }

    .submit {
      background: var(--uui-color-positive, #1c7430);
      color: var(--uui-color-positive-contrast, #ffffff);
      border-color: var(--uui-color-positive, #1c7430);
    }

    .submit:hover:not(:disabled) {
      background: var(--uui-color-positive-emphasis, #155724);
      border-color: var(--uui-color-positive-emphasis, #155724);
    }

    .success {
      padding: var(--uui-size-space-3, 0.75rem);
      background: var(--uui-color-positive-standalone, #d4edda);
      color: var(--uui-color-positive-standalone-contrast, #155724);
      border-radius: var(--uui-border-radius, 4px);
      margin: 0;
    }

    .error {
      padding: var(--uui-size-space-3, 0.75rem);
      background: var(--uui-color-danger-standalone, #f8d7da);
      color: var(--uui-color-danger-standalone-contrast, #721c24);
      border-radius: var(--uui-border-radius, 4px);
      margin: var(--uui-size-space-3, 0.75rem) 0 0;
    }
  `;
c([
  d()
], i.prototype, "_score", 2);
c([
  d()
], i.prototype, "_comment", 2);
c([
  d()
], i.prototype, "_state", 2);
c([
  d()
], i.prototype, "_errorMessage", 2);
i = c([
  p("cogworks-agent-feedback")
], i);
const z = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 1e4
  }
];
export {
  z as manifests
};
//# sourceMappingURL=cogworks-umbracoai-agentmemory.js.map
