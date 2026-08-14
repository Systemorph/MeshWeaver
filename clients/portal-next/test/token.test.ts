// The server-side credential forward: the INCOMING request's cookies go to the portal's
// cookie-authorized READ verbs. The server holds no API token and mints none (issue #1477) —
// see ssrMintsNothing.test.tsx for the assertion on the count.
import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchNodeSnapshot, fetchViewer, resolvePortalOrigin } from "../src/server/snapshot";

const realFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = realFetch;
  vi.restoreAllMocks();
});

function mockFetch(handler: (url: string, init: RequestInit) => Response | Promise<Response>) {
  const spy = vi.fn((url: string | URL | Request, init?: RequestInit) => Promise.resolve(handler(String(url), init ?? {})));
  globalThis.fetch = spy as unknown as typeof fetch;
  return spy;
}

describe("fetchViewer", () => {
  it("forwards the incoming request's cookies to the portal's identity read", async () => {
    const spy = mockFetch(
      () =>
        new Response(JSON.stringify({ userId: "rbuergi", name: "Roland", email: "r@systemorph.com" }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
    );

    const viewer = await fetchViewer("https://portal.example", "MemexAuth=chunk1; other=2");

    expect(spy).toHaveBeenCalledTimes(1);
    const [url, init] = spy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("https://portal.example/api/mesh/whoami");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>).cookie).toBe("MemexAuth=chunk1; other=2");
    // 🚨 No credential is created and none is presented — the cookie IS the credential.
    expect((init.headers as Record<string, string>).authorization).toBeUndefined();

    expect(viewer).toEqual({ userId: "rbuergi", name: "Roland", email: "r@systemorph.com" });
  });

  it("takes the home partition from the resolved mesh user id (the default route)", async () => {
    mockFetch(() => new Response(JSON.stringify({ userId: "acme-admin" }), { status: 200 }));
    const viewer = await fetchViewer("https://p", "c=1; unique-a");
    expect(viewer?.userId).toBe("acme-admin");
  });

  it("returns null when there is no cookie to forward (anonymous request)", async () => {
    const spy = mockFetch(() => new Response("{}", { status: 200 }));
    expect(await fetchViewer("https://p", "")).toBeNull();
    expect(spy).not.toHaveBeenCalled();
  });

  it("returns null when the portal resolved no real mesh user", async () => {
    mockFetch(() => new Response(JSON.stringify({ userId: null }), { status: 200 }));
    expect(await fetchViewer("https://p", "c=1; unique-d")).toBeNull();
  });

  it("returns null on an unauthenticated read (401) instead of throwing", async () => {
    mockFetch(() => new Response("", { status: 401 }));
    expect(await fetchViewer("https://p", "c=1; unique-b")).toBeNull();
  });

  it("returns null when the origin has no portal (network error)", async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new Error("ECONNREFUSED"))) as unknown as typeof fetch;
    expect(await fetchViewer("https://p", "c=1; unique-c")).toBeNull();
  });
});

describe("fetchNodeSnapshot", () => {
  it("POSTs /api/mesh/get with the forwarded cookie and parses the node JSON", async () => {
    const spy = mockFetch(
      () =>
        new Response(
          JSON.stringify({
            path: "Doc/GUI",
            name: "GUI Documentation",
            nodeType: "Markdown",
            content: { markdown: "# Hello mesh" },
          }),
          { status: 200 },
        ),
    );

    const snap = await fetchNodeSnapshot("https://portal.example", "MemexAuth=abc", "Doc/GUI");

    const [url, init] = spy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("https://portal.example/api/mesh/get");
    expect((init.headers as Record<string, string>).cookie).toBe("MemexAuth=abc");
    expect((init.headers as Record<string, string>).authorization).toBeUndefined();
    expect(JSON.parse(String(init.body))).toEqual({ path: "Doc/GUI" });

    expect(snap).toEqual({
      path: "Doc/GUI",
      name: "GUI Documentation",
      nodeType: "Markdown",
      markdown: "# Hello mesh",
    });
  });

  it("tolerates PascalCase hub serialization", async () => {
    mockFetch(
      () =>
        new Response(JSON.stringify({ Path: "A/B", Name: "B", NodeType: "Space", Content: { Description: "desc" } }), {
          status: 200,
        }),
    );
    const snap = await fetchNodeSnapshot("https://p", "c=1", "A/B");
    expect(snap).toEqual({ path: "A/B", name: "B", nodeType: "Space", markdown: "desc" });
  });

  it("handles the MeshOperations 'Error:'/'Not found:' sentinel strings (shipped as raw text)", async () => {
    mockFetch(() => new Response("Not found: No/Such/Node", { status: 200 }));
    expect(await fetchNodeSnapshot("https://p", "c=1", "No/Such/Node")).toBeNull();

    mockFetch(() => new Response("Error: access denied", { status: 200 }));
    expect(await fetchNodeSnapshot("https://p", "c=1", "X")).toBeNull();
  });

  it("unwraps the broken-NodeType envelope ({ node, compilationError })", async () => {
    mockFetch(
      () =>
        new Response(JSON.stringify({ node: { path: "T/Broken", name: "Broken" }, compilationError: "CS0103" }), {
          status: 200,
        }),
    );
    const snap = await fetchNodeSnapshot("https://p", "c=1", "T/Broken");
    expect(snap?.name).toBe("Broken");
  });
});

describe("resolvePortalOrigin", () => {
  const env = (vars: Record<string, string>) => vars as unknown as NodeJS.ProcessEnv;

  it("prefers the PORTAL_ORIGIN env", () => {
    const h = new Headers({ host: "ignored.example" });
    expect(resolvePortalOrigin(h, env({ PORTAL_ORIGIN: "https://memex.meshweaver.cloud/" }))).toBe(
      "https://memex.meshweaver.cloud",
    );
  });

  it("defaults to the forwarded host (same-host deployment behind the ingress)", () => {
    const h = new Headers({ "x-forwarded-host": "memex.meshweaver.cloud", "x-forwarded-proto": "https", host: "pod" });
    expect(resolvePortalOrigin(h, env({}))).toBe("https://memex.meshweaver.cloud");
  });

  it("uses http for bare localhost", () => {
    const h = new Headers({ host: "localhost:3300" });
    expect(resolvePortalOrigin(h, env({}))).toBe("http://localhost:3300");
  });
});
