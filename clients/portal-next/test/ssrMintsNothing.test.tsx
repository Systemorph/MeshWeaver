// 🚨 THE COUNTING TEST for issue #1477: a server-side page render must leave NOTHING behind.
//
// Every `POST /api/tokens` writes TWO PERMANENT mesh nodes into the visitor's partition —
// `{userId}/ApiToken/{hash12}` and the global `ApiToken/{hash12}` index (ApiTokenService.CreateToken).
// Nothing ever deleted them on the client's behalf, so an SSR that minted per render turned plain
// page traffic into unbounded growth of CREDENTIAL nodes: 50 pages browsed = 100 permanent tokens,
// a crawler far worse. The mint call is the seam where that growth is observable from here, so this
// asserts the count of mints — not the shape of the code that avoids them.
//
// Pre-fix this file fails on the very first assertion: AreaSnapshot's first act was `mintToken`.
import { afterEach, describe, expect, it, vi } from "vitest";
import { AreaSnapshot } from "../app/[[...meshPath]]/AreaSnapshot";
import { resetRequest, setRequest } from "./stubs/next-headers";

const realFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = realFetch;
  resetRequest();
  vi.restoreAllMocks();
});

interface Call {
  url: string;
  init: RequestInit;
}

/** A portal that answers every SSR read verb, recording what was asked of it. */
function portal(): Call[] {
  const calls: Call[] = [];
  globalThis.fetch = vi.fn((url: string | URL | Request, init?: RequestInit) => {
    const u = String(url);
    calls.push({ url: u, init: init ?? {} });
    if (u.endsWith("/api/mesh/whoami"))
      return Promise.resolve(
        new Response(JSON.stringify({ userId: "rbuergi", name: "Roland", email: "r@example.com" }), { status: 200 }),
      );
    if (u.endsWith("/api/mesh/resolve"))
      return Promise.resolve(new Response(JSON.stringify({ prefix: "Doc/GUI", remainder: "" }), { status: 200 }));
    if (u.endsWith("/api/mesh/render-area"))
      return Promise.resolve(
        new Response(
          JSON.stringify({ areas: { '""': { $type: "LabelControl", data: "GUI Documentation" } }, data: {} }),
          { status: 200 },
        ),
      );
    if (u.endsWith("/api/tokens"))
      return Promise.resolve(
        new Response(JSON.stringify({ rawToken: "mw_x", nodePath: "rbuergi/ApiToken/abc" }), { status: 200 }),
      );
    return Promise.resolve(new Response("Not found: unexpected verb", { status: 200 }));
  }) as unknown as typeof fetch;
  return calls;
}

const signedIn = { cookies: { MemexAuth: "session-value" }, headers: { host: "portal.example" } };

describe("SSR page render mints no API token", () => {
  it("renders a node page without a single POST /api/tokens", async () => {
    const calls = portal();
    setRequest(signedIn);

    await AreaSnapshot({ path: "Doc/GUI" });

    expect(calls.filter((c) => c.url.endsWith("/api/tokens"))).toHaveLength(0);
    // …and it did do its job: the viewer was resolved and the area was rendered.
    expect(calls.some((c) => c.url.endsWith("/api/mesh/whoami"))).toBe(true);
    expect(calls.some((c) => c.url.endsWith("/api/mesh/render-area"))).toBe(true);
  });

  it("browsing 50 pages leaves the ApiToken node count exactly where it started", async () => {
    const calls = portal();
    setRequest(signedIn);

    for (let i = 0; i < 50; i++) await AreaSnapshot({ path: `Doc/Page${i}` });

    const mints = calls.filter((c) => c.url.endsWith("/api/tokens")).length;
    // 2 permanent ApiToken nodes per mint — pre-fix this was 50 mints ⇒ 100 nodes.
    expect(mints * 2).toBe(0);
  });

  it("authorizes every snapshot read with the FORWARDED session cookie, never a Bearer token", async () => {
    const calls = portal();
    setRequest(signedIn);

    await AreaSnapshot({ path: "Doc/GUI" });

    expect(calls.length).toBeGreaterThan(0);
    for (const call of calls) {
      const headers = (call.init.headers ?? {}) as Record<string, string>;
      expect(headers.cookie).toBe("MemexAuth=session-value");
      expect(headers.authorization).toBeUndefined();
    }
  });

  it("renders the viewer's home partition on the bare route — resolved from whoami, not from a mint", async () => {
    portal();
    setRequest(signedIn);

    const element = await AreaSnapshot({ path: "" });

    // The home route binds {userId}/Activity, exactly as the Blazor Index.razor does.
    expect(element.props.path).toBe("rbuergi");
    expect(element.props.target).toMatchObject({ address: "rbuergi", area: "Activity" });
    expect(element.props.unauthenticated).toBe(false);
  });

  it("degrades to the app shell for an anonymous request — and still mints nothing", async () => {
    const calls = portal();
    setRequest({ headers: { host: "portal.example" } }); // no cookies at all

    const element = await AreaSnapshot({ path: "Doc/GUI" });

    expect(element.props.unauthenticated).toBe(true);
    expect(calls.filter((c) => c.url.endsWith("/api/tokens"))).toHaveLength(0);
  });
});
