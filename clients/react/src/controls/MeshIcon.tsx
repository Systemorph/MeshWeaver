// The ONE web icon renderer for every mesh icon value — used by the Icon control, nav links/groups,
// menu items, buttons, search results and the portal shells. Dispatches on classifyIcon (shared,
// platform-neutral) and renders each kind with the right DOM element:
//   svg    → sanitized inline <svg> (node Icons, IconGenerator avatars)
//   url    → <img> (e.g. /static/NodeTypeIcons/person.svg), flex-shrink-proof
//   emoji  → text span
//   fluent → curated Fluent component (resolveIconByName)
//   none / unmapped fluent → the `fallback` node (an initial bubble via <InitialBubble>, or nothing)
// Before this existed, IconView / NavLink / portal-next NodeIcon / mesh.tsx cards each classified
// icons their own way — nav and search dropped every URL/emoji icon ("most SVGs not showing").

import type { CSSProperties, ReactNode } from "react";
import type { Json } from "../area/types.js";
import { resolveIconByName } from "./icon.js";
import { classifyIcon, sanitizeInlineSvg } from "./iconValue.js";

export interface MeshIconProps {
  /** The raw icon value: name, `{provider,id,…}` object, inline SVG, URL/path, or emoji. */
  value: Json;
  /** Square box size in px (default 20 — the Fluent icon size). */
  size?: number;
  /** Rendered when the value is empty or an unmapped Fluent name (Blazor renders nothing;
   *  pass an <InitialBubble> where the Blazor side shows the initial-letter placeholder). */
  fallback?: ReactNode;
  style?: CSSProperties;
  className?: string;
  onClick?: () => void;
}

export function MeshIcon({ value, size = 20, fallback = null, style, className, onClick }: MeshIconProps): ReactNode {
  const cursor = onClick ? "pointer" : undefined;
  const box: CSSProperties = {
    width: size,
    height: size,
    display: "inline-flex",
    alignItems: "center",
    justifyContent: "center",
    flexShrink: 0,
    cursor,
    ...style,
  };
  const classified = classifyIcon(value);
  switch (classified.kind) {
    case "svg":
      return (
        <span
          onClick={onClick}
          style={box}
          className={mergeCls("mw-inline-svg", className)}
          dangerouslySetInnerHTML={{ __html: sanitizeInlineSvg(classified.text) }}
        />
      );
    case "url":
      return (
        <img
          src={classified.text}
          alt=""
          width={size}
          height={size}
          onClick={onClick}
          className={className}
          style={{ objectFit: "contain", flexShrink: 0, cursor, ...style }}
        />
      );
    case "emoji":
      return (
        <span onClick={onClick} className={className} style={{ ...box, fontSize: Math.round(size * 0.85), lineHeight: 1 }} aria-hidden>
          {classified.text}
        </span>
      );
    case "fluent": {
      const Cmp = resolveIconByName(classified.text);
      if (Cmp) return <Cmp onClick={onClick} className={className} style={{ cursor, flexShrink: 0, ...style }} />;
      return fallback;
    }
    default:
      return fallback;
  }
}

/** The initial-letter placeholder the Blazor search bar / catalogs show when a node has no
 *  renderable icon — pass as MeshIcon's `fallback` in node contexts. */
export function InitialBubble({
  name,
  size = 20,
  style,
  className,
}: {
  name?: string | null;
  size?: number;
  style?: CSSProperties;
  className?: string;
}): ReactNode {
  return (
    <span
      aria-hidden
      className={className}
      style={{
        width: size,
        height: size,
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        flexShrink: 0,
        borderRadius: "50%",
        background: "var(--colorBrandBackground2)",
        color: "var(--colorBrandForeground2)",
        fontSize: Math.round(size * 0.55),
        fontWeight: 600,
        ...style,
      }}
    >
      {(name ?? "?").charAt(0).toUpperCase() || "?"}
    </span>
  );
}

function mergeCls(a: string, b?: string): string {
  return b ? `${a} ${b}` : a;
}
