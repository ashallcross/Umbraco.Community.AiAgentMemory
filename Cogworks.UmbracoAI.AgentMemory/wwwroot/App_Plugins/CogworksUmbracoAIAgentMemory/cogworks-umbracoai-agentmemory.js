import { css as p, state as h, customElement as _, nothing as b, html as l } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement as f } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT as y } from "@umbraco-cms/backoffice/auth";
class c extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function k(t, e, r) {
  const o = await t();
  if (!o)
    throw new c("Auth context unavailable");
  const s = o.getOpenApiConfiguration();
  let a;
  try {
    a = await s.token();
  } catch {
    throw new c("Token acquisition failed");
  }
  if (!a || a.trim() === "")
    throw new c("Token acquisition returned empty");
  const n = r.body !== void 0, m = {
    Accept: "application/json",
    Authorization: `Bearer ${a}`,
    ...n ? { "Content-Type": "application/json" } : {}
  }, d = { ...r.headers ?? {} };
  delete d.Authorization, delete d.authorization;
  const g = {
    ...m,
    ...d
  };
  return fetch(`${s.base}${e}`, {
    method: r.method,
    credentials: s.credentials,
    signal: r.signal,
    headers: g,
    body: n ? JSON.stringify(r.body) : void 0
  });
}
var v = Object.defineProperty, w = Object.getOwnPropertyDescriptor, u = (t, e, r, o) => {
  for (var s = o > 1 ? void 0 : o ? w(e, r) : e, a = t.length - 1, n; a >= 0; a--)
    (n = t[a]) && (s = (o ? n(e, r, s) : n(s)) || s);
  return o && s && v(e, r, s), s;
};
let i = class extends f {
  constructor() {
    super(...arguments), this._score = null, this._comment = "", this._state = "idle", this._errorMessage = "", this._abortController = null, this._dismiss = () => {
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
        const e = await k(
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
        if (e instanceof c) {
          this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
          return;
        }
        this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
      }
    };
  }
  connectedCallback() {
    super.connectedCallback(), this.closest("uui-modal-sidebar")?.setAttribute("size", "small");
  }
  disconnectedCallback() {
    this._abortController?.abort(), super.disconnectedCallback();
  }
  render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }
  _renderForm() {
    const t = this._score !== null, e = !t || this._state === "submitting", r = this._state === "submitting" ? "waiting" : void 0;
    return l`
      <umb-body-layout headline="Run Feedback">
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

          ${t ? l`<uui-textarea
                auto-height
                label="Optional comment"
                placeholder="Optional — explain why (helps the agent learn)"
                maxlength="4000"
                .value=${this._comment}
                @input=${this._onCommentInput}
              ></uui-textarea>` : b}

          ${this._state === "error" ? this._renderError() : b}
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
            state=${r ?? b}
            @click=${this._submit}
          >
            Submit feedback
          </uui-button>
        </div>
      </umb-body-layout>
    `;
  }
  _renderSuccess() {
    return l`
      <umb-body-layout headline="Run Feedback">
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
    return l`<p role="alert" class="error">${this._errorMessage}</p>`;
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
          const r = Object.values(e.errors)[0];
          if (Array.isArray(r) && typeof r[0] == "string") {
            this._state = "error", this._errorMessage = r[0];
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
i.styles = p`
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
const T = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 1e4
  }
];
export {
  T as manifests
};
//# sourceMappingURL=cogworks-umbracoai-agentmemory.js.map
