"use client";

// The one live-mesh connection for the whole browser session, held in a client provider that the
// App Router layout keeps mounted across navigations. One gRPC-web connection; one live AreaSource
// per visited mesh path, kept for the connection's lifetime (instant back-nav — the area stream
// stays subscribed), exactly like the Vite SPA's per-path source map. All of this is CLIENT state:
// the server renders per-request snapshots only and holds no streams (see server/snapshot.ts).

import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import type { AreaSource } from "@meshweaver/react/core";
import { connectLive, createLiveAreaSource, type LiveMesh } from "./live";

export type LiveState =
  | { kind: "connecting" }
  | { kind: "live"; mesh: LiveMesh }
  | { kind: "offline"; reason: string };

export interface LiveConnection {
  state: LiveState;
  /** Per-path live area stream errors (the stream faulted after subscribe). */
  areaErrors: Record<string, string>;
  /** The live AreaSource for a mesh path — created on first use, cached per connection.
   *  Returns null until the connection is live. */
  getAreaSource(path: string): AreaSource | null;
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

export function LiveConnectionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<LiveState>({ kind: "connecting" });
  const [areaErrors, setAreaErrors] = useState<Record<string, string>>({});
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
    (path: string): AreaSource | null => {
      if (!connection || !path) return null;
      const cached = sourcesRef.current.get(path);
      if (cached) return cached;
      const source = createLiveAreaSource(connection, path, (error) =>
        setAreaErrors((e) => ({ ...e, [path]: String((error as Error)?.message ?? error) })),
      );
      sourcesRef.current.set(path, source);
      return source;
    },
    [connection],
  );

  return (
    <LiveConnectionContext.Provider value={{ state, areaErrors, getAreaSource }}>
      {children}
    </LiveConnectionContext.Provider>
  );
}
