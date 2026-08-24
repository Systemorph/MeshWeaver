import { createContext, useCallback, useContext, useMemo, useSyncExternalStore } from "react";
import type { ReactNode } from "react";
import type { AreaSource, AreaTree, Json, MeshEvent } from "./types.js";
import { resolve as resolveBinding, bindingPointer, isBinding } from "./pointer.js";
import { NodeBindingContext } from "./nodeBindingStore.js";
import {
  fieldPointerOf,
  nodeFieldPatch,
  parseMeshNodeDataContext,
  resolveNodeField,
} from "./meshNodeBinding.js";

interface AreaScope {
  source: AreaSource;
  /** Key of the area currently being rendered (used to scope click/edit events). */
  area: string;
  /** Optional data-context pointer prefix for relative bindings. */
  dataContext?: string;
}

const ScopeCtx = createContext<AreaScope | null>(null);

export function useScope(): AreaScope {
  const v = useContext(ScopeCtx);
  if (!v) throw new Error("MeshWeaver control rendered outside <MeshAreaView>");
  return v;
}

export function useAreaState(): AreaTree {
  const { source } = useScope();
  return useSyncExternalStore(source.subscribe, source.getState, source.getState);
}

/**
 * The live snapshot of the node a node-bound DataContext addresses — undefined for an ordinary
 * `/data/{id}` context, or when no host supplied a store. Subscribes for the component's lifetime
 * (never `.Take(1)`), so the view re-renders as the node changes, like every other binding.
 */
function useBoundNode(dataContext?: string): Record<string, unknown> | undefined {
  const store = useContext(NodeBindingContext);
  const path = useMemo(() => parseMeshNodeDataContext(dataContext)?.nodePath, [dataContext]);
  const subscribe = useCallback(
    (listener: () => void) => (store && path ? store.subscribe(path, listener) : () => {}),
    [store, path],
  );
  const snapshot = useCallback(() => (store && path ? store.get(path) : undefined), [store, path]);
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/**
 * Resolve a control property to its value — a literal, a `/data` pointer into the area tree, or a
 * field on the MESH NODE when the scope's DataContext is node-bound (the client twin of Blazor's
 * `BlazorView.DataBind` branch). Without the node branch every node-bound editor renders empty:
 * its value lives on the node stream, and the area tree has no `/$meshNode/…` entry to index into.
 */
export function useResolve(value: Json): Json {
  const { dataContext } = useScope();
  const state = useAreaState();
  const node = useBoundNode(dataContext);
  return useMemo(() => {
    const ctx = parseMeshNodeDataContext(dataContext);
    // A node-bound context still resolves ABSOLUTE pointers against the area tree — transient view
    // state (the click-to-edit `editState_…` flags) legitimately lives there, not on the node.
    if (ctx && isBinding(value) && !value.pointer.startsWith("/")) return resolveNodeField(node, ctx, value.pointer);
    return resolveBinding(state, value, dataContext);
  }, [state, node, value, dataContext]);
}

/** The absolute pointer a bound property writes back to (for form edits). */
export function useBindingPointer(value: Json): string | undefined {
  const { dataContext } = useScope();
  return useMemo(() => bindingPointer(value, dataContext), [value, dataContext]);
}

/**
 * The event sink for the current area. A node-bound `update` is routed to the NODE (a per-field
 * merge patch through `MeshOps.patch`) instead of the layout-area stream — the client twin of
 * Blazor's `BlazorView.UpdatePointer` branch. Posting it to the area stream would write the edit
 * into a `/$meshNode/…` path the layout store has no business holding, and the node would never
 * see it. Clicks, blurs and dialogs are area events either way.
 */
export function useEmit(): (event: MeshEvent) => void {
  const { source, dataContext } = useScope();
  const store = useContext(NodeBindingContext);
  return useCallback(
    (event: MeshEvent) => {
      const ctx = event.kind === "update" && event.pointer ? parseMeshNodeDataContext(dataContext) : null;
      if (ctx && store && dataContext) {
        const field = fieldPointerOf(dataContext, event.pointer!);
        if (field) {
          store.write(ctx.nodePath, nodeFieldPatch(store.get(ctx.nodePath), ctx, field, event.value));
          return;
        }
      }
      source.emit(event);
    },
    [source, dataContext, store],
  );
}

export function ScopeProvider({
  source,
  area,
  dataContext,
  children,
}: {
  source: AreaSource;
  area: string;
  dataContext?: string;
  children: ReactNode;
}) {
  const value = useMemo<AreaScope>(() => ({ source, area, dataContext }), [source, area, dataContext]);
  return <ScopeCtx.Provider value={value}>{children}</ScopeCtx.Provider>;
}
