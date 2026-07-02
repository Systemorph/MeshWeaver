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

import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import type { AreaSource } from "@meshweaver/react/core";
import { connectLive, createLiveAreaSource, targetKey, type AreaTarget, type LiveMesh } from "./live";

export type LiveState =
  | { kind: "connecting" }
  | { kind: "live"; mesh: LiveMesh }
  | { kind: "offline"; reason: string };

export interface LiveConnection {
  state: LiveState;
  /** Per-target live area stream errors (the stream faulted after subscribe), keyed by targetKey. */
  areaErrors: Record<string, string>;
  /** The live AreaSource for a resolved area target — created on first use, cached per connection.
   *  Returns null until the connection is live. */
  getAreaSource(target: AreaTarget): AreaSource | null;
}

const LiveConnectionContext = createContext<LiveConnection>({
  // Default context (also what SSR renders): not connected yet, no sources.
  state: { kind: "connecting" },
  areaErrors: {},
  getAreaSource: () => null,
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
  // Instance (per-provider) source cache — dies with the provider/connection, never module-static.
  const sourcesRef = useRef(new Map<string, AreaSource>());

  // Join the mesh once on mount (client only) — the browser mints its own token same-origin.
  useEffect(() => {
    let cancelled = false;
    let mesh: LiveMesh | null = null;
    connectLive().then(
      (m) => {
        mesh = m;
        if (cancelled) m.close();
        else setState({ kind: "live", mesh: m });
      },
      (error) => {
        if (!cancelled) setState({ kind: "offline", reason: String((error as Error)?.message ?? error) });
      },
    );
    return () => {
      cancelled = true;
      mesh?.close();
      sourcesRef.current.clear();
    };
  }, []);

  const connection = state.kind === "live" ? state.mesh.connection : null;

  const getAreaSource = useCallback(
    (target: AreaTarget): AreaSource | null => {
      if (!connection || !target.address) return null;
      const key = targetKey(target);
      const cached = sourcesRef.current.get(key);
      if (cached) return cached;
      const source = createLiveAreaSource(connection, target, (error) =>
        setAreaErrors((e) => ({ ...e, [key]: String((error as Error)?.message ?? error) })),
      );
      sourcesRef.current.set(key, source);
      return source;
    },
    [connection],
  );

  const setCurrent = useCallback((s: NavigationState) => {
    setNav((prev) => (prev.path === s.path && targetOf(prev) === targetOf(s) ? prev : s));
  }, []);

  return (
    <LiveConnectionContext.Provider value={{ state, areaErrors, getAreaSource }}>
      <NavigationContext.Provider value={{ ...nav, setCurrent }}>{children}</NavigationContext.Provider>
    </LiveConnectionContext.Provider>
  );
}

function targetOf(s: NavigationState): string {
  return s.target ? targetKey(s.target) : "";
}
