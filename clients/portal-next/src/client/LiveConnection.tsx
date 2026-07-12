"use client";

// The one live-mesh connection for the whole browser session, held in a client provider that the
// App Router layout keeps mounted across navigations. One gRPC-web connection; one live AreaSource
// per visited area target, kept for the connection's lifetime (instant back-nav — the area stream
// stays subscribed), exactly like the Vite SPA's per-path source map. All of this is CLIENT state:
// the server renders per-request snapshots only and holds no streams (see server/snapshot.ts).
//
// This file also carries the NAVIGATION context: the page's LiveArea publishes the current
// resolved target (node address + area), and the shell chrome (menus, settings, search scoping)
// reads it — the React twin of the Blazor INavigationService.NavigationContext.

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { AreaSource, AreaSourceFactory } from "@meshweaver/react/core";
import { MeshAreaRegistry } from "@meshweaver/react/core";
import { acquireLiveMesh, targetKey, type AreaTarget, type LiveMesh } from "./live";

export type LiveState =
  | { kind: "connecting" }
  | { kind: "live"; mesh: LiveMesh }
  | { kind: "offline"; reason: string };

export interface LiveConnection {
  state: LiveState;
  /** Per-target live area stream errors (the stream faulted after subscribe), keyed by targetKey. */
  areaErrors: Record<string, string>;
  /** The live AreaSource for a resolved area target — resolved through the session's ONE registry
   *  (deduped per address/area/id). Returns null until the connection is live. */
  getAreaSource(target: AreaTarget): AreaSource | null;
  /** The embed factory for nested `@@` areas — backed by the SAME registry as {@link getAreaSource}
   *  (one hub: a page area and an embed of it share ONE stream). Null until the connection is live. */
  embeddedFactory: AreaSourceFactory | null;
}

const LiveConnectionContext = createContext<LiveConnection>({
  // Default context (also what SSR renders): not connected yet, no sources.
  state: { kind: "connecting" },
  areaErrors: {},
  getAreaSource: () => null,
  embeddedFactory: null,
});

export function useLiveConnection(): LiveConnection {
  return useContext(LiveConnectionContext);
}

/** The currently-displayed area target — published by the routed LiveArea, read by the chrome. */
export interface NavigationState {
  /** The URL mesh path as routed (may include area segments). */
  path: string;
  /** The resolved subscription target (node address + area reference); null before first page. */
  target: AreaTarget | null;
}

interface NavigationContextValue extends NavigationState {
  setCurrent(state: NavigationState): void;
}

const NavigationContext = createContext<NavigationContextValue>({
  path: "",
  target: null,
  setCurrent: () => {},
});

export function useNavigationState(): NavigationState {
  return useContext(NavigationContext);
}

export function usePublishNavigation(): (state: NavigationState) => void {
  return useContext(NavigationContext).setCurrent;
}

export function LiveConnectionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<LiveState>({ kind: "connecting" });
  const [areaErrors, setAreaErrors] = useState<Record<string, string>>({});
  const [nav, setNav] = useState<NavigationState>({ path: "", target: null });
  // Targets whose faulted-stream surfacing we've already wired (subscribe-once per key).
  const wiredErrors = useRef(new Set<string>());

  // Join the mesh once on mount (client only) — the browser mints its own token same-origin.
  // The connection is the TAB's shared one (acquireLiveMesh): a provider remount / StrictMode
  // double-mount must reuse it, never open-and-close a second connection for the same per-tab
  // participant address (closing the first disposed the server-side participant hub out from
  // under the survivor). The tab owns the connection's lifetime (pagehide closes it), so the
  // cleanup here only cancels state updates.
  useEffect(() => {
    let cancelled = false;
    acquireLiveMesh().then(
      (m: LiveMesh) => {
        if (!cancelled) setState({ kind: "live", mesh: m });
      },
      (error) => {
        if (!cancelled) setState({ kind: "offline", reason: String((error as Error)?.message ?? error) });
      },
    );
    return () => {
      cancelled = true;
    };
  }, []);

  const connection = state.kind === "live" ? state.mesh.connection : null;

  // THE session hub: ONE subscription registry over the connection. Both the routed page area
  // (getAreaSource) and every nested `@@` embed (embeddedFactory) resolve through it, so a given
  // (address, area, id) has exactly ONE live stream — no per-render/per-embed churn, no duplicate
  // subscriptions racing. Recreated only when the connection itself changes; closed on teardown.
  const registry = useMemo(() => (connection ? new MeshAreaRegistry(connection) : null), [connection]);
  useEffect(() => {
    if (!registry) return;
    wiredErrors.current = new Set();
    setAreaErrors({});
    return () => registry.close();
  }, [registry]);

  const embeddedFactory = useMemo<AreaSourceFactory | null>(
    () => (registry ? registry.embeddedFactory() : null),
    [registry],
  );

  const getAreaSource = useCallback(
    (target: AreaTarget): AreaSource | null => {
      if (!registry || !target.address) return null;
      const source = registry.get(target.address, { area: target.area, id: target.id || undefined });
      // Surface a faulted stream once per target — the source records `.error` and notifies.
      const key = targetKey(target);
      if (!wiredErrors.current.has(key)) {
        wiredErrors.current.add(key);
        source.subscribe(() => {
          const err = source.error;
          if (err) setAreaErrors((e) => (e[key] === err ? e : { ...e, [key]: err }));
        });
      }
      return source;
    },
    [registry],
  );

  const setCurrent = useCallback((s: NavigationState) => {
    setNav((prev) => (prev.path === s.path && targetOf(prev) === targetOf(s) ? prev : s));
  }, []);

  return (
    <LiveConnectionContext.Provider value={{ state, areaErrors, getAreaSource, embeddedFactory }}>
      <NavigationContext.Provider value={{ ...nav, setCurrent }}>{children}</NavigationContext.Provider>
    </LiveConnectionContext.Provider>
  );
}

function targetOf(s: NavigationState): string {
  return s.target ? targetKey(s.target) : "";
}
