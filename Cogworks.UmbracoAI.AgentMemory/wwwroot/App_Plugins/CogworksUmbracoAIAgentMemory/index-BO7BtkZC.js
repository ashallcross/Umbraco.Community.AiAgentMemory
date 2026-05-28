import { css as f, state as c, customElement as y, nothing as u, html as o } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement as k } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT as b } from "@umbraco-cms/backoffice/auth";
import { UMB_CURRENT_USER_CONTEXT as v } from "@umbraco-cms/backoffice/current-user";
class p extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function m(e, t, s) {
  const a = await e();
  if (!a)
    throw new p("Auth context unavailable");
  const i = a.getOpenApiConfiguration();
  let r;
  try {
    r = await i.token();
  } catch {
    throw new p("Token acquisition failed");
  }
  if (!r || r.trim() === "")
    throw new p("Token acquisition returned empty");
  const d = s.body !== void 0, g = {
    Accept: "application/json",
    Authorization: `Bearer ${r}`,
    ...d ? { "Content-Type": "application/json" } : {}
  }, h = { ...s.headers ?? {} };
  delete h.Authorization, delete h.authorization;
  const _ = {
    ...g,
    ...h
  };
  return fetch(`${i.base}${t}`, {
    method: s.method ?? "GET",
    credentials: i.credentials,
    signal: s.signal,
    headers: _,
    body: d ? JSON.stringify(s.body) : void 0
  });
}
var x = Object.defineProperty, w = Object.getOwnPropertyDescriptor, l = (e, t, s, a) => {
  for (var i = a > 1 ? void 0 : a ? w(t, s) : t, r = e.length - 1, d; r >= 0; r--)
    (d = e[r]) && (i = (a ? d(t, s, i) : d(i)) || i);
  return a && i && x(t, s, i), i;
};
let n = class extends k {
  constructor() {
    super(...arguments), this._score = null, this._comment = "", this._state = "idle", this._errorMessage = "", this._runDetailState = "loading", this._runDetail = null, this._existingFeedbackState = "loading", this._existingFeedback = null, this._currentUserId = null, this._siblings = [], this._siblingsState = "loading", this._selectedRunId = null, this._abortController = null, this._runDetailAbortController = null, this._existingFeedbackAbortController = null, this._siblingsAbortController = null, this._hasSeededFromExisting = !1, this._currentUserIdReady = null, this._dismiss = () => {
      this._abortController?.abort(), this._rejectModal();
    }, this._onCommentInput = (e) => {
      this._comment = e.target.value;
    }, this._submit = async () => {
      if (!this._score)
        return;
      const e = this.data?.runId ?? "";
      if (e.length === 0) {
        this._state = "error", this._errorMessage = "Couldn't load this run's details. Refresh the page and try again.";
        return;
      }
      const t = this._selectedRunId, s = this._score, a = this._comment;
      this._abortController?.abort(), this._abortController = new AbortController(), this._state = "submitting", this._errorMessage = "";
      try {
        const i = await m(
          () => this.getContext(b),
          "/umbraco/management/api/v1/cogworks-agent-memory/feedback",
          {
            method: "POST",
            body: {
              runId: e,
              score: s,
              comment: a.length > 0 ? a : null,
              // Story 4.12 — picker submissions include selectedRunId so the
              // controller records feedback under the per-iteration RunId
              // (creating distinct supersede keys per iteration). Omitted for
              // non-picker submissions (single-iteration flows) so the legacy
              // ThreadId-keyed path is preserved byte-compatibly.
              selectedRunId: t
            },
            signal: this._abortController.signal
          }
        );
        if (i.ok) {
          this._state = "success";
          return;
        }
        await this._handleHttpError(i);
      } catch (i) {
        if (i instanceof DOMException && i.name === "AbortError")
          return;
        if (i instanceof p) {
          this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
          return;
        }
        this._state = "error", this._errorMessage = "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
      }
    };
  }
  connectedCallback() {
    super.connectedCallback(), this.closest("uui-modal-sidebar")?.setAttribute("size", "small"), this._loadRunDetail(null), this._currentUserIdReady = this._resolveCurrentUserId(), this._loadExistingFeedback(null), this._loadSiblings();
  }
  disconnectedCallback() {
    this._abortController?.abort(), this._runDetailAbortController?.abort(), this._existingFeedbackAbortController?.abort(), this._siblingsAbortController?.abort(), super.disconnectedCallback();
  }
  async _resolveCurrentUserId() {
    try {
      const e = await this.getContext(v);
      this._currentUserId = e?.getUnique() ?? null;
    } catch {
      this._currentUserId = null;
    }
  }
  async _loadRunDetail(e) {
    const t = this.data?.runId ?? "";
    if (t.length === 0)
      return this._runDetailState = "unavailable", !1;
    this._runDetailAbortController?.abort();
    const s = new AbortController();
    this._runDetailAbortController = s, this._runDetailState = "loading";
    const a = e !== null ? `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(t)}?selectedRunId=${encodeURIComponent(e)}` : `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(t)}`;
    try {
      const i = await m(
        () => this.getContext(b),
        a,
        { signal: s.signal }
      );
      if (s.signal.aborted || this._selectedRunId !== e)
        return !0;
      if (!i.ok)
        return this._runDetailState = "unavailable", !1;
      const r = await i.json();
      return s.signal.aborted || this._selectedRunId !== e ? !0 : !Array.isArray(r.issues) || !Array.isArray(r.suggestions) ? (this._runDetailState = "unavailable", !1) : r.score === null && r.issues.length === 0 && r.suggestions.length === 0 && !r.memoryUsed ? (this._runDetailState = "unavailable", !1) : (this._runDetail = r, this._runDetailState = "loaded", !0);
    } catch (i) {
      return i instanceof DOMException && i.name === "AbortError" || s.signal.aborted ? !0 : (this._runDetailState = "unavailable", !1);
    }
  }
  async _loadExistingFeedback(e) {
    const t = this.data?.runId ?? "";
    if (t.length === 0)
      return this._existingFeedbackState = "unavailable", !1;
    this._existingFeedbackAbortController?.abort();
    const s = new AbortController();
    this._existingFeedbackAbortController = s, this._existingFeedbackState = "loading";
    const a = e ?? t;
    try {
      const i = await m(
        () => this.getContext(b),
        `/umbraco/management/api/v1/cogworks-agent-memory/feedback/${encodeURIComponent(a)}`,
        { signal: s.signal }
      );
      if (s.signal.aborted || this._selectedRunId !== e)
        return !0;
      if (!i.ok)
        return this._existingFeedbackState = "unavailable", !1;
      const r = await i.json();
      return s.signal.aborted || this._selectedRunId !== e ? !0 : Array.isArray(r.existing) ? (this._existingFeedback = r.existing, this._existingFeedbackState = "loaded", this._currentUserIdReady !== null && (await this._currentUserIdReady, s.signal.aborted) || (this._hasSeededFromExisting = !0), !0) : (this._existingFeedbackState = "unavailable", !1);
    } catch (i) {
      return i instanceof DOMException && i.name === "AbortError" || s.signal.aborted ? !0 : (this._existingFeedbackState = "unavailable", !1);
    }
  }
  /**
   * Story 4.12 — fetches the per-iteration sibling list for the ThreadId.
   * When the workflow ran For Each over N items, this surfaces all N agent
   * invocations so the editor can flip between them via the picker without
   * leaving the modal.
   *
   * Behaviour:
   *  - `< 2` siblings → picker stays hidden; legacy detail/feedback fetches
   *    already in flight settle normally. `_selectedRunId` stays `null`.
   *  - `≥ 2` siblings → initialise `_selectedRunId` to the FIRST iteration
   *    in the ASC list (oldest first per LD#3a), then kick off refetches of
   *    detail + feedback targeting that specific iteration. The legacy fetches
   *    started in `connectedCallback` are aborted via the per-fetch reassign
   *    so their responses don't clobber selected-sibling state.
   *  - Any failure (404 / parse / network) → unavailable; widget falls
   *    through to legacy single-iteration behaviour.
   */
  async _loadSiblings() {
    const e = this.data?.runId ?? "";
    if (e.length === 0) {
      this._siblingsState = "unavailable";
      return;
    }
    this._siblingsAbortController?.abort();
    const t = new AbortController();
    this._siblingsAbortController = t;
    try {
      const s = await m(
        () => this.getContext(b),
        `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(e)}/siblings`,
        { signal: t.signal }
      );
      if (t.signal.aborted)
        return;
      if (!s.ok) {
        this._siblingsState = "unavailable";
        return;
      }
      const a = await s.json();
      if (t.signal.aborted)
        return;
      if (!Array.isArray(a)) {
        this._siblingsState = "unavailable";
        return;
      }
      const i = a;
      if (this._siblings = i, this._siblingsState = "loaded", i.length > 1) {
        const r = i[0].runId;
        if (this._selectedRunId = r, this._hasSeededFromExisting = !1, !this.isConnected)
          return;
        this._loadRunDetail(r), this._loadExistingFeedback(r);
      }
    } catch (s) {
      if (s instanceof DOMException && s.name === "AbortError" || t.signal.aborted)
        return;
      this._siblingsState = "unavailable";
    }
  }
  /**
   * Story 4.12 — picker arrow click handler. Changes `_selectedRunId` to the
   * sibling at the new index, resets the existing-feedback seed flag so the
   * form seeds from THAT iteration's prior feedback, and kicks off detail +
   * feedback refetches.
   *
   * Submit-in-flight guard: the picker arrows are disabled in the template
   * while `_state === "submitting"` so an in-flight feedback POST can't end
   * up attributed to the wrong iteration. This handler is a defence-in-depth
   * no-op in that state.
   *
   * Failure rollback (AC4.e): when either fetch comes back unavailable, the
   * picker rolls back to the previous iteration so the editor doesn't see a
   * mid-state half-populated modal. The previous iteration's content is
   * deliberately NOT cleared synchronously here — letting it remain visible
   * during the loading transition (and stay if the new fetch fails) avoids
   * the "flash to empty" footgun the spec calls out.
   */
  async _onPickerChange(e) {
    if (this._state === "submitting" || e < 0 || e >= this._siblings.length) return;
    const t = this._siblings[e];
    if (t.runId === this._selectedRunId) return;
    this._state = "idle", this._errorMessage = "", this._score = null, this._comment = "";
    const s = [
      this._selectedRunId,
      this._runDetail,
      this._runDetailState,
      this._existingFeedback,
      this._existingFeedbackState,
      this._hasSeededFromExisting
    ];
    this._selectedRunId = t.runId, this._hasSeededFromExisting = !1;
    const [a, i] = await Promise.all([
      this._loadRunDetail(t.runId),
      this._loadExistingFeedback(t.runId)
    ]);
    this._selectedRunId === t.runId && (!a || !i) && ([
      this._selectedRunId,
      this._runDetail,
      this._runDetailState,
      this._existingFeedback,
      this._existingFeedbackState,
      this._hasSeededFromExisting
    ] = s);
  }
  /**
   * Returns the row whose `createdBy` matches the resolved current-user id.
   * Returns `undefined` if no current-user-id is available (Task 0h fallback)
   * OR if no row matches — Submit-disable + Edit gating both treat both
   * cases as "no existing row for this editor".
   */
  _findCurrentUserRow(e) {
    if (this._currentUserId !== null)
      return e.find((t) => t.createdBy === this._currentUserId);
  }
  render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }
  _renderForm() {
    const e = this._score !== null, t = this._existingFeedback !== null ? this._findCurrentUserRow(this._existingFeedback) : void 0, s = t?.score === "Neutral" ? null : t?.score ?? null, a = t?.comment ?? "", i = t !== void 0 && this._score === s && this._comment === a, r = !e || this._state === "submitting" || i, d = this._state === "submitting" ? "waiting" : void 0;
    return o`
      <umb-body-layout headline="Run Feedback">
        ${this._renderExistingFeedback()}
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

          ${e ? o`<uui-textarea
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
            ?disabled=${r}
            state=${d ?? u}
            @click=${this._submit}
          >
            Submit feedback
          </uui-button>
        </div>
      </umb-body-layout>
    `;
  }
  _renderSuccess() {
    return o`
      <umb-body-layout headline="Run Feedback">
        ${this._renderExistingFeedback()}
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
    return o`<p role="alert" class="error">${this._errorMessage}</p>`;
  }
  /**
   * Story 4.5 Q1 — renders editor feedback rows that already exist for this
   * run (Previous-feedback block) so the editor sees their (and others')
   * prior thumbs-up/down + comments. The current user's row carries an Edit
   * affordance that pre-populates the form for supersede; other editors'
   * rows render in display-only mode (collegial collaboration framing).
   *
   * Render conditions: omitted entirely when state is not "loaded" OR the
   * `existing` array is empty (no UI noise on the no-prior-feedback case).
   *
   * XSS defence pin (Story 4.5 AC11 + Story 2.3 AC9 + Story 4.2 AC6):
   * comment text + display name render via Lit auto-encoding; no
   * Lit's raw-HTML directive.
   */
  _renderExistingFeedback() {
    if (this._existingFeedbackState !== "loaded")
      return u;
    const e = this._existingFeedback;
    if (e === null || e.length === 0)
      return u;
    const t = this._findCurrentUserRow(e), s = e.filter((i) => i !== t).sort((i, r) => r.createdUtc.localeCompare(i.createdUtc)), a = t !== void 0 ? [t, ...s] : s;
    return o`
      <uui-box headline="Previous feedback" class="previous-feedback-box">
        ${a.map((i) => this._renderExistingFeedbackRow(i, i === t))}
      </uui-box>
    `;
  }
  _renderExistingFeedbackRow(e, t) {
    const s = e.score === "ThumbsUp" ? "👍" : e.score === "ThumbsDown" ? "👎" : "•", a = e.createdByDisplayName ?? "An editor";
    let i = e.createdUtc;
    try {
      const r = new Date(e.createdUtc);
      Number.isNaN(r.getTime()) || (i = r.toLocaleString());
    } catch {
    }
    return o`
      <div class="previous-feedback-row">
        <p class="previous-feedback-content">
          <span class="previous-feedback-emoji" aria-hidden="true">${s}</span>
          ${e.comment !== null && e.comment.length > 0 ? o`<span class="previous-feedback-comment">${e.comment}</span>` : o`<span class="previous-feedback-no-comment">(no comment)</span>`}
        </p>
        <p class="previous-feedback-footer">
          ${a} · ${i}
        </p>
        ${t && e.score !== "Neutral" ? o`<uui-button
              look="secondary"
              label="Edit"
              class="previous-feedback-edit-button"
              @click=${() => this._onEditClick(e)}
            >
              Edit
            </uui-button>` : u}
      </div>
    `;
  }
  /**
   * Story 4.5 AC8.j + DRIFT-4.12-CR-3 (Adam UX 2026-05-28) — Edit-button
   * click pre-populates the form from the existing-feedback row + acts as
   * the explicit intent signal for supersede. The auto-seed in
   * `_loadExistingFeedback` was removed (form stays empty until Edit is
   * clicked) so this handler is the ONLY path that populates the form for
   * an existing-feedback row.
   *
   * Submit-disable-on-no-change (AC10) keeps Submit disabled until the
   * editor actually mutates score or comment from the seeded values —
   * clicking Edit alone does NOT enable Submit.
   *
   * After seeding, scrolls the textarea into view + focuses it so the
   * editor's next action is "modify your prior feedback" (anchor-to-edit-
   * box UX per Adam 2026-05-28).
   *
   * Neutral score is display-only in the existing-feedback block (Story 2.3
   * widget only emits ThumbsUp / ThumbsDown); Neutral rows do NOT carry an
   * Edit button per AC8.i so this handler never receives one.
   */
  async _onEditClick(e) {
    this._score = e.score === "Neutral" ? null : e.score, this._comment = e.comment ?? "", this._state === "error" && (this._state = "idle", this._errorMessage = ""), await this.updateComplete;
    const t = this.shadowRoot?.querySelector("uui-textarea");
    t != null && (t.scrollIntoView({ behavior: "smooth", block: "nearest" }), t.focus());
  }
  /**
   * Story 4.12 — picker row above the agent-output content. Renders only when
   * `_siblings.length > 1` (LD#6: single-iteration flows preserve Story 4.5
   * UX byte-identically). Shape: `[←] Iteration N of M · {hh:mm:ss} [→]` —
   * agent-agnostic per LD#4; no content-type vocabulary in v0.1.
   *
   * Arrows are disabled at boundaries (first iteration: ← disabled; last
   * iteration: → disabled) AND during in-flight feedback submission
   * (`_state === "submitting"` per § Failure edges — submit-in-flight + picker
   * change race protection).
   *
   * XSS defence: all picker labels are static strings or trusted timestamp
   * strings; no user-controlled content is rendered via this template.
   */
  _renderPicker() {
    if (this._siblingsState === "loaded" && this._siblings.length === 0)
      return o`
        <p class="picker-empty-batch" role="status">
          No iterations available — workflow may have iterated over zero items.
        </p>
      `;
    if (this._siblingsState !== "loaded" || this._siblings.length <= 1)
      return u;
    const e = this._siblings.findIndex(
      (_) => _.runId === this._selectedRunId
    );
    e < 0 && console.warn(
      "[cogworks-agent-feedback] picker: _selectedRunId is not present in _siblings; falling back to index 0. Submit will POST the orphan RunId.",
      { selectedRunId: this._selectedRunId, siblingCount: this._siblings.length }
    );
    const t = e >= 0 ? e : 0, s = this._siblings.length, a = this._siblings[t], i = this._state === "submitting", r = t === 0 || i, d = t === s - 1 || i, g = new Date(a.startedUtc), h = Number.isNaN(g.getTime()) ? a.startedUtc : g.toLocaleTimeString();
    return o`
      <div class="picker-row" role="group" aria-label="Iteration picker">
        <uui-button
          compact
          look="secondary"
          label="Previous iteration"
          class="picker-prev"
          ?disabled=${r}
          @click=${() => this._onPickerChange(t - 1)}
        >
          ←
        </uui-button>
        <span class="picker-counter" aria-live="polite">
          Iteration ${t + 1} of ${s} · ${h}
        </span>
        <uui-button
          compact
          look="secondary"
          label="Next iteration"
          class="picker-next"
          ?disabled=${d}
          @click=${() => this._onPickerChange(t + 1)}
        >
          →
        </uui-button>
      </div>
    `;
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
   * Lit's raw-HTML directive is NEVER imported in this file — static grep gate
   * over the directive token returns zero matches.
   *
   * Story 4.12 — the picker row is rendered FIRST inside the agent-output
   * uui-box (above the score/issues/suggestions content) when siblings > 1.
   * Hidden otherwise.
   */
  _renderAgentOutput() {
    if (this._runDetailState === "loading")
      return o`
        <uui-box headline="Agent output" class="agent-output-box">
          ${this._renderPicker()}
          <p class="agent-output-loading">
            <uui-loader></uui-loader>
            Loading agent output…
          </p>
        </uui-box>
      `;
    if (this._runDetailState === "unavailable" || this._runDetail === null)
      return o`
        <uui-box headline="Agent output" class="agent-output-box">
          ${this._renderPicker()}
          <p class="agent-output-unavailable">
            Agent output unavailable; you can still submit feedback below.
          </p>
        </uui-box>
      `;
    const e = this._runDetail, t = e.issues.length > 0, s = e.suggestions.length > 0, a = e.score !== null || t || s, i = e.memoryUsed && e.citedMemories.length > 0;
    return o`
      <uui-box headline="Agent output" class="agent-output-box">
        ${e.memoryUsed ? o`<uui-tag
              slot="header-actions"
              color="positive"
              class="memory-used-badge"
            >
              Memory used
            </uui-tag>` : u}
        ${this._renderPicker()}
        <p class="agent-output-identity">
          ${e.agentDisplayName ?? `Agent ${e.agentId?.slice(0, 8) ?? "unknown"}`}
        </p>
        ${e.score !== null ? o`<p class="agent-output-score">
              Score: <strong>${e.score}</strong>
            </p>` : u}
        ${t ? o`
              <h5 class="agent-output-section-heading">Flagged issues</h5>
              <ul class="agent-output-issues">
                ${e.issues.map(
      (r) => o`
                    <li>
                      <span class="agent-output-issue-text">${r.text}</span>
                      ${r.reason ? o`<span class="agent-output-issue-reason">
                            — ${r.reason}
                          </span>` : u}
                    </li>
                  `
    )}
              </ul>
            ` : u}
        ${s ? o`
              <h5 class="agent-output-section-heading">Suggestions</h5>
              <ul class="agent-output-suggestions">
                ${e.suggestions.map(
      (r) => o`<li>${r}</li>`
    )}
              </ul>
            ` : u}
        ${a ? u : o`<p class="agent-output-empty-note">
              (no structured output captured for this run)
            </p>`}
        ${i ? o`
              <details class="cited-memories-details">
                <summary>
                  ${e.citedMemories.length === 1 ? "1 memory cited" : `${e.citedMemories.length} memories cited`}
                </summary>
                <ul class="cited-memories-list">
                  ${e.citedMemories.map(
      (r) => o`
                      <li class="cited-memory-row">
                        <span class="cited-memory-run">Run ${r.runIdPrefix}</span>
                        <span class="cited-memory-emoji" aria-hidden="true">${r.emoji}</span>
                        ${r.commentSnippet !== null ? o`<span class="cited-memory-snippet">
                              · "${r.commentSnippet}"
                            </span>` : o`<span class="cited-memory-no-comment">
                              · (no comment)
                            </span>`}
                      </li>
                    `
    )}
                </ul>
              </details>
            ` : u}
      </uui-box>
    `;
  }
  _selectScore(e) {
    this._score = e, this._state === "error" && (this._state = "idle", this._errorMessage = "");
  }
  async _handleHttpError(e) {
    if (e.status === 401) {
      this._state = "error", this._errorMessage = "Couldn't authenticate your backoffice session. Refresh the page and try again.";
      return;
    }
    if (e.status === 404) {
      this._state = "error", this._errorMessage = "This run hasn't been audit-logged yet. Wait a moment and try again — or refresh the page if it keeps failing.";
      return;
    }
    if (e.status === 400) {
      try {
        const t = await e.json();
        if (typeof t?.detail == "string" && t.detail.length > 0) {
          this._state = "error", this._errorMessage = t.detail;
          return;
        }
        if (t?.errors && typeof t.errors == "object") {
          const s = Object.values(t.errors)[0];
          if (Array.isArray(s) && typeof s[0] == "string") {
            this._state = "error", this._errorMessage = s[0];
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
n.styles = f`
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

    /* Story 4.8 — agent attribution line above the score. Falls back to
       "Agent {first-8-of-guid}" when AgentDisplayName is null (NFR-R1
       graceful degradation from IAIAgentService.GetAgentAsync). */
    .agent-output-identity {
      margin: 0 0 var(--uui-size-space-2) 0;
      font-weight: 600;
      color: var(--uui-color-text-alt);
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

    /* Story 4.5 — Previous-feedback block (Q1; AC8) + Memory-used badge + cited memories (Q2a; AC9). */
    .previous-feedback-box {
      display: block;
      margin-bottom: var(--uui-size-space-4);
    }

    .previous-feedback-row {
      padding: var(--uui-size-space-2) 0;
      border-bottom: 1px solid var(--uui-color-border);
    }

    .previous-feedback-row:last-child {
      border-bottom: none;
    }

    .previous-feedback-content {
      margin: 0 0 var(--uui-size-space-1) 0;
      display: flex;
      align-items: flex-start;
      gap: var(--uui-size-space-2);
    }

    .previous-feedback-emoji {
      flex-shrink: 0;
    }

    .previous-feedback-comment {
      white-space: pre-wrap;
      word-break: break-word;
    }

    .previous-feedback-no-comment {
      color: var(--uui-color-text-alt);
      font-style: italic;
    }

    .previous-feedback-footer {
      margin: 0 0 var(--uui-size-space-2) 0;
      color: var(--uui-color-text-alt);
      font-size: var(--uui-type-small-size, 0.875rem);
    }

    .previous-feedback-edit-button {
      margin-top: var(--uui-size-space-1);
    }

    /* Memory-used badge sits in the agent-output uui-box's header-actions slot. */
    .memory-used-badge {
      align-self: center;
    }

    .cited-memories-details {
      margin-top: var(--uui-size-space-3);
      font-size: var(--uui-type-small-size, 0.875rem);
    }

    .cited-memories-details summary {
      cursor: pointer;
      color: var(--uui-color-text-alt);
      user-select: none;
    }

    .cited-memories-list {
      margin: var(--uui-size-space-2) 0 0 0;
      padding-left: var(--uui-size-space-5);
    }

    .cited-memory-row {
      margin-bottom: var(--uui-size-space-1);
    }

    .cited-memory-run {
      font-weight: 600;
    }

    .cited-memory-snippet {
      color: var(--uui-color-text-alt);
    }

    .cited-memory-no-comment {
      color: var(--uui-color-text-alt);
      font-style: italic;
    }

    /* Story 4.12 — picker row at the top of the agent-output uui-box.
       Single horizontal row, agent-agnostic shape. Token-driven foreground
       per Story 3.4 precedent (uui-color-text-alt). */
    .picker-row {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-2);
      margin: 0 0 var(--uui-size-space-3) 0;
    }

    .picker-counter {
      color: var(--uui-color-text-alt);
      font-size: var(--uui-type-small-size, 0.875rem);
      font-variant-numeric: tabular-nums;
    }

    .picker-empty-batch {
      margin: 0 0 var(--uui-size-space-3) 0;
      color: var(--uui-color-text-alt);
      font-style: italic;
    }
  `;
l([
  c()
], n.prototype, "_score", 2);
l([
  c()
], n.prototype, "_comment", 2);
l([
  c()
], n.prototype, "_state", 2);
l([
  c()
], n.prototype, "_errorMessage", 2);
l([
  c()
], n.prototype, "_runDetailState", 2);
l([
  c()
], n.prototype, "_runDetail", 2);
l([
  c()
], n.prototype, "_existingFeedbackState", 2);
l([
  c()
], n.prototype, "_existingFeedback", 2);
l([
  c()
], n.prototype, "_currentUserId", 2);
l([
  c()
], n.prototype, "_siblings", 2);
l([
  c()
], n.prototype, "_siblingsState", 2);
l([
  c()
], n.prototype, "_selectedRunId", 2);
n = l([
  y("cogworks-agent-feedback")
], n);
const A = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 1e4
  },
  // Story 4.9 — Memory Learning Wall dashboard. Mounts under the existing
  // Umbraco.AI section (alias `'ai'`) per Task 0a contract probe against the
  // canonical Umbraco.AI Welcome-dashboard shape at
  // `Umbraco.AI/.../Client/src/section/dashboard/manifests.ts`. The condition
  // gates the dashboard to the AI section only — adopters running the package
  // without Umbraco.AI installed will see neither the section nor the
  // dashboard (graceful absence; no boot-time error).
  //
  // `as unknown as UmbExtensionManifest`: the `UmbExtensionManifest` union
  // discriminates by `type` literal but TS doesn't always narrow the array
  // literal correctly across heterogeneous entries (modal + dashboard); the
  // cast disambiguates to the `ManifestDashboard` shape from
  // `@umbraco-cms/backoffice/dashboard` (verified at Task 0a).
  {
    type: "dashboard",
    alias: "Cogworks.AgentMemory.Dashboard.MemoryWall",
    name: "Memory Learning Wall",
    element: () => import("./cogworks-memory-wall.element-sf_vyVTe.js"),
    weight: 100,
    meta: {
      label: "Memory Learning Wall",
      pathname: "memory-learning-wall"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "ai"
      }
    ]
  }
];
export {
  p as A,
  m as a,
  A as m
};
//# sourceMappingURL=index-BO7BtkZC.js.map
