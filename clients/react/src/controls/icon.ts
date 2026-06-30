import type { ComponentType } from "react";
import * as Icons from "@fluentui/react-icons";

/** Resolve a MeshWeaver icon name (e.g. "Save", "fluent:Add") to a Fluent React icon component. */
export function resolveIconByName(name: string): ComponentType<any> | undefined {
  if (!name) return undefined;
  const base = name.replace(/^(fluent:|Icon)/i, "").replace(/[^A-Za-z0-9]/g, "");
  if (!base) return undefined;
  const candidates = [base, `${base}20Regular`, `${base}24Regular`, `${base}Regular`, `${base}20Filled`, `${base}16Regular`];
  for (const c of candidates) {
    const cmp = (Icons as Record<string, unknown>)[c];
    if (cmp) return cmp as ComponentType<any>;
  }
  return undefined;
}
