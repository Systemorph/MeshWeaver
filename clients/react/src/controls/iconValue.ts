// Icon VALUE classification — pure logic, no DOM, no Fluent. The one shared decision table for
// every mesh icon value, replacing the three divergent copies that used to live in IconView
// (display.tsx), portal-next's icons.tsx and mesh.tsx's card image: a mesh icon is EITHER an
// inline SVG document (node Icons, IconGenerator avatars), an image URL/path/data-URI
// (/static/NodeTypeIcons/…), an emoji, or a Fluent icon NAME / serialized
// `MeshWeaver.Domain.Icon` {provider,id,…} object. Web and RN leaf packs render each kind with
// their own native element; the classification is identical everywhere.

import type { Json } from "../area/types.js";

export type IconKind = "none" | "svg" | "url" | "emoji" | "fluent";

export interface ClassifiedIcon {
  kind: IconKind;
  /** The string payload — SVG markup, URL, emoji text, or icon name (kind-dependent). */
  text: string;
}

/** True when the value is an inline SVG DOCUMENT (`<svg …>…</svg>`), the shape node Icons and the
 *  IconGenerator produce — as opposed to an icon NAME, a URL, or a data URI. */
export function isInlineSvg(value: string): boolean {
  return /^\s*<svg[\s>]/i.test(value);
}

/**
 * Defense-in-depth for inline-SVG injection: node Icons come from the (trusted) IconGenerator, but
 * an Icon field CAN be user-authored, and SVG supports active content. Strip the obvious vectors —
 * `<script>`/`<foreignObject>` elements, `on*` event handlers, and `javascript:` hrefs — before
 * injecting. (The Blazor side trusts the same field via MarkupString; this is a strictly safer
 * mirror, not a full sanitizer.)
 */
export function sanitizeInlineSvg(svg: string): string {
  return svg
    .replace(/<script[\s\S]*?<\/script\s*>/gi, "")
    .replace(/<foreignObject[\s\S]*?<\/foreignObject\s*>/gi, "")
    .replace(/\son\w+\s*=\s*"[^"]*"/gi, "")
    .replace(/\son\w+\s*=\s*'[^']*'/gi, "")
    .replace(/\son\w+\s*=\s*[^\s>]+/gi, "")
    .replace(/((?:xlink:)?href)\s*=\s*(["'])\s*javascript:[^"']*\2/gi, "");
}

/** An image reference: absolute/rooted URL, data URI, or a path ending in an image extension. */
export function isIconUrl(value: string): boolean {
  return /^(https?:|data:|blob:|\/)/i.test(value) || /\.(svg|png|jpg|jpeg|gif|webp|ico)(\?|#|$)/i.test(value);
}

/** An ASCII-letters-only word ("Save", "arrowSync") — an icon NAME, never emoji. */
function isLettersOnlyName(value: string): boolean {
  return /^[A-Za-z]+$/.test(value);
}

/** Emoji detection — short strings that are not a path/URL/SVG reference or a letters-only icon
 *  name (PortalLayoutBase.IsEmoji / MeshNodeImageHelper.IsEmoji). */
export function isEmojiIcon(value: string): boolean {
  if (!value || value.length > 8) return false;
  if (isInlineSvg(value) || isIconUrl(value)) return false;
  return !isLettersOnlyName(value);
}

/** Legacy Fluent icon names on NODE icons render as nothing. EXACTLY the server's
 *  MeshNodeImageHelper.IsFluentIconName: ASCII letters only, starting UPPERCASE ("Document",
 *  "ArrowLeft") — lowercase-start or digit-carrying values are NOT filtered. */
export function isFluentIconName(value: string): boolean {
  return /^[A-Z][A-Za-z]*$/.test(value);
}

/** Returns the node-icon value for rendering, or null for legacy Fluent icon names
 *  (MeshNodeImageHelper.GetIconForRendering). */
export function iconForRendering(icon: string | null | undefined): string | null {
  if (!icon) return null;
  if (isFluentIconName(icon)) return null;
  return icon;
}

/** Extract the icon NAME from either a bare string ("Save", "fluent:Add") or the framework's
 *  serialized `MeshWeaver.Domain.Icon` `{ provider, id, size, variant }` object — the shape nav /
 *  group / toolbar icon props carry over the wire. Without this the object stringified to
 *  "[object Object]" and every such icon rendered blank. */
export function iconNameOf(value: Json): string {
  if (value == null) return "";
  if (typeof value === "string") return value;
  if (typeof value === "object") {
    const o = value as Record<string, Json>;
    const id = o.id ?? o.Id ?? o.name ?? o.Name;
    return typeof id === "string" ? id : "";
  }
  return "";
}

/** Classify any mesh icon value into its render kind. `{provider,id}` objects and ASCII
 *  identifier strings are `fluent` (resolve against a name→component map per platform). */
export function classifyIcon(value: Json): ClassifiedIcon {
  if (value != null && typeof value === "object") {
    const name = iconNameOf(value);
    return name ? { kind: "fluent", text: name } : { kind: "none", text: "" };
  }
  const s = value == null ? "" : String(value);
  if (!s) return { kind: "none", text: "" };
  if (isInlineSvg(s)) return { kind: "svg", text: s };
  if (isIconUrl(s)) return { kind: "url", text: s };
  if (isEmojiIcon(s)) return { kind: "emoji", text: s };
  // Any letters-only word tries the curated Fluent map (layout-area icon props carry lowercase
  // names like "save" too — broader than the NODE-icon legacy filter isFluentIconName).
  if (/^(fluent-ui:|fluent:)/i.test(s) || isLettersOnlyName(s)) return { kind: "fluent", text: s };
  return { kind: "emoji", text: s };
}
