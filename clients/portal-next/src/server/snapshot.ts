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
//   - Rendered-area snapshot (PRIMARY): POST {origin}/api/mesh/render-area
//     (MeshApiEndpoints.HandleRenderArea → MeshOperations.RenderArea). The portal subscribes the
//     node's DEFAULT layout area server-side, takes the first fully-materialised Full frame, and
//     ships it verbatim: the SAME {areas,data} EntityStore JSON the gRPC wire delivers
//     ($type discriminators, JSON-encoded InstanceCollection keys). Folding it through
//     normalizeEntityStore — the exact fold GrpcAreaSource applies to a live Full frame — yields
//     a full-fidelity AreaTree that seeds StaticAreaSource without translation, rooted at the
//     same areas[""] default-area indirection the live subscription uses.
//   - Node snapshot (FALLBACK — older portals whose /api/mesh has no render-area verb, timeouts,
//     denials): POST {origin}/api/mesh/get (MeshApiEndpoints.MapMeshApi — the REST
//     transport-mirror of the MCP tools, Bearer mw_… authorized). Returns the MeshNode JSON, or
//     a bare "Error: …" / "Not found: …" sentinel string (MeshOperations contract); the SSR then
//     synthesizes an app-shell preview tree (title/type/markdown), replaced by the live
//     gRPC-web area after hydration.

import "server-only";
import * as React from "react";
// The React-FREE wire-folding entry — importing it (unlike ./core) keeps the renderer's React
// client-context modules OUT of this file's RSC server graph (next build rejects createContext
// in server components).
import { normalizeEntityStore } from "@meshweaver/react/wire";
// React-FREE classifier (own subpath, like /wire) — safe in this RSC server module.
import { isAccessDenied } from "@meshweaver/react/accessError";
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

/** Outcome of a server-side rendered-area fetch:
 *  - `ok`     — a hydratable {areas,data} tree (seeds StaticAreaSource);
 *  - `denied` — an RLS access denial (the caller may redirect to the node's public cover / paywall);
 *  - `none`   — nothing seedable: older portal without the verb, render timeout, not-found, or an
 *               unparseable body (degrade to the node-snapshot preview). */
export type RenderedAreaResult =
  | { kind: "ok"; tree: AreaTree }
  | { kind: "denied" }
  | { kind: "none" };

/**
 * Fetch the node's rendered DEFAULT layout area over REST (`POST /api/mesh/render-area`) — the
 * PRIMARY SSR seed. The response is the first fully-materialised Full frame of the same
 * subscription the live client opens (reference {area: ""}), delivered EXACTLY as the gRPC wire
 * ships it; normalizeEntityStore folds the wire keys the same way GrpcAreaSource folds a live
 * Full frame, so the resulting tree seeds StaticAreaSource with wire fidelity.
 *
 * An RLS denial arrives as the HTTP-200 sentinel `"Error: Access denied…"`; this distinguishes it
 * (`denied`) from a plain miss (`none`) so the caller can send a not-yet-enrolled visitor to the
 * node's public cover — the same "no access ⇒ redirect here" the Blazor NamedAreaView does — instead
 * of a dead-end shell preview.
 */
export async function fetchRenderedArea(
  origin: string,
  token: string,
  path: string,
  area?: string,
  id?: string,
): Promise<RenderedAreaResult> {
  try {
    const resp = await fetch(`${origin}/api/mesh/render-area`, {
      method: "POST",
      cache: "no-store",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
      body: JSON.stringify({ path, ...(area ? { area } : {}), ...(id ? { id } : {}) }),
    });
    if (!resp.ok) return { kind: "none" }; // 404 = older portal, 504 = render timeout — degrade to preview
    const text = await resp.text();
    if (text.startsWith("Error:") || text.startsWith("Not found:")) {
      return isAccessDenied(text) ? { kind: "denied" } : { kind: "none" };
    }
    let parsed: Json;
    try {
      parsed = JSON.parse(text);
    } catch {
      return { kind: "none" };
    }
    if (parsed == null || typeof parsed !== "object" || Array.isArray(parsed)) return { kind: "none" };
    const tree = normalizeEntityStore(parsed);
    // A hydratable frame must carry rendered areas; anything else is not seedable.
    if (tree.areas == null || Object.keys(tree.areas).length === 0) return { kind: "none" };
    return { kind: "ok", tree };
  } catch {
    return { kind: "none" };
  }
}

/** The live-subscription target for a URL path: the resolved node ADDRESS plus the layout-area
 *  reference split off the unmatched remainder — the same {address}/{area}/{id} split the Blazor
 *  ApplicationPage applies. area "" subscribes the node's DEFAULT area. */
export interface AreaTarget {
  address: string;
  area: string;
  id: string;
  /** The node's configured "if no access ⇒ redirect here" target (public cover / paywall), or null.
   *  Populated from the resolve response's `redirectOnDenied` (the REST twin of
   *  hub.GetRedirectOnDenied); the shell redirects a denied viewer there instead of a dead-end error,
   *  gated by isSafeRedirect. */
  redirectOnDenied?: string | null;
}

/** Split a resolution remainder into (area, id) — first segment is the area, the rest the id
 *  (the exact ParseSidePanelRemainder / SplitAreaRemainder split the Blazor portal uses). */
export function splitRemainder(remainder: string | null | undefined): { area: string; id: string } {
  const r = (remainder ?? "").replace(/^\/+|\/+$/g, "");
  if (!r) return { area: "", id: "" };
  const slash = r.indexOf("/");
  return slash < 0 ? { area: r, id: "" } : { area: r.slice(0, slash), id: r.slice(slash + 1) };
}

/**
 * Resolve a URL path into its live-subscription target over REST (`POST /api/mesh/resolve` —
 * MeshOperations.Resolve, the transport twin of the Blazor GUI's ResolveNavigationPath). The
 * client's GrpcAreaSource must target the actual NODE hub (with the remainder as the area
 * reference), never the raw URL path — subscribing a non-node address is the NotFound-storm
 * hazard the Blazor portal avoids by resolving first. Returns the whole-path fallback when the
 * verb is missing (older portal) or resolution fails, matching the pre-resolution behavior.
 */
export async function fetchAreaTarget(origin: string, token: string, path: string): Promise<AreaTarget> {
  const fallback: AreaTarget = { address: path, area: "", id: "", redirectOnDenied: null };
  if (!path) return fallback;
  try {
    const resp = await fetch(`${origin}/api/mesh/resolve`, {
      method: "POST",
      cache: "no-store",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
      body: JSON.stringify({ path }),
    });
    if (!resp.ok) return fallback;
    const text = await resp.text();
    if (text.startsWith("Error:") || text.startsWith("Not found:")) return fallback;
    let parsed: Json;
    try {
      parsed = JSON.parse(text);
    } catch {
      return fallback;
    }
    const prefix = asString(pick(parsed, "prefix"));
    if (!prefix) return fallback;
    const { area, id } = splitRemainder(asString(pick(parsed, "remainder")));
    // The node's configured paywall/cover redirect (nearest-ancestor policy), or null when none.
    const redirectOnDenied = asString(pick(parsed, "redirectOnDenied")) ?? null;
    return { address: prefix, area, id, redirectOnDenied };
  } catch {
    return fallback;
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
