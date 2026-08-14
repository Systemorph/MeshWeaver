// The React-Native MeshOps — built at the APP level exactly like clients/portal-next/src/client/live.ts
// (adaptOps), over the SAME shared building blocks: @meshweaver/client-web's `Mesh` (watch/patch/create/
// thread) + the interactive-markdown contract from @meshweaver/react/core. The only RN-specific bits are
// (a) it talks to the LocalMesh sidecar ANONYMOUSLY (no token mint — the sidecar is same-origin, no auth),
// and (b) render-markdown goes over a plain fetch POST (non-streaming, so the RN global fetch is fine —
// only the gRPC-web server-stream needs nativeStreamingFetch). renderMarkdown reuses the ONE server Markdig
// parser; the Markdown leaf hydrates its HTML with the shared splitRenderedHtml — no bespoke parser.
import { Mesh, type MeshWebConnection } from "@meshweaver/client-web";
import type {
  ContentListing,
  DocumentDownload,
  DocumentExportOptions,
  MarkdownCellSubmission,
  MarkdownKernelSession,
  MeshNodeState,
  MeshOps,
  RenderedMarkdown,
  ThreadSubmitOptions,
} from "@meshweaver/react/core";

/** JSON headers + Bearer when a token is present — `/api/mesh/*` on a remote portal is
 * Bearer-only (issue #1474: RN sent no token, so every REST op 401'd against real portals;
 * the anonymous sidecar keeps working because an empty token adds no header). */
function jsonHeaders(token: string): Record<string, string> {
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (token) headers["authorization"] = `Bearer ${token}`;
  return headers;
}

/** Server-side Markdig render (POST {baseUrl}/api/mesh/render-markdown) — the ONE markdown parser. */
async function renderMarkdown(baseUrl: string, token: string, markdown: string, nodePath?: string): Promise<RenderedMarkdown> {
  const resp = await fetch(`${baseUrl.replace(/\/+$/, "")}/api/mesh/render-markdown`, {
    method: "POST",
    headers: jsonHeaders(token),
    body: JSON.stringify({ markdown, nodePath: nodePath ?? null }),
  });
  if (!resp.ok) throw new Error(`render-markdown failed (${resp.status})`);
  const text = await resp.text();
  if (text.startsWith("Error:")) throw new Error(text);
  const parsed = JSON.parse(text) as { html?: string; codeSubmissions?: MarkdownCellSubmission[] };
  return { html: parsed.html ?? "", codeSubmissions: parsed.codeSubmissions ?? [] };
}

/**
 * Content-collection listing (`POST {baseUrl}/api/mesh/content/list`) — the read half of the file
 * browser. `path` is `{node}/{collection}[/{dir}]`. Same endpoint and same response shaping as
 * portal-next's listContent; the sidecar is same-origin and anonymous (empty token), while a
 * remote portal needs the instance's Bearer token.
 */
async function listContent(baseUrl: string, token: string, path: string): Promise<ContentListing> {
  const resp = await fetch(`${baseUrl.replace(/\/+$/, "")}/api/mesh/content/list`, {
    method: "POST",
    headers: jsonHeaders(token),
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

/** Content upload (`POST {baseUrl}/api/mesh/upload`, multipart). */
async function uploadContent(baseUrl: string, token: string, path: string, file: File): Promise<void> {
  const form = new FormData();
  form.append("path", path);
  form.append("file", file as unknown as Blob, (file as { name?: string }).name ?? "upload");
  const headers: Record<string, string> = {};
  if (token) headers["authorization"] = `Bearer ${token}`; // multipart: FormData sets content-type
  const resp = await fetch(`${baseUrl.replace(/\/+$/, "")}/api/mesh/upload`, { method: "POST", headers, body: form });
  const text = await resp.text();
  if (!resp.ok || text.startsWith("Error:")) throw new Error(text || `upload failed (${resp.status})`);
}

/** Decode a byte[] wire value (base64 string, or a number array) to bytes. */
function decodeBytes(raw: unknown): Uint8Array {
  if (typeof raw === "string") {
    const bin = globalThis.atob ? globalThis.atob(raw) : "";
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  return Array.isArray(raw) ? Uint8Array.from(raw as number[]) : new Uint8Array();
}

/**
 * Run a document export the way the Blazor ExportDocumentView does: post ExportDocumentRequest to
 * the SOURCE node hub, get back an ActivityPath, watch that Activity to a terminal Status, then
 * decode the RenderedDocument (FileName, MimeType, Content:byte[]) from ActivityLog.ReturnValue.
 */
async function exportDocument(mesh: Mesh, sourcePath: string, options: DocumentExportOptions): Promise<DocumentDownload> {
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
  const m = resp.message as Record<string, unknown>;
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
        bytes: decodeBytes(rv["content"] ?? rv["Content"]),
      };
    }
  }
  throw new Error("export activity ended without a document");
}

function newId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  return (g.crypto?.randomUUID?.() ?? `${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`).replace(/-/g, "");
}

/**
 * The per-view interactive-markdown kernel — the RN twin of portal-next's startMarkdownKernel /
 * MarkdownViewLogic.CreateActivityAndSubmit: create the kernel Activity under `partition` (the only
 * place the viewer can Create), wait until its per-node hub is ROUTABLE (first stream emission proves
 * it — posting before that NotFound-storms the router), then post the cell submissions IN ORDER.
 */
async function startMarkdownKernel(
  connection: MeshWebConnection,
  mesh: Mesh,
  partition: string,
  cells: MarkdownCellSubmission[],
): Promise<MarkdownKernelSession> {
  if (!partition) throw new Error("no partition to anchor the kernel activity");
  const kernelId = newId();
  const ns = `${partition}/_Activity`;
  const activityPath = `${ns}/markdown-${kernelId}`;

  await mesh.create({
    id: `markdown-${kernelId}`,
    namespace: ns,
    path: activityPath,
    name: `Markdown view ${kernelId.slice(0, 8)}`,
    nodeType: "Activity",
    mainNode: partition,
    state: "Active",
    content: {
      $type: "ActivityLog",
      category: "MarkdownExecution",
      id: `markdown-${kernelId}`,
      hubPath: partition,
      status: "Running",
    },
  });

  // Routable gate: the activity node's own stream is served BY its per-node hub, so the first emission
  // proves the hub is live (CreateNode completing only means PERSISTED).
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
      void mesh.delete(activityPath).catch(() => undefined);
    },
  };
}

async function* watchNode(mesh: Mesh, path: string): AsyncIterable<MeshNodeState> {
  for await (const node of mesh.watch(path)) {
    yield { ...node.raw, path: node.path ?? path, name: node.name, nodeType: node.nodeType, content: node.content };
  }
}

/**
 * Build the RN MeshOps from a live connection. `partition` anchors the interactive-markdown kernel
 * activity (the viewer's home partition — CHAT.namespacePath on the anonymous sidecar).
 */
export function buildMeshOps(connection: MeshWebConnection, baseUrl: string, partition: string, token = ""): MeshOps {
  // The REST reach-back (search + the helpers below) carries the instance's Bearer token; empty
  // = the anonymous sidecar. Without it every REST op 401'd against a remote portal (#1474).
  const mesh = Mesh.from(connection, "mesh/main", { url: baseUrl, token: token || undefined });
  return {
    watch: (path: string) => watchNode(mesh, path),
    startThread: (namespacePath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.startThread(namespacePath, userText, opts),
    submitMessage: (threadPath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.submitMessage(threadPath, userText, opts),
    patch: (path: string, fields: Record<string, unknown>) => mesh.patch(path, fields),
    search: (query: string, basePath?: string, limit?: number) =>
      mesh.search(query, basePath, limit).catch(() => [] as Record<string, unknown>[]),
    renderMarkdown: (markdown: string, nodePath?: string) => renderMarkdown(baseUrl, token, markdown, nodePath),
    startMarkdownKernel: (cells: MarkdownCellSubmission[]) => startMarkdownKernel(connection, mesh, partition, cells),
    getNode: (path: string) => mesh.get(path).then((n) => n.raw as Record<string, unknown>).catch(() => null),
    createNode: (node: Record<string, unknown>) => mesh.create(node).then(() => undefined),
    // The file-browser and document-export ops. These are plain REST / mesh-request calls with no
    // browser-only dependency, so RN wires the SAME three the web shells do — without them the
    // FileBrowser and ExportDocument leaves could only ever render their "not available" state.
    listContent: (path: string) => listContent(baseUrl, token, path),
    uploadContent: (path: string, file: File) => uploadContent(baseUrl, token, path, file),
    exportDocument: (sourcePath: string, options: DocumentExportOptions) => exportDocument(mesh, sourcePath, options),
  };
}
