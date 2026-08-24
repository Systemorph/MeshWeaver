// The seam `area/context` reads node-bound values through. The area layer stays transport-free:
// it only knows there MAY be a store of live node snapshots; `live/nodeBinding` supplies one over
// MeshOps (watch + patch), and hosts without MeshOps simply render the pre-fix (empty) behaviour.
import { createContext } from "react";

export interface NodeSnapshotStore {
  /** The node's current snapshot (undefined while the first state is in flight, or on failure). */
  get(path: string): Record<string, unknown> | undefined;
  /** Subscribe to that node's stream for the listener's lifetime; returns the unsubscribe. */
  subscribe(path: string, listener: () => void): () => void;
  /** Per-field write-back (RFC 7396 merge patch at the node root). */
  write(path: string, fields: Record<string, unknown>): void;
}

export const NodeBindingContext = createContext<NodeSnapshotStore | null>(null);
