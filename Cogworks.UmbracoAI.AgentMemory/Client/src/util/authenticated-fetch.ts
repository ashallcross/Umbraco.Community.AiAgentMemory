/**
 * Story 2.3 Task 4 — shared authenticated-fetch helper.
 *
 * **Lifted VERBATIM from the LlmsTxt sibling package's Story 6.0b AC6
 * implementation** (`UmbracoCommunityAiVisibility/Umbraco.Community.AiVisibility/Client/src/util/authenticated-fetch.ts`).
 * Both packages live on the same Umbraco 17.3.2 baseline and consume the same
 * `UMB_AUTH_CONTEXT.getOpenApiConfiguration()` surface, so re-implementing this
 * would re-introduce the same footguns LlmsTxt already closed.
 *
 * Spike 0.B locked decision #11 (carried forward to Story 2.2 AC6) underpins
 * both packages: Umbraco's Management API enforces bearer-token auth via
 * OpenIddict — cookie-only fetches return 401.
 *
 * Inherited safety contract:
 * - Throws {@link AuthContextUnavailableError} when the auth context is
 *   unavailable (auth context not registered yet — boot-time race).
 * - Throws {@link AuthContextUnavailableError} when `config.token()` raises.
 * - Throws {@link AuthContextUnavailableError} when `config.token()` returns
 *   `undefined` / `null` / empty string / whitespace-only — defends against the
 *   silent `Authorization: Bearer ` (literal empty-bearer) header that LlmsTxt's
 *   Codex review finding #11 surfaced.
 * - Strips caller-supplied `Authorization` header (both casings) before merge
 *   so the empty-bearer guard cannot be bypassed by callers passing
 *   `{ Authorization: undefined }`.
 *
 * Callers pass an `authContextResolver` thunk (typically
 * `() => this.getContext(UMB_AUTH_CONTEXT)`) so the helper stays agnostic of
 * Bellissima's `UmbElementMixin` type system and works against any caller that
 * can produce an auth-context Promise.
 */
export class AuthContextUnavailableError extends Error {
  override readonly name = "AuthContextUnavailableError";
}

export type AuthContextResolver = () => Promise<unknown>;

export interface AuthenticatedFetchOptions {
  method?: string;
  /**
   * If set, the helper JSON-stringifies it and adds `Content-Type:
   * application/json`. Use `undefined` for GET requests; use a serialisable
   * object for PUT/POST.
   */
  body?: unknown;
  /**
   * Caller-managed AbortSignal so each surface retains abort control over
   * its own in-flight requests (the widget lazy-creates an AbortController
   * in `_submit` and disposes in `disconnectedCallback`).
   */
  signal: AbortSignal;
  /**
   * Extra request headers merged on top of the helper's defaults
   * (`Accept: application/json`, `Authorization: Bearer ...`,
   * `Content-Type: application/json` when `body` is present). Caller-supplied
   * keys override defaults — **except** `Authorization` (any casing), which
   * is helper-managed and silently dropped from caller headers before the
   * merge. This preserves the empty-bearer guard.
   */
  headers?: Record<string, string>;
}

export async function authenticatedFetch(
  authContextResolver: AuthContextResolver,
  path: string,
  options: AuthenticatedFetchOptions,
): Promise<Response> {
  // Spike 0.B locked decision #11 — bearer-token only against the Management
  // API. Cookie-only fetches return 401 because the Management API enforces
  // OpenIddict bearer-token auth, not cookie auth.
  const authContext = await authContextResolver();
  if (!authContext) {
    throw new AuthContextUnavailableError("Auth context unavailable");
  }
  const config = (
    authContext as {
      getOpenApiConfiguration: () => {
        base: string;
        credentials: RequestCredentials;
        token: () => Promise<string | undefined>;
      };
    }
  ).getOpenApiConfiguration();

  let token: string | undefined;
  try {
    token = await config.token();
  } catch {
    throw new AuthContextUnavailableError("Token acquisition failed");
  }
  // Whitespace-only tokens are also rejected — `!token` only catches falsy
  // values, but `"   "` would otherwise produce `Authorization: Bearer    `,
  // i.e. the same silent-empty-bearer shape that LlmsTxt Codex finding #11
  // surfaced.
  if (!token || token.trim() === "") {
    throw new AuthContextUnavailableError("Token acquisition returned empty");
  }

  const hasBody = options.body !== undefined;
  const baseHeaders: Record<string, string> = {
    Accept: "application/json",
    Authorization: `Bearer ${token}`,
    ...(hasBody ? { "Content-Type": "application/json" } : {}),
  };
  // Caller-supplied keys merge AFTER base, so they can override Accept /
  // Content-Type when needed — but `Authorization` is helper-managed and MUST
  // NOT be overridable, otherwise the empty-bearer guard above is bypassed by
  // a caller passing `{ Authorization: undefined }` (or any other value).
  // Strip both casings before the merge so that lowercase `authorization` keys
  // (which would otherwise duplicate the `Authorization` header rather than
  // override it) cannot slip through either.
  const callerHeaders: Record<string, string> = { ...(options.headers ?? {}) };
  delete callerHeaders.Authorization;
  delete callerHeaders.authorization;
  const mergedHeaders = {
    ...baseHeaders,
    ...callerHeaders,
  };

  return fetch(`${config.base}${path}`, {
    method: options.method ?? "GET",
    credentials: config.credentials,
    signal: options.signal,
    headers: mergedHeaders,
    body: hasBody ? JSON.stringify(options.body) : undefined,
  });
}
