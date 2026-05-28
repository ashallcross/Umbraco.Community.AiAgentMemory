/**
 * Story 2.3 Task 5 — tests for the `authenticated-fetch` helper.
 *
 * Pure-logic tests — no Bellissima dependencies. Pins the Codex finding #11
 * empty-bearer guards + the Authorization-header strip-before-merge protection
 * that this helper inherits verbatim from LlmsTxt Story 6.0b.
 *
 * Widget-element behaviour is covered separately by
 * `feedback-widget/aiam-agent-feedback.test.ts`; this file stays focused
 * on the shared bearer-token fetch helper.
 */

import { expect } from "@open-wc/testing";
import {
  authenticatedFetch,
  AuthContextUnavailableError,
} from "./authenticated-fetch.js";

function makeAuthContext(token: string | undefined) {
  return {
    getOpenApiConfiguration: () => ({
      base: "https://example.test",
      credentials: "include" as RequestCredentials,
      token: async () => token,
    }),
  };
}

function makeThrowingAuthContext(error: unknown) {
  return {
    getOpenApiConfiguration: () => ({
      base: "https://example.test",
      credentials: "include" as RequestCredentials,
      token: async () => {
        throw error;
      },
    }),
  };
}

interface FetchCall {
  url: string;
  init: RequestInit | undefined;
}

function stubFetch(response: Response): { calls: FetchCall[]; restore: () => void } {
  const calls: FetchCall[] = [];
  const original = globalThis.fetch;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  globalThis.fetch = async (input: any, init?: RequestInit) => {
    const url = typeof input === "string" ? input : (input as Request).url;
    calls.push({ url, init });
    return response;
  };
  return {
    calls,
    restore: () => {
      globalThis.fetch = original;
    },
  };
}

describe("authenticatedFetch — happy path", () => {
  it("issues a GET with bearer-token header derived from token()", async () => {
    const stub = stubFetch(new Response(null, { status: 200 }));
    try {
      const ac = new AbortController();
      await authenticatedFetch(
        async () => makeAuthContext("abc-token"),
        "/some/path",
        { signal: ac.signal },
      );
      expect(stub.calls).to.have.lengthOf(1);
      const call = stub.calls[0];
      expect(call.url).to.equal("https://example.test/some/path");
      expect(call.init?.method).to.equal("GET");
      const headers = call.init?.headers as Record<string, string>;
      expect(headers.Authorization).to.equal("Bearer abc-token");
      expect(headers.Accept).to.equal("application/json");
      // No body → no Content-Type added.
      expect(headers["Content-Type"]).to.be.undefined;
    } finally {
      stub.restore();
    }
  });

  it("issues a POST with JSON-stringified body and Content-Type when body supplied", async () => {
    const stub = stubFetch(new Response(null, { status: 200 }));
    try {
      const ac = new AbortController();
      await authenticatedFetch(
        async () => makeAuthContext("abc-token"),
        "/some/path",
        {
          method: "POST",
          body: { runId: "x", score: "ThumbsDown", comment: null },
          signal: ac.signal,
        },
      );
      const call = stub.calls[0];
      expect(call.init?.method).to.equal("POST");
      const headers = call.init?.headers as Record<string, string>;
      expect(headers["Content-Type"]).to.equal("application/json");
      expect(call.init?.body).to.equal(
        JSON.stringify({ runId: "x", score: "ThumbsDown", comment: null }),
      );
    } finally {
      stub.restore();
    }
  });
});

describe("authenticatedFetch — guards", () => {
  it("throws AuthContextUnavailableError when auth context resolver returns null", async () => {
    const ac = new AbortController();
    let thrown: unknown;
    try {
      await authenticatedFetch(
        async () => null,
        "/x",
        { signal: ac.signal },
      );
    } catch (err) {
      thrown = err;
    }
    expect(thrown).to.be.instanceOf(AuthContextUnavailableError);
  });

  it("throws AuthContextUnavailableError when token() raises", async () => {
    const ac = new AbortController();
    let thrown: unknown;
    try {
      await authenticatedFetch(
        async () => makeThrowingAuthContext(new Error("network")),
        "/x",
        { signal: ac.signal },
      );
    } catch (err) {
      thrown = err;
    }
    expect(thrown).to.be.instanceOf(AuthContextUnavailableError);
  });

  it("throws AuthContextUnavailableError when token() returns undefined", async () => {
    const ac = new AbortController();
    let thrown: unknown;
    try {
      await authenticatedFetch(
        async () => makeAuthContext(undefined),
        "/x",
        { signal: ac.signal },
      );
    } catch (err) {
      thrown = err;
    }
    expect(thrown).to.be.instanceOf(AuthContextUnavailableError);
  });

  it("throws AuthContextUnavailableError when token() returns empty string", async () => {
    const ac = new AbortController();
    let thrown: unknown;
    try {
      await authenticatedFetch(
        async () => makeAuthContext(""),
        "/x",
        { signal: ac.signal },
      );
    } catch (err) {
      thrown = err;
    }
    expect(thrown).to.be.instanceOf(AuthContextUnavailableError);
  });

  it("throws AuthContextUnavailableError when token() returns whitespace-only", async () => {
    const ac = new AbortController();
    let thrown: unknown;
    try {
      await authenticatedFetch(
        async () => makeAuthContext("   "),
        "/x",
        { signal: ac.signal },
      );
    } catch (err) {
      thrown = err;
    }
    expect(thrown).to.be.instanceOf(AuthContextUnavailableError);
  });
});

describe("authenticatedFetch — Authorization header is not overridable", () => {
  it("strips caller-supplied Authorization (PascalCase) before merge", async () => {
    const stub = stubFetch(new Response(null, { status: 200 }));
    try {
      const ac = new AbortController();
      await authenticatedFetch(
        async () => makeAuthContext("real-token"),
        "/x",
        {
          signal: ac.signal,
          headers: { Authorization: "Bearer attacker-supplied" },
        },
      );
      const headers = stub.calls[0].init?.headers as Record<string, string>;
      // The helper's bearer token wins; caller's value is silently dropped.
      expect(headers.Authorization).to.equal("Bearer real-token");
    } finally {
      stub.restore();
    }
  });

  it("strips caller-supplied authorization (lowercase) before merge", async () => {
    const stub = stubFetch(new Response(null, { status: 200 }));
    try {
      const ac = new AbortController();
      await authenticatedFetch(
        async () => makeAuthContext("real-token"),
        "/x",
        {
          signal: ac.signal,
          headers: { authorization: "Bearer attacker-supplied" } as Record<
            string,
            string
          >,
        },
      );
      const headers = stub.calls[0].init?.headers as Record<string, string>;
      expect(headers.Authorization).to.equal("Bearer real-token");
      // Lowercase variant also dropped — fetch would otherwise have BOTH
      // (would-be duplicate Authorization headers).
      expect(headers.authorization).to.be.undefined;
    } finally {
      stub.restore();
    }
  });
});

describe("authenticatedFetch — caller-supplied non-auth headers", () => {
  it("merges caller-supplied Accept override on top of base headers", async () => {
    const stub = stubFetch(new Response(null, { status: 200 }));
    try {
      const ac = new AbortController();
      await authenticatedFetch(
        async () => makeAuthContext("t"),
        "/x",
        {
          signal: ac.signal,
          headers: { Accept: "text/plain" },
        },
      );
      const headers = stub.calls[0].init?.headers as Record<string, string>;
      expect(headers.Accept).to.equal("text/plain");
    } finally {
      stub.restore();
    }
  });
});
