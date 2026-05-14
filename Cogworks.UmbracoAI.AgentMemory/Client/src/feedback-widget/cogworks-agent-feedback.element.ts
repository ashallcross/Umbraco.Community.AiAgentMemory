import { html, css, customElement, state, nothing } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import {
  authenticatedFetch,
  AuthContextUnavailableError,
} from "../util/authenticated-fetch.js";

/**
 * Modal data shape — populated by Bellissima when our extension replaces
 * Automate's `Ua.Modal.RunDetail` (Strategy B locked at Story 2.3 Task 0).
 * The modal hands us a single field: `runId`. Semantically this is the
 * upstream `Metadata.Umbraco.AI.Agent.ThreadId` (the workflow-run-level
 * conversation grouping key, 1 per Automate workflow run); the server
 * resolves agentId from it via `IAgentRunReader.GetRunsForThreadAsync`.
 */
type AgentFeedbackModalData = {
  runId: string;
};

type ScoreString = "ThumbsUp" | "ThumbsDown";
type WidgetState = "idle" | "submitting" | "success" | "error";

/**
 * Story 2.3 — inline feedback widget for an agent run.
 * Story 3.4 — UUI primitive adoption + Bellissima-native chrome.
 *
 * Replaces `Ua.Modal.RunDetail` (Strategy B locked at Story 2.3 Task 0). Renders
 * thumbs-up / thumbs-down + optional comment + submit. On submit, POSTs to
 * `/umbraco/management/api/v1/cogworks-agent-memory/feedback` (Story 2.2's
 * controller) via bearer-token auth (`UMB_AUTH_CONTEXT.getOpenApiConfiguration()`).
 *
 * NFR-A1/A2: thumbs state distinguished by BOTH icon AND text label (never colour
 * alone); `aria-pressed` toggles on selected thumb; `:focus-visible` via UUI
 * primitives' built-in focus styles; success state announced via `role="status"`;
 * error state via `role="alert"`.
 *
 * XSS defence (Story 2.3 AC9): all rendered content goes through Lit's auto-
 * encoding template interpolation. `unsafeHTML` is NEVER imported.
 *
 * Confirm-before-cancel-with-unsaved-typed-comment: deferred to v0.2 per Story 3.4
 * Locked decision #4. Current behaviour: Cancel discards unsaved comment without
 * prompting. A pure-state inline confirm OR Bellissima confirm-modal pattern is
 * a candidate when v0.2 ships broader UX polish.
 */
@customElement("cogworks-agent-feedback")
export class CogworksAgentFeedbackElement extends UmbModalBaseElement<
  AgentFeedbackModalData,
  // The modal doesn't emit a return value — feedback is fire-and-go via POST.
  // The editor closes the modal manually (X / Esc) after the success state.
  void
> {
  @state() private _score: ScoreString | null = null;
  @state() private _comment = "";
  @state() private _state: WidgetState = "idle";
  @state() private _errorMessage = "";

  private _abortController: AbortController | null = null;

  override connectedCallback() {
    super.connectedCallback();
    // Automate's opener passes `size="large"` to the `<uui-modal-sidebar>` wrapper
    // when opening `Ua.Modal.RunDetail`. We don't control the opener (we only
    // replace the modal's CONTENT element via Strategy B), but `large` is
    // excessive for the feedback form — it takes ~half the screen. Override the
    // ancestor sidebar's `size` attribute to `small` so the modal frame fits
    // the form content cleanly. Captured as DRIFT-3.4-impl-2 at Story 3.4 manual
    // gate 2026-05-14. `small` chosen at 2026-05-14 visual-gate iteration after
    // medium still rendered too large; the comment textarea's auto-height
    // growth still fits cleanly within the small frame.
    this.closest("uui-modal-sidebar")?.setAttribute("size", "small");
  }

  override disconnectedCallback() {
    this._abortController?.abort();
    super.disconnectedCallback();
  }

  override render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }

  private _renderForm() {
    const scoreSelected = this._score !== null;
    const submitDisabled = !scoreSelected || this._state === "submitting";
    const submitState = this._state === "submitting" ? "waiting" : undefined;

    // Modelled verbatim on Bellissima's `<umb-current-user-modal>` chrome:
    // `<umb-body-layout headline="...">` provides the modal-level h3 title
    // (rendered "Run Feedback" in our case) + the auto-rendered
    // `<umb-footer-layout>` whose `slot="actions"` carries the bottom-anchored
    // action buttons. Inside, a `<uui-box headline="...">` card holds the
    // prompt h5 + the form controls. Captured as DRIFT-3.4-impl-3 at Story 3.4
    // manual gate 2026-05-14 (visual gate). The X close icon from Story 3.4
    // first cut was dropped — the current-user-modal doesn't carry one either;
    // Cancel button + Esc + backdrop click are the canonical dismissal paths.
    return html`
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

          ${scoreSelected
            ? html`<uui-textarea
                auto-height
                label="Optional comment"
                placeholder="Optional — explain why (helps the agent learn)"
                maxlength="4000"
                .value=${this._comment}
                @input=${this._onCommentInput}
              ></uui-textarea>`
            : nothing}

          ${this._state === "error" ? this._renderError() : nothing}
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
            ?disabled=${submitDisabled}
            state=${submitState ?? nothing}
            @click=${this._submit}
          >
            Submit feedback
          </uui-button>
        </div>
      </umb-body-layout>
    `;
  }

  private _renderSuccess() {
    return html`
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

  private _renderError() {
    // Lit auto-encodes the template-literal interpolation — _errorMessage
    // renders as text, NEVER as HTML (Story 2.3 AC9 XSS-defence contract).
    return html`<p role="alert" class="error">${this._errorMessage}</p>`;
  }

  private _dismiss = () => {
    // Cancel any in-flight submission before closing.
    this._abortController?.abort();
    // UmbModalBaseElement provides _rejectModal() / _submitModal() — either
    // closes the modal frame. We use _rejectModal because we're not emitting
    // a return value (feedback was fire-and-go via POST already if we're in
    // success state; cancel is the user's choice if we're in idle/error).
    this._rejectModal();
  };

  private _onCommentInput = (e: Event) => {
    // `<uui-textarea>` forwards the native input event from its inner textarea
    // (`@fires InputEvent#input on input` per its .d.ts). We cast through
    // `unknown` to a structural type so we don't depend on the
    // UUITextareaElement import — `EventTarget` doesn't carry `value` so a
    // direct cast won't compile under strict TS.
    this._comment = (e.target as unknown as { value: string }).value;
  };

  private _selectScore(score: ScoreString) {
    this._score = score;
    // Clear any prior error state when the editor picks a different score.
    if (this._state === "error") {
      this._state = "idle";
      this._errorMessage = "";
    }
  }

  private _submit = async () => {
    if (!this._score) {
      // Defensive — submit is disabled until a thumb is selected. Acts as a
      // type-narrowing guard for the POST body construction below.
      return;
    }
    const runId = this.data?.runId ?? "";
    if (runId.length === 0) {
      // Bellissima/Strategy-B contract violation: the modal opened without a
      // runId in its data payload. Distinct copy from the auth-error branch so
      // the editor's next step is "reload the page", not "re-login".
      this._state = "error";
      this._errorMessage =
        "Couldn't load this run's details. Refresh the page and try again.";
      return;
    }

    // Cancel any prior in-flight submission (resubmit / retry path).
    this._abortController?.abort();
    this._abortController = new AbortController();
    this._state = "submitting";
    this._errorMessage = "";

    try {
      const response = await authenticatedFetch(
        () => this.getContext(UMB_AUTH_CONTEXT),
        "/umbraco/management/api/v1/cogworks-agent-memory/feedback",
        {
          method: "POST",
          body: {
            runId,
            score: this._score,
            comment: this._comment.length > 0 ? this._comment : null,
          },
          signal: this._abortController.signal,
        },
      );
      if (response.ok) {
        this._state = "success";
        return;
      }
      await this._handleHttpError(response);
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") {
        // Silent — user navigated away mid-submit. Don't surface an error.
        return;
      }
      if (err instanceof AuthContextUnavailableError) {
        this._state = "error";
        this._errorMessage =
          "Couldn't authenticate your backoffice session. Refresh the page and try again.";
        return;
      }
      this._state = "error";
      this._errorMessage =
        "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
    }
  };

  private async _handleHttpError(response: Response) {
    if (response.status === 401) {
      this._state = "error";
      this._errorMessage =
        "Couldn't authenticate your backoffice session. Refresh the page and try again.";
      return;
    }
    if (response.status === 404) {
      // Story 2.3 Task 0.6 — controller returns 404 when the AIAuditLog row
      // for this runId/ThreadId isn't yet visible to IAgentRunReader. Two
      // benign causes: (a) the agent run hasn't finished writing its audit-
      // log row yet (microsecond race window), OR (b) upstream Fork (i)
      // metadata propagation isn't deployed on this host. Either way, the
      // editor's recovery action is the same: wait + retry.
      this._state = "error";
      this._errorMessage =
        "This run hasn't been audit-logged yet. Wait a moment and try again — or refresh the page if it keeps failing.";
      return;
    }
    if (response.status === 400) {
      // Two response shapes per Story 2.2 DRIFT-2.2-impl-5:
      //   (a) our ProblemDetails  →  `{ detail: "..." }`
      //   (b) framework ModelState →  `{ errors: { "$.field": ["..."] } }`
      try {
        const body = await response.json();
        if (typeof body?.detail === "string" && body.detail.length > 0) {
          this._state = "error";
          this._errorMessage = body.detail;
          return;
        }
        if (body?.errors && typeof body.errors === "object") {
          const firstFieldErrors = Object.values(body.errors)[0];
          if (
            Array.isArray(firstFieldErrors) &&
            typeof firstFieldErrors[0] === "string"
          ) {
            this._state = "error";
            this._errorMessage = firstFieldErrors[0];
            return;
          }
        }
      } catch {
        // Body parse failure — fall through to the 400-specific generic below.
      }
      // 400 reached but the body matched neither ProblemDetails nor ModelState.
      // Validation failure of unknown shape — surface as "your input was rejected"
      // not "server hiccup, try again" so the editor's next step is to review
      // their input, not to retry the same submission.
      this._state = "error";
      this._errorMessage =
        "Your submission was rejected. Refresh and try again — if it keeps failing, the run may no longer accept feedback.";
      return;
    }
    this._state = "error";
    this._errorMessage =
      "Something went wrong submitting your feedback. Try again — if it keeps failing, refresh the page.";
  }

  static override styles = css`
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
}

declare global {
  interface HTMLElementTagNameMap {
    "cogworks-agent-feedback": CogworksAgentFeedbackElement;
  }
}
