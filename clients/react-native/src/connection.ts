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
  /** Optional explicit environment glyph (overrides the URL-derived one). */
  icon?: string;
  /** Optional explicit environment accent color (hex). */
  color?: string;
  /** Optional explicit environment kind label (e.g. "Prod", "Local · k8s"). */
  kind?: string;
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
        : u.includes("atioz")
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

const INSTANCES_KEY = "mw.instances";
const CURRENT_KEY = "mw.currentInstance";
const SEEDED_KEY = "mw.seedVersion";
const SEED_VERSION = 1;

/**
 * The user's known environments, seeded once so the instance switcher is populated out of the box
 * (the built-in "Local" SQLite sidecar is always present via localInstance()). Tokens are empty —
 * anonymous connect; paste an API token per portal in the Connect screen to sign in.
 */
export const KNOWN_INSTANCES: MeshInstance[] = [
  { name: "memex.localhost (k8s)", url: "https://memex.localhost:8443", token: "", local: false, icon: "☸", color: "#d29922", kind: "Local · k8s" },
  { name: "memex", url: "https://memex.meshweaver.cloud", token: "", local: false, icon: "☁️", color: "#4c8dff", kind: "Prod" },
  { name: "atioz", url: "https://atioz.meshweaver.cloud", token: "", local: false, icon: "🏢", color: "#a371f7", kind: "Client" },
];

/**
 * Idempotently add the known environments to the saved-instances list on first run (versioned, so
 * removing a seeded instance won't resurrect it). Never clobbers a user-edited instance of the same name.
 */
export function seedDefaultInstances(): void {
  if (typeof localStorage === "undefined") return;
  if ((read<number>(SEEDED_KEY) ?? 0) >= SEED_VERSION) return;
  const existing = (read<MeshInstance[]>(INSTANCES_KEY) ?? []).filter((i) => !i.local && i.url);
  const byName = new Map(existing.map((i) => [i.name, i]));
  for (const k of KNOWN_INSTANCES) if (!byName.has(k.name)) byName.set(k.name, k);
  write(INSTANCES_KEY, [...byName.values()]);
  write(SEEDED_KEY, SEED_VERSION);
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
