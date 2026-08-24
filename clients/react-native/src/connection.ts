// Mesh instances — the RN/web twin of the MAUI app's InstanceStore, with the SAME storage backend:
// THE LOCAL MESH. Instances are MemexInstance nodes (Instance/{id}) in the mesh this app dials by
// default — the monolith/SQLite sidecar (Memex.LocalMesh) on native/desktop, the serving portal on
// web. There is NO device storage: the app connects to the local mesh as its MAIN mesh, hydrates the
// switcher's list from it (attachInstanceStore, on the Local connect), and every edit (add / token /
// remove) writes the node back. The in-module list is only a render cache of those nodes; it resets
// to Local-only on launch, exactly until the local mesh answers.

export interface MeshInstance {
  /** Display name. */
  name: string;
  /** Base URL the gRPC-web client dials (same-origin for Local). */
  url: string;
  /** Bearer token (mw_…) for a remote portal; empty ⇒ anonymous. */
  token: string;
  /** True for the mesh that served this app (same origin). */
  local: boolean;
  /** Optional explicit environment glyph (overrides the URL-derived one). */
  icon?: string;
  /** Optional explicit environment accent color (hex). */
  color?: string;
  /** Optional explicit environment kind label (e.g. "Prod", "Local · k8s"). */
  kind?: string;
  /** OAuth refresh token (browser sign-in); absent for pasted API tokens. */
  refreshToken?: string;
  /** OAuth client id this app registered at the portal (dynamic client registration). */
  clientId?: string;
  /** Epoch ms when the OAuth access token expires; pasted API tokens have none. */
  tokenExpiresAt?: number;
}

/**
 * The VISUAL identity of an environment — an icon, an accent color and a "tone" that drives the
 * typesetting — so you can tell at a glance WHICH mesh you're pointed at (a packaged SQLite sidecar,
 * the local k8s cluster, prod, or a client portal all look different). Explicit fields on the
 * instance win; otherwise it's derived from the URL.
 */
export interface InstanceIdentity {
  icon: string;
  color: string;
  kind: string;
  /** Typesetting class: prod/client are UPPERCASE + heavy (loud, "you're on a real one"); local is calm. */
  tone: "local" | "k8s" | "prod" | "client" | "remote";
}

export function instanceIdentity(inst: MeshInstance): InstanceIdentity {
  const u = (inst.url || "").toLowerCase();
  // Explicit override (a seeded/known instance carries its own icon+color+kind).
  const base: Partial<InstanceIdentity> =
    inst.local || u.includes("localhost:5250") || u === ""
      ? { icon: "🖥️", color: "#2ea043", kind: "Local · SQLite", tone: "local" }
      : u.includes("memex.localhost")
        ? { icon: "☸", color: "#d29922", kind: "Local · k8s", tone: "k8s" }
        // Keep this token OUT of the words that appear in ordinary portal hostnames — it is
        // tested BEFORE meshweaver.cloud, so a generic substring like "prod" would swallow
        // every production host and label it "Client".
        : u.includes("client")
          ? { icon: "🏢", color: "#a371f7", kind: "Client", tone: "client" }
          : u.includes("meshweaver.cloud")
            ? { icon: "☁️", color: "#4c8dff", kind: "Prod", tone: "prod" }
            : { icon: "🌐", color: "#8b949e", kind: "Remote", tone: "remote" };
  return {
    icon: inst.icon || base.icon || "🌐",
    color: inst.color || base.color || "#8b949e",
    kind: inst.kind || base.kind || "Remote",
    tone: base.tone || "remote",
  };
}

import Constants from "expo-constants";

// Live connect status — a tiny observable the Connect screen renders, because a Release
// build swallows console and a failed connect must be VISIBLE truth on the device, not a
// silently reopened onboarding.
type StatusListener = () => void;
const statusListeners = new Set<StatusListener>();
let lastConnectStatus = "";
export function setConnectStatus(status: string): void {
  lastConnectStatus = status;
  for (const l of statusListeners) l();
}
export function getConnectStatus(): string { return lastConnectStatus; }
export function onConnectStatus(listener: StatusListener): () => void {
  statusListeners.add(listener);
  return () => statusListeners.delete(listener);
}

// The instance list changed (hydrated from the mesh, or edited) — the switcher and the Connect
// screen subscribe so an async hydration reaches an already-rendered list.
const instanceListeners = new Set<StatusListener>();
export function onInstancesChanged(listener: StatusListener): () => void {
  instanceListeners.add(listener);
  return () => instanceListeners.delete(listener);
}
function emitInstancesChanged(): void {
  for (const l of instanceListeners) l();
}

// ── the store: MemexInstance nodes on the local mesh ─────────────────────────────

/** The node-op surface the store needs — structurally a @meshweaver/client-web Mesh. */
export interface InstanceStoreMesh {
  search(query: string, basePath?: string, limit?: number): Promise<Record<string, unknown>[]>;
  get(path: string): Promise<unknown>;
  create(node: Record<string, unknown>): Promise<Record<string, unknown>>;
  patch(path: string, fields: Record<string, unknown>): void;
  delete(path: string): Promise<void>;
}

const INSTANCE_NODE_TYPE = "MemexInstance";
const INSTANCE_SEGMENT = "Instance";

let storeMesh: InstanceStoreMesh | null = null;
let remotes: MeshInstance[] = [];
let localDisplayName: string | null = null; // from the own-instance node (named after the device)
let currentName = "";                       // "" = the Local default

/**
 * Attach the LOCAL mesh connection as the instance store and hydrate the list from its
 * MemexInstance nodes. App.tsx calls this on a Local connect (pass null when dialing a remote —
 * the cache keeps serving; edits then only live for the session).
 */
export function attachInstanceStore(mesh: InstanceStoreMesh | null): Promise<void> {
  storeMesh = mesh;
  return mesh ? hydrate(mesh) : Promise.resolve();
}

async function hydrate(mesh: InstanceStoreMesh): Promise<void> {
  try {
    const rows = await mesh.search(`nodeType:${INSTANCE_NODE_TYPE}`, undefined, 50);
    const loaded: MeshInstance[] = [];
    let ownName: string | null = null;
    for (const r of rows) {
      const path = String((r as any).path ?? "");
      if (!path) continue;
      const node: any = await mesh.get(path).catch(() => null);
      if (!node) continue;
      const c: any = node.content ?? {};
      const name = String(node.name ?? c.displayName ?? path);
      if (!c.url) { ownName = name; continue; } // the own-instance node: it names Local
      loaded.push({
        name,
        url: String(c.url).replace(/\/+$/, ""),
        token: String(c.token ?? ""),
        local: false,
        refreshToken: c.refreshToken ? String(c.refreshToken) : undefined,
        clientId: c.clientId ? String(c.clientId) : undefined,
        tokenExpiresAt: c.tokenExpiresAt ? Date.parse(String(c.tokenExpiresAt)) : undefined,
      });
    }
    remotes = loaded;
    localDisplayName = ownName;
    emitInstancesChanged();
  } catch {
    // Store unreachable — the session keeps its in-memory list; next Local connect re-hydrates.
  }
}

/** The Instance/{id} node id for a portal URL — its HOST, port-free: the exact meshId MAUI's
 *  MeshConnector derives (`new Uri(url).Host`), so both clients patch ONE node per portal. */
function instanceIdFor(url: string): string {
  return url.replace(/^https?:\/\//i, "").replace(/\/.*$/, "").replace(/:.*$/, "").toLowerCase();
}

/** Persist one instance as its MemexInstance node — create-or-patch, fire-and-forget (the in-memory
 *  list is already updated; a failed write surfaces on the next hydration, not as a blocked tap). */
function persist(inst: MeshInstance): void {
  const mesh = storeMesh;
  if (!mesh) return;
  const id = instanceIdFor(inst.url);
  const path = `${INSTANCE_SEGMENT}/${id}`;
  const content = {
    $type: "MemexInstanceContent",
    displayName: inst.name,
    url: inst.url,
    token: inst.token || null,
    meshId: id,
    refreshToken: inst.refreshToken ?? null,
    clientId: inst.clientId ?? null,
    tokenExpiresAt: inst.tokenExpiresAt ? new Date(inst.tokenExpiresAt).toISOString() : null,
  };
  void (async () => {
    try {
      const existing = await mesh.search(`path:${path}`, undefined, 1);
      if (existing.length) mesh.patch(path, { name: inst.name, content });
      else
        await mesh.create({
          id,
          namespace: INSTANCE_SEGMENT,
          path,
          name: inst.name,
          nodeType: INSTANCE_NODE_TYPE,
          content,
        });
    } catch {
      /* best-effort — see above */
    }
  })();
}

// The mesh a NATIVE build dials by default (it has no serving origin like the web build). Configured in
// app.json → expo.extra.portalUrl; defaults to the LOCAL monolith mesh — Memex.LocalMesh, the in-process
// SQLite-backed mesh sidecar (the MAUI-parity local-first host), reachable from the iOS simulator at
// http://localhost:5250 anonymously (no token). Point it at a remote portal via Connect-to-mesh + a token.
const DEFAULT_PORTAL_URL = String((Constants.expoConfig?.extra as any)?.portalUrl ?? "http://localhost:5250");

/** The default portal URL a native build dials (app.json → expo.extra.portalUrl); prefill for the connect form. */
export function defaultPortalUrl(): string {
  return DEFAULT_PORTAL_URL;
}

/**
 * Resolve a mesh-relative asset URL (a node Icon like "/static/NodeTypeIcons/book.svg", an inline
 * doc image) against the CURRENT instance, so a native build — which has no serving origin — can
 * actually fetch it. Absolute and data:/blob: URLs pass through; on web the same-origin base is
 * the serving portal, so the result is identical to the browser's own resolution.
 */
export function resolveAssetUrl(src: string): string {
  if (!src || /^(https?:|data:|blob:)/i.test(src)) return src;
  const base = (currentInstance().url || "").replace(/\/+$/, "");
  return base ? `${base}/${src.replace(/^\/+/, "")}` : src;
}

const sameOrigin = (): string =>
  typeof window !== "undefined" && window.location ? window.location.origin : "";

/**
 * The always-present default instance — the MAIN mesh. Web (served by the mesh) → same-origin,
 * anonymous. Native has no serving origin, so it dials the configured default portal. Its display
 * name comes from the mesh itself (the own-instance node, named after the device).
 */
export function localInstance(): MeshInstance {
  const origin = sameOrigin();
  return origin
    ? { name: localDisplayName ?? "Local", url: origin, token: "", local: true }
    : { name: localDisplayName ?? "Local mesh", url: DEFAULT_PORTAL_URL, token: "", local: true };
}

/**
 * Discover the fleet from a connected mesh: instances are MESH NODES (nodeType
 * `Hosting/Deployment`, populated in the mesh's Deployments space), so the connect list is
 * data on the mesh — this app is public and carries no environment inventory of its own.
 * Best-effort by design: no Hosting plugin, no permission, or offline all yield [].
 */
export async function discoverInstances(from: MeshInstance): Promise<MeshInstance[]> {
  try {
    const base = from.url.replace(/\/+$/, "");
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (from.token) headers.Authorization = `Bearer ${from.token}`;
    const search = await fetch(`${base}/api/mesh/search`, {
      method: "POST", headers,
      body: JSON.stringify({ query: "nodeType:Hosting/Deployment scope:subtree" }),
    });
    if (!search.ok) return [];
    const results: Array<{ path?: string; name?: string }> = ((await search.json()) as any)?.results ?? [];
    const found: MeshInstance[] = [];
    for (const r of results.slice(0, 20)) {
      if (!r.path) continue;
      const got = await fetch(`${base}/api/mesh/get`, {
        method: "POST", headers, body: JSON.stringify({ path: `@${r.path}` }),
      });
      if (!got.ok) continue;
      const content: any = ((await got.json()) as any)?.content ?? {};
      // An explicit content.url wins (a record can name a non-HTTPS scheme/port — e.g. the local
      // sidecar); otherwise the host is a public portal reached over HTTPS. Records are hand-edited
      // data — trim before building URLs and names from them.
      const recordUrl = typeof content.url === "string" ? content.url.trim() : "";
      const host = typeof content.host === "string" ? content.host.trim() : "";
      const url = recordUrl || (host ? `https://${host}` : "");
      if (!url) continue;
      found.push({ name: (r.name ?? "").trim() || host || url, url: url.replace(/\/+$/, ""), token: "", local: false, kind: "Prod" });
    }
    return found;
  } catch {
    return [];
  }
}

/** Merge discovered instances into the list — an existing entry (its token, edits) always wins,
 *  and a record describing THIS app's own local mesh never duplicates the Local entry.
 *  New ones are persisted to the local mesh (the store), best-effort. */
export function mergeDiscovered(discovered: MeshInstance[]): MeshInstance[] {
  const byName = new Map(remotes.map((i) => [i.name, i]));
  const byUrl = new Set(remotes.map((i) => i.url));
  const localUrl = localInstance().url;
  for (const d of discovered)
    if (!byName.has(d.name) && !byUrl.has(d.url) && d.url !== localUrl) {
      byName.set(d.name, d);
      persist(d);
    }
  remotes = [...byName.values()];
  emitInstancesChanged();
  return remotes;
}

/** All instances: Local first, then the remotes hydrated from the local mesh. */
export function loadInstances(): MeshInstance[] {
  return [localInstance(), ...remotes];
}

/** The instance the app is currently pointed at (defaults to Local — the main mesh). */
export function currentInstance(): MeshInstance {
  return remotes.find((i) => i.name === currentName) ?? localInstance();
}

export function setCurrentInstance(name: string): void {
  currentName = remotes.some((i) => i.name === name) ? name : "";
}

import { refreshOAuth, signInWithOAuth } from "./oauth";

/**
 * Make `name` the current instance, ensuring a REMOTE portal is signed in FIRST — the app never
 * dials a portal anonymously (an anonymous viewer gets 401s and renders nothing, which reads as a
 * broken app). Local is the device's own mesh and connects as-is. An expired OAuth token refreshes
 * silently; a missing or dead token opens the portal's own browser sign-in. onReady() fires when
 * the selection is ready to dial; onError(message) when sign-in failed — the selection is NOT
 * switched then, so the app stays on the mesh it could actually talk to.
 */
export function selectAndSignIn(name: string, onReady: () => void, onError: (message: string) => void): void {
  const inst = loadInstances().find((i) => i.name === name);
  if (!inst) return onError(`Unknown mesh: ${name}`);
  if (inst.local) { setCurrentInstance(name); return onReady(); }
  const expired = !!inst.tokenExpiresAt && inst.tokenExpiresAt < Date.now();
  if (inst.token && !expired) { setCurrentInstance(name); return onReady(); }
  const finish = (r: { accessToken: string; refreshToken?: string; clientId: string; expiresAt?: number }) => {
    saveInstance({ ...inst, token: r.accessToken, refreshToken: r.refreshToken, clientId: r.clientId, tokenExpiresAt: r.expiresAt });
    onReady();
  };
  const browser = () => signInWithOAuth(inst.url)
    .then(finish)
    .catch((e) => onError(`${inst.name}: ${e?.message ?? "sign-in failed"}`));
  if (expired && inst.refreshToken && inst.clientId)
    refreshOAuth(inst.url, inst.clientId, inst.refreshToken).then((r) => (r ? finish(r) : browser()), browser);
  else void browser();
}

/** Add or replace a remote instance (keyed by name), persist its node, and make it current. */
export function saveInstance(inst: MeshInstance): void {
  const clean: MeshInstance = { ...inst, local: false, url: inst.url.replace(/\/+$/, "") };
  remotes = [...remotes.filter((i) => i.name !== clean.name), clean];
  currentName = clean.name;
  persist(clean);
  emitInstancesChanged();
}

export function removeInstance(name: string): void {
  const inst = remotes.find((i) => i.name === name);
  remotes = remotes.filter((i) => i.name !== name);
  if (currentName === name) currentName = "";
  if (inst && storeMesh)
    storeMesh.delete(`${INSTANCE_SEGMENT}/${instanceIdFor(inst.url)}`).catch(() => {});
  emitInstancesChanged();
}
