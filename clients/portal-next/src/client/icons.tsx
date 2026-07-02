"use client";

// Icon classification + rendering for mesh node icons — the client port of
// MeshNodeImageHelper.GetIconForRendering (src/MeshWeaver.Graph/MeshNodeImageHelper.cs) and the
// rendering branches the Blazor SearchBar / menus apply: inline SVG markup, emoji, image URL,
// or an initial-letter fallback.

import type { CSSProperties, ReactNode } from "react";

/** Legacy Fluent icon names (PascalCase ASCII words like "Document") render as nothing —
 *  the same filter GetIconForRendering applies. */
function isFluentIconName(icon: string): boolean {
  return /^[A-Z][A-Za-z0-9]*$/.test(icon);
}

/** Returns the icon value for rendering, or null for legacy Fluent icon names. */
export function iconForRendering(icon: string | null | undefined): string | null {
  if (!icon) return null;
  if (isFluentIconName(icon)) return null;
  return icon;
}

/** Emoji detection — short strings that are not a path/URL/SVG reference
 *  (PortalLayoutBase.IsEmoji). */
export function isEmojiIcon(icon: string): boolean {
  if (icon.length > 8) return false;
  if (icon.startsWith("/") || icon.startsWith("http") || icon.includes(".svg")) return false;
  return true;
}

export interface NodeIconProps {
  icon?: string | null;
  /** Fallback initial source (the node name) when no renderable icon exists. */
  name?: string;
  size?: number;
  style?: CSSProperties;
}

/** Renders a node icon: inline SVG markup, emoji span, <img>, or the name's first letter. */
export function NodeIcon({ icon, name, size = 24, style }: NodeIconProps): ReactNode {
  const resolved = iconForRendering(icon);
  const box: CSSProperties = {
    width: size,
    height: size,
    display: "inline-flex",
    alignItems: "center",
    justifyContent: "center",
    flexShrink: 0,
    ...style,
  };
  if (resolved && resolved.trimStart().toLowerCase().startsWith("<svg")) {
    // Trusted mesh-supplied SVG — the same MarkupString render the Blazor search bar applies.
    return <span style={box} dangerouslySetInnerHTML={{ __html: resolved }} />;
  }
  if (resolved && !resolved.includes("/") && !resolved.startsWith("data:")) {
    return (
      <span style={{ ...box, fontSize: `${size * 0.85}px`, lineHeight: 1 }} aria-hidden>
        {resolved}
      </span>
    );
  }
  if (resolved) {
    // eslint-disable-next-line @next/next/no-img-element
    return <img src={resolved} alt="" style={{ ...box, objectFit: "contain" }} />;
  }
  return (
    <span
      style={{
        ...box,
        borderRadius: "50%",
        background: "var(--colorBrandBackground2)",
        color: "var(--colorBrandForeground2)",
        fontSize: `${size * 0.55}px`,
        fontWeight: 600,
      }}
      aria-hidden
    >
      {(name ?? "?").charAt(0).toUpperCase() || "?"}
    </span>
  );
}

/** Menu-item icon: emoji span or 16px <img> (PortalLayoutBase menu rendering). */
export function MenuItemIcon({ icon }: { icon?: string | null }): ReactNode {
  if (!icon) return null;
  if (isEmojiIcon(icon)) return <span style={{ marginRight: 8 }}>{icon}</span>;
  // eslint-disable-next-line @next/next/no-img-element
  return <img src={icon} alt="" style={{ width: 16, height: 16, marginRight: 8 }} />;
}

/** "MeshWeaver.Graph/Story" → "Story" — the node-type display the search dropdown shows. */
export function nodeTypeDisplay(nodeType: string | null | undefined): string {
  if (!nodeType) return "";
  const lastSlash = nodeType.lastIndexOf("/");
  return lastSlash >= 0 ? nodeType.slice(lastSlash + 1) : nodeType;
}

/** "just now" / "5m ago" / "3h ago" / "2d ago" / "Jun 3, 14:05" — NotificationCenterPanel.FormatTime. */
export function formatRelativeTime(iso: string | null | undefined): string {
  if (!iso) return "";
  const ts = new Date(iso);
  if (Number.isNaN(ts.getTime())) return "";
  const deltaSec = (Date.now() - ts.getTime()) / 1000;
  if (deltaSec < 60) return "just now";
  if (deltaSec < 3600) return `${Math.floor(deltaSec / 60)}m ago`;
  if (deltaSec < 86400) return `${Math.floor(deltaSec / 3600)}h ago`;
  if (deltaSec < 7 * 86400) return `${Math.floor(deltaSec / 86400)}d ago`;
  return ts.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}
