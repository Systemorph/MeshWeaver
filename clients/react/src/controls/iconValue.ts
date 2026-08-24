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

/** Force the REQUESTED render size onto an inline SVG's ROOT tag. Authored icons can carry
 *  explicit width/height intrinsics (width="24" height="24"), and both SvgXml (native) and inline
 *  DOM rendering honor them over the surrounding box — a 24px paint inside a 64px tile, while
 *  viewBox-only icons fill (the "tiles render tiny" defect; Plugins #620 stripped the authored
 *  set, this is the structural half so the next fixed-size icon cannot regress it). The root
 *  tag's own width/height are stripped and the requested size injected FIRST on the tag (a
 *  duplicate attribute later in a tag is ignored by the parser), so the caller's size always
 *  wins; viewBox is preserved so content scales; CHILD width/height (<rect width=…>) are
 *  untouched. */
export function sizeInlineSvg(svg: string, size: number): string {
  return svg.replace(/<svg\b([^>]*)>/i, (_m, attrs: string) => {
    const cleaned = attrs
      .replace(/\s(?:width|height)\s*=\s*"[^"]*"/gi, "")
      .replace(/\s(?:width|height)\s*=\s*'[^']*'/gi, "")
      .replace(/\s(?:width|height)\s*=\s*[^\s>]+/gi, "");
    return `<svg width="${size}" height="${size}"${cleaned}>`;
  });
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

// ---- Icon backplate policy — EXACTLY the server's IconBackplate (MeshWeaver.Graph), keep in step.
// Every inline-svg icon renders on a full-bleed rounded plate: an icon authored without one gets a
// generated plate here (hue = stable FNV-1a hash of the markup over the shared palette,
// currentColor recolored to white), so a monochrome outline can never vanish on one theme. Icons
// that already paint a plate — authored store marks, thread identicons — pass through unchanged.

/** The shared plate palette — same hues, same ORDER as IconBackplate.Palette (the hash indexes it). */
export const backplatePalette = [
  "#4338ca", // indigo
  "#1f6feb", // blue
  "#0e7490", // cyan
  "#0f766e", // teal
  "#15803d", // green
  "#b45309", // amber
  "#b91c1c", // red
  "#be185d", // pink
  "#7c3aed", // violet
  "#334155", // slate
];

/** Stable FNV-1a (32-bit, UTF-16 code units — identical to the C# char loop) into the palette. */
export function backplateHue(seed: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < seed.length; i++) {
    hash = Math.imul(hash ^ seed.charCodeAt(i), 0x01000193) >>> 0;
  }
  return backplatePalette[hash % backplatePalette.length];
}

function attrOf(attrs: string, name: string): string | null {
  // Lookbehind keeps `x` from reading rx="16" and `width` from reading stroke-width.
  const m = new RegExp(`(?<![-\\w])${name}\\s*=\\s*(["'])(.*?)\\1`, "i").exec(attrs);
  return m ? m[2].trim() : null;
}

function numberOf(attrs: string, name: string, fallback: number): number {
  const raw = attrOf(attrs, name);
  if (raw == null) return fallback;
  if (raw.endsWith("%")) {
    const pct = Number.parseFloat(raw.slice(0, -1));
    return Number.isFinite(pct) ? (fallback * pct) / 100 : fallback;
  }
  const v = Number.parseFloat(raw);
  return Number.isFinite(v) ? v : fallback;
}

function canvasOf(svg: string): { w: number; h: number } {
  const open = /<svg\b([^>]*)>/i.exec(svg);
  if (!open) return { w: 24, h: 24 };
  const attrs = open[1];
  const viewBox = attrOf(attrs, "viewBox");
  if (viewBox) {
    const parts = viewBox.split(/[\s,]+/).filter(Boolean);
    if (parts.length === 4) {
      const vw = Number.parseFloat(parts[2]);
      const vh = Number.parseFloat(parts[3]);
      if (vw > 0 && vh > 0) return { w: vw, h: vh };
    }
  }
  const w = numberOf(attrs, "width", 24);
  const h = numberOf(attrs, "height", 24);
  return { w: w > 0 ? w : 24, h: h > 0 ? h : 24 };
}

/** Whether the svg already paints a full-bleed plate: first drawable is a rect (or circle)
 *  covering ≥90% of its OWN canvas with a real fill (IconBackplate.HasBackplate). */
export function hasBackplate(svg: string): boolean {
  if (!svg || !svg.trim()) return false;
  const { w: cw, h: ch } = canvasOf(svg);
  const first = /<(rect|circle|ellipse|path|polygon|polyline|line|text)\b([^>]*?)\/?>/i.exec(svg);
  if (!first) return false;
  const [, tag, attrs] = first;
  const fill = attrOf(attrs, "fill");
  if (fill === "none" || fill === "transparent") return false;
  const threshold = 0.9;
  if (tag.toLowerCase() === "rect") {
    return (
      numberOf(attrs, "width", cw) >= cw * threshold &&
      numberOf(attrs, "height", ch) >= ch * threshold &&
      numberOf(attrs, "x", 0) <= cw * (1 - threshold) &&
      numberOf(attrs, "y", 0) <= ch * (1 - threshold)
    );
  }
  if (tag.toLowerCase() === "circle") {
    return numberOf(attrs, "r", 0) >= (Math.min(cw, ch) * threshold) / 2;
  }
  return false;
}

/** The one entry point (IconBackplate.Ensure): plated svg unchanged; anything else wrapped on a
 *  generated rx=5 plate, the original nested inset-3 with its own viewBox intact. */
export function ensureBackplate(svg: string): string {
  if (!svg || !isInlineSvg(svg) || hasBackplate(svg)) return svg;
  const hue = backplateHue(svg);
  let inner = svg.replace(/currentColor/gi, "#fff");
  const open = /<svg\b([^>]*)>/i.exec(inner);
  if (open) {
    const attrs = open[1];
    const hadViewBox = attrOf(attrs, "viewBox") != null;
    const { w, h } = canvasOf(inner);
    const kept = attrs.replace(/\s+(x|y|width|height)\s*=\s*(["']).*?\2/gi, "");
    const viewBox = hadViewBox ? "" : ` viewBox='0 0 ${w} ${h}'`;
    inner =
      inner.slice(0, open.index) +
      `<svg x='3' y='3' width='18' height='18'${viewBox}${kept}>` +
      inner.slice(open.index + open[0].length);
  }
  return (
    "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'>" +
    "<rect width='24' height='24' rx='5' fill='" +
    hue +
    "' stroke='none'/>" +
    inner +
    "</svg>"
  );
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
  if (isInlineSvg(s)) return { kind: "svg", text: ensureBackplate(s) };
  if (isIconUrl(s)) return { kind: "url", text: s };
  if (isEmojiIcon(s)) return { kind: "emoji", text: s };
  // Any letters-only word tries the curated Fluent map (layout-area icon props carry lowercase
  // names like "save" too — broader than the NODE-icon legacy filter isFluentIconName).
  if (/^(fluent-ui:|fluent:)/i.test(s) || isLettersOnlyName(s)) return { kind: "fluent", text: s };
  return { kind: "emoji", text: s };
}
