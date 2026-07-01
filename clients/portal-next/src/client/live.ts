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
import type { MeshNodeState, MeshOps, ThreadSubmitOptions } from "@meshweaver/react/core";
import { connect, Mesh, type MeshWebConnection } from "@meshweaver/client-web";

export interface LiveMesh {
  connection: MeshWebConnection;
  /** The thread/node operations surface ThreadChat consumes (Mesh.from(connection), adapted). */
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
    body: JSON.stringify({ label: "react-portal-next", expiresInDays: 1 }),
  });
  if (!resp.ok) throw new Error(`token mint failed (${resp.status} ${resp.statusText})`);
  const { rawToken, nodePath } = (await resp.json()) as { rawToken?: string; nodePath?: string };
  if (!rawToken) throw new Error("token mint returned no rawToken");

  const connection = await connect(baseUrl, { token: rawToken });
  const mesh = Mesh.from(connection);
  return {
    connection,
    ops: adaptOps(mesh),
    userId: (nodePath ?? "").split("/")[0] ?? "",
    close: () => connection.close(),
  };
}

/**
 * Live AreaSource for a mesh node path — subscribes the node's DEFAULT layout area (area "": the
 * server resolves the default and the first frame carries the areas[""] indirection, so the
 * renderer's root key is ""). `onError` fires if the live stream faults.
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

/** Root area key of a default-area subscription (matches the SSR preview tree's root, so live
 *  takeover swaps the AreaSource under the SAME MeshAreaView rootArea). */
export const DEFAULT_ROOT_AREA = "";

// Mesh satisfies MeshOps in spirit, but not structurally to the letter (MeshNode.path is optional
// where MeshNodeState.path is required) — adapt instead of casting.
function adaptOps(mesh: Mesh): MeshOps {
  return {
    watch: (path: string) => watchNode(mesh, path),
    startThread: (namespacePath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.startThread(namespacePath, userText, opts),
    submitMessage: (threadPath: string, userText: string, opts?: ThreadSubmitOptions) =>
      mesh.submitMessage(threadPath, userText, opts),
    patch: (path: string, fields: Record<string, unknown>) => mesh.patch(path, fields),
    search: (query: string, basePath?: string, limit?: number) => mesh.search(query, basePath, limit),
  };
}

async function* watchNode(mesh: Mesh, path: string): AsyncIterable<MeshNodeState> {
  for await (const node of mesh.watch(path)) {
    yield { ...node.raw, path: node.path ?? path, name: node.name, nodeType: node.nodeType, content: node.content };
  }
}
