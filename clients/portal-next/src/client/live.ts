// Live mesh connection for the Next portal — the CLIENT half of the SSR/live split, ported from
// clients/portal/src/live.ts (the Vite SPA). After hydration the browser mints its own short-lived
// token same-origin (POST /api/tokens, cookie-authorized) and joins the mesh over gRPC-web
// (POST {origin}/meshweaver.v1.Mesh/Connect + /Deliver — mapped at the ORIGIN ROOT, independent of
// this app's /next basePath). The token lives in memory only — never persisted.
//
// The mint response's nodePath ("{userId}/ApiToken/…") reveals the caller's home partition, which
// is the default route — the same derivation the server-side snapshot module uses.
//
// This file is client-only by construction (window.location default); the server-side counterpart
// (src/server/snapshot.ts) is REST-only and never opens a stream.

import { GrpcAreaSource, type AreaSource } from "@meshweaver/react/core";
import type {
  ContentListing,
  MarkdownCellSubmission,
  MarkdownKernelSession,
  MeshNodeState,
  MeshOps,
  RenderedMarkdown,
  ThreadSubmitOptions,
} from "@meshweaver/react/core";
import { connect, Mesh, type MeshWebConnection } from "@meshweaver/client-web";

/** A queried mesh node row — the hub-serialized MeshNode shape (camelCase, $type-tagged content). */
export type MeshNodeRow = Record<string, unknown>;

/** One autocomplete suggestion off the wire AutocompleteResponse (AutocompleteItem, camelCase). */
export interface AutocompleteRow {
  label?: string;
  insertText?: string;
  description?: string;
  icon?: string;
  path?: string;
}

export interface LiveMesh {
  connection: MeshWebConnection;
  /** The thread/node operations surface ThreadChat consumes (Mesh.from(connection), adapted). */
  ops: MeshOps;
  /** The signed-in user's mesh id (their home partition) — the default route. */
  userId: string;
  /** One node snapshot (first state off its live stream). */
  getNode(path: string): Promise<MeshNodeRow | null>;
  /** Full-node mesh query over REST (`POST /api/mesh/query-nodes`) — the browser twin of the
   *  Blazor shell's IMeshService.Query<MeshNode> reads (search suggestions, notification bell).
   *  Returns [] on any failure — the shell degrades, never crashes. */
  queryNodes(query: string, limit?: number): Promise<MeshNodeRow[]>;
  /** Streaming-final autocomplete snapshot (the one-shot AutocompleteRequest the search bar's
   *  @path branch uses) — targeted at the user's own hub, which aggregates every provider. */
  autocomplete(query: string, contextPath?: string): Promise<AutocompleteRow[]>;
  close(): void;
}

/**
 * A STABLE PER-TAB participant id — a GUID kept in <c>sessionStorage</c> (unique per browser tab,
 * survives reloads within that tab, cleared when the tab closes). The client joins as
 * <c>portal/&lt;id&gt;</c> (its own server-side participant hub); because the id is stable across a
 * tab's reloads AND reconnects, the server keeps ONE participant hub + its
 * per-stream sync sub-hubs alive across them. A fresh random address per load (the old
 * <c>node/&lt;newId()&gt;</c> default) made every reload a NEW participant, orphaning the previous hubs
 * and racing sync-hub creation against incoming DataChangedEvents — the server then DROPS those events
 * ("no synchronization hub found … never-created sync hub"), so the page renders a random subset.
 * Per-TAB (not a shared cookie) so two tabs never collide on one participant address.
 */
function stableClientId(): string {
  const key = "mw-client-id";
  try {
    const existing = sessionStorage.getItem(key);
    if (existing) return existing;
    const id = crypto.randomUUID();
    sessionStorage.setItem(key, id);
    return id;
  } catch {
    // sessionStorage unavailable (private mode / SSR) — a per-load id still beats a per-connection one.
    return crypto.randomUUID();
  }
}

/** Mint a short-lived API token off the browser session, then join the mesh over gRPC-web. */
export async function connectLive(baseUrl: string = window.location.origin): Promise<LiveMesh> {
  const resp = await fetch(`${baseUrl}/api/tokens`, {
    method: "POST",
    credentials: "same-origin", // the session cookie authorizes the mint
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ label: "react-portal-next", expiresInDays: 1 }),
  });
  if (!resp.ok) throw new Error(`token mint failed (${resp.status} ${resp.statusText})`);
  const { rawToken, nodePath } = (await resp.json()) as { rawToken?: string; nodePath?: string };
  if (!rawToken) throw new Error("token mint returned no rawToken");

  // Join as a STABLE per-tab participant `portal/<id>` so the server keeps THIS tab's participant hub
  // + its per-stream sync sub-hubs alive across reloads/reconnects instead of dropping frames.
  const connection = await connect(baseUrl, { token: rawToken, address: `portal/${stableClientId()}` });
  const mesh = Mesh.from(connection);
  const userId = (nodePath ?? "").split("/")[0] ?? "";
  const runQuery = (query: string, limit = 50) => queryNodes(baseUrl, rawToken, query, limit);
  const runAutocomplete = (query: string, contextPath?: string) =>
    autocomplete(connection, userId, query, contextPath);
  const runRenderMarkdown = (markdown: string, nodePath?: string) =>
    renderMarkdown(baseUrl, rawToken, markdown, nodePath);
  const runDeleteNode = (path: string) => deleteNode(baseUrl, rawToken, path);
  const runStartKernel = (cells: MarkdownCellSubmission[]) =>
    startMarkdownKernel(connection, mesh, userId, cells, runDeleteNode);
  const runListContent = (path: string) => listContent(baseUrl, rawToken, path);
  const runUploadContent = (path: string, file: File) => uploadContent(baseUrl, rawToken, path, file);
  const runTranscribe = (audio: Blob, language?: string) => transcribe(baseUrl, rawToken, audio, language);
  return {
    connection,
    ops: adaptOps(mesh, runQuery, runAutocomplete, runRenderMarkdown, runStartKernel, runListContent, runUploadContent, runTranscribe),
    userId,
    getNode: (path: string) => mesh.get(path).then((n) => n.raw as MeshNodeRow).catch(() => null),
    queryNodes: runQuery,
    autocomplete: runAutocomplete,
    close: () => connection.close(),
  };
}

/** Node delete (`POST /api/mesh/delete`) — REST because the gRPC DeleteNodeRequest still targets
 *  the mesh/main address, which is unroutable on the distributed portal. Best-effort semantics
 *  belong to the caller. The Paths field is a JSON-encoded ARRAY (the verb's wire shape). */
async function deleteNode(baseUrl: string, token: string, path: string): Promise<void> {
  const resp = await fetch(`${baseUrl}/api/mesh/delete`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify({ paths: JSON.stringify([path]) }),
  });
  if (!resp.ok) throw new Error(`delete failed (${resp.status})`);
}

/** Server-side Markdig render (`POST /api/mesh/render-markdown`) — the ONE markdown parser. */
async function renderMarkdown(
  baseUrl: string,
  token: string,
  markdown: string,
  nodePath?: string,
): Promise<RenderedMarkdown> {
  const resp = await fetch(`${baseUrl}/api/mesh/render-markdown`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify({ markdown, nodePath: nodePath ?? null }),
  });
  if (!resp.ok) throw new Error(`render-markdown failed (${resp.status})`);
  const text = await resp.text();
  if (text.startsWith("Error:")) throw new Error(text);
  const parsed = JSON.parse(text) as { html?: string; codeSubmissions?: MarkdownCellSubmission[] };
  return { html: parsed.html ?? "", codeSubmissions: parsed.codeSubmissions ?? [] };
}

/** Speech-to-text (`POST /api/speech/transcribe`) — multipart audio → {text, language}, Bearer-auth. */
async function transcribe(
  baseUrl: string,
  token: string,
  audio: Blob,
  language?: string,
): Promise<{ text: string; language?: string }> {
  const form = new FormData();
  form.append("file", audio, "audio.webm");
  if (language) form.append("language", language);
  const resp = await fetch(`${baseUrl}/api/speech/transcribe`, {
    method: "POST",
    headers: { authorization: `Bearer ${token}` },
    body: form,
  });
  const parsed = (await resp.json().catch(() => ({}))) as { text?: string; language?: string; error?: string };
  if (!resp.ok || parsed.error) throw new Error(parsed.error ?? `transcribe failed (${resp.status})`);
  return { text: parsed.text ?? "", language: parsed.language };
}

/**
 * The per-view interactive-markdown kernel — the client twin of
 * MarkdownViewLogic.CreateActivityAndSubmit: create the kernel Activity node under the VIEWER's
 * partition (the only place the viewer can Create), wait until its per-node hub is ROUTABLE (the
 * node's own stream first emission proves it — posting before that NotFound-storms the router),
 * then post the cell submissions IN ORDER (each awaited: the kernel builds state cell by cell).
 */
async function startMarkdownKernel(
  connection: MeshWebConnection,
  mesh: Mesh,
  userId: string,
  cells: MarkdownCellSubmission[],
  deleteActivity?: (path: string) => Promise<void>,
): Promise<MarkdownKernelSession> {
  if (!userId) throw new Error("no viewing user — nowhere to anchor the kernel activity");
  const kernelId = newRandomId();
  const activityNamespace = `${userId}/_Activity`;
  const activityPath = `${activityNamespace}/markdown-${kernelId}`;

  await mesh.create({
    id: `markdown-${kernelId}`,
    namespace: activityNamespace,
    path: activityPath,
    name: `Markdown view ${kernelId.slice(0, 8)}`,
    nodeType: "Activity",
    mainNode: userId,
    state: "Active",
    content: {
      $type: "ActivityLog",
      category: "MarkdownExecution",
      id: `markdown-${kernelId}`,
      hubPath: userId,
      status: "Running",
    },
  });

  // Routable gate: the activity node's own stream is served BY its per-node hub, so the first
  // emission proves the hub is live (CreateNode completing only means PERSISTED).
  await Promise.race([
    (async () => {
      for await (const node of mesh.watch(activityPath)) if (node) return;
    })(),
    new Promise((_, reject) =>
      setTimeout(() => reject(new Error("kernel activity did not become routable within 15s")), 15_000),
    ),
  ]);

  const submit = (cell: MarkdownCellSubmission) =>
    connection.observe(activityPath, "SubmitCodeRequest", {
      code: cell.code,
      id: cell.id,
      language: cell.language ?? "csharp",
    });

  // Initial submissions in document order — sequential (the kernel builds state cell by cell).
  void (async () => {
    for (const cell of cells) {
      try {
        await submit(cell);
      } catch {
        // an individual cell failure surfaces in its own result area; later cells still run
      }
    }
  })();

  return {
    kernelAddress: activityPath,
    submit: (cell) => void submit(cell).catch(() => undefined),
    // View unmount RELEASES the kernel: cancel any running script (the control-plane
    // requestedStatus=Cancelled — the hub.CancelActivity twin), then DELETE the per-view
    // activity node — the node-lifecycle statement that tears the kernel hub down
    // (SubscribeToOwnDeletion) and frees its Roslyn state NOW instead of after the 15-min
    // idle timeout. Merely cancelling left every visited doc page's kernel hub resident —
    // the accumulation that pegged the e2e pod at its 4Gi limit — and piled throwaway
    // markdown-* Activity rows up in the user's partition forever.
    dispose: () => {
      mesh.patch(activityPath, { content: { requestedStatus: "Cancelled" } });
      void deleteActivity?.(activityPath).catch(() => undefined);
    },
  };
}

function newRandomId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  return (g.crypto?.randomUUID?.() ?? `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`).replace(/-/g, "");
}

async function queryNodes(baseUrl: string, token: string, query: string, limit: number): Promise<MeshNodeRow[]> {
  try {
    const resp = await fetch(`${baseUrl}/api/mesh/query-nodes`, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
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

async function autocomplete(
  connection: MeshWebConnection,
  userId: string,
  query: string,
  contextPath?: string,
): Promise<AutocompleteRow[]> {
  if (!userId) return [];
  try {
    // One-shot AutocompleteRequest(Query, Context) — every data-enabled hub handles it
    // (DataExtensions.HandleAutocompleteRequest); the user's home hub aggregates all providers.
    const resp = await connection.observe(userId, "AutocompleteRequest", {
      query,
      context: contextPath ?? null,
    });
    const items = (resp.message["items"] ?? resp.message["Items"]) as AutocompleteRow[] | undefined;
    return Array.isArray(items) ? items : [];
  } catch {
    return [];
  }
}

/** The live-subscription target for a URL path — resolved server-side (see server/snapshot.ts):
 *  the node ADDRESS plus the {area, id} reference split off the URL remainder. */
export interface AreaTarget {
  address: string;
  area: string;
  id: string;
}

/**
 * Live AreaSource for a resolved area target. An empty `area` subscribes the node's DEFAULT
 * layout area (the server resolves it and the first frame carries the areas[""] indirection, so
 * the renderer's root key follows it). `onError` fires if the live stream faults.
 */
export function createLiveAreaSource(
  connection: MeshWebConnection,
  target: AreaTarget,
  onError: (error: unknown) => void,
): AreaSource {
  const source = new GrpcAreaSource(connection, target.address, {
    area: target.area,
    ...(target.id ? { id: target.id } : {}),
  });
  source.start().catch(onError);
  return source;
}

/** Root area key of an area subscription. A default-area subscribe roots at "" (matching the SSR
 *  preview tree); an EXPLICIT area roots at its own name. */
export function rootAreaOf(target: AreaTarget): string {
  return target.area;
}

/** Cache key for one target's live source — one stream per (address, area, id). */
export function targetKey(target: AreaTarget): string {
  return `${target.address}\u0000${target.area}\u0000${target.id}`;
}

/** Root area key of a default-area subscription (matches the SSR preview tree's root, so live
 *  takeover swaps the AreaSource under the SAME MeshAreaView rootArea). */
export const DEFAULT_ROOT_AREA = "";

// Mesh satisfies MeshOps in spirit, but not structurally to the letter (MeshNode.path is optional
// where MeshNodeState.path is required) — adapt instead of casting.
//
// `search` routes through the REST query-nodes verb, NOT Mesh.search: the gRPC "QueryRequest"
// Mesh.search posts has no server handler (its WIRE note was never confirmed), so every consumer
// (MeshSearchView, the ThreadChat agent/model selectors) would silently get empty results. The
// REST rows are full MeshNodes — a superset of the {path,name,nodeType,content} shape consumers
// read. basePath composes as namespace scoping, exactly like MeshOperations.Search.
function adaptOps(
  mesh: Mesh,
  runQuery: (query: string, limit?: number) => Promise<MeshNodeRow[]>,
  runAutocomplete: (query: string, contextPath?: string) => Promise<AutocompleteRow[]>,
  runRenderMarkdown: (markdown: string, nodePath?: string) => Promise<RenderedMarkdown>,
  runStartKernel: (cells: MarkdownCellSubmission[]) => Promise<MarkdownKernelSession>,
  runListContent: (path: string) => Promise<ContentListing>,
  runUploadContent: (path: string, file: File) => Promise<void>,
  runTranscribe: (audio: Blob, language?: string) => Promise<{ text: string; language?: string }>,
): MeshOps {
  return {
    watch: (path: string) => watchNode(mesh, path),
    startThread: (namespacePath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.startThread(namespacePath, userText, opts),
    submitMessage: (threadPath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.submitMessage(threadPath, userText, opts),
    patch: (path: string, fields: Record<string, unknown>) => mesh.patch(path, fields),
    search: (query: string, basePath?: string, limit?: number) =>
      runQuery(basePath ? `namespace:${basePath} ${query.replace(/namespace:\S*/g, "").trim()}`.trim() : query, limit),
    // Feeds the composer's @-mention dropdown (the Blazor MeshNodeAutocomplete twin).
    autocomplete: (query: string, contextPath?: string) => runAutocomplete(query, contextPath),
    // Interactive markdown: the ONE Markdig parser renders server-side; the client hydrates.
    renderMarkdown: runRenderMarkdown,
    startMarkdownKernel: runStartKernel,
    // Node read/create — NodeExport bundles a subtree of getNode reads; NodeImport re-creates them.
    getNode: (path: string) => mesh.get(path).then((n) => n.raw as Record<string, unknown>).catch(() => null),
    createNode: (node: Record<string, unknown>) => mesh.create(node).then(() => undefined),
    // Document export: post ExportDocumentRequest, watch the Activity, return the rendered bytes.
    exportDocument: (sourcePath: string, options) => exportDocument(mesh, sourcePath, options),
    // File browser: list a content-collection directory + upload (REST — content isn't a mesh node).
    listContent: runListContent,
    uploadContent: runUploadContent,
    // Speech-to-text: the composer's mic records + POSTs here (→ /api/speech/transcribe → Whisper).
    transcribe: runTranscribe,
  };
}

/** Content-collection listing (`POST /api/mesh/content/list`) — `path` = {node}/{collection}[/{dir}]. */
async function listContent(baseUrl: string, token: string, path: string): Promise<ContentListing> {
  const resp = await fetch(`${baseUrl}/api/mesh/content/list`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify({ path }),
  });
  const text = await resp.text();
  if (!resp.ok || text.startsWith("Error:")) throw new Error(text || `content list failed (${resp.status})`);
  const parsed = JSON.parse(text) as ContentListing;
  return {
    collection: String(parsed.collection ?? ""),
    path: String(parsed.path ?? ""),
    editable: !!parsed.editable,
    items: Array.isArray(parsed.items) ? parsed.items : [],
  };
}

/** Content upload (`POST /api/mesh/upload`, multipart) — `path` = {node}/{collection}/{filePath}. */
async function uploadContent(baseUrl: string, token: string, path: string, file: File): Promise<void> {
  const form = new FormData();
  form.append("path", path);
  form.append("file", file, file.name);
  const resp = await fetch(`${baseUrl}/api/mesh/upload`, {
    method: "POST",
    headers: { authorization: `Bearer ${token}` },
    body: form,
  });
  const text = await resp.text();
  if (!resp.ok || text.startsWith("Error:")) throw new Error(text || `upload failed (${resp.status})`);
}

/**
 * Run a document export the way the Blazor ExportDocumentView does: post ExportDocumentRequest to
 * the SOURCE node hub, get back an ActivityPath, watch that Activity to a terminal Status, then
 * decode the RenderedDocument (FileName, MimeType, Content:byte[]) from ActivityLog.ReturnValue.
 * Content is base64 on the wire (byte[] JSON) → Uint8Array for the browser download.
 */
async function exportDocument(
  mesh: Mesh,
  sourcePath: string,
  options: { format?: "pdf" | "docx"; title?: string; includeChildren?: boolean; coverPage?: boolean; tableOfContents?: boolean },
): Promise<{ fileName: string; mimeType: string; bytes: Uint8Array }> {
  if (!sourcePath) throw new Error("no source path to export");
  const resp = await mesh.connection.observe(sourcePath, "ExportDocumentRequest", {
    sourcePath,
    options: {
      format: options.format === "docx" ? "Docx" : "Pdf", // ExportFormat (string enum)
      title: options.title ?? null,
      includeChildren: options.includeChildren ?? false,
      coverPage: options.coverPage ?? true,
      tableOfContents: options.tableOfContents ?? true,
    },
  });
  const m = resp.message;
  const err = m["error"] ?? m["Error"];
  if (err) throw new Error(String(err));
  const activityPath = String(m["activityPath"] ?? m["ActivityPath"] ?? "");
  if (!activityPath) throw new Error("export did not start (no activity path)");

  for await (const node of mesh.watch(activityPath)) {
    const content = (node.content ?? {}) as Record<string, unknown>;
    const status = String(content["status"] ?? content["Status"] ?? "");
    if (status === "Failed") throw new Error(String(content["error"] ?? content["Error"] ?? "export failed"));
    if (status === "Succeeded" || status === "Completed") {
      const rvRaw = content["returnValue"] ?? content["ReturnValue"];
      const rv = (typeof rvRaw === "string" ? JSON.parse(rvRaw) : rvRaw) as Record<string, unknown> | null;
      if (!rv) throw new Error("export produced no document");
      return {
        fileName: String(rv["fileName"] ?? rv["FileName"] ?? "document"),
        mimeType: String(rv["mimeType"] ?? rv["MimeType"] ?? "application/octet-stream"),
        bytes: base64ToBytes(String(rv["content"] ?? rv["Content"] ?? "")),
      };
    }
  }
  throw new Error("export activity ended before completion");
}

function base64ToBytes(b64: string): Uint8Array {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

async function* watchNode(mesh: Mesh, path: string): AsyncIterable<MeshNodeState> {
  for await (const node of mesh.watch(path)) {
    yield { ...node.raw, path: node.path ?? path, name: node.name, nodeType: node.nodeType, content: node.content };
  }
}
