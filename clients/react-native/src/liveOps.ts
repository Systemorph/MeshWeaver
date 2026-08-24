// The React-Native MeshOps — built at the APP level exactly like clients/portal-next/src/client/live.ts
// (adaptOps), over the SAME shared building blocks: @meshweaver/client-web's `Mesh` (watch/patch/create/
// thread) + the interactive-markdown contract from @meshweaver/react/core. The only RN-specific bits are
// (a) it talks to the LocalMesh sidecar ANONYMOUSLY (no token mint — the sidecar is same-origin, no auth),
// and (b) render-markdown goes over a plain fetch POST (non-streaming, so the RN global fetch is fine —
// only the gRPC-web server-stream needs nativeStreamingFetch). renderMarkdown reuses the ONE server Markdig
// parser; the Markdown leaf hydrates its HTML with the shared splitRenderedHtml — no bespoke parser.
import { Mesh, MeshRest, type MeshWebConnection } from "@meshweaver/client-web";
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
  // The four shared /api/mesh verbs come from ONE implementation (#1497). This shell is the reason
  // that matters: its copies carried NO Authorization header at all, so every REST op 401'd against
  // a real portal and the file browser looked merely EMPTY (#1474). MeshRest applies Bearer in one
  // place — and omits it entirely for the anonymous same-origin sidecar, which is the behaviour this
  // shell had right and the web shells did not.
  const rest = new MeshRest({ baseUrl, token });
  return {
    // The viewer's partition doubles as their mesh id — viewer-scoped reads (the home Apps
    // grid's most-recently-used ordering over {viewer}/_UserActivity) resolve it from here.
    userId: partition,
    watch: (path: string) => watchNode(mesh, path),
    startThread: (namespacePath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.startThread(namespacePath, userText, opts),
    submitMessage: (threadPath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.submitMessage(threadPath, userText, opts),
    patch: (path: string, fields: Record<string, unknown>) => mesh.patch(path, fields),
    search: (query: string, basePath?: string, limit?: number) =>
      mesh.search(query, basePath, limit).catch(() => [] as Record<string, unknown>[]),
    renderMarkdown: (markdown: string, nodePath?: string) => rest.renderMarkdown(markdown, nodePath),
    startMarkdownKernel: (cells: MarkdownCellSubmission[]) => startMarkdownKernel(connection, mesh, partition, cells),
    getNode: (path: string) => mesh.get(path).then((n) => n.raw as Record<string, unknown>).catch(() => null),
    createNode: (node: Record<string, unknown>) => mesh.create(node).then(() => undefined),
    // The file-browser and document-export ops. These are plain REST / mesh-request calls with no
    // browser-only dependency, so RN wires the SAME three the web shells do — without them the
    // FileBrowser and ExportDocument leaves could only ever render their "not available" state.
    listContent: (path: string) => rest.listContent(path),
    uploadContent: (path: string, file: File) => rest.uploadContent(path, file),
    exportDocument: (sourcePath: string, options: DocumentExportOptions) => exportDocument(mesh, sourcePath, options),
  };
}
