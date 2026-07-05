// Turn a MeshOps node watch (an async-iterable of a node's live state) into React state. This is the
// read primitive every NODE-bound control needs — the client twin of Blazor's
// Hub.GetMeshNodeStream(path) subscription. Kept transport-free: it drives whatever MeshOps the
// nearest MeshOpsProvider supplied (@meshweaver/client-web's Mesh in the portal; a fake in tests).

import { useEffect, useState } from "react";
import type { MeshNodeState, MeshOps } from "./meshOps.js";

/**
 * Drive an async-iterable node watch into a callback; returns the stop function. A missing
 * node/satellite surfaces as a stream error (the cache's DeliveryFailure) — expected, so the view
 * keeps rendering from whatever it already has; a caller that needs to distinguish a genuine
 * transport/auth failure passes <paramref name="onError"/>. The underlying iterator is ALWAYS
 * released (a throw from <c>it.next()</c> must not leak the watch subscription).
 */
export function watchInto(
  ops: MeshOps,
  path: string,
  onState: (n: MeshNodeState) => void,
  onError?: (error: unknown) => void,
): () => void {
  let live = true;
  const it = ops.watch(path)[Symbol.asyncIterator]();
  void (async () => {
    try {
      while (live) {
        const r = await it.next();
        if (r.done) break;
        if (live && r.value) onState(r.value);
      }
    } catch (error) {
      if (live) onError?.(error);
    } finally {
      void Promise.resolve(it.return?.()).catch(() => undefined);
    }
  })();
  return () => {
    live = false;
    void Promise.resolve(it.return?.()).catch(() => undefined);
  };
}

/** The live state of one node (null until the first emission / when unset). */
export function useNodeState(ops: MeshOps | null, path: string | null): MeshNodeState | null {
  const [node, setNode] = useState<MeshNodeState | null>(null);
  useEffect(() => {
    setNode(null);
    if (!ops || !path) return;
    return watchInto(ops, path, setNode);
  }, [ops, path]);
  return node;
}
