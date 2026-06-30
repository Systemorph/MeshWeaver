import { createContext, useContext, useMemo, useSyncExternalStore } from "react";
import type { ReactNode } from "react";
import type { AreaSource, AreaTree, Json, MeshEvent } from "./types.js";
import { resolve as resolveBinding, bindingPointer } from "./pointer.js";

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

/** Resolve a control property to its value (literal or bound /data pointer). */
export function useResolve(value: Json): Json {
  const { dataContext } = useScope();
  const state = useAreaState();
  return useMemo(() => resolveBinding(state, value, dataContext), [state, value, dataContext]);
}

/** The absolute pointer a bound property writes back to (for form edits). */
export function useBindingPointer(value: Json): string | undefined {
  const { dataContext } = useScope();
  return useMemo(() => bindingPointer(value, dataContext), [value, dataContext]);
}

export function useEmit(): (event: MeshEvent) => void {
  const { source } = useScope();
  return source.emit;
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
