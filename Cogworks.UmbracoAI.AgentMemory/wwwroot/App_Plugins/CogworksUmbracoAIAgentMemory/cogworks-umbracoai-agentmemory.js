import { css as g, state as h, customElement as _, nothing as m, html as c } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement as f } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT as y } from "@umbraco-cms/backoffice/auth";
class l extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function v(t, e, r) {
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
  }, d = { ...r.headers ?? {} };
  delete d.Authorization, delete d.authorization;
  const p = {
    ...b,
    ...d
  };
  return fetch(`${s.base}${e}`, {
    method: r.method,
    credentials: s.credentials,
    signal: r.signal,
    headers: p,
    body: n ? JSON.stringify(r.body) : void 0
  });
}
var k = Object.defineProperty, w = Object.getOwnPropertyDescriptor, u = (t, e, r, a) => {
  for (var s = a > 1 ? void 0 : a ? w(e, r) : e, o = t.length - 1, n; o >= 0; o--)
    (n = t[o]) && (s = (a ? n(e, r, s) : n(s)) || s);
  return a && s && k(e, r, s), s;
};
let i = class extends f {
  constructor() {
    super(...arguments), this._score = null, this._comment = "", this._state = "idle", this._errorMessage = "", this._abortController = null, this._dismiss = () => {
      this._abortController?.abort(), this._rejectModal();
    }, this._onCommentInput = (t) => {
      this._comment = t.target.value;
    };
  }
  disconnectedCallback() {
    this._abortController?.abort(), super.disconnectedCallback();
  }
  render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }
  _renderForm() {
    const t = this._score !== null, e = !t || this._state === "submitting", r = this._state === "submitting" ? "waiting" : void 0;
    return c`
      <uui-box headline="How was this run?">
        <uui-button
          slot="header-actions"
          look="placeholder"
          compact
          label="Close"
          @click=${this._dismiss}
        >
          <uui-icon name="remove"></uui-icon>
        </uui-button>

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

        ${t ? c`<uui-textarea
              auto-height
              label="Optional comment"
              placeholder="Optional — explain why (helps the agent learn)"
              maxlength="4000"
              .value=${this._comment}
              @input=${this._onCommentInput}
            ></uui-textarea>` : m}

        <div class="actions">
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
            state=${r ?? m}
            @click=${this._submit}
          >
            Submit feedback
          </uui-button>
        </div>

        ${this._state === "error" ? this._renderError() : m}
      </uui-box>
    `;
  }
  _renderSuccess() {
    return c`
      <uui-box headline="Feedback recorded">
        <uui-button
          slot="header-actions"
          look="placeholder"
          compact
          label="Close"
          @click=${this._dismiss}
        >
          <uui-icon name="remove"></uui-icon>
        </uui-button>

        <p role="status" class="success">Thanks — your feedback was recorded.</p>

        <div class="actions">
          <uui-button
            look="primary"
            color="positive"
            label="Close"
            @click=${this._dismiss}
          >
            Close
          </uui-button>
        </div>
      </uui-box>
    `;
  }
  _renderError() {
    return c`<p role="alert" class="error">${this._errorMessage}</p>`;
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
      const e = await v(
        () => this.getContext(y),
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
i.styles = g`
    :host {
      display: block;
      max-width: 540px;
      margin: 0 auto;
      font-family: var(--uui-font-family);
      color: var(--uui-color-text);
    }

    .row {
      display: flex;
      gap: var(--uui-size-space-2);
      margin-bottom: var(--uui-size-space-3);
    }

    .actions {
      display: flex;
      gap: var(--uui-size-space-2);
      justify-content: flex-end;
      margin-top: var(--uui-size-space-3);
    }

    uui-textarea {
      display: block;
      width: 100%;
      margin-bottom: var(--uui-size-space-3);
      --uui-textarea-min-height: 5rem;
    }

    .success {
      padding: var(--uui-size-space-3);
      background: var(--uui-color-positive-standalone, #d4edda);
      color: var(--uui-color-positive-standalone-contrast, #155724);
      border-radius: var(--uui-border-radius);
      margin: 0 0 var(--uui-size-space-3);
    }

    .error {
      padding: var(--uui-size-space-3);
      background: var(--uui-color-danger-standalone, #f8d7da);
      color: var(--uui-color-danger-standalone-contrast, #721c24);
      border-radius: var(--uui-border-radius);
      margin: var(--uui-size-space-3) 0 0;
    }
  `;
u([
  h()
], i.prototype, "_score", 2);
u([
  h()
], i.prototype, "_comment", 2);
u([
  h()
], i.prototype, "_state", 2);
u([
  h()
], i.prototype, "_errorMessage", 2);
i = u([
  _("cogworks-agent-feedback")
], i);
const M = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 1e4
  }
];
export {
  M as manifests
};
//# sourceMappingURL=cogworks-umbracoai-agentmemory.js.map
