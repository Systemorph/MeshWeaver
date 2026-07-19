// POST /api/mesh/resolve turns a URL path into its live-subscription target (node address + area/id)
// AND carries the node's configured RedirectOnDenied (the REST twin of hub.GetRedirectOnDenied), so a
// denied viewer can be redirected to the public cover instead of a dead-end error.
import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchAreaTarget } from "../src/server/snapshot";

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

describe("fetchAreaTarget", () => {
  it("parses prefix/remainder and the redirectOnDenied hint", async () => {
    const spy = mockFetch(
      () =>
        new Response(
          JSON.stringify({ prefix: "Course/Lesson1", remainder: "Overview", redirectOnDenied: "Course/Cover" }),
          { status: 200 },
        ),
    );

    const target = await fetchAreaTarget("https://p", "mw_t", "Course/Lesson1/Overview");

    const [url, init] = spy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("https://p/api/mesh/resolve");
    expect((init.headers as Record<string, string>).authorization).toBe("Bearer mw_t");
    expect(target).toEqual({
      address: "Course/Lesson1",
      area: "Overview",
      id: "",
      redirectOnDenied: "Course/Cover",
    });
  });

  it("defaults redirectOnDenied to null when the resolve response omits it", async () => {
    mockFetch(() => new Response(JSON.stringify({ prefix: "ACME/Pricing", remainder: "" }), { status: 200 }));
    const target = await fetchAreaTarget("https://p", "t", "ACME/Pricing");
    expect(target).toEqual({ address: "ACME/Pricing", area: "", id: "", redirectOnDenied: null });
  });

  it("falls back to the whole path (redirectOnDenied null) on a sentinel / error / older portal", async () => {
    mockFetch(() => new Response("Not found: No/Such/Node", { status: 200 }));
    expect(await fetchAreaTarget("https://p", "t", "No/Such/Node")).toEqual({
      address: "No/Such/Node",
      area: "",
      id: "",
      redirectOnDenied: null,
    });

    mockFetch(() => new Response("Not Found", { status: 404 }));
    expect(await fetchAreaTarget("https://p", "t", "A/B")).toEqual({
      address: "A/B",
      area: "",
      id: "",
      redirectOnDenied: null,
    });
  });
});
