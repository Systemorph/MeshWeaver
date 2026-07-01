// Server-side, STATELESS snapshot access to the portal — one request in, one snapshot out.
//
// 🚨 HARD RULE (the whole point of this architecture): the server NEVER opens a gRPC/stream
// subscription. Everything here is a bounded HTTPS request/response against the portal origin —
// mint a short-lived token off the INCOMING request's cookies, fetch a snapshot, render, done.
// Live state is exclusively the CLIENT's job (see client/live.ts — the browser mints its own
// token same-origin, exactly like the Vite SPA).
//
// Snapshot surface (investigated against the source):
//   - Token mint: POST {origin}/api/tokens (cookie-authorized; ApiTokenController in
//     memex/Memex.Portal.Shared/Authentication/ApiTokenController.cs). Response
//     { rawToken, nodePath, … } — nodePath is "{userId}/ApiToken/…", which reveals the caller's
//     home partition (the SPA derives its default route the same way). The token is held in
//     request-scoped locals ONLY — never persisted, never sent to the client.
//   - Node snapshot: POST {origin}/api/mesh/get (MeshApiEndpoints.MapMeshApi — the REST
//     transport-mirror of the MCP tools, Bearer mw_… authorized). Returns the MeshNode JSON, or
//     a bare "Error: …" / "Not found: …" sentinel string (MeshOperations contract).
//   - ⚠️ There is NO REST render-area endpoint: MapMeshApi maps get/search/create/update/patch/
//     delete/move/copy/recycle/compile/diagnostics/execute-script/mirror/navigate-to/base-url/
//     upload — no render. The MCP `render_area` tool (McpMeshPlugin.RenderArea) does NOT return
//     the rendered UiControl tree either — it returns an MCP-UI iframe embed pointing at the
//     portal URL. Neither shape is hydratable, so the SSR snapshot is the documented FALLBACK:
//     an app-shell + a node-snapshot preview tree (title/type/markdown synthesized from
//     /api/mesh/get), replaced by the live gRPC-web area after hydration.

import "server-only";
import * as React from "react";
import type { AreaTree, Json, UiControl } from "@meshweaver/react/core";

// React.cache exists in Next's vendored server runtime (per-request memoization). Stable react
// (vitest) doesn't export it — fall through to the uncached function there.
const requestCache: <T extends (...args: never[]) => unknown>(fn: T) => T =
  (React as { cache?: <T>(fn: T) => T }).cache ?? ((fn) => fn);

export interface MintedToken {
  rawToken: string;
  /** "{userId}/ApiToken/…" — the caller's home partition prefix. */
  nodePath: string;
  /** The signed-in user's mesh id (home partition) — the default route. */
  userId: string;
}

/** Resolve the portal origin: PORTAL_ORIGIN env wins; otherwise same host as the incoming
 *  request (x-forwarded-proto/host aware — the ingress fronts both the portal and this app). */
export function resolvePortalOrigin(headers: Headers, env: NodeJS.ProcessEnv = process.env): string {
  const configured = env.PORTAL_ORIGIN;
  if (configured) return configured.replace(/\/+$/, "");
  const host = headers.get("x-forwarded-host") ?? headers.get("host") ?? "localhost";
  const proto =
    headers.get("x-forwarded-proto") ?? (/^(localhost|127\.0\.0\.1)(:\d+)?$/.test(host) ? "http" : "https");
  return `${proto}://${host}`;
}

/**
 * Mint a short-lived API token by forwarding the incoming request's cookies to the portal's
 * cookie-authorized mint endpoint. Returns null when the session is not authenticated (or no
 * portal lives on the origin) — SSR then degrades to the app shell and the client still attempts
 * its own takeover. Wrapped in React's per-request `cache` so multiple areas in one render share
 * one mint.
 */
export const mintToken = requestCache(async (origin: string, cookieHeader: string): Promise<MintedToken | null> => {
  if (!cookieHeader) return null;
  try {
    const resp = await fetch(`${origin}/api/tokens`, {
      method: "POST",
      cache: "no-store",
      headers: {
        "content-type": "application/json",
        cookie: cookieHeader, // the incoming request's session cookie authorizes the mint
      },
      body: JSON.stringify({ label: "portal-next-ssr", expiresInDays: 1 }),
    });
    if (!resp.ok) return null;
    const body = (await resp.json()) as { rawToken?: string; nodePath?: string };
    if (!body.rawToken) return null;
    const nodePath = body.nodePath ?? "";
    return { rawToken: body.rawToken, nodePath, userId: nodePath.split("/")[0] ?? "" };
  } catch {
    return null; // no portal on this origin / network error — SSR degrades gracefully
  }
});

/** Tolerant property pick — the hub serializer casing differs between hosts (camel vs Pascal),
 *  exactly like the grpc-web client's `m["change"] ?? m["Change"]` reads. */
function pick(obj: Json, key: string): Json {
  if (obj == null || typeof obj !== "object") return undefined;
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  return (obj as Record<string, Json>)[key] ?? (obj as Record<string, Json>)[pascal];
}

export interface NodeSnapshot {
  path: string;
  name: string;
  nodeType?: string;
  /** Best-effort markdown-ish body extracted from the node content (may be undefined). */
  markdown?: string;
}

/**
 * Fetch the MeshNode snapshot over REST (`POST /api/mesh/get`). Handles the MeshOperations
 * response contract: a JSON document on success, or a bare "Error: …" / "Not found: …" string
 * (shipped as-is with an application/json content type — NOT valid JSON, so parse defensively).
 */
export async function fetchNodeSnapshot(
  origin: string,
  token: string,
  path: string,
): Promise<NodeSnapshot | null> {
  try {
    const resp = await fetch(`${origin}/api/mesh/get`, {
      method: "POST",
      cache: "no-store",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
      body: JSON.stringify({ path }),
    });
    if (!resp.ok) return null;
    const text = await resp.text();
    if (text.startsWith("Error:") || text.startsWith("Not found:")) return null;
    let parsed: Json;
    try {
      parsed = JSON.parse(text);
    } catch {
      return null;
    }
    if (parsed == null || typeof parsed !== "object") return null;
    // A broken-NodeType read arrives wrapped: { node, compilationError }.
    const node = pick(parsed, "node") ?? parsed;
    return toSnapshot(node, path);
  } catch {
    return null;
  }
}

function toSnapshot(node: Json, fallbackPath: string): NodeSnapshot | null {
  if (node == null || typeof node !== "object") return null;
  const path = asString(pick(node, "path")) ?? fallbackPath;
  const name = asString(pick(node, "name")) ?? path.split("/").pop() ?? path;
  const nodeType = asString(pick(node, "nodeType"));
  const content = pick(node, "content");
  const markdown =
    typeof content === "string"
      ? content
      : (asString(pick(content, "markdown")) ??
        asString(pick(content, "text")) ??
        asString(pick(content, "description")));
  return { path, name, nodeType, markdown };
}

function asString(v: Json): string | undefined {
  return typeof v === "string" && v.length > 0 ? v : undefined;
}

/** Root area key of the SSR preview tree — the same key the live default-area subscription uses
 *  (the server resolves the default area into areas[""]), so live takeover swaps the source only. */
export const SSR_ROOT_AREA = "";

/**
 * Synthesize the SSR preview AreaTree from a node snapshot — rendered server-side through the
 * EXISTING @meshweaver/react registry (same {areas,data} shape the mesh streams; see
 * clients/portal/src/sample.ts for the canonical literal-tree form). This is honest "app shell+"
 * content: the node's real name/type/markdown as first paint, replaced by the live area.
 */
export function buildInitialTree(snapshot: NodeSnapshot): AreaTree {
  const ref = (area: string): UiControl => ({ $type: "NamedArea", area });
  const children = [ref("ssrTitle"), ref("ssrMeta")];
  const areas: Record<string, UiControl> = {
    [SSR_ROOT_AREA]: {
      $type: "Stack",
      skins: [{ $type: "LayoutStack", verticalGap: 12 }],
      areas: children,
    },
    ssrTitle: { $type: "Label", typo: "PageTitle", data: snapshot.name },
    ssrMeta: {
      $type: "Label",
      typo: "Subtitle",
      data: snapshot.nodeType ? `${snapshot.nodeType} · ${snapshot.path}` : snapshot.path,
    },
  };
  if (snapshot.markdown) {
    children.push(ref("ssrBody"));
    areas.ssrBody = { $type: "Markdown", data: snapshot.markdown };
  }
  return { areas };
}
