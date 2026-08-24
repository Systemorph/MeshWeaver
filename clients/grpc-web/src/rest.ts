// THE ONE /api/mesh REST CLIENT — the shells' shared implementation of the four verbs each of them
// used to hand-roll (issue #1497).
//
// Three shells carried their own copy of `query-nodes`, `render-markdown`, `content/list` and
// `upload`: clients/portal-next/src/client/live.ts, clients/portal/src/live.ts and
// MeshWeaver.Plugins/app/react-native/src/liveOps.ts. Duplicating a fetch does not merely repeat code — it lets the
// copies DRIFT in ways nobody chose, and they had:
//
//   * THE TOKEN. RN's three verbs carried no Authorization header at all — that IS #1474, and it is
//     exactly the failure a duplicated fetch invites: the two web shells were written with the
//     token, the third copy was not, and a 401 there reads as "the file browser is empty", not as an
//     auth error.
//   * ERROR SEMANTICS. `queryNodes` returned [] on failure in the web shells but throws in
//     `Mesh.search`. Neither convention was written down; each copy picked one.
//   * THE BASE URL. Only RN normalised a trailing slash, so `https://host/` + `/api/mesh/...`
//     produced a double slash in the other two.
//
// So the rules are decided ONCE, here:
//
//   1. `Authorization: Bearer` is applied in ONE place — and ONLY when a token is present. An empty
//      token means the anonymous same-origin sidecar, which must not receive the header at all
//      (sending `Bearer ` is not the same as sending nothing). RN had this right; the web shells
//      did not.
//   2. The base URL is normalised once, on construction.
//   3. A LISTING degrades, a WRITE throws. `queryNodes` answers [] on any failure, because a query
//      that cannot be served should render an empty list rather than break the page that embeds it;
//      `renderMarkdown`, `listContent` and `uploadContent` throw, because silently rendering nothing
//      or silently not uploading is worse than an error the caller can show. That is the convention,
//      and it is now written down rather than re-decided per copy.
//   4. Multipart sets NO content-type — `FormData` supplies it with its boundary, and overriding it
//      makes the server unable to parse the body.
//
// 🚨 Verbs genuinely local to one shell stay there: portal-next's `deleteNode`, `transcribe` and
// `autocomplete` are not part of this surface. This owns the four the shells actually shared.
//
// `restContract.test.ts` (in this package) already asserts that both backends serve every path a
// client posts to; it is the natural home for this client's own tests too.

/** How a shell reaches the portal's REST surface. */
export interface MeshRestOptions {
  /** Portal origin serving `/api/mesh/*` — trailing slashes are normalised away. */
  baseUrl: string;
  /** Bearer token. OMIT (or pass "") for the anonymous same-origin sidecar — see rule 1. */
  token?: string;
  /** Custom fetch — the React Native seam. Unary REST works with the platform fetch. */
  fetch?: typeof globalThis.fetch;
}

/** One row of a `query-nodes` result — the node fields the shells actually read. */
export interface MeshNodeRow {
  path?: string;
  name?: string;
  nodeType?: string;
  [key: string]: unknown;
}

/** One entry in a content-collection directory. */
export interface ContentItem {
  kind: "folder" | "file" | "unknown";
  name: string;
  path: string;
  itemCount?: number;
  lastModified?: string;
}

/** A content-collection listing. */
export interface ContentListing {
  collection: string;
  path: string;
  editable: boolean;
  items: ContentItem[];
}

/** Server-rendered markdown plus the executable cells the Markdig pipeline found. */
export interface RenderedMarkdown {
  html: string;
  codeSubmissions: MarkdownCellSubmission[];
}

/**
 * An executable code cell the server's markdown pipeline emitted.
 *
 * 🚨 Declared here rather than imported from `@meshweaver/react`: this package is the transport
 * layer and the renderer depends on IT, never the reverse. The members are the SERVER's wire shape
 * (Markdig's `ExecutableCodeBlockRenderer`), which is what makes it correct to own at this layer —
 * and it must stay structurally assignable to the renderer's `MarkdownCellSubmission`, or every
 * shell has to cast the result of `renderMarkdown`.
 */
export interface MarkdownCellSubmission {
  code: string;
  id: string;
  language?: string;
}

export class MeshRest {
  private readonly baseUrl: string;
  private readonly token: string;
  private readonly doFetch: typeof globalThis.fetch;

  constructor(options: MeshRestOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.token = options.token ?? "";
    // 🚨 `fetch` is a Web IDL operation: the browser requires the GLOBAL as its `this`. Storing it
    // bare on the instance made every call `this.doFetch(...)` — i.e. `fetch` invoked with the
    // MeshRest as `this` — which throws `TypeError: Failed to execute 'fetch' on 'Window': Illegal
    // invocation`. That killed the DEFAULT path every shell uses (no injected fetch): markdown
    // pages rendered their own raw source (`renderMarkdown` rejects → the caller's fallback) and the
    // file browser read empty (`queryNodes` swallows failures by rule 3). Invisible to the tests
    // because every one of them injects a plain-function fetch, for which `this` never mattered.
    // So the call is wrapped ONCE, here: the closure fixes `this` for the default AND normalises an
    // injected fetch, which the caller has already detached from its own receiver by passing it.
    const supplied = options.fetch;
    this.doFetch = supplied
      ? (input, init) => supplied(input, init)
      : (input, init) => globalThis.fetch(input, init);
  }

  /**
   * Full-node mesh query (`POST /api/mesh/query-nodes`) — the browser twin of `IMeshService.Query`.
   * Answers `[]` on ANY failure (rule 3): a listing that cannot be served renders empty rather than
   * breaking the page that embeds it.
   */
  async queryNodes(query: string, limit = 50): Promise<MeshNodeRow[]> {
    try {
      const resp = await this.doFetch(`${this.baseUrl}/api/mesh/query-nodes`, {
        method: "POST",
        headers: this.jsonHeaders(),
        body: JSON.stringify({ query, limit }),
      });
      if (!resp.ok) return [];
      const text = await resp.text();
      if (text.startsWith("Error:") || text.startsWith("Not found:")) return [];
      const parsed = JSON.parse(text) as { results?: MeshNodeRow[] };
      return Array.isArray(parsed.results) ? parsed.results : [];
    } catch {
      return [];
    }
  }

  /** Server-side Markdig render (`POST /api/mesh/render-markdown`) — the ONE markdown parser. */
  async renderMarkdown(markdown: string, nodePath?: string): Promise<RenderedMarkdown> {
    const resp = await this.doFetch(`${this.baseUrl}/api/mesh/render-markdown`, {
      method: "POST",
      headers: this.jsonHeaders(),
      body: JSON.stringify({ markdown, nodePath: nodePath ?? null }),
    });
    if (!resp.ok) throw new Error(`render-markdown failed (${resp.status})`);
    const text = await resp.text();
    if (text.startsWith("Error:")) throw new Error(text);
    const parsed = JSON.parse(text) as { html?: string; codeSubmissions?: MarkdownCellSubmission[] };
    return { html: parsed.html ?? "", codeSubmissions: parsed.codeSubmissions ?? [] };
  }

  /**
   * Content-collection listing (`POST /api/mesh/content/list`) — the read half of the file browser.
   * `path` is `{node}/{collection}[/{dir}]`.
   */
  async listContent(path: string): Promise<ContentListing> {
    const resp = await this.doFetch(`${this.baseUrl}/api/mesh/content/list`, {
      method: "POST",
      headers: this.jsonHeaders(),
      body: JSON.stringify({ path }),
    });
    const text = await resp.text();
    if (!resp.ok || text.startsWith("Error:")) throw new Error(text || `content list failed (${resp.status})`);
    const parsed = JSON.parse(text) as Partial<ContentListing>;
    return {
      collection: String(parsed.collection ?? ""),
      path: String(parsed.path ?? ""),
      editable: !!parsed.editable,
      items: Array.isArray(parsed.items) ? parsed.items : [],
    };
  }

  /**
   * Content upload (`POST /api/mesh/upload`, multipart) — `path` is `{node}/{collection}/{filePath}`.
   * 🚨 No content-type header: FormData supplies it WITH its boundary (rule 4).
   */
  async uploadContent(path: string, file: File | Blob, fileName?: string): Promise<void> {
    const form = new FormData();
    form.append("path", path);
    form.append("file", file as Blob, fileName ?? (file as { name?: string }).name ?? "upload");
    const resp = await this.doFetch(`${this.baseUrl}/api/mesh/upload`, {
      method: "POST",
      headers: this.authHeaders(),
      body: form,
    });
    const text = await resp.text();
    if (!resp.ok || text.startsWith("Error:")) throw new Error(text || `upload failed (${resp.status})`);
  }

  /**
   * Bearer, and ONLY when there is a token (rule 1). The anonymous same-origin sidecar must receive
   * no Authorization header at all — `Bearer ` with an empty value is not the same as absent.
   */
  private authHeaders(): Record<string, string> {
    return this.token ? { authorization: `Bearer ${this.token}` } : {};
  }

  private jsonHeaders(): Record<string, string> {
    return { "content-type": "application/json", ...this.authHeaders() };
  }
}
