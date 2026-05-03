import { LitElement, html, css } from "@umbraco-cms/backoffice/external/lit";
import { customElement, property, state } from "@umbraco-cms/backoffice/external/lit";

/**
 * Inline feedback widget for an agent run.
 *
 * Renders thumbs-up / thumbs-down buttons + a comment textarea. On submit,
 * POSTs to /umbraco/cogworks-agent-memory/api/v1/feedback. Designed to drop
 * into the Automate run explorer next to a RunAgentAction step output, or
 * inside the Copilot chat after each agent message.
 *
 * v0.1 placeholder — Week 2 of the sprint plan completes the implementation.
 */
@customElement("cogworks-agent-feedback")
export class CogworksAgentFeedbackElement extends LitElement {
  @property({ type: String, attribute: "run-id" })
  runId = "";

  @state()
  private _score: "up" | "down" | null = null;

  @state()
  private _comment = "";

  @state()
  private _submitted = false;

  static styles = css`
    :host {
      display: block;
      padding: var(--uui-size-space-3, 0.75rem);
      border: 1px solid var(--uui-color-border, #ccc);
      border-radius: var(--uui-border-radius, 4px);
    }

    .row {
      display: flex;
      gap: var(--uui-size-space-2, 0.5rem);
      align-items: center;
    }

    button {
      cursor: pointer;
      padding: var(--uui-size-space-2, 0.5rem) var(--uui-size-space-3, 0.75rem);
    }

    button.active {
      background: var(--uui-color-selected, #3544b1);
      color: white;
    }

    textarea {
      width: 100%;
      min-height: 4rem;
      margin-top: var(--uui-size-space-2, 0.5rem);
    }
  `;

  render() {
    if (this._submitted) {
      return html`<p>Thanks — feedback recorded.</p>`;
    }

    return html`
      <div class="row">
        <button
          class=${this._score === "up" ? "active" : ""}
          @click=${() => (this._score = "up")}
          aria-label="Thumbs up"
        >
          👍
        </button>
        <button
          class=${this._score === "down" ? "active" : ""}
          @click=${() => (this._score = "down")}
          aria-label="Thumbs down"
        >
          👎
        </button>
        <span>How was this output?</span>
      </div>
      <textarea
        placeholder="Optional: explain why (helps the agent learn)"
        .value=${this._comment}
        @input=${(e: Event) =>
          (this._comment = (e.target as HTMLTextAreaElement).value)}
      ></textarea>
      <button @click=${this._submit} ?disabled=${this._score === null}>
        Submit feedback
      </button>
    `;
  }

  private async _submit() {
    if (!this._score || !this.runId) {
      return;
    }
    // Week 2: replace with real API call.
    // await fetch(`/umbraco/cogworks-agent-memory/api/v1/feedback`, { ... })
    this._submitted = true;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    "cogworks-agent-feedback": CogworksAgentFeedbackElement;
  }
}
