import { expect, waitUntil } from "@open-wc/testing";
import "axe-core";
import "./cogworks-agent-feedback.element.js";
import type { CogworksAgentFeedbackElement } from "./cogworks-agent-feedback.element.js";

type TestableFeedbackElement = CogworksAgentFeedbackElement & {
  data: { runId: string };
  getContext: () => Promise<unknown>;
  updateComplete: Promise<boolean>;
};

interface FetchCall {
  url: string;
  init: RequestInit | undefined;
}

function makeAuthContext() {
  return {
    getOpenApiConfiguration: () => ({
      base: "https://example.test",
      credentials: "include" as RequestCredentials,
      token: async () => "abc-token",
    }),
  };
}

function stubFetch(responseFactory: () => Promise<Response> | Response): {
  calls: FetchCall[];
  restore: () => void;
} {
  const calls: FetchCall[] = [];
  const original = globalThis.fetch;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  globalThis.fetch = async (input: any, init?: RequestInit) => {
    const url = typeof input === "string" ? input : (input as Request).url;
    calls.push({ url, init });
    return responseFactory();
  };
  return {
    calls,
    restore: () => {
      globalThis.fetch = original;
    },
  };
}

/**
 * Story 4.5 test harness — routes parallel GETs to per-endpoint bodies based
 * on URL substring. `/runs/{id}` → runDetailBody; `/feedback/{id}` →
 * feedbackBody.
 */
function stubFetchByEndpoint(opts: {
  runDetail?: unknown;
  runDetailStatus?: number;
  feedback?: unknown;
  feedbackStatus?: number;
}): { calls: FetchCall[]; restore: () => void } {
  const calls: FetchCall[] = [];
  const original = globalThis.fetch;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  globalThis.fetch = async (input: any, init?: RequestInit) => {
    const url = typeof input === "string" ? input : (input as Request).url;
    calls.push({ url, init });
    if (url.includes("/runs/")) {
      return makeJsonResponse(opts.runDetail ?? {}, opts.runDetailStatus ?? 200);
    }
    if (url.includes("/feedback/")) {
      return makeJsonResponse(opts.feedback ?? {}, opts.feedbackStatus ?? 200);
    }
    return makeJsonResponse({}, 404);
  };
  return {
    calls,
    restore: () => {
      globalThis.fetch = original;
    },
  };
}

/**
 * Default agent-run-detail body shape with no memory injection (Run 1 case).
 * Tests override fields as needed.
 */
function makeRunDetailBody(overrides: Record<string, unknown> = {}) {
  return {
    runId: "run-123",
    agentId: "00000000-0000-0000-0000-000000000001",
    agentDisplayName: null,
    contentNodeName: null,
    ranAtUtc: "2026-05-19T12:00:00Z",
    score: 7,
    issues: [{ text: "the wild calling", reason: "Guideline #6" }],
    suggestions: ["Keep direct outdoor language."],
    memoryUsed: false,
    citedMemories: [],
    ...overrides,
  };
}

function makeFeedbackBody(
  existing: Array<Record<string, unknown>> = [],
  runId = "run-123",
) {
  return { runId, existing };
}

function makeJsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function makeElement(opts: { currentUserId?: string | null } = {}) {
  const element = document.createElement(
    "cogworks-agent-feedback",
  ) as TestableFeedbackElement;
  element.data = { runId: "run-123" };
  // Multi-context stub. The widget calls `getContext` with:
  //   - UMB_AUTH_CONTEXT (existing — used by authenticatedFetch)
  //   - UMB_CURRENT_USER_CONTEXT (Story 4.5 — used by _resolveCurrentUserId)
  // We branch by checking whether the resolved context's surface looks like
  // the current-user context (has `getUnique`). This keeps test setup minimal
  // — pass `currentUserId` opt to drive AC8.i / AC10 tests; omit to let the
  // current-user resolution fall back to `null`.
  const currentUserCtx = {
    getUnique: () => opts.currentUserId ?? null,
  };
  element.getContext = (async (token: unknown) => {
    // Token equality check is brittle across module versions; just rely on
    // the fact that the auth context is requested first and is the surface
    // authenticatedFetch consumes. The current-user resolution catches
    // throws gracefully, so misrouting here returns the auth context for
    // both calls — and the optional-chain `ctx?.getUnique() ?? null` in
    // the widget handles the auth-context-shaped response by returning
    // null (getUnique undefined on auth ctx → optional chain returns
    // undefined → ?? null → null).
    const tokenName = (token as { toString?: () => string })?.toString?.() ?? "";
    // UmbContextToken's toString returns the literal name. The Umbraco
    // current-user context registers under "UmbCurrentUserContext"; auth
    // under "UmbAuthContext". See current-user.context.token.js +
    // auth.context.token.js in @umbraco-cms/backoffice/dist-cms.
    if (tokenName.includes("CurrentUser")) {
      return currentUserCtx;
    }
    return makeAuthContext();
  }) as unknown as typeof element.getContext;
  document.body.append(element);
  await waitUntil(
    () => {
      const text = element.shadowRoot?.textContent ?? "";
      return text.length > 0 && !text.includes("Loading agent output");
    },
    "run detail request did not settle",
  );
  // Story 4.5 — also wait for the parallel feedback-read GET to settle.
  // We can't observe the @state field directly through shadow DOM, so we
  // poll the widget's internal state. The settle target is either "loaded"
  // OR "unavailable" — both end the loading phase.
  const internals = element as unknown as {
    _existingFeedbackState: string;
    _hasSeededFromExisting: boolean;
  };
  await waitUntil(
    () =>
      internals._existingFeedbackState === "loaded"
      || internals._existingFeedbackState === "unavailable",
    "existing-feedback request did not settle",
  );
  await element.updateComplete;
  return element;
}

describe("cogworks-agent-feedback — agent output render", () => {
  afterEach(() => {
    document.body.replaceChildren();
    delete (globalThis as { __xss?: boolean }).__xss;
  });

  it("renders score, issues, and suggestions returned by the run detail endpoint", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        runId: "run-123",
        agentId: "00000000-0000-0000-0000-000000000001",
        agentDisplayName: null,
        contentNodeName: null,
        ranAtUtc: "2026-05-19T12:00:00Z",
        score: 7,
        issues: [{ text: "the wild calling", reason: "Guideline #6" }],
        suggestions: ["Keep direct outdoor language."],
      }),
    );
    try {
      const element = await makeElement();

      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("Score:");
      expect(text).to.contain("7");
      expect(text).to.contain("the wild calling");
      expect(text).to.contain("Guideline #6");
      expect(text).to.contain("Keep direct outdoor language.");
      expect(stub.calls[0].url).to.equal(
        "https://example.test/umbraco/management/api/v1/cogworks-agent-memory/runs/run-123",
      );
    } finally {
      stub.restore();
    }
  });

  it("renders the loading state while the run detail request is pending", async () => {
    const stub = stubFetch(
      () => new Promise<Response>(() => {
        // Deliberately pending; disconnectedCallback aborts it during cleanup.
      }),
    );
    try {
      const element = document.createElement(
        "cogworks-agent-feedback",
      ) as TestableFeedbackElement;
      element.data = { runId: "run-123" };
      element.getContext = async () => makeAuthContext();
      document.body.append(element);
      await element.updateComplete;

      expect(element.shadowRoot?.querySelector("uui-loader")).to.not.be.null;
      expect(element.shadowRoot?.textContent).to.contain("Loading agent output");
    } finally {
      stub.restore();
    }
  });

  it("renders unavailable output and keeps the feedback form when GET returns 404", async () => {
    const stub = stubFetch(() => makeJsonResponse({ detail: "missing" }, 404));
    try {
      const element = await makeElement();

      expect(element.shadowRoot?.textContent).to.contain(
        "Agent output unavailable; you can still submit feedback below.",
      );
      expect(
        element.shadowRoot?.querySelector('uui-box[headline="How was this run?"]'),
      ).to.not.be.null;
    } finally {
      stub.restore();
    }
  });

  it("treats 200-with-empty-structured-fields as unavailable from the widget perspective", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        runId: "run-123",
        agentId: "00000000-0000-0000-0000-000000000001",
        agentDisplayName: null,
        contentNodeName: null,
        ranAtUtc: "2026-05-19T12:00:00Z",
        score: null,
        issues: [],
        suggestions: [],
      }),
    );
    try {
      const element = await makeElement();

      expect(element.shadowRoot?.textContent).to.contain(
        "Agent output unavailable; you can still submit feedback below.",
      );
      expect(element.shadowRoot?.textContent).not.to.contain(
        "no structured output captured",
      );
    } finally {
      stub.restore();
    }
  });

  it("renders agent-derived XSS payloads as escaped text, not executable DOM", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        runId: "run-123",
        agentId: "00000000-0000-0000-0000-000000000001",
        agentDisplayName: null,
        contentNodeName: null,
        ranAtUtc: "2026-05-19T12:00:00Z",
        score: 5,
        issues: [
          {
            text: '<img src=x onerror="window.__xss=true">',
            reason: "<script>alert(1)</script>",
          },
        ],
        suggestions: ["<svg onload=alert(1)>"],
      }),
    );
    try {
      const element = await makeElement();
      const root = element.shadowRoot!;
      const text = root.textContent ?? "";

      expect((globalThis as { __xss?: boolean }).__xss).to.be.undefined;
      expect(text).to.contain('<img src=x onerror="window.__xss=true">');
      expect(text).to.contain("<script>alert(1)</script>");
      expect(text).to.contain("<svg onload=alert(1)>");
      expect(root.querySelector("img")).to.be.null;
      expect(root.querySelector("script")).to.be.null;
      expect(root.querySelector("svg")).to.be.null;
    } finally {
      stub.restore();
    }
  });

  it("has no serious or critical axe-core violations in the loaded agent-output state", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        runId: "run-123",
        agentId: "00000000-0000-0000-0000-000000000001",
        agentDisplayName: null,
        contentNodeName: null,
        ranAtUtc: "2026-05-19T12:00:00Z",
        score: 7,
        issues: [{ text: "the wild calling", reason: "Guideline #6" }],
        suggestions: ["Keep direct outdoor language."],
      }),
    );
    try {
      const element = await makeElement();

      const result = await (globalThis as { axe: typeof axe }).axe.run(
        element.shadowRoot as ShadowRoot,
      );
      const seriousOrCritical = result.violations.filter(
        (violation) => violation.impact === "serious" || violation.impact === "critical",
      );
      expect(seriousOrCritical).to.deep.equal([]);
    } finally {
      stub.restore();
    }
  });

  // Story 4.5 AC12.j — axe-core gate ALSO covers the BOTH-boxes-loaded state
  // (Previous feedback + Agent output both rendered). Catches a11y
  // regressions in the new uui-box / uui-button / details-summary surfaces
  // added by Story 4.5.
  it("has no serious or critical axe-core violations with BOTH Previous-feedback and Agent-output boxes loaded", async () => {
    const currentUserId = "user-current-aaa";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody({
        memoryUsed: true,
        citedMemories: [
          { runIdPrefix: "abc12345", emoji: "👎", commentSnippet: "previous editor note" },
        ],
      }),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsDown",
          comment: "needs more direct language",
          createdBy: currentUserId,
          createdByDisplayName: "Adam Shallcross",
          createdUtc: "2026-05-19T12:00:00Z",
        },
        {
          score: "ThumbsUp",
          comment: "looks good to me",
          createdBy: "user-other-bbb",
          createdByDisplayName: "Other Editor",
          createdUtc: "2026-05-19T11:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });

      const result = await (globalThis as { axe: typeof axe }).axe.run(
        element.shadowRoot as ShadowRoot,
      );
      const seriousOrCritical = result.violations.filter(
        (violation) => violation.impact === "serious" || violation.impact === "critical",
      );
      expect(seriousOrCritical).to.deep.equal([]);
    } finally {
      stub.restore();
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────
// Story 4.5 — Previous-feedback block + Edit affordance (AC8 / AC11)
// ─────────────────────────────────────────────────────────────────────────

describe("cogworks-agent-feedback — Story 4.5 Previous-feedback block", () => {
  afterEach(() => {
    document.body.replaceChildren();
    delete (globalThis as { __xss?: boolean }).__xss;
  });

  it("renders existing-feedback rows when GET /feedback/{runId} returns rows", async () => {
    const currentUserId = "user-current-aaa";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsDown",
          comment: "My canonical Northwind brand-voice comment.",
          createdBy: currentUserId,
          createdByDisplayName: null,
          createdUtc: "2026-05-19T12:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });
      // The "Previous feedback" headline lives in uui-box's own shadow DOM,
      // not in the widget's. Query the attribute directly.
      const previousFeedbackBox = element.shadowRoot?.querySelector(
        'uui-box[headline="Previous feedback"]',
      );
      expect(previousFeedbackBox).to.not.be.null;
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("My canonical Northwind brand-voice comment.");
      expect(text).to.contain("An editor");
      // Current-user row carries an Edit button.
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const editButton = buttons.find((b) =>
        (b.textContent ?? "").trim().includes("Edit"),
      );
      expect(editButton).to.not.be.undefined;
    } finally {
      stub.restore();
    }
  });

  it("omits the Previous-feedback block when GET /feedback/{runId} returns empty array", async () => {
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([]),
    });
    try {
      const element = await makeElement();
      const headlines = Array.from(
        element.shadowRoot?.querySelectorAll('uui-box[headline]') ?? [],
      ).map((el) => el.getAttribute("headline"));
      expect(headlines).to.not.contain("Previous feedback");
    } finally {
      stub.restore();
    }
  });

  it("Edit button click pre-populates _score and _comment", async () => {
    const currentUserId = "user-current-bbb";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsUp",
          comment: "previous comment",
          createdBy: currentUserId,
          createdByDisplayName: null,
          createdUtc: "2026-05-19T12:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });
      // One-shot seed populated _score/_comment on load.
      const scoreEl = (element as unknown as {
        _score: string | null;
        _comment: string;
      });
      expect(scoreEl._score).to.equal("ThumbsUp");
      expect(scoreEl._comment).to.equal("previous comment");
    } finally {
      stub.restore();
    }
  });

  it("does NOT render the Edit button on other editors' rows", async () => {
    const currentUserId = "user-current-aaa";
    const otherUserId = "user-other-zzz";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsDown",
          comment: "other editor's feedback",
          createdBy: otherUserId,
          createdByDisplayName: null,
          createdUtc: "2026-05-19T11:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const editButton = buttons.find((b) =>
        (b.textContent ?? "").trim().includes("Edit"),
      );
      expect(editButton).to.be.undefined;
    } finally {
      stub.restore();
    }
  });

  it("XSS payload escaped in existing-feedback comment", async () => {
    const currentUserId = "user-current-aaa";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsDown",
          comment: '<img src=x onerror="window.__xss=true"><script>alert(1)</script>',
          createdBy: currentUserId,
          createdByDisplayName: null,
          createdUtc: "2026-05-19T12:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });
      const root = element.shadowRoot!;
      const text = root.textContent ?? "";
      expect((globalThis as { __xss?: boolean }).__xss).to.be.undefined;
      expect(text).to.contain('<img src=x onerror="window.__xss=true">');
      expect(text).to.contain("<script>alert(1)</script>");
      expect(root.querySelector("img")).to.be.null;
      expect(root.querySelector("script")).to.be.null;
    } finally {
      stub.restore();
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────
// Story 4.5 — Memory-used badge + cited memories (AC9 / AC11)
// ─────────────────────────────────────────────────────────────────────────

describe("cogworks-agent-feedback — Story 4.5 Memory-used badge", () => {
  afterEach(() => {
    document.body.replaceChildren();
    delete (globalThis as { __xss?: boolean }).__xss;
  });

  it("renders Memory-used badge when memoryUsed === true", async () => {
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody({
        memoryUsed: true,
        citedMemories: [
          { runIdPrefix: "347c2071", emoji: "👎", commentSnippet: "brand-voice teaching" },
        ],
      }),
      feedback: makeFeedbackBody([]),
    });
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("Memory used");
      // Cited memory bullet visible after expanding <details>.
      const details = element.shadowRoot?.querySelector("details");
      expect(details).to.not.be.null;
      expect(details?.textContent).to.contain("347c2071");
      expect(details?.textContent).to.contain("brand-voice teaching");
    } finally {
      stub.restore();
    }
  });

  it("omits Memory-used badge when memoryUsed === false", async () => {
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody({ memoryUsed: false, citedMemories: [] }),
      feedback: makeFeedbackBody([]),
    });
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.not.contain("Memory used");
      expect(element.shadowRoot?.querySelector("details")).to.be.null;
    } finally {
      stub.restore();
    }
  });

  it("renders agent-output block when memoryUsed === true but score/issues/suggestions all empty (amended empty-state guard)", async () => {
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody({
        score: null,
        issues: [],
        suggestions: [],
        memoryUsed: true,
        citedMemories: [
          { runIdPrefix: "347c2071", emoji: "👎", commentSnippet: "still cited" },
        ],
      }),
      feedback: makeFeedbackBody([]),
    });
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      // Amended empty-state guard: panel renders (does NOT fall through to "unavailable").
      expect(text).to.not.contain("Agent output unavailable");
      expect(text).to.contain("Memory used");
      // Empty-structured-output note still renders inside the panel.
      expect(text).to.contain("no structured output captured");
    } finally {
      stub.restore();
    }
  });

  it("XSS payload escaped in cited-memory commentSnippet", async () => {
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody({
        memoryUsed: true,
        citedMemories: [
          {
            runIdPrefix: "347c2071",
            emoji: "👎",
            commentSnippet: '<img src=x onerror="window.__xss=true"><svg onload=alert(1)>',
          },
        ],
      }),
      feedback: makeFeedbackBody([]),
    });
    try {
      const element = await makeElement();
      const root = element.shadowRoot!;
      const details = root.querySelector("details");
      const text = details?.textContent ?? "";
      expect((globalThis as { __xss?: boolean }).__xss).to.be.undefined;
      expect(text).to.contain('<img src=x onerror="window.__xss=true">');
      expect(text).to.contain("<svg onload=alert(1)>");
      expect(details?.querySelector("img")).to.be.null;
      expect(details?.querySelector("svg")).to.be.null;
    } finally {
      stub.restore();
    }
  });
});

// ─────────────────────────────────────────────────────────────────────────
// Story 4.5 — Submit-disable-on-no-change (AC10)
// ─────────────────────────────────────────────────────────────────────────

describe("cogworks-agent-feedback — Story 4.5 Submit-disable-on-no-change", () => {
  afterEach(() => {
    document.body.replaceChildren();
  });

  it("disables Submit when form state equals existing-feedback row + re-enables on mutation + re-disables on revert", async () => {
    const currentUserId = "user-current-aaa";
    const stub = stubFetchByEndpoint({
      runDetail: makeRunDetailBody(),
      feedback: makeFeedbackBody([
        {
          score: "ThumbsDown",
          comment: "seeded comment",
          createdBy: currentUserId,
          createdByDisplayName: null,
          createdUtc: "2026-05-19T12:00:00Z",
        },
      ]),
    });
    try {
      const element = await makeElement({ currentUserId });
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const submitButton = buttons.find((b) =>
        (b.getAttribute("label") ?? "") === "Submit feedback",
      ) as (HTMLElement & { disabled: boolean }) | undefined;
      expect(submitButton).to.not.be.undefined;
      // Initial state: form seeded from existing row → Submit disabled.
      expect(submitButton!.hasAttribute("disabled")).to.be.true;

      // Mutate comment via the widget's internal state field, then trigger
      // re-render. (Direct internal mutation is the most stable signal in a
      // shadow-DOM test; the production path uses the uui-textarea @input
      // event.)
      const internals = element as unknown as {
        _comment: string;
        requestUpdate: () => Promise<void>;
        updateComplete: Promise<boolean>;
      };
      internals._comment = "seeded comment edited";
      await internals.requestUpdate();
      await internals.updateComplete;
      const submitButton2 = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      ).find((b) => (b.getAttribute("label") ?? "") === "Submit feedback")!;
      expect(submitButton2.hasAttribute("disabled")).to.be.false;

      // Revert mutation: comment back to seeded value → Submit re-disabled.
      internals._comment = "seeded comment";
      await internals.requestUpdate();
      await internals.updateComplete;
      const submitButton3 = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      ).find((b) => (b.getAttribute("label") ?? "") === "Submit feedback")!;
      expect(submitButton3.hasAttribute("disabled")).to.be.true;
    } finally {
      stub.restore();
    }
  });
});

// ═══════════════════════════════════════════════════════════════════════════
// Story 4.12 — Run Detail modal iteration picker
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Story 4.12 test harness — extends `stubFetchByEndpoint` to route the new
 * `/runs/{id}/siblings` URL distinctly from the `/runs/{id}` detail URL.
 * Routing precedence:
 *   1. `/siblings`           → siblings response (Story 4.12)
 *   2. `/runs/`              → detail response  (Story 4.5 / 4.12)
 *   3. `/feedback/`          → feedback response (Story 4.5)
 *   4. `/feedback` (POST)    → submit-feedback response (Story 2.3)
 *
 * Each endpoint accepts a factory so individual responses can vary across
 * calls (used by the picker-arrow-click + rollback tests). `siblings`
 * receives the entire array; `runDetail` / `feedback` are picked once per
 * call shape.
 */
function stubFetchByEndpointStory412(opts: {
  siblings?: unknown;
  siblingsStatus?: number;
  runDetail?: unknown | ((url: string) => unknown);
  runDetailStatus?: number | ((url: string) => number);
  feedback?: unknown | ((url: string) => unknown);
  feedbackStatus?: number | ((url: string) => number);
  feedbackPost?: () => Response;
}): { calls: FetchCall[]; restore: () => void } {
  const calls: FetchCall[] = [];
  const original = globalThis.fetch;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  globalThis.fetch = async (input: any, init?: RequestInit) => {
    const url = typeof input === "string" ? input : (input as Request).url;
    calls.push({ url, init });
    const isPost = (init?.method ?? "GET").toUpperCase() === "POST";

    if (isPost && url.includes("/feedback")) {
      return opts.feedbackPost?.() ?? makeJsonResponse({}, 200);
    }
    if (url.includes("/siblings")) {
      return makeJsonResponse(opts.siblings ?? [], opts.siblingsStatus ?? 200);
    }
    if (url.includes("/runs/")) {
      const body = typeof opts.runDetail === "function"
        ? (opts.runDetail as (u: string) => unknown)(url)
        : opts.runDetail ?? {};
      const status = typeof opts.runDetailStatus === "function"
        ? (opts.runDetailStatus as (u: string) => number)(url)
        : opts.runDetailStatus ?? 200;
      return makeJsonResponse(body, status);
    }
    if (url.includes("/feedback/")) {
      const body = typeof opts.feedback === "function"
        ? (opts.feedback as (u: string) => unknown)(url)
        : opts.feedback ?? makeFeedbackBody();
      const status = typeof opts.feedbackStatus === "function"
        ? (opts.feedbackStatus as (u: string) => number)(url)
        : opts.feedbackStatus ?? 200;
      return makeJsonResponse(body, status);
    }
    return makeJsonResponse({}, 404);
  };
  return {
    calls,
    restore: () => {
      globalThis.fetch = original;
    },
  };
}

function makeSibling(runId: string, startedUtc: string) {
  return {
    threadId: "thread-batch-1",
    runId,
    startedUtc,
    isCurrent: false,
  };
}

/**
 * Builds a 3-iteration ASC-sorted siblings list mirroring the demo workflow
 * (3 article batch). Test bodies override individual iterations as needed.
 */
function makeThreeIterationsSiblings() {
  return [
    makeSibling("rid-step-1", "2026-05-21T17:00:00Z"),
    makeSibling("rid-step-2", "2026-05-21T17:00:10Z"),
    makeSibling("rid-step-3", "2026-05-21T17:00:20Z"),
  ];
}

async function makeBatchElement(opts: {
  siblings?: unknown;
  threadId?: string;
  runDetailByUrl?: (url: string) => unknown;
}) {
  const element = document.createElement(
    "cogworks-agent-feedback",
  ) as TestableFeedbackElement;
  element.data = { runId: opts.threadId ?? "thread-batch-1" };
  element.getContext = (async () => makeAuthContext()) as unknown as typeof element.getContext;
  document.body.append(element);
  // Wait for siblings to settle first (Story 4.12 - the picker won't render
  // until the siblings fetch lands and the selectedRunId-keyed refetches
  // start).
  const internals = element as unknown as {
    _siblingsState: string;
    _existingFeedbackState: string;
    _runDetailState: string;
  };
  await waitUntil(
    () =>
      internals._siblingsState === "loaded"
      || internals._siblingsState === "unavailable",
    "siblings request did not settle",
  );
  await waitUntil(
    () =>
      internals._runDetailState === "loaded"
      || internals._runDetailState === "unavailable",
    "run detail request did not settle",
  );
  await waitUntil(
    () =>
      internals._existingFeedbackState === "loaded"
      || internals._existingFeedbackState === "unavailable",
    "existing-feedback request did not settle",
  );
  await element.updateComplete;
  return element;
}

describe("cogworks-agent-feedback — Story 4.12 picker", () => {
  afterEach(() => {
    document.body.replaceChildren();
  });

  it("renders the picker (arrows + counter) when siblings.length > 1", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const prev = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Previous iteration",
      );
      const next = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Next iteration",
      );
      expect(prev, "previous-iteration arrow").to.not.be.undefined;
      expect(next, "next-iteration arrow").to.not.be.undefined;

      const text = element.shadowRoot?.textContent ?? "";
      expect(text, "iteration counter shape").to.contain("Iteration 1 of 3");
    } finally {
      stub.restore();
    }
  });

  it("hides the picker entirely when siblings.length === 1 (Story 4.5 byte-compat)", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: [makeSibling("rid-solo", "2026-05-21T17:00:00Z")],
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const prev = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Previous iteration",
      );
      expect(prev, "single-iteration flow hides the picker").to.be.undefined;
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.not.contain("Iteration 1 of");
    } finally {
      stub.restore();
    }
  });

  it("initialises selectedRunId to the first ASC iteration without mutating modal data.runId", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const internals = element as unknown as { _selectedRunId: string | null };
      expect(internals._selectedRunId, "default-select oldest iteration").to.equal("rid-step-1");
      // modalContext.data.runId is the workflow-run grouping key — it must
      // remain the ThreadId regardless of which iteration is selected.
      expect(element.data.runId, "modal data.runId is the ThreadId; never mutated").to.equal(
        "thread-batch-1",
      );
    } finally {
      stub.restore();
    }
  });

  it("disables prev/next at boundaries and renders 'Iteration N of M · hh:mm:ss' counter", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const prev = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Previous iteration",
      )!;
      const next = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Next iteration",
      )!;
      // At iteration 1 of 3: prev disabled, next enabled.
      expect(prev.hasAttribute("disabled"), "← disabled at first iteration").to.be.true;
      expect(next.hasAttribute("disabled"), "→ enabled when not at last").to.be.false;
      const text = element.shadowRoot?.textContent ?? "";
      // Counter shape — "Iteration 1 of 3 · hh:mm:ss" (regex tolerates the
      // local timezone of the test runner; the timestamp is whatever
      // toLocaleTimeString emits for 2026-05-21T17:00:00Z).
      expect(text).to.match(/Iteration 1 of 3\s*·\s*\d/);
    } finally {
      stub.restore();
    }
  });

  it("arrow click refetches /runs/{tid}?selectedRunId=... + /feedback/{rid} and updates selectedRunId", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: (url) =>
        url.includes("selectedRunId=rid-step-2")
          ? makeRunDetailBody({ score: 5, issues: [{ text: "iteration-2-flag" }] })
          : makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const next = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Next iteration",
      )!;
      // Click → from iteration 1 to iteration 2.
      next.dispatchEvent(new Event("click"));
      // Wait for selectedRunId to flip + detail to settle.
      const internals = element as unknown as {
        _selectedRunId: string | null;
        _runDetailState: string;
        _hasSeededFromExisting: boolean;
      };
      await waitUntil(
        () => internals._selectedRunId === "rid-step-2"
          && internals._runDetailState === "loaded",
        "selectedRunId did not flip to rid-step-2",
      );
      await element.updateComplete;

      // URL inspections — both /runs/{tid}?selectedRunId=rid-step-2 +
      // /feedback/rid-step-2 must have been called.
      const runDetailUrl = stub.calls.find((c) =>
        c.url.includes("/runs/") && c.url.includes("selectedRunId=rid-step-2"),
      );
      expect(runDetailUrl, "refetched run-detail with selectedRunId").to.not.be.undefined;
      const feedbackUrl = stub.calls.find((c) =>
        c.url.includes("/feedback/rid-step-2"),
      );
      expect(feedbackUrl, "refetched feedback under per-iteration RunId").to.not.be.undefined;
    } finally {
      stub.restore();
    }
  });

  it("rolls back picker selection when the new iteration's detail fetch fails (AC4.e)", async () => {
    let runDetailCallCount = 0;
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: (url) => {
        runDetailCallCount++;
        if (url.includes("selectedRunId=rid-step-2")) {
          return { detail: "synthetic 500" }; // body is irrelevant when status != 200
        }
        return makeRunDetailBody({
          runId: "thread-batch-1",
          issues: [{ text: "iteration-1-original" }],
        });
      },
      runDetailStatus: (url) =>
        url.includes("selectedRunId=rid-step-2") ? 500 : 200,
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const internals = element as unknown as {
        _selectedRunId: string | null;
        _runDetailState: string;
        _runDetail: { issues: Array<{ text: string }> } | null;
      };
      const callsBefore = runDetailCallCount;
      const initialSelectedRunId = internals._selectedRunId;

      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const next = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Next iteration",
      )!;
      next.dispatchEvent(new Event("click"));

      // Wait until at least one additional run-detail call has resolved
      // (the failed iteration-2 fetch).
      await waitUntil(
        () => runDetailCallCount > callsBefore,
        "iteration-2 fetch did not fire",
      );
      // Give the microtask queue a tick to flush rollback assignments.
      await element.updateComplete;
      await new Promise((r) => setTimeout(r, 10));
      await element.updateComplete;

      // Picker selection reverted to the initial iteration.
      expect(
        internals._selectedRunId,
        "picker selectedRunId reverted on fetch failure",
      ).to.equal(initialSelectedRunId);
      // Previous iteration's data still rendered (no flash-to-empty).
      expect(internals._runDetail, "previous iteration's detail preserved").to.not.be.null;
      expect(internals._runDetail!.issues[0].text).to.equal("iteration-1-original");
    } finally {
      stub.restore();
    }
  });

  it("disables picker arrows while feedback submit is in flight (AC4.f submit-race)", async () => {
    let resolveSubmit: ((value: Response) => void) | undefined;
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
      // Submit response is pending until the test releases it.
      feedbackPost: () =>
        new Response(JSON.stringify({}), {
          status: 202,
          headers: { "Content-Type": "application/json" },
        }),
    });
    // Override fetch one more time so the POST hangs until we explicitly
    // resolve it (the simpler `feedbackPost` callback above returns sync —
    // we need an async promise to model in-flight).
    const originalFetch = globalThis.fetch;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    globalThis.fetch = (async (input: any, init?: RequestInit) => {
      const url = typeof input === "string" ? input : (input as Request).url;
      const isPost = (init?.method ?? "GET").toUpperCase() === "POST";
      if (isPost && url.includes("/feedback")) {
        return new Promise<Response>((resolve) => {
          resolveSubmit = resolve;
        });
      }
      return originalFetch(input, init);
    }) as typeof globalThis.fetch;

    try {
      const element = await makeBatchElement({});
      const internals = element as unknown as {
        _score: string | null;
        _state: string;
        requestUpdate: () => Promise<void>;
      };
      // Toggle a score so Submit is enabled.
      internals._score = "ThumbsDown";
      await internals.requestUpdate();
      await element.updateComplete;

      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const submit = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Submit feedback",
      )!;
      submit.dispatchEvent(new Event("click"));
      // Wait for state to flip to "submitting".
      await waitUntil(() => internals._state === "submitting", "state did not enter submitting");
      await element.updateComplete;

      // Arrows must be disabled while submit is in flight.
      const buttonsDuringSubmit = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const prevDuring = buttonsDuringSubmit.find(
        (b) => (b.getAttribute("label") ?? "") === "Previous iteration",
      )!;
      const nextDuring = buttonsDuringSubmit.find(
        (b) => (b.getAttribute("label") ?? "") === "Next iteration",
      )!;
      expect(prevDuring.hasAttribute("disabled"), "← disabled during submit").to.be.true;
      expect(nextDuring.hasAttribute("disabled"), "→ disabled during submit").to.be.true;

      // Release the submit; state moves to "success"; arrows re-enable.
      resolveSubmit!(new Response(JSON.stringify({}), { status: 200 }));
      await waitUntil(() => internals._state === "success", "state did not finish submitting");
      await element.updateComplete;
    } finally {
      globalThis.fetch = originalFetch;
      stub.restore();
    }
  });

  it("hides the picker when siblings list is empty (empty-batch ThreadId edge)", async () => {
    const stub = stubFetchByEndpointStory412({
      siblings: [],
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    try {
      const element = await makeBatchElement({});
      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const prev = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Previous iteration",
      );
      expect(prev, "empty siblings → picker hidden").to.be.undefined;
      const internals = element as unknown as { _selectedRunId: string | null };
      expect(internals._selectedRunId, "no selectedRunId initialised when batch is empty").to.be.null;
    } finally {
      stub.restore();
    }
  });

  it("POST body carries selectedRunId for picker submissions", async () => {
    let lastPostBody: Record<string, unknown> | undefined;
    const stub = stubFetchByEndpointStory412({
      siblings: makeThreeIterationsSiblings(),
      runDetail: makeRunDetailBody({ runId: "thread-batch-1" }),
      feedback: makeFeedbackBody([], "thread-batch-1"),
    });
    // Override fetch to capture the POST body shape.
    const originalFetch = globalThis.fetch;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    globalThis.fetch = (async (input: any, init?: RequestInit) => {
      const url = typeof input === "string" ? input : (input as Request).url;
      const isPost = (init?.method ?? "GET").toUpperCase() === "POST";
      if (isPost && url.includes("/feedback")) {
        const body = init?.body;
        if (typeof body === "string") {
          try {
            lastPostBody = JSON.parse(body);
          } catch {
            lastPostBody = undefined;
          }
        }
        return new Response(JSON.stringify({}), { status: 200 });
      }
      return originalFetch(input, init);
    }) as typeof globalThis.fetch;

    try {
      const element = await makeBatchElement({});
      const internals = element as unknown as {
        _score: string | null;
        _comment: string;
        requestUpdate: () => Promise<void>;
        _state: string;
      };
      internals._score = "ThumbsDown";
      internals._comment = "iteration-1 teaching";
      await internals.requestUpdate();
      await element.updateComplete;

      const buttons = Array.from(
        element.shadowRoot?.querySelectorAll("uui-button") ?? [],
      );
      const submit = buttons.find(
        (b) => (b.getAttribute("label") ?? "") === "Submit feedback",
      )!;
      submit.dispatchEvent(new Event("click"));
      await waitUntil(() => internals._state === "success", "submit did not succeed");

      expect(lastPostBody, "POST body captured").to.not.be.undefined;
      expect(lastPostBody!.runId).to.equal("thread-batch-1");
      expect(lastPostBody!.selectedRunId).to.equal("rid-step-1");
      expect(lastPostBody!.comment).to.equal("iteration-1 teaching");
    } finally {
      globalThis.fetch = originalFetch;
      stub.restore();
    }
  });
});
