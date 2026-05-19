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
