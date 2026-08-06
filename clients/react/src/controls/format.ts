// .NET-style value formatting — pure, no renderer imports, so every leaf pack (web, React Native)
// formats a number the SAME way. Extracted out of data.tsx, which imports Fluent and therefore
// could not be reached from a native bundle; data.tsx re-exports it for back-compat.

import type { Json } from "../area/types.js";
import { str } from "./common.js";

/** .NET-style numeric formatting ("N0", "C2", "P1"; "{0:N2}" tolerated). Shared with PivotGrid. */
export function formatValue(v: Json, format?: string): string {
  if (v == null) return "";
  const f = format?.replace(/^\{0:(.+)\}$/, "$1");
  if (f && typeof v === "number") {
    const m = /^([NCP])(\d+)?$/i.exec(f);
    if (m) {
      const digits = m[2] ? Number(m[2]) : m[1].toUpperCase() === "N" ? 0 : 2;
      const n = v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
      if (m[1].toUpperCase() === "C") return `$${n}`;
      if (m[1].toUpperCase() === "P") return `${(v * 100).toFixed(digits)}%`;
      return n;
    }
  }
  return str(v);
}
