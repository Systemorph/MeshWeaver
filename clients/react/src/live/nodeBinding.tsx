// The LIVE half of node-bound data contexts (see area/meshNodeBinding): one shared subscription per
// node path over MeshOps.watch, and per-field write-back through MeshOps.patch — the client twin of
// the server's `hub.GetMeshNodeStream(path)` / `.Update(...)` seam, and the same "one handle per
// path" discipline IMeshNodeStreamCache enforces there.
//
// Mounted by MeshOpsProvider, so every host that already provides MeshOps gets node binding with no
// extra wiring; a host without MeshOps keeps the plain area-tree resolver.
import { useMemo, useRef, type ReactNode } from "react";
import { NodeBindingContext, type NodeSnapshotStore } from "../area/nodeBindingStore.js";
import type { MeshOps } from "./meshOps.js";

interface Entry {
  snapshot?: Record<string, unknown>;
  listeners: Set<() => void>;
  cancelled: boolean;
}

/** Build the store for one MeshOps. Subscriptions are refcounted and torn down with the last reader. */
function createStore(ops: MeshOps): NodeSnapshotStore {
  const entries = new Map<string, Entry>();

  const pump = (path: string, entry: Entry): void => {
    void (async () => {
      try {
        for await (const node of ops.watch(path)) {
          if (entry.cancelled) return;
          // The watch yields the node state; keep it as a plain record so the pure resolver can
          // walk `content` (or the node itself) without knowing the transport's class shape.
          entry.snapshot = node as unknown as Record<string, unknown>;
          entry.listeners.forEach((l) => l());
        }
      } catch {
        // A node that cannot be read (absent, no permission, transport blip) leaves the snapshot
        // undefined — every bound control renders empty rather than throwing, and a later
        // subscription re-attempts. Never a thrown render.
      }
    })();
  };

  return {
    get: (path) => entries.get(path)?.snapshot,
    subscribe: (path, listener) => {
      let entry = entries.get(path);
      if (!entry) {
        entry = { listeners: new Set(), cancelled: false };
        entries.set(path, entry);
        pump(path, entry);
      }
      entry.listeners.add(listener);
      const held = entry;
      return () => {
        held.listeners.delete(listener);
        if (held.listeners.size === 0) {
          held.cancelled = true;
          entries.delete(path);
        }
      };
    },
    write: (path, fields) => {
      // Optimistic local merge so the edited field reflects immediately; the node stream echoes the
      // authoritative state right behind it (the same shape GrpcAreaSource applies for /data edits).
      const entry = entries.get(path);
      if (entry?.snapshot) {
        entry.snapshot = mergeDeep(entry.snapshot, fields);
        entry.listeners.forEach((l) => l());
      }
      ops.patch(path, fields);
    },
  };
}

/** RFC 7396 merge of a patch body into a snapshot (objects merge, everything else replaces). */
function mergeDeep(target: Record<string, unknown>, patch: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = { ...target };
  for (const [key, value] of Object.entries(patch)) {
    const current = out[key];
    out[key] =
      value != null && typeof value === "object" && !Array.isArray(value) &&
      current != null && typeof current === "object" && !Array.isArray(current)
        ? mergeDeep(current as Record<string, unknown>, value as Record<string, unknown>)
        : value;
  }
  return out;
}

export function NodeBindingProvider({ ops, children }: { ops: MeshOps | null; children: ReactNode }): ReactNode {
  // One store per ops instance. Rebuilding it on every render would drop every node subscription
  // and re-open it — the churn that makes embeds re-subscribe on each parent render.
  const ref = useRef<{ ops: MeshOps; store: NodeSnapshotStore } | null>(null);
  const store = useMemo(() => {
    if (!ops) return null;
    if (ref.current?.ops !== ops) ref.current = { ops, store: createStore(ops) };
    return ref.current.store;
  }, [ops]);
  return <NodeBindingContext.Provider value={store}>{children}</NodeBindingContext.Provider>;
}
