import type { CSSProperties } from "react";
import type { Json, UiControl } from "../area/types.js";

export function cssSize(v: Json): string | undefined {
  if (v == null) return undefined;
  return typeof v === "number" ? `${v}px` : String(v);
}

export function cssAlign(v: Json): CSSProperties["alignItems"] {
  switch (String(v ?? "").toLowerCase()) {
    case "start":
    case "left":
    case "top":
      return "flex-start";
    case "center":
      return "center";
    case "end":
    case "right":
    case "bottom":
      return "flex-end";
    case "stretch":
      return "stretch";
    case "spacebetween":
      return "space-between";
    default:
      return undefined;
  }
}

export function controlStyle(control: UiControl): CSSProperties {
  const s = control.style;
  if (s == null) return {};
  if (typeof s === "object") return s as CSSProperties;
  if (typeof s === "string") return parseInline(s);
  return {};
}

export function controlClass(control: UiControl): string | undefined {
  return typeof control.class === "string" ? control.class : undefined;
}

/** Join className fragments (a control's hardcoded class + its WithClass value), dropping empties. */
export function mergeClass(...parts: (string | undefined | null | false)[]): string | undefined {
  const joined = parts.filter((p): p is string => typeof p === "string" && p.length > 0).join(" ");
  return joined.length > 0 ? joined : undefined;
}

function parseInline(s: string): CSSProperties {
  const out: Record<string, string> = {};
  for (const decl of s.split(";")) {
    const idx = decl.indexOf(":");
    if (idx <= 0) continue;
    const key = decl.slice(0, idx).trim().replace(/-([a-z])/g, (_, c: string) => c.toUpperCase());
    out[key] = decl.slice(idx + 1).trim();
  }
  return out as CSSProperties;
}
