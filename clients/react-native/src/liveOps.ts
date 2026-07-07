// The React-Native MeshOps — built at the APP level exactly like clients/portal-next/src/client/live.ts
// (adaptOps), over the SAME shared building blocks: @meshweaver/client-web's `Mesh` (watch/patch/create/
// thread) + the interactive-markdown contract from @meshweaver/react/core. The only RN-specific bits are
// (a) it talks to the LocalMesh sidecar ANONYMOUSLY (no token mint — the sidecar is same-origin, no auth),
// and (b) render-markdown goes over a plain fetch POST (non-streaming, so the RN global fetch is fine —
// only the gRPC-web server-stream needs nativeStreamingFetch). renderMarkdown reuses the ONE server Markdig
// parser; the Markdown leaf hydrates its HTML with the shared splitRenderedHtml — no bespoke parser.
import { Mesh, type MeshWebConnection } from "@meshweaver/client-web";
import type {
  MarkdownCellSubmission,
  MarkdownKernelSession,
  MeshNodeState,
  MeshOps,
  RenderedMarkdown,
  ThreadSubmitOptions,
} from "@meshweaver/react/core";

/** Server-side Markdig render (POST {baseUrl}/api/mesh/render-markdown) — the ONE markdown parser. */
async function renderMarkdown(baseUrl: string, markdown: string, nodePath?: string): Promise<RenderedMarkdown> {
  const resp = await fetch(`${baseUrl.replace(/\/+$/, "")}/api/mesh/render-markdown`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ markdown, nodePath: nodePath ?? null }),
  });
  if (!resp.ok) throw new Error(`render-markdown failed (${resp.status})`);
  const text = await resp.text();
  if (text.startsWith("Error:")) throw new Error(text);
  const parsed = JSON.parse(text) as { html?: string; codeSubmissions?: MarkdownCellSubmission[] };
  return { html: parsed.html ?? "", codeSubmissions: parsed.codeSubmissions ?? [] };
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
export function buildMeshOps(connection: MeshWebConnection, baseUrl: string, partition: string): MeshOps {
  const mesh = Mesh.from(connection);
  return {
    watch: (path: string) => watchNode(mesh, path),
    startThread: (namespacePath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.startThread(namespacePath, userText, opts),
    submitMessage: (threadPath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.submitMessage(threadPath, userText, opts),
    patch: (path: string, fields: Record<string, unknown>) => mesh.patch(path, fields),
    search: (query: string, basePath?: string, limit?: number) =>
      mesh.search(query, basePath, limit).catch(() => [] as Record<string, unknown>[]),
    renderMarkdown: (markdown: string, nodePath?: string) => renderMarkdown(baseUrl, markdown, nodePath),
    startMarkdownKernel: (cells: MarkdownCellSubmission[]) => startMarkdownKernel(connection, mesh, partition, cells),
    getNode: (path: string) => mesh.get(path).then((n) => n.raw as Record<string, unknown>).catch(() => null),
    createNode: (node: Record<string, unknown>) => mesh.create(node).then(() => undefined),
  };
}
