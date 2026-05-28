import { html, css, customElement, state, nothing } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement } from "@umbraco-cms/backoffice/modal";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";
import { UMB_CURRENT_USER_CONTEXT } from "@umbraco-cms/backoffice/current-user";
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
type RunDetailState = "loading" | "loaded" | "unavailable";
type ExistingFeedbackState = "loading" | "loaded" | "unavailable";
type SiblingsState = "loading" | "loaded" | "unavailable";

/**
 * Story 4.12 — one sibling iteration in a For Each batch workflow. Returned
 * by GET /umbraco/management/api/v1/cogworks-agent-memory/runs/{threadId}/siblings.
 *
 * `threadId` is the workflow-run grouping key (modalContext.data.runId);
 * `runId` is the per-iteration agent-invocation key surfaced by the picker
 * arrows. Sort order from the server is ASC by `startedUtc` (oldest first)
 * so the widget can walk forward through the batch.
 */
type AgentRunSibling = {
  threadId: string;
  runId: string;
  startedUtc: string;
};

/**
 * Wire-format of `FeedbackScore` per Umbraco Management-API's default
 * `JsonStringEnumConverter` — string-name, NOT numeric ordinal. Verified at
 * Story 4.5 Task 0e against `AgentFeedbackControllerTests.cs:89, 105` + the
 * existing widget's POST body shape. Literal-union (not a remote-enum copy)
 * so the widget stays decoupled from server enum ordinal changes.
 */
type FeedbackScoreWire = "ThumbsUp" | "ThumbsDown" | "Neutral";

/**
 * One bullet from Story 3.3's "Lessons from past runs" memory-injection block,
 * parsed by `AgentRunReadController.ParseMemoryInjection` and surfaced to the
 * widget. Story 4.5 Q2a — Memory-used indicator + cited memories.
 */
type AgentRunCitedMemory = {
  runIdPrefix: string;
  emoji: string;
  commentSnippet: string | null;
};

/**
 * Shape of the GET /umbraco/management/api/v1/cogworks-agent-memory/runs/{runId}
 * response — projected from `AgentRunRecord.ResponseSnapshotJoined` parsed as
 * the Brand Voice Auditor agent's structured-output schema (Story 4.1 AC3)
 * + Story 4.5 memory-injection citation surface.
 *
 * Story 4.2 closes DRIFT-4.1-12 by rendering score/issues/suggestions above
 * the feedback form. Story 4.5 extends with `memoryUsed` + `citedMemories`
 * for the Memory-used badge + expandable cited-memories list.
 */
type AgentRunDetail = {
  runId: string;
  agentId: string;
  agentDisplayName: string | null;
  contentNodeName: string | null;
  ranAtUtc: string;
  score: number | null;
  issues: { text: string; reason: string | null }[];
  suggestions: string[];
  memoryUsed: boolean;
  citedMemories: AgentRunCitedMemory[];
};

/**
 * Shape of the GET /umbraco/management/api/v1/cogworks-agent-memory/feedback/{runId}
 * response — Story 4.5 Q1 feedback-read endpoint composing on
 * `IAgentFeedbackService.GetFeedbackForRunAsync`.
 */
type AgentRunFeedbackEntry = {
  score: FeedbackScoreWire;
  comment: string | null;
  createdBy: string;
  createdByDisplayName: string | null;
  createdUtc: string;
};

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
   * encoding template interpolation. Lit's raw-HTML directive is NEVER imported.
 *
 * Confirm-before-cancel-with-unsaved-typed-comment: deferred to v0.2 per Story 3.4
 * Locked decision #4. Current behaviour: Cancel discards unsaved comment without
 * prompting. A pure-state inline confirm OR Bellissima confirm-modal pattern is
 * a candidate when v0.2 ships broader UX polish.
 */
@customElement("aiam-agent-feedback")
export class AiamAgentFeedbackElement extends UmbModalBaseElement<
  AgentFeedbackModalData,
  // The modal doesn't emit a return value — feedback is fire-and-go via POST.
  // The editor closes the modal manually (X / Esc) after the success state.
  void
> {
  @state() private _score: ScoreString | null = null;
  @state() private _comment = "";
  @state() private _state: WidgetState = "idle";
  @state() private _errorMessage = "";
  @state() private _runDetailState: RunDetailState = "loading";
  @state() private _runDetail: AgentRunDetail | null = null;
  @state() private _existingFeedbackState: ExistingFeedbackState = "loading";
  @state() private _existingFeedback: AgentRunFeedbackEntry[] | null = null;
  @state() private _currentUserId: string | null = null;

  // Story 4.12 — picker state. `_siblings` is empty until the siblings fetch
  // settles; `_selectedRunId` is null until we know we have > 1 sibling, at
  // which point we initialise it to the FIRST iteration in the ASC list
  // (oldest — natural sequential-walk-through entry point per LD#3a).
  @state() private _siblings: AgentRunSibling[] = [];
  @state() private _siblingsState: SiblingsState = "loading";
  @state() private _selectedRunId: string | null = null;

  private _abortController: AbortController | null = null;
  private _runDetailAbortController: AbortController | null = null;
  private _existingFeedbackAbortController: AbortController | null = null;
  private _siblingsAbortController: AbortController | null = null;

  // Promise of the current-user-id resolution. Captured at connect time so
  // `_loadExistingFeedback` can await it before computing render-time
  // current-user-row matching (Submit-disable-on-no-change at line ~625
  // depends on `_findCurrentUserRow` resolving consistently).
  private _currentUserIdReady: Promise<void> | null = null;

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

    // Story 4.5 Task 0h — resolve the current authenticated user GUID via
    // UMB_CURRENT_USER_CONTEXT so the widget can decide which feedback row
    // gets the Edit button (AC8.i) + drive the Submit-disable-on-no-change
    // computation (AC10). Independent of run-detail/feedback fetches; runs
    // in parallel with the siblings probe.
    this._currentUserIdReady = this._resolveCurrentUserId();

    // Story 4.12 — siblings probe is the SINGLE entry point for run-detail
    // and existing-feedback fetches. When it lands it decides the mode:
    //   - length > 1 → picker mode → fire fetches keyed by selectedRunId
    //     (first iteration in ASC order per LD#3a)
    //   - length ≤ 1 OR unavailable → legacy mode → fire fetches keyed by
    //     ThreadId (Story 4.5 byte-compat single-iteration flow)
    //
    // Eliminates AC3.b first-render flash that would otherwise occur if the
    // legacy ThreadId fetches resolved before the siblings probe (they
    // would render runs[0] briefly before the picker overwrote it). One
    // extra round-trip of latency vs the previous parallel kickoff; siblings
    // is a cheap projection from the same IAgentRunReader call the legacy
    // detail fetch already makes, so cost is sub-100ms typical.
    void this._loadSiblings();
  }

  override disconnectedCallback() {
    this._abortController?.abort();
    this._runDetailAbortController?.abort();
    this._existingFeedbackAbortController?.abort();
    this._siblingsAbortController?.abort();
    super.disconnectedCallback();
  }

  private async _resolveCurrentUserId() {
    try {
      const ctx = await this.getContext(UMB_CURRENT_USER_CONTEXT);
      // `getUnique()` returns the user GUID-as-string (matches server-side
      // IBackOfficeSecurityAccessor.CurrentUser?.Key). When unavailable
      // (context throws, returns undefined, or getUnique() returns null),
      // `_currentUserId` stays null and `_findCurrentUserRow` returns
      // undefined for every row — no Edit button renders for ANY row +
      // Submit-disable-on-no-change does not engage. This is the conservative
      // fallback (no false-positive Edit affordance on others' rows); v0.1.x
      // could expand to "all rows show Edit; POST handler rejects mismatches"
      // (Story 4.5 Task 0h fallback path) if adopter telemetry shows the
      // current-user resolution failing in the wild. Manual gate Step 5
      // confirmed the canonical UMB_CURRENT_USER_CONTEXT path works in
      // production against Bellissima 17.3.2.
      this._currentUserId = ctx?.getUnique() ?? null;
    } catch {
      this._currentUserId = null;
    }
  }

  private async _loadRunDetail(selectedRunIdAtLaunch: string | null): Promise<boolean> {
    const threadId = this.data?.runId ?? "";
    if (threadId.length === 0) {
      // Bellissima/Strategy-B contract violation already surfaced by _submit;
      // don't double-error here. Just mark the agent-output panel unavailable.
      this._runDetailState = "unavailable";
      return false;
    }

    this._runDetailAbortController?.abort();
    // Capture the controller locally so post-await guards check the SAME
    // controller's signal — concurrent re-entry would reassign the field
    // before the prior fetch resolves, stranding stale data otherwise.
    // Story 4.2 § Review Findings patch #7 (race) + patch #2 (abort-during-
    // json) + patch #8 (shape-mismatch defensive guard).
    const controller = new AbortController();
    this._runDetailAbortController = controller;
    this._runDetailState = "loading";

    // Story 4.12 — when the picker is active, the URL targets a specific
    // iteration via ?selectedRunId=. When inactive (initial load OR single-
    // iteration flow), the URL is the Story 4.5 ThreadId-only shape.
    const url = selectedRunIdAtLaunch !== null
      ? `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(threadId)}?selectedRunId=${encodeURIComponent(selectedRunIdAtLaunch)}`
      : `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(threadId)}`;

    try {
      const response = await authenticatedFetch(
        () => this.getContext(UMB_AUTH_CONTEXT),
        url,
        { signal: controller.signal },
      );
      // If the controller has been aborted (component disconnected, or a
      // concurrent _loadRunDetail invocation has superseded us), bail out
      // silently rather than mutating @state on a stale or disconnected element.
      // Stale early-returns surface as `true` to the caller so picker-change
      // rollback (Story 4.12 AC4.e) doesn't fire on staleness alone.
      if (controller.signal.aborted) {
        return true;
      }
      // Story 4.12 — second staleness check: if the picker has moved on since
      // we launched (e.g. legacy fetch resolves AFTER the siblings fetch has
      // already initialised _selectedRunId), don't clobber the currently-
      // selected iteration's state. Abort would normally cover this, but the
      // race window between abort and state mutation is wide enough to warrant
      // explicit defence (mirrors Story 4.9 review-patch pattern).
      if (this._selectedRunId !== selectedRunIdAtLaunch) {
        return true;
      }
      if (!response.ok) {
        // 404 / 500 / any non-2xx → graceful degradation. The feedback form
        // remains usable; the agent-output panel surfaces the unavailable
        // notice.
        this._runDetailState = "unavailable";
        return false;
      }
      const body = (await response.json()) as AgentRunDetail;
      if (controller.signal.aborted) {
        return true;
      }
      if (this._selectedRunId !== selectedRunIdAtLaunch) {
        return true;
      }
      // Defensive shape guard — if the server returns a 200 with a structure
      // that doesn't match AgentRunDetail (e.g., a ProblemDetails body shipped
      // accidentally, or an adopter-deployed proxy rewrites the response),
      // treat as unavailable rather than crashing the render path later.
      if (!Array.isArray(body.issues) || !Array.isArray(body.suggestions)) {
        this._runDetailState = "unavailable";
        return false;
      }
      // Backend parse-failure / empty-structured-output shape. AC7 treats this
      // the same as 404 from the widget perspective: the editor sees the
      // unavailable notice and can still submit feedback below.
      //
      // Story 4.5 AC9.b — amended guard: when `memoryUsed === true` we render
      // the agent-output box anyway (Memory-used badge + cited memories) even
      // if score/issues/suggestions are all empty (edge case: agent emits a
      // chat reply without a structured-output JSON tail).
      if (
        body.score === null &&
        body.issues.length === 0 &&
        body.suggestions.length === 0 &&
        !body.memoryUsed
      ) {
        this._runDetailState = "unavailable";
        return false;
      }
      this._runDetail = body;
      this._runDetailState = "loaded";
      return true;
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") {
        // Editor navigated away mid-fetch; don't surface anything.
        return true;
      }
      // `response.json()` mid-stream abort throws a `TypeError` (not an
      // `AbortError`) per the fetch spec — so the AbortError catch above
      // doesn't cover that case. Check the controller's signal explicitly:
      // if we're aborted, the editor navigated away after the fetch resolved
      // but before the body finished streaming — silently bail.
      if (controller.signal.aborted) {
        return true;
      }
      // AuthContextUnavailableError, network error, JSON parse failure — all
      // converge on the same UX: the agent-output panel marks unavailable;
      // the feedback form is still rendered.
      this._runDetailState = "unavailable";
      return false;
    }
  }

  private async _loadExistingFeedback(selectedRunIdAtLaunch: string | null): Promise<boolean> {
    const threadId = this.data?.runId ?? "";
    if (threadId.length === 0) {
      this._existingFeedbackState = "unavailable";
      return false;
    }

    this._existingFeedbackAbortController?.abort();
    // Capture controller locally so post-await guards check the SAME
    // controller's signal — mirror of `_loadRunDetail`'s hardened idiom per
    // Story 4.2 review patches #2 + #7 + #8.
    const controller = new AbortController();
    this._existingFeedbackAbortController = controller;
    this._existingFeedbackState = "loading";

    // Story 4.12 — when the picker is active, the GET /feedback/{id} call
    // targets the per-iteration RunId (so the form seeds from THAT iteration's
    // prior feedback). When inactive, the URL is the Story 4.5 ThreadId
    // shape — preserves the seed contract byte-compatibly.
    const feedbackId = selectedRunIdAtLaunch ?? threadId;

    try {
      const response = await authenticatedFetch(
        () => this.getContext(UMB_AUTH_CONTEXT),
        `/umbraco/management/api/v1/cogworks-agent-memory/feedback/${encodeURIComponent(feedbackId)}`,
        { signal: controller.signal },
      );
      // Stale early-returns surface as `true` so picker rollback (AC4.e)
      // doesn't fire on staleness alone.
      if (controller.signal.aborted) {
        return true;
      }
      if (this._selectedRunId !== selectedRunIdAtLaunch) {
        return true;
      }
      if (!response.ok) {
        this._existingFeedbackState = "unavailable";
        return false;
      }
      const body = (await response.json()) as {
        runId: string;
        existing: AgentRunFeedbackEntry[];
      };
      if (controller.signal.aborted) {
        return true;
      }
      if (this._selectedRunId !== selectedRunIdAtLaunch) {
        return true;
      }
      // Defensive shape guard — ProblemDetails accidentally shipped on 200,
      // adopter proxy rewriting, etc. → unavailable.
      if (!Array.isArray(body.existing)) {
        this._existingFeedbackState = "unavailable";
        return false;
      }
      this._existingFeedback = body.existing;
      this._existingFeedbackState = "loaded";

      // DRIFT-4.12-CR-3 (Adam UX 2026-05-28) — DO NOT auto-seed `_score` /
      // `_comment` from the existing-feedback row. The Edit button on the
      // current user's prior row is the explicit unlock for supersede; until
      // clicked, the form stays empty (no thumb selected → textarea hidden
      // by the `scoreSelected` render guard). This makes the Previous-feedback
      // block a true read-only summary AND gives the Edit button a visible
      // job (auto-seeding made Edit a no-op because the form was already
      // pre-filled before the editor could click it). Submit-disable-on-
      // no-change (line ~625-640) still works because `seededScore` /
      // `seededComment` are computed at render time from `_existingFeedback`,
      // not from `_score` / `_comment`. The seed lives in `_onEditClick`.
      //
      // We still wait for `_currentUserIdReady` so `_findCurrentUserRow` in
      // the render path resolves consistently (used to decide whether to
      // show the Edit button against a row).
      if (this._currentUserIdReady !== null) {
        await this._currentUserIdReady;
        if (controller.signal.aborted) {
          return true;
        }
      }
      return true;
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") {
        return true;
      }
      if (controller.signal.aborted) {
        return true;
      }
      // AuthContextUnavailableError, network errors, JSON parse failures,
      // shape mismatches — all converge on unavailable; form remains usable.
      this._existingFeedbackState = "unavailable";
      return false;
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
  private async _loadSiblings() {
    const threadId = this.data?.runId ?? "";
    if (threadId.length === 0) {
      this._siblingsState = "unavailable";
      this._kickOffDownstreamFetches(null);
      return;
    }

    this._siblingsAbortController?.abort();
    const controller = new AbortController();
    this._siblingsAbortController = controller;

    try {
      const response = await authenticatedFetch(
        () => this.getContext(UMB_AUTH_CONTEXT),
        `/umbraco/management/api/v1/cogworks-agent-memory/runs/${encodeURIComponent(threadId)}/siblings`,
        { signal: controller.signal },
      );
      if (controller.signal.aborted) {
        return;
      }
      if (!response.ok) {
        this._siblingsState = "unavailable";
        this._kickOffDownstreamFetches(null);
        return;
      }
      const body = (await response.json()) as unknown;
      if (controller.signal.aborted) {
        return;
      }
      // Defensive shape guard — controller emits IReadOnlyList<AgentRunSiblingResponse>
      // (a JSON array). Any other shape (ProblemDetails on 200, proxy rewrite,
      // …) marks unavailable and falls through to the legacy single-iteration path.
      if (!Array.isArray(body)) {
        this._siblingsState = "unavailable";
        this._kickOffDownstreamFetches(null);
        return;
      }
      const siblings = body as AgentRunSibling[];
      this._siblings = siblings;
      this._siblingsState = "loaded";

      // Picker only activates when we have > 1 iteration. Single-iteration
      // (or empty) responses preserve Story 4.5 single-article UX byte-
      // identically (AC3.g).
      if (siblings.length > 1) {
        // Default-select FIRST iteration (oldest in ASC-sorted list) per
        // LD#3a — natural sequential-walk-through entry point.
        const firstRunId = siblings[0].runId;
        this._selectedRunId = firstRunId;
        this._kickOffDownstreamFetches(firstRunId);
      } else {
        // Legacy single-iteration flow (length ≤ 1) — keyed by ThreadId per
        // Story 4.5 byte-compat. No picker; selectedRunId stays null.
        this._kickOffDownstreamFetches(null);
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") {
        return;
      }
      if (controller.signal.aborted) {
        return;
      }
      this._siblingsState = "unavailable";
      this._kickOffDownstreamFetches(null);
    }
  }

  /**
   * Story 4.12 — fires run-detail + existing-feedback fetches with the
   * resolved key. Called by `_loadSiblings` after the probe settles so the
   * fetches don't flash the legacy runs[0] response before picker mode is
   * known (AC3.b). Lifecycle-guarded — bails if the element disconnected
   * mid-probe.
   *
   * @param selectedRunIdAtLaunch  per-iteration RunId (picker mode) or
   *                                `null` for legacy ThreadId-keyed mode
   */
  private _kickOffDownstreamFetches(selectedRunIdAtLaunch: string | null) {
    if (!this.isConnected) {
      return;
    }
    void this._loadRunDetail(selectedRunIdAtLaunch);
    void this._loadExistingFeedback(selectedRunIdAtLaunch);
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
  private async _onPickerChange(newIndex: number) {
    if (this._state === "submitting") return;
    if (newIndex < 0 || newIndex >= this._siblings.length) return;
    const target = this._siblings[newIndex];
    if (target.runId === this._selectedRunId) return;

    // DRIFT-4.12-CR-2 (Adam UX reversal 2026-05-28) — each picker iteration
    // is a distinct page, NOT a peek. Clear all form state on every
    // navigation so the new iteration renders either (a) empty form +
    // thumb-fresh OR (b) seeded from that iteration's OWN existing feedback
    // row via _loadExistingFeedback's seed branch. This reverts the P22
    // preserve-draft contract (D3 option b ratified 2026-05-27) in favour of
    // option a (clear on nav) per Adam's live-backoffice finding that
    // preserved drafts read as "iter-1's feedback rendering against iter-2"
    // and defeats the per-iteration teaching flow.
    //
    // Cleared:
    //  - `_state` → "idle" (drops sticky error/success banners — DRIFT-CR-1)
    //  - `_errorMessage` → "" (paired with state reset)
    //  - `_score` → null  (form starts fresh)
    //  - `_comment` → ""  (form starts fresh)
    //
    // Trade-off accepted: typing a draft on iter-N then clicking arrow
    // loses the draft without warning. A future v0.2 confirm-modal could
    // soften this (see deferred-work.md).
    this._state = "idle";
    this._errorMessage = "";
    this._score = null;
    this._comment = "";

    // Snapshot fields needed for AC4.e rollback. Tuple over object literal to
    // keep the compiled-bundle footprint tight. The form-state fields above
    // are not in the snapshot — they were just cleared deliberately as the
    // intent of this handler; reverting to whatever they were before would
    // re-introduce the cross-iteration leakage we just escaped.
    const prev: [
      string | null, AgentRunDetail | null, RunDetailState,
      AgentRunFeedbackEntry[] | null, ExistingFeedbackState,
    ] = [
      this._selectedRunId, this._runDetail, this._runDetailState,
      this._existingFeedback, this._existingFeedbackState,
    ];

    this._selectedRunId = target.runId;

    const [detailOk, feedbackOk] = await Promise.all([
      this._loadRunDetail(target.runId),
      this._loadExistingFeedback(target.runId),
    ]);

    // Newer click owns the state — don't rollback to a now-stale snapshot.
    if (this._selectedRunId !== target.runId) return;

    if (!detailOk || !feedbackOk) {
      [
        this._selectedRunId, this._runDetail, this._runDetailState,
        this._existingFeedback, this._existingFeedbackState,
      ] = prev;
    }
  }

  /**
   * Returns the row whose `createdBy` matches the resolved current-user id.
   * Returns `undefined` if no current-user-id is available (Task 0h fallback)
   * OR if no row matches — Submit-disable + Edit gating both treat both
   * cases as "no existing row for this editor".
   */
  private _findCurrentUserRow(
    rows: AgentRunFeedbackEntry[],
  ): AgentRunFeedbackEntry | undefined {
    if (this._currentUserId === null) {
      return undefined;
    }
    return rows.find((r) => r.createdBy === this._currentUserId);
  }

  override render() {
    return this._state === "success" ? this._renderSuccess() : this._renderForm();
  }

  private _renderForm() {
    const scoreSelected = this._score !== null;

    // Story 4.5 AC10 — Submit-disable-on-no-change: when the current user has
    // an existing-feedback row AND the form state exactly matches it,
    // Submit stays disabled. Edit-button click is the explicit intent signal
    // for supersede; subsequent mutation (score-toggle or comment input)
    // re-enables Submit. NO confirm-modal at v0.1 per Story 3.4 LD#4 +
    // Story 4.5 supersede confirmation drop.
    const myRow = this._existingFeedback !== null
      ? this._findCurrentUserRow(this._existingFeedback)
      : undefined;
    const seededScore = myRow?.score === "Neutral"
      ? null
      : (myRow?.score as ScoreString | undefined) ?? null;
    const seededComment = myRow?.comment ?? "";
    const equalsExisting = myRow !== undefined
      && this._score === seededScore
      && this._comment === seededComment;

    const submitDisabled =
      !scoreSelected || this._state === "submitting" || equalsExisting;
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

  private _renderError() {
    // Lit auto-encodes the template-literal interpolation — _errorMessage
    // renders as text, NEVER as HTML (Story 2.3 AC9 XSS-defence contract).
    return html`<p role="alert" class="error">${this._errorMessage}</p>`;
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
  private _renderExistingFeedback() {
    if (this._existingFeedbackState !== "loaded") {
      return nothing;
    }
    const rows = this._existingFeedback;
    if (rows === null || rows.length === 0) {
      return nothing;
    }
    // Sort: current user's row first (when present), then others by
    // createdUtc DESC. Story 4.5 AC8.k.
    const myRow = this._findCurrentUserRow(rows);
    const others = rows
      .filter((r) => r !== myRow)
      .sort((a, b) => b.createdUtc.localeCompare(a.createdUtc));
    const sorted = myRow !== undefined ? [myRow, ...others] : others;

    return html`
      <uui-box headline="Previous feedback" class="previous-feedback-box">
        ${sorted.map((row) => this._renderExistingFeedbackRow(row, row === myRow))}
      </uui-box>
    `;
  }

  private _renderExistingFeedbackRow(
    row: AgentRunFeedbackEntry,
    isCurrentUserRow: boolean,
  ) {
    const emoji = row.score === "ThumbsUp"
      ? "👍"
      : row.score === "ThumbsDown"
      ? "👎"
      : "•";
    const displayName = row.createdByDisplayName ?? "An editor";
    // Best-effort locale-aware timestamp; falls back to ISO when Intl parsing
    // throws (e.g. malformed string from a misconfigured server). Lit auto-
    // encodes the resulting string.
    let timestamp = row.createdUtc;
    try {
      const date = new Date(row.createdUtc);
      if (!Number.isNaN(date.getTime())) {
        timestamp = date.toLocaleString();
      }
    } catch {
      // Keep ISO fallback.
    }
    return html`
      <div class="previous-feedback-row">
        <p class="previous-feedback-content">
          <span class="previous-feedback-emoji" aria-hidden="true">${emoji}</span>
          ${row.comment !== null && row.comment.length > 0
            ? html`<span class="previous-feedback-comment">${row.comment}</span>`
            : html`<span class="previous-feedback-no-comment">(no comment)</span>`}
        </p>
        <p class="previous-feedback-footer">
          ${displayName} · ${timestamp}
        </p>
        ${isCurrentUserRow && row.score !== "Neutral"
          ? html`<uui-button
              look="secondary"
              label="Edit"
              class="previous-feedback-edit-button"
              @click=${() => this._onEditClick(row)}
            >
              Edit
            </uui-button>`
          : nothing}
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
  private async _onEditClick(row: AgentRunFeedbackEntry) {
    this._score = row.score === "Neutral"
      ? null
      : (row.score as ScoreString);
    this._comment = row.comment ?? "";
    // Clear any prior error state — Edit signals a fresh attempt.
    if (this._state === "error") {
      this._state = "idle";
      this._errorMessage = "";
    }

    // Wait for the render pass that surfaces the textarea (it's conditional
    // on `scoreSelected`, which just flipped true via _score = ThumbsUp/Down
    // above). Then scroll the textarea into view + focus it so the editor's
    // cursor is in place for supersede edits.
    await this.updateComplete;
    const textarea = this.shadowRoot?.querySelector("uui-textarea");
    if (textarea !== null && textarea !== undefined) {
      // Smooth-scroll + nearest block alignment keeps existing-feedback row
      // visible above; focus-shift moves caret into textarea (UUI textarea
      // forwards focus to its inner native element).
      textarea.scrollIntoView({ behavior: "smooth", block: "nearest" });
      (textarea as HTMLElement).focus();
    }
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
  private _renderPicker() {
    // P1 — empty-batch error state (AC3.g + AC5 frontend test 8 + § Failure
    // edges). When the workflow iterated over zero items, the siblings list
    // resolves loaded-but-empty. Picker is hidden; an explicit notice tells
    // the editor why (otherwise the modal would look like a normal
    // single-iteration view, which is misleading for a batch workflow).
    if (this._siblingsState === "loaded" && this._siblings.length === 0) {
      return html`
        <p class="picker-empty-batch" role="status">
          No iterations available — workflow may have iterated over zero items.
        </p>
      `;
    }
    // Picker only renders once the siblings fetch has settled successfully
    // AND there is more than one iteration. Hides during the initial load
    // phase + on unavailable so we don't flash a stub picker during the
    // network round-trip.
    if (this._siblingsState !== "loaded" || this._siblings.length <= 1) {
      return nothing;
    }
    const selectedIndex = this._siblings.findIndex(
      (s) => s.runId === this._selectedRunId,
    );
    // P15 — if the selected RunId is orphan (rollback restored a stale value;
    // shouldn't happen in practice since siblings is one-shot, but defensive),
    // emit a console.warn so the corruption is visible to adopter devtools
    // rather than silently masked by the idx=0 fallback. POST'd feedback uses
    // the cached _selectedRunId so an orphan would surface as a 404 on
    // submit — better to know.
    if (selectedIndex < 0) {
      // eslint-disable-next-line no-console
      console.warn(
        "[aiam-agent-feedback] picker: _selectedRunId is not present in _siblings; falling back to index 0. Submit will POST the orphan RunId.",
        { selectedRunId: this._selectedRunId, siblingCount: this._siblings.length },
      );
    }
    const idx = selectedIndex >= 0 ? selectedIndex : 0;
    const total = this._siblings.length;
    const sibling = this._siblings[idx];
    const isSubmitting = this._state === "submitting";
    const prevDisabled = idx === 0 || isSubmitting;
    const nextDisabled = idx === total - 1 || isSubmitting;

    // P14 — `new Date()` doesn't throw on invalid input; it returns Invalid
    // Date which `Number.isNaN(date.getTime())` already guards. The previous
    // try/catch was dead code. `toLocaleTimeString` doesn't throw under
    // normal conditions either.
    const date = new Date(sibling.startedUtc);
    const timestamp = Number.isNaN(date.getTime())
      ? sibling.startedUtc
      : date.toLocaleTimeString();

    return html`
      <div class="picker-row" role="group" aria-label="Iteration picker">
        <uui-button
          compact
          look="secondary"
          label="Previous iteration"
          class="picker-prev"
          ?disabled=${prevDisabled}
          @click=${() => this._onPickerChange(idx - 1)}
        >
          ←
        </uui-button>
        <span class="picker-counter" aria-live="polite">
          Iteration ${idx + 1} of ${total} · ${timestamp}
        </span>
        <uui-button
          compact
          look="secondary"
          label="Next iteration"
          class="picker-next"
          ?disabled=${nextDisabled}
          @click=${() => this._onPickerChange(idx + 1)}
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
  private _renderAgentOutput() {
    if (this._runDetailState === "loading") {
      return html`
        <uui-box headline="Agent output" class="agent-output-box">
          ${this._renderPicker()}
          <p class="agent-output-loading">
            <uui-loader></uui-loader>
            Loading agent output…
          </p>
        </uui-box>
      `;
    }

    if (this._runDetailState === "unavailable" || this._runDetail === null) {
      return html`
        <uui-box headline="Agent output" class="agent-output-box">
          ${this._renderPicker()}
          <p class="agent-output-unavailable">
            Agent output unavailable; you can still submit feedback below.
          </p>
        </uui-box>
      `;
    }

    const detail = this._runDetail;
    const hasIssues = detail.issues.length > 0;
    const hasSuggestions = detail.suggestions.length > 0;
    const hasStructuredOutput =
      detail.score !== null || hasIssues || hasSuggestions;
    const hasCitedMemories =
      detail.memoryUsed && detail.citedMemories.length > 0;

    return html`
      <uui-box headline="Agent output" class="agent-output-box">
        ${detail.memoryUsed
          ? html`<uui-tag
              slot="header-actions"
              color="positive"
              class="memory-used-badge"
            >
              Memory used
            </uui-tag>`
          : nothing}
        ${this._renderPicker()}
        <p class="agent-output-identity">
          ${detail.agentDisplayName ?? `Agent ${detail.agentId?.slice(0, 8) ?? "unknown"}`}
        </p>
        ${detail.score !== null
          ? html`<p class="agent-output-score">
              Score: <strong>${detail.score}</strong>
            </p>`
          : nothing}
        ${hasIssues
          ? html`
              <h5 class="agent-output-section-heading">Flagged issues</h5>
              <ul class="agent-output-issues">
                ${detail.issues.map(
                  (issue) => html`
                    <li>
                      <span class="agent-output-issue-text">${issue.text}</span>
                      ${issue.reason
                        ? html`<span class="agent-output-issue-reason">
                            — ${issue.reason}
                          </span>`
                        : nothing}
                    </li>
                  `,
                )}
              </ul>
            `
          : nothing}
        ${hasSuggestions
          ? html`
              <h5 class="agent-output-section-heading">Suggestions</h5>
              <ul class="agent-output-suggestions">
                ${detail.suggestions.map(
                  (suggestion) => html`<li>${suggestion}</li>`,
                )}
              </ul>
            `
          : nothing}
        ${!hasStructuredOutput
          ? html`<p class="agent-output-empty-note">
              (no structured output captured for this run)
            </p>`
          : nothing}
        ${hasCitedMemories
          ? html`
              <details class="cited-memories-details">
                <summary>
                  ${detail.citedMemories.length === 1
                    ? "1 memory cited"
                    : `${detail.citedMemories.length} memories cited`}
                </summary>
                <ul class="cited-memories-list">
                  ${detail.citedMemories.map(
                    (mem) => html`
                      <li class="cited-memory-row">
                        <span class="cited-memory-run">Run ${mem.runIdPrefix}</span>
                        <span class="cited-memory-emoji" aria-hidden="true">${mem.emoji}</span>
                        ${mem.commentSnippet !== null
                          ? html`<span class="cited-memory-snippet">
                              · "${mem.commentSnippet}"
                            </span>`
                          : html`<span class="cited-memory-no-comment">
                              · (no comment)
                            </span>`}
                      </li>
                    `,
                  )}
                </ul>
              </details>
            `
          : nothing}
      </uui-box>
    `;
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

    // P16 — capture _selectedRunId at submit start. The picker arrows are
    // disabled while submit is in flight, but defence-in-depth against
    // programmatic mutation between submit-start and POST-body construction.
    const submitSelectedRunId = this._selectedRunId;
    const submitScore = this._score;
    const submitComment = this._comment;

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
            score: submitScore,
            comment: submitComment.length > 0 ? submitComment : null,
            // Story 4.12 — picker submissions include selectedRunId so the
            // controller records feedback under the per-iteration RunId
            // (creating distinct supersede keys per iteration). Omitted for
            // non-picker submissions (single-iteration flows) so the legacy
            // ThreadId-keyed path is preserved byte-compatibly.
            selectedRunId: submitSelectedRunId,
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
}

declare global {
  interface HTMLElementTagNameMap {
    "aiam-agent-feedback": AiamAgentFeedbackElement;
  }
}
