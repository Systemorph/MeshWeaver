// NODE-BOUND DATA CONTEXTS — the client twin of MeshWeaver.Mesh.MeshNodeBindingExtensions.
//
// A layout area edits a mesh node's content by handing the GUI a reserved DataContext instead of a
// /data/{id} replica (LayoutAreaReference.GetMeshNodeDataContext):
//
//     /$meshNode/{base64url(nodePath)}/{c|n}[/{base64url(subPath)}]
//
// `c` binds field pointers against the node's Content JSON, `n` against the whole node (Name,
// Description, … plus a nested content/… path). The values live on the NODE STREAM, not in the
// area tree — so the ordinary pointer resolver (which indexes into the area tree) finds nothing
// and every such control renders EMPTY. That is what this module fixes: `area/context` branches
// here, so every form control — and the click-to-edit LabelControls the node property editor is
// built from — inherits node binding exactly as Blazor's DataBind/UpdatePointer seams do.
//
// Everything here is pure and platform-free (no atob: Hermes has none). The live half — subscribing
// to the node stream and writing fields back — is `live/nodeBinding`.

import type { Json } from "./types.js";

export const MESH_NODE_PREFIX = "$meshNode";
const CONTENT_TARGET = "c";
const FIELDS_TARGET = "n";

export interface MeshNodeDataContext {
  /** The node whose stream backs this context. */
  nodePath: string;
  /** Bind field pointers against the node's Content (`c`) or the whole node (`n`). */
  bindContent: boolean;
  /** Optional content sub-path every field pointer nests under (e.g. "composer"). */
  subPath?: string;
}

const B64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/** Base64url → UTF-8 string. Hand-rolled because Hermes has no atob and RN has no Buffer. */
function base64UrlDecode(input: string): string {
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const ch of input) {
    const index = B64_ALPHABET.indexOf(ch);
    if (index < 0) {
      if (ch === "=") continue;
      throw new Error(`not base64url: ${input}`);
    }
    buffer = (buffer << 6) | index;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  // UTF-8 decode (node paths are usually ASCII, but a partition may be named in any script).
  let out = "";
  for (let i = 0; i < bytes.length; ) {
    const b = bytes[i];
    if (b < 0x80) { out += String.fromCharCode(b); i += 1; }
    else if (b < 0xe0) { out += String.fromCharCode(((b & 0x1f) << 6) | (bytes[i + 1] & 0x3f)); i += 2; }
    else if (b < 0xf0) { out += String.fromCharCode(((b & 0x0f) << 12) | ((bytes[i + 1] & 0x3f) << 6) | (bytes[i + 2] & 0x3f)); i += 3; }
    else {
      const cp = ((b & 0x07) << 18) | ((bytes[i + 1] & 0x3f) << 12) | ((bytes[i + 2] & 0x3f) << 6) | (bytes[i + 3] & 0x3f);
      out += String.fromCodePoint(cp);
      i += 4;
    }
  }
  return out;
}

/**
 * Decode a node-bound DataContext; null for an ordinary `/data/{id}` context, so the binding hot
 * path branches cheaply. Mirrors LayoutAreaReference.TryParseMeshNodeDataContext, including its
 * "unknown target token defaults to content" rule.
 */
export function parseMeshNodeDataContext(dataContext?: string): MeshNodeDataContext | null {
  if (!dataContext) return null;
  const parts = (dataContext.startsWith("/") ? dataContext.slice(1) : dataContext).split("/");
  if (parts.length < 3 || parts.length > 4 || parts[0] !== MESH_NODE_PREFIX) return null;
  try {
    return {
      nodePath: base64UrlDecode(parts[1]),
      bindContent: parts[2] !== FIELDS_TARGET,
      subPath: parts.length === 4 ? base64UrlDecode(parts[3]) : undefined,
    };
  } catch {
    return null; // a malformed context is not node-bound — fall back to the area tree
  }
}

/**
 * True when this control property reads from the node rather than the area tree: a node-bound
 * context AND a RELATIVE pointer. An absolute (`/…`) pointer is a layout-area path and is never
 * node-bound — the same condition the server's IsNodeBound applies.
 */
export function isNodeBoundPointer(dataContext: string | undefined, pointer: string): boolean {
  return !pointer.startsWith("/") && parseMeshNodeDataContext(dataContext) !== null;
}

/** Join the context's sub-path with a field pointer, as the server's Combine does. */
export function combineFieldPointer(subPath: string | undefined, pointer: string): string {
  const field = pointer.replace(/^\/+/, "");
  return subPath ? `${subPath}/${field}` : field;
}

/**
 * The field pointer an absolute, node-bound pointer addresses — i.e. what `toAbsolute` appended to
 * the DataContext. Returns null when `pointer` does not sit under `dataContext`.
 */
export function fieldPointerOf(dataContext: string, pointer: string): string | null {
  if (pointer === dataContext) return null;
  return pointer.startsWith(`${dataContext}/`) ? pointer.slice(dataContext.length + 1) : null;
}

function splitPointer(pointer: string): string[] {
  return pointer.split("/").filter((s) => s.length > 0);
}

function getCaseInsensitive(obj: Record<string, unknown>, key: string): unknown {
  if (Object.prototype.hasOwnProperty.call(obj, key)) return obj[key];
  const lower = key.toLowerCase();
  for (const k of Object.keys(obj)) if (k.toLowerCase() === lower) return obj[k];
  return undefined;
}

function existingKey(obj: Record<string, unknown>, key: string): string {
  if (Object.prototype.hasOwnProperty.call(obj, key)) return key;
  const lower = key.toLowerCase();
  for (const k of Object.keys(obj)) if (k.toLowerCase() === lower) return k;
  return key;
}

/** The object a node-bound field pointer resolves against: the node's content, or the node itself. */
function bindingRoot(node: Record<string, unknown> | undefined, bindContent: boolean): Record<string, unknown> | undefined {
  if (!node) return undefined;
  if (!bindContent) return node;
  const content = node.content;
  return content != null && typeof content === "object" && !Array.isArray(content)
    ? (content as Record<string, unknown>)
    : undefined;
}

/**
 * Read the field at `pointer` off a node snapshot. Case-insensitive at every segment, so a
 * PascalCase metadata pointer (`Name`) and a camelCase content pointer (`fullName`) both bind
 * without the caller knowing the JSON casing — the same rule the server applies.
 */
export function resolveNodeField(
  node: Record<string, unknown> | undefined,
  ctx: MeshNodeDataContext,
  pointer: string,
): Json {
  let current: unknown = bindingRoot(node, ctx.bindContent);
  const segments = splitPointer(combineFieldPointer(ctx.subPath, pointer));
  if (segments.length === 0) return undefined;
  for (const segment of segments) {
    if (current == null || typeof current !== "object" || Array.isArray(current)) return undefined;
    current = getCaseInsensitive(current as Record<string, unknown>, segment);
  }
  return current as Json;
}

/**
 * The RFC 7396 merge-patch body that writes `value` into `pointer` — the fields object
 * `MeshOps.patch(nodePath, fields)` merges at the node root. Only the edited field is touched;
 * intermediate objects are created, and an existing key is matched case-insensitively so the patch
 * lands on the SAME key the node already uses rather than adding a casing-variant duplicate.
 */
export function nodeFieldPatch(
  node: Record<string, unknown> | undefined,
  ctx: MeshNodeDataContext,
  pointer: string,
  value: Json,
): Record<string, unknown> {
  const segments = splitPointer(combineFieldPointer(ctx.subPath, pointer));
  if (segments.length === 0) return {};
  let existing = bindingRoot(node, ctx.bindContent);
  const build = (index: number): Record<string, unknown> => {
    const key = existing ? existingKey(existing, segments[index]) : segments[index];
    if (index === segments.length - 1) return { [key]: value as unknown };
    const next = existing ? getCaseInsensitive(existing, segments[index]) : undefined;
    existing = next != null && typeof next === "object" && !Array.isArray(next) ? (next as Record<string, unknown>) : undefined;
    return { [key]: build(index + 1) };
  };
  const fields = build(0);
  return ctx.bindContent ? { content: fields } : fields;
}
