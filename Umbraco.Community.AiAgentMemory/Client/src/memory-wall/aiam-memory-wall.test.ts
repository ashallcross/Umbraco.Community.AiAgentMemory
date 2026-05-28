import { expect, waitUntil } from "@open-wc/testing";
import "axe-core";
import "./aiam-memory-wall.element.js";
import type { AiamMemoryWallElement } from "./aiam-memory-wall.element.js";

type TestableWallElement = AiamMemoryWallElement & {
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

function makeJsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

type WireEntry = {
  runId: string;
  agentId: string;
  agentDisplayName: string | null;
  digestText: string;
  score: "ThumbsUp" | "ThumbsDown" | "Neutral" | null;
  feedbackComment: string | null;
  createdBy: string | null;
  createdByDisplayName: string | null;
  createdUtc: string;
};

function makeEntry(overrides: Partial<WireEntry> = {}): WireEntry {
  return {
    runId: "run-1",
    agentId: "00000000-0000-0000-0000-000000000001",
    agentDisplayName: "Brand Voice Auditor",
    digestText: "Northwind brand-voice teaching: keep direct language",
    score: "ThumbsDown",
    feedbackComment: "Avoid 'the wild calling' — too florid for our brand",
    createdBy: "11111111-1111-1111-1111-111111111111",
    createdByDisplayName: "Adam Editor",
    createdUtc: "2026-05-21T12:00:00Z",
    ...overrides,
  };
}

async function makeElement(): Promise<TestableWallElement> {
  const element = document.createElement(
    "aiam-memory-wall",
  ) as TestableWallElement;
  element.getContext = async () => makeAuthContext();
  document.body.append(element);
  // Settle after either "loaded" (success/empty) OR "error" — both end the
  // loading phase.
  const internals = element as unknown as { _state: string };
  await waitUntil(
    () => internals._state === "loaded" || internals._state === "error",
    "memory wall request did not settle",
  );
  await element.updateComplete;
  return element;
}

describe("aiam-memory-wall — Memory Learning Wall dashboard", () => {
  afterEach(() => {
    document.body.replaceChildren();
  });

  // ───────────────────────────────────────────────────────────────────────
  // AC7.d.i — happy/loaded render with 3 entries × 2 agents
  // ───────────────────────────────────────────────────────────────────────
  it("renders grouped agent boxes with one row per entry", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [
          makeEntry({
            runId: "run-1",
            agentId: "agent-a",
            agentDisplayName: "Brand Voice Auditor",
            createdByDisplayName: "Adam Editor",
            score: "ThumbsDown",
            digestText: "first entry",
          }),
          makeEntry({
            runId: "run-2",
            agentId: "agent-b",
            agentDisplayName: "Tone Checker",
            createdByDisplayName: "Mara Editor",
            score: "ThumbsUp",
            digestText: "second entry",
          }),
          makeEntry({
            runId: "run-3",
            agentId: "agent-a",
            agentDisplayName: "Brand Voice Auditor",
            createdByDisplayName: "Adam Editor",
            score: "ThumbsDown",
            digestText: "third entry",
          }),
        ],
      }),
    );
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";

      // Two agent groups render (per-agent uui-box).
      const groupBoxes = Array.from(
        element.shadowRoot?.querySelectorAll(".agent-group-box") ?? [],
      );
      expect(groupBoxes.length).to.equal(2);

      // Agent display names land on the `headline` attribute of each uui-box
      // (the headline renders in uui-box's OWN shadow root, not the wall's
      // shadow root textContent — see aiam-agent-feedback.test.ts:241 for
      // the precedent on querying headline by attribute).
      const headlines = groupBoxes
        .map((b) => b.getAttribute("headline"))
        .sort();
      expect(headlines).to.deep.equal(["Brand Voice Auditor", "Tone Checker"]);

      // Score labels render alongside emojis per NFR-A2 colour-not-sole-signifier.
      expect(text).to.contain("Helpful");
      expect(text).to.contain("Not helpful");
      // Editor display names render verbatim.
      expect(text).to.contain("Adam Editor");
      expect(text).to.contain("Mara Editor");
      // Digest snippets render.
      expect(text).to.contain("first entry");
      expect(text).to.contain("third entry");

      // Endpoint URL pinned.
      expect(stub.calls[0].url).to.equal(
        "https://example.test/umbraco/management/api/v1/cogworks-agent-memory/memory-entries?take=100",
      );
    } finally {
      stub.restore();
    }
  });

  // ───────────────────────────────────────────────────────────────────────
  // AC7.d.ii — axe-core AA gate on the loaded state
  // ───────────────────────────────────────────────────────────────────────
  it("has no serious or critical axe-core violations in the loaded state", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [
          makeEntry({ runId: "run-1", agentId: "agent-a" }),
          makeEntry({
            runId: "run-2",
            agentId: "agent-b",
            agentDisplayName: "Tone Checker",
            score: "ThumbsUp",
          }),
          makeEntry({ runId: "run-3", agentId: "agent-a" }),
        ],
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

  // ───────────────────────────────────────────────────────────────────────
  // AC7.d.iii — empty-state render
  // ───────────────────────────────────────────────────────────────────────
  it("renders the empty-state copy when the endpoint returns zero entries", async () => {
    const stub = stubFetch(() => makeJsonResponse({ entries: [] }));
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("No memories learned yet");
      expect(text).to.contain("submit feedback on agent runs to teach the agent");
      // No agent-group boxes render when the list is empty.
      const groupBoxes = Array.from(
        element.shadowRoot?.querySelectorAll(".agent-group-box") ?? [],
      );
      expect(groupBoxes.length).to.equal(0);
    } finally {
      stub.restore();
    }
  });

  // ───────────────────────────────────────────────────────────────────────
  // DRIFT-4.9-6 — wall-side digest display filter strips transcript markers
  // ───────────────────────────────────────────────────────────────────────
  it("strips [tool_call:...] and [tool:...] markers from digestText at render time (newline-separated form)", async () => {
    // Synthetic digest mirroring the AR35 emission shape that surfaced at
    // the Story 4.9 manual gate iteration 1: editor comment leads, then
    // ResponseSnapshotJoined transcript fragments with tool-call markers,
    // each on its own line.
    const noisyDigest = [
      "looks good",
      "[tool_call:toolu_01ABC] list_context_resources({\"args\":{}})",
      "[tool:toolu_01ABC] -> {\"resources\":[],\"message\":\"No on-demand context resources are available.\"}",
      "[assistant] {\"score\":7,\"issues\":[]}",
    ].join("\n");
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [makeEntry({ digestText: noisyDigest })],
      }),
    );
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";

      // Editor comment + assistant turn both survive the filter.
      expect(text).to.contain("looks good");
      expect(text).to.contain("[assistant]");
      // Transcript markers are stripped — neither surfaces in the rendered
      // wall cell (NOT in the wire shape; only in the editor-facing render).
      expect(text).not.to.contain("[tool_call:");
      expect(text).not.to.contain("[tool:toolu_");
      expect(text).not.to.contain("list_context_resources");
    } finally {
      stub.restore();
    }
  });

  it("strips [tool_call:...] and [tool:...] markers when the transcript is on a single line (manual-gate iteration 3 case)", async () => {
    // Manual-gate iteration 3 empirical capture — the actual digest
    // surfaced this shape with NO newlines between the segments (the
    // transcript-emit path may run them together, or the digest
    // truncation drops trailing newlines). Filter must handle the
    // same-line case so the regex-pattern approach is genuinely
    // load-bearing vs the line-based approach (which DRIFT-4.9-6
    // iteration 1 used).
    const noisyDigest =
      "looks good [tool_call:toolu_01WVtm1YCB66TcVDqiVMsLcZ] list_context_resources({\"args\":{}}) [assistant] {\"score\":9,\"issues\":[{\"text\":\"trail crew\",\"reason\":\"Informal colloquialism\"}]}";
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [makeEntry({ digestText: noisyDigest })],
      }),
    );
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";

      // Editor comment + assistant turn survive.
      expect(text).to.contain("looks good");
      expect(text).to.contain("[assistant]");
      expect(text).to.contain("trail crew");
      // tool_call marker + its trailing tool-name + args fully stripped.
      expect(text).not.to.contain("[tool_call:");
      expect(text).not.to.contain("list_context_resources");
    } finally {
      stub.restore();
    }
  });

  // ───────────────────────────────────────────────────────────────────────
  // Code-review patch — AC5.b "An editor" fallback copy is rendered when
  // `createdByDisplayName` is null. The other tests default to a populated
  // display name so the null-fallback branch was previously untested.
  // ───────────────────────────────────────────────────────────────────────
  it("renders the 'An editor' fallback when createdByDisplayName is null", async () => {
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [
          makeEntry({
            createdByDisplayName: null,
            createdBy: null,
            digestText: "fallback-cell digest",
          }),
        ],
      }),
    );
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("An editor");
      // Sanity — the actual digest still renders so we know we're in the
      // loaded state, not the empty or error state.
      expect(text).to.contain("fallback-cell digest");
    } finally {
      stub.restore();
    }
  });

  // ───────────────────────────────────────────────────────────────────────
  // Code-review patch — AC6.d 200-char digest truncation contract. The other
  // tests use ~50-char fixtures so the `…` truncation branch was previously
  // unexercised.
  // ───────────────────────────────────────────────────────────────────────
  it("truncates digest snippets at 200 chars with an ellipsis", async () => {
    // 250-char body well past the DIGEST_SNIPPET_MAX threshold. No
    // transcript markers, so `_cleanDigestForDisplay` is a no-op and the
    // truncation contract is the only thing under test.
    const longDigest = "x".repeat(250);
    const stub = stubFetch(() =>
      makeJsonResponse({
        entries: [makeEntry({ digestText: longDigest })],
      }),
    );
    try {
      const element = await makeElement();
      const digestCell = element.shadowRoot?.querySelector(".digest-cell");
      const rendered = digestCell?.textContent?.trim() ?? "";
      // 200 x's + the ellipsis character.
      expect(rendered.endsWith("…")).to.equal(true);
      // The pre-ellipsis content is exactly 200 chars (no more, no less).
      const withoutEllipsis = rendered.slice(0, -1);
      expect(withoutEllipsis.length).to.equal(200);
    } finally {
      stub.restore();
    }
  });

  // ───────────────────────────────────────────────────────────────────────
  // AC7.d.iv — error-state render + retry button
  // ───────────────────────────────────────────────────────────────────────
  it("renders the error state when the endpoint returns 500 and exposes a Retry button", async () => {
    const stub = stubFetch(() => makeJsonResponse({ detail: "boom" }, 500));
    try {
      const element = await makeElement();
      const text = element.shadowRoot?.textContent ?? "";
      expect(text).to.contain("Failed to load memory entries");
      // No agent-group boxes render in the error state.
      const groupBoxes = Array.from(
        element.shadowRoot?.querySelectorAll(".agent-group-box") ?? [],
      );
      expect(groupBoxes.length).to.equal(0);
      // Retry button present (label-text bound on uui-button).
      const retryButton = element.shadowRoot?.querySelector("uui-button");
      expect(retryButton).to.not.be.null;
    } finally {
      stub.restore();
    }
  });
});
