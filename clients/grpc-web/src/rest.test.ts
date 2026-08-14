import { describe, expect, it } from "vitest";
import { MeshRest } from "./rest";

// THE SHARED /api/mesh CLIENT'S CONTRACT (issue #1497).
//
// Three shells hand-rolled these four verbs and drifted in ways nobody chose — the token, the error
// convention, and the base-URL composition. These cases pin the decisions that replaced the drift,
// so a future change has to break a test rather than break one shell quietly.

/** Records every request and replays a canned response. */
function stub(body: string, init: { status?: number; ok?: boolean } = {}) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchImpl = (async (url: string | URL | Request, requestInit?: RequestInit) => {
    calls.push({ url: String(url), init: requestInit ?? {} });
    const status = init.status ?? 200;
    return {
      ok: init.ok ?? status < 400,
      status,
      text: async () => body,
    } as Response;
  }) as unknown as typeof globalThis.fetch;
  return { calls, fetchImpl };
}

const headersOf = (init: RequestInit) => (init.headers ?? {}) as Record<string, string>;

describe("the Authorization header is applied in ONE place", () => {
  it("every verb carries Bearer when a token is present", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [], items: [], html: "" }));
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "mw_tok", fetch: fetchImpl });

    await rest.queryNodes("nodeType:Story");
    await rest.renderMarkdown("# hi");
    await rest.listContent("acme/Docs");
    await rest.uploadContent("acme/Docs/a.txt", new Blob(["x"]), "a.txt");

    expect(calls).toHaveLength(4);
    for (const call of calls) {
      expect(headersOf(call.init)["authorization"]).toBe("Bearer mw_tok");
    }
  });

  // 🚨 #1474 in one assertion. RN's copies carried NO token and every REST op 401'd against a real
  // portal — and a 401 here reads as "the file browser is empty", not as an auth error.
  it("no verb is left without it — the defect #1474 was", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [], items: [] }));
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "mw_tok", fetch: fetchImpl });

    await rest.listContent("acme/Docs");
    await rest.uploadContent("acme/Docs/a.txt", new Blob(["x"]), "a.txt");

    expect(calls.every((c) => "authorization" in headersOf(c.init))).toBe(true);
  });

  // The anonymous same-origin sidecar must receive NO header — `Bearer ` with an empty value is not
  // the same as absent, and the sidecar rejects it.
  it("an absent token sends NO Authorization header at all", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [], items: [] }));
    const rest = new MeshRest({ baseUrl: "http://127.0.0.1:5055", fetch: fetchImpl });

    await rest.queryNodes("nodeType:Story");
    await rest.listContent("acme/Docs");

    expect(calls.every((c) => !("authorization" in headersOf(c.init)))).toBe(true);
  });

  it("an EMPTY token is the same as absent, not `Bearer `", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [] }));
    const rest = new MeshRest({ baseUrl: "http://127.0.0.1:5055", token: "", fetch: fetchImpl });

    await rest.queryNodes("nodeType:Story");

    expect(headersOf(calls[0].init)["authorization"]).toBeUndefined();
  });
});

describe("the error convention, decided once", () => {
  // A listing that cannot be served renders empty rather than breaking the page embedding it.
  it("queryNodes answers [] on a non-ok response", async () => {
    const { fetchImpl } = stub("nope", { status: 500 });
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await expect(rest.queryNodes("nodeType:Story")).resolves.toEqual([]);
  });

  it("queryNodes answers [] on the server's Error:/Not found: text sentinels", async () => {
    for (const body of ["Error: boom", "Not found: acme"]) {
      const { fetchImpl } = stub(body);
      const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });
      await expect(rest.queryNodes("q")).resolves.toEqual([]);
    }
  });

  it("queryNodes answers [] when fetch itself throws", async () => {
    const fetchImpl = (async () => {
      throw new Error("network down");
    }) as unknown as typeof globalThis.fetch;
    const rest = new MeshRest({ baseUrl: "https://portal.example", fetch: fetchImpl });

    await expect(rest.queryNodes("q")).resolves.toEqual([]);
  });

  // …but a WRITE, and a render whose output the page depends on, must not fail silently.
  it("listContent throws rather than returning an empty listing", async () => {
    const { fetchImpl } = stub("Error: denied");
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await expect(rest.listContent("acme/Docs")).rejects.toThrow(/denied/);
  });

  it("uploadContent throws — a silent non-upload is worse than an error", async () => {
    const { fetchImpl } = stub("Error: too large", { status: 413 });
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await expect(rest.uploadContent("acme/Docs/a.txt", new Blob(["x"]), "a.txt")).rejects.toThrow();
  });

  it("renderMarkdown throws on a non-ok response", async () => {
    const { fetchImpl } = stub("", { status: 500 });
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await expect(rest.renderMarkdown("# hi")).rejects.toThrow(/render-markdown failed/);
  });
});

describe("composition rules", () => {
  it("normalises a trailing slash on the base URL exactly once", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [] }));
    const rest = new MeshRest({ baseUrl: "https://portal.example///", token: "t", fetch: fetchImpl });

    await rest.queryNodes("q");

    expect(calls[0].url).toBe("https://portal.example/api/mesh/query-nodes");
  });

  // 🚨 FormData supplies its own content-type WITH the boundary; overriding it makes the server
  // unable to parse the body at all.
  it("multipart sets no content-type", async () => {
    const { calls, fetchImpl } = stub("");
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await rest.uploadContent("acme/Docs/a.txt", new Blob(["x"]), "a.txt");

    expect(headersOf(calls[0].init)["content-type"]).toBeUndefined();
    expect(headersOf(calls[0].init)["authorization"]).toBe("Bearer t");
  });

  it("the JSON verbs do send content-type", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [] }));
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await rest.queryNodes("q");

    expect(headersOf(calls[0].init)["content-type"]).toBe("application/json");
  });

  it("each verb posts to its own documented path", async () => {
    const { calls, fetchImpl } = stub(JSON.stringify({ results: [], items: [], html: "" }));
    const rest = new MeshRest({ baseUrl: "https://portal.example", token: "t", fetch: fetchImpl });

    await rest.queryNodes("q");
    await rest.renderMarkdown("# hi");
    await rest.listContent("acme/Docs");
    await rest.uploadContent("acme/Docs/a.txt", new Blob(["x"]), "a.txt");

    expect(calls.map((c) => c.url)).toEqual([
      "https://portal.example/api/mesh/query-nodes",
      "https://portal.example/api/mesh/render-markdown",
      "https://portal.example/api/mesh/content/list",
      "https://portal.example/api/mesh/upload",
    ]);
  });
});
