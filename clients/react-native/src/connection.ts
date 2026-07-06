// Mesh instances — the RN/web twin of the MAUI app's InstanceStore (memex/Memex.Client/Services/
// InstanceStore.cs). "Local" is the mesh that served this app (same origin, anonymous). Additional
// remote instances are portals the user connects to by URL + API token; the live gRPC-web client dials
// whichever instance is current. Persisted in localStorage (the browser twin of MAUI Preferences).

export interface MeshInstance {
  /** Display name. */
  name: string;
  /** Base URL the gRPC-web client dials (same-origin for Local). */
  url: string;
  /** Bearer token (mw_…) for a remote portal; empty ⇒ anonymous. */
  token: string;
  /** True for the mesh that served this app (same origin). */
  local: boolean;
}

import Constants from "expo-constants";

const INSTANCES_KEY = "mw.instances";
const CURRENT_KEY = "mw.currentInstance";

// The mesh a NATIVE build dials by default (it has no serving origin like the web build). Configured in
// app.json → expo.extra.portalUrl; defaults to the LOCAL monolith mesh — Memex.LocalMesh, the in-process
// SQLite-backed mesh sidecar (the MAUI-parity local-first host), reachable from the iOS simulator at
// http://localhost:5250 anonymously (no token). Point it at a remote portal via Connect-to-mesh + a token.
const DEFAULT_PORTAL_URL = String((Constants.expoConfig?.extra as any)?.portalUrl ?? "http://localhost:5250");

/** The default portal URL a native build dials (app.json → expo.extra.portalUrl); prefill for the connect form. */
export function defaultPortalUrl(): string {
  return DEFAULT_PORTAL_URL;
}

const sameOrigin = (): string =>
  typeof window !== "undefined" && window.location ? window.location.origin : "";

/**
 * The always-present default instance. Web (served by the mesh) → same-origin, anonymous. Native has no
 * serving origin, so it dials the configured default portal (the user supplies a token in-app).
 */
export function localInstance(): MeshInstance {
  const origin = sameOrigin();
  return origin
    ? { name: "Local", url: origin, token: "", local: true }
    : { name: "Local mesh", url: DEFAULT_PORTAL_URL, token: "", local: true };
}

function read<T>(key: string): T | null {
  if (typeof localStorage === "undefined") return null;
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write(key: string, value: unknown): void {
  if (typeof localStorage === "undefined") return;
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage disabled — instances just won't persist */
  }
}

/** All instances: Local first, then the saved remotes. */
export function loadInstances(): MeshInstance[] {
  const saved = (read<MeshInstance[]>(INSTANCES_KEY) ?? []).filter((i) => !i.local && i.url);
  return [localInstance(), ...saved];
}

/** The instance the app is currently pointed at (defaults to Local). */
export function currentInstance(): MeshInstance {
  const name = read<string>(CURRENT_KEY);
  return loadInstances().find((i) => i.name === name) ?? localInstance();
}

export function setCurrentInstance(name: string): void {
  write(CURRENT_KEY, name);
}

/** Add or replace a remote instance (keyed by name) and make it current. */
export function saveInstance(inst: MeshInstance): void {
  const others = (read<MeshInstance[]>(INSTANCES_KEY) ?? []).filter((i) => !i.local && i.name !== inst.name);
  write(INSTANCES_KEY, [...others, { ...inst, local: false }]);
  setCurrentInstance(inst.name);
}

export function removeInstance(name: string): void {
  const others = (read<MeshInstance[]>(INSTANCES_KEY) ?? []).filter((i) => !i.local && i.name !== name);
  write(INSTANCES_KEY, others);
  if (read<string>(CURRENT_KEY) === name) setCurrentInstance("Local");
}
