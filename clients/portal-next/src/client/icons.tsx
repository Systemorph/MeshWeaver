"use client";

// Node/menu icon rendering for the shell — thin wrappers over the SHARED renderer pieces
// (@meshweaver/react MeshIcon/InitialBubble + the iconValue classification). This file used to
// carry its own emoji/SVG/URL branching; the one decision table now lives in the renderer core
// so search, menus, nav and layout areas classify icons identically.

import type { CSSProperties, ReactNode } from "react";
import { InitialBubble, MeshIcon, iconForRendering, isEmojiIcon } from "@meshweaver/react";

export { iconForRendering, isEmojiIcon };

export interface NodeIconProps {
  icon?: string | null;
  /** Fallback initial source (the node name) when no renderable icon exists. */
  name?: string;
  size?: number;
  style?: CSSProperties;
}

/** Renders a node icon: inline SVG markup, emoji span, <img>, or the name's first letter —
 *  the client port of MeshNodeImageHelper.GetIconForRendering + the Blazor search-bar branches. */
export function NodeIcon({ icon, name, size = 24, style }: NodeIconProps): ReactNode {
  return (
    <MeshIcon
      value={iconForRendering(icon)}
      size={size}
      style={style}
      fallback={<InitialBubble name={name} size={size} style={style} />}
    />
  );
}

/** Menu-item icon: emoji span or 16px <img> (PortalLayoutBase menu rendering). */
export function MenuItemIcon({ icon }: { icon?: string | null }): ReactNode {
  if (!icon) return null;
  return <MeshIcon value={icon} size={16} style={{ marginRight: 8 }} />;
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
