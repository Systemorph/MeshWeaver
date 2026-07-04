// Live mesh connection for the portal example — same-origin gRPC-web (the Connect+Deliver split).
//
// Connection route (pinned against the server source): `MapMeshWeaverGrpc()` maps the
// `meshweaver.v1.Mesh` service at the ORIGIN ROOT (POST {origin}/meshweaver.v1.Mesh/Connect and
// /Deliver, grpc-web enabled — GrpcHostingExtensions). The SPA is served under /app/, but that is
// only where its static assets live — the transport base URL is `location.origin`.
//
// Auth (GrpcConnectionRegistry.Authenticate): the gRPC connection is identified by a Bearer
// `mw_…` API token in the call metadata — the browser's session COOKIE is NOT read on this path
// (no token ⇒ Anonymous ⇒ the user's content is RLS-denied). The SPA is served to an
// already-authenticated browser session, so it bootstraps a short-lived token from the portal's
// cookie-authorized mint endpoint (POST /api/tokens — the same endpoint the E2E fixture's
// MintTokenAsync uses) via a same-origin fetch. The token lives in memory only — never persisted.
//
// The mint response's nodePath (`{userId}/ApiToken/…`) reveals the caller's mesh partition — the
// ApiTokenController routes the token node into exactly the user's home partition — which gives
// the SPA its default route (the user's home, mirroring what the Blazor portal shows at /{user})
// without a separate who-am-I endpoint.

import { GrpcAreaSource, type AreaSource } from "@meshweaver/react/core";
import type {
  MarkdownCellSubmission,
  MarkdownKernelSession,
  MeshNodeState,
  MeshOps,
  RenderedMarkdown,
  ThreadSubmitOptions,
} from "@meshweaver/react/core";
import { connect, Mesh, type MeshWebConnection } from "@meshweaver/client-web";

/** A queried mesh node row — the hub-serialized MeshNode shape (camelCase, $type-tagged content). */
type MeshNodeRow = Record<string, unknown>;

/** One @-mention autocomplete suggestion (the wire AutocompleteItem, camelCase). */
interface AutocompleteRow {
  label?: string;
  insertText?: string;
  description?: string;
  icon?: string;
  path?: string;
}

export interface LiveMesh {
  connection: MeshWebConnection;
  /** The thread/node operations surface ThreadChat + interactive markdown consume. */
  ops: MeshOps;
  /** The signed-in user's mesh id (their home partition) — the default route. */
  userId: string;
  close(): void;
}

/** Mint a short-lived API token off the browser session, then join the mesh over gRPC-web. */
export async function connectLive(baseUrl: string = window.location.origin): Promise<LiveMesh> {
  const resp = await fetch(`${baseUrl}/api/tokens`, {
    method: "POST",
    credentials: "same-origin", // the session cookie authorizes the mint
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ label: "react-portal", expiresInDays: 1 }),
  });
  if (!resp.ok) throw new Error(`token mint failed (${resp.status} ${resp.statusText})`);
  const { rawToken, nodePath } = (await resp.json()) as { rawToken?: string; nodePath?: string };
  if (!rawToken) throw new Error("token mint returned no rawToken");

  const connection = await connect(baseUrl, { token: rawToken });
  const mesh = Mesh.from(connection);
  const userId = (nodePath ?? "").split("/")[0] ?? "";

  // REST-backed ops (the browser twin of the Blazor shell's IMeshService reads). `search` routes
  // through /api/mesh/query-nodes — NOT Mesh.search (the gRPC QueryRequest has no server handler,
  // so it would silently return empty). renderMarkdown / startMarkdownKernel light up interactive
  // doc pages in the SPA (previously they showed the "execution unavailable" notice).
  const runQuery = (query: string, limit = 50) => queryNodes(baseUrl, rawToken, query, limit);
  const runAutocomplete = (query: string, contextPath?: string) => autocomplete(connection, userId, query, contextPath);
  const runRenderMarkdown = (markdown: string, nodePath2?: string) => renderMarkdown(baseUrl, rawToken, markdown, nodePath2);
  const runDeleteNode = (path: string) => deleteNode(baseUrl, rawToken, path);
  const runStartKernel = (cells: MarkdownCellSubmission[]) => startMarkdownKernel(connection, mesh, userId, cells, runDeleteNode);

  return {
    connection,
    ops: adaptOps(mesh, runQuery, runAutocomplete, runRenderMarkdown, runStartKernel),
    userId,
    close: () => connection.close(),
  };
}

/**
 * Live AreaSource for a mesh node path — subscribes the node's DEFAULT layout area (area "": the
 * server resolves the default and the first frame carries the areas[""] indirection, so the
 * renderer's root key is ""). This mirrors the Blazor AreaPage, which resolves /{path} to the node
 * address + a null-area LayoutAreaReference. `onError` fires if the live stream faults.
 */
export function createLiveAreaSource(
  connection: MeshWebConnection,
  path: string,
  onError: (error: unknown) => void,
): AreaSource {
  const source = new GrpcAreaSource(connection, path, { area: "" });
  source.start().catch(onError);
  return source;
}

/** Root area key of a default-area subscription (the server's NamedArea indirection lives there). */
export const DEFAULT_ROOT_AREA = "";

// Mesh satisfies MeshOps in spirit, but not structurally to the letter (MeshNode.path is optional
// where MeshNodeState.path is required) — adapt instead of casting.
function adaptOps(
  mesh: Mesh,
  runQuery: (query: string, limit?: number) => Promise<MeshNodeRow[]>,
  runAutocomplete: (query: string, contextPath?: string) => Promise<AutocompleteRow[]>,
  runRenderMarkdown: (markdown: string, nodePath?: string) => Promise<RenderedMarkdown>,
  runStartKernel: (cells: MarkdownCellSubmission[]) => Promise<MarkdownKernelSession>,
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
    autocomplete: (query: string, contextPath?: string) => runAutocomplete(query, contextPath),
    renderMarkdown: runRenderMarkdown,
    startMarkdownKernel: runStartKernel,
  };
}

async function* watchNode(mesh: Mesh, path: string): AsyncIterable<MeshNodeState> {
  for await (const node of mesh.watch(path)) {
    yield { ...node.raw, path: node.path ?? path, name: node.name, nodeType: node.nodeType, content: node.content };
  }
}

// ---- REST-backed mesh ops (parity with clients/portal-next/src/client/live.ts) -------------------

/** Full-node mesh query (`POST /api/mesh/query-nodes`) — the browser twin of IMeshService.Query. */
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

/** One-shot @-mention autocomplete — every data-enabled hub handles AutocompleteRequest; the
 *  user's home hub aggregates all providers. Empty on any failure (the composer just shows none). */
async function autocomplete(
  connection: MeshWebConnection,
  userId: string,
  query: string,
  contextPath?: string,
): Promise<AutocompleteRow[]> {
  if (!userId) return [];
  try {
    const resp = await connection.observe(userId, "AutocompleteRequest", { query, context: contextPath ?? null });
    const items = (resp.message["items"] ?? resp.message["Items"]) as AutocompleteRow[] | undefined;
    return Array.isArray(items) ? items : [];
  } catch {
    return [];
  }
}

/** Node delete (`POST /api/mesh/delete`) — REST (the gRPC path targets the node's own hub, but the
 *  kernel-activity teardown is fire-and-forget from a view unmount where REST is simplest). The
 *  Paths field is a JSON-encoded ARRAY (the verb's wire shape). */
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

/**
 * The per-view interactive-markdown kernel — the client twin of
 * MarkdownViewLogic.CreateActivityAndSubmit: create the kernel Activity under the VIEWER's
 * partition (the only place they can Create), wait until its per-node hub is ROUTABLE (the node's
 * own stream first emission proves it — posting before that NotFound-storms the router), then post
 * the cell submissions IN ORDER. On unmount, dispose cancels the running script AND deletes the
 * activity node so the kernel hub tears down NOW instead of at the 15-min idle timeout.
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
