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

function makeJsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function makeElement() {
  const element = document.createElement(
    "cogworks-agent-feedback",
  ) as TestableFeedbackElement;
  element.data = { runId: "run-123" };
  element.getContext = async () => makeAuthContext();
  document.body.append(element);
  await waitUntil(
    () => {
      const text = element.shadowRoot?.textContent ?? "";
      return text.length > 0 && !text.includes("Loading agent output");
    },
    "run detail request did not settle",
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
});
