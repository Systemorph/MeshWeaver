// Nested layout-area embedding — the contract LayoutAreaControl renders through.
//
// A LayoutAreaControl references a DIFFERENT area stream ((address, LayoutAreaReference)); rendering
// it for real needs a SECOND AreaSource. The renderer stays transport-free: the host installs an
// AreaSourceFactory (e.g. one that opens a GrpcAreaSource on the shared connection and starts it),
// and LayoutAreaView asks it for a source per referenced area. Without a provider the control falls
// back to its link marker — static demos and tests keep working.
//
//   <EmbeddedAreaProvider factory={(address, ref) => {
//     const src = new GrpcAreaSource(connection, address, ref);
//     void src.start();
//     return { source: src, rootArea: ref.area ?? "" };
//   }}> ... </EmbeddedAreaProvider>

import { createContext, useContext } from "react";
import type { ReactNode } from "react";
import type { AreaSource } from "../area/types.js";

/** The reference of the embedded area — mirrors the wire LayoutAreaReference. */
export interface EmbeddedAreaReference {
  /** Area name; empty/absent subscribes the target's DEFAULT area (resolved server-side,
   *  delivered as the `areas[""]` NamedArea indirection). */
  area?: string;
  id?: unknown;
  layout?: string;
}

export interface EmbeddedAreaHandle {
  source: AreaSource;
  /** Root area key to render (defaults to the reference's area, "" for the server default). */
  rootArea?: string;
  /** Called when the embed unmounts or its reference changes — close the subscription here. */
  dispose?: () => void;
}

/** Produce a live AreaSource for a referenced (address, area, id) — or null when unavailable. */
export type AreaSourceFactory = (address: string, reference: EmbeddedAreaReference) => EmbeddedAreaHandle | null;

const FactoryCtx = createContext<AreaSourceFactory | null>(null);

/** The nearest factory, or null when the host renders without nested-area support. */
export function useAreaSourceFactory(): AreaSourceFactory | null {
  return useContext(FactoryCtx);
}

export function EmbeddedAreaProvider({ factory, children }: { factory: AreaSourceFactory | null; children: ReactNode }) {
  return <FactoryCtx.Provider value={factory}>{children}</FactoryCtx.Provider>;
}
