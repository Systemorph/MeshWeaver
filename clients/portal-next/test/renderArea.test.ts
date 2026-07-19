// The PRIMARY SSR seed: POST /api/mesh/render-area returns the first Full {areas,data} frame
// EXACTLY as the gRPC wire delivers it ($type discriminators, JSON-encoded InstanceCollection
// keys); fetchRenderedArea folds it with the SAME normalize the live GrpcAreaSource applies, and
// returns a discriminated result: `ok` (hydratable tree), `denied` (RLS access denial ⇒ the shell
// may redirect to the node's public cover), or `none` (any other miss ⇒ node-snapshot preview).
import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchRenderedArea } from "../src/server/snapshot";

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

/** A wire-faithful Full frame: EntityStore $type marker, JSON-ENCODED instance keys
 *  (InstanceCollectionConverter: "Overview" rides as the property `"\"Overview\""`), and the
 *  default-area areas[""] NamedArea indirection the base frame statically seeds. */
const wireFrame = {
  $type: "MeshWeaver.Data.EntityStore",
  areas: {
    '""': { $type: "NamedAreaControl", area: "Overview", skins: [] },
    '"Overview"': {
      $type: "StackControl",
      areas: [
        { $type: "NamedAreaControl", area: "Overview/1" },
        { $type: "NamedAreaControl", area: "Overview/2" },
      ],
      skins: [{ $type: "LayoutStackSkin", orientation: "Vertical" }],
    },
    '"Overview/1"': { $type: "HtmlControl", data: "<h1>Pricing Cornerstone</h1>" },
    '"Overview/2"': { $type: "MarkdownControl", data: "Live **mesh** content." },
  },
  data: {
    '"progress"': { message: "", progress: 100 },
  },
};

describe("fetchRenderedArea", () => {
  it("POSTs /api/mesh/render-area with the Bearer token and folds the wire frame", async () => {
    const spy = mockFetch(() => new Response(JSON.stringify(wireFrame), { status: 200 }));

    const result = await fetchRenderedArea("https://portal.example", "mw_abc", "ACME/Pricing");

    const [url, init] = spy.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("https://portal.example/api/mesh/render-area");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>).authorization).toBe("Bearer mw_abc");
    expect(JSON.parse(String(init.body))).toEqual({ path: "ACME/Pricing" });

    // Instance keys decoded to PLAIN names (the shape StaticAreaSource / the renderer consume);
    // the $type collection marker is dropped, control $types stay wire-faithful.
    expect(result.kind).toBe("ok");
    const tree = result.kind === "ok" ? result.tree : null;
    expect(Object.keys(tree!.areas!)).toEqual(["", "Overview", "Overview/1", "Overview/2"]);
    expect(tree!.areas![""]).toMatchObject({ $type: "NamedAreaControl", area: "Overview" });
    expect(tree!.areas!["Overview/2"]).toMatchObject({ $type: "MarkdownControl" });
    expect(tree!.data).toMatchObject({ progress: { progress: 100 } });
    expect(tree).not.toHaveProperty("$type");
  });

  it("is `none` on 404 — an older portal without the render-area verb (preview fallback)", async () => {
    mockFetch(() => new Response("Not Found", { status: 404 }));
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");
  });

  it("is `none` on the 504 render-timeout JSON error", async () => {
    mockFetch(
      () =>
        new Response(JSON.stringify({ error: "Timed out after 30s waiting for the first full frame" }), {
          status: 504,
        }),
    );
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");
  });

  it("is `denied` on the RLS access-denied sentinel (so the shell can redirect to the paywall)", async () => {
    mockFetch(() => new Response("Error: Access denied", { status: 200 }));
    expect((await fetchRenderedArea("https://p", "t", "Secret/Node")).kind).toBe("denied");

    mockFetch(
      () => new Response("Error: user 'u' lacks Read permission on 'Course/Lesson'", { status: 200 }),
    );
    expect((await fetchRenderedArea("https://p", "t", "Course/Lesson")).kind).toBe("denied");
  });

  it("is `none` on a non-denial sentinel (unknown path / other error)", async () => {
    mockFetch(() => new Response("Not found: No/Such/Node", { status: 200 }));
    expect((await fetchRenderedArea("https://p", "t", "No/Such/Node")).kind).toBe("none");

    mockFetch(() => new Response("Error: something else broke", { status: 200 }));
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");
  });

  it("is `none` when the frame carries no areas (not hydratable)", async () => {
    mockFetch(() => new Response(JSON.stringify({ $type: "MeshWeaver.Data.EntityStore", data: {} }), { status: 200 }));
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");
  });

  it("is `none` on an unparseable body or network error", async () => {
    mockFetch(() => new Response("<html>proxy error</html>", { status: 200 }));
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");

    globalThis.fetch = vi.fn(() => Promise.reject(new Error("ECONNREFUSED"))) as unknown as typeof fetch;
    expect((await fetchRenderedArea("https://p", "t", "A/B")).kind).toBe("none");
  });
});
