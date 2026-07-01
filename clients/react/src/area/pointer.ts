// RFC 6901 JSON-pointer resolution + RFC 7396 merge-patch, and the binding resolver that turns a
// control property (literal OR a JsonPointerReference into /data) into its value.

import type { AreaTree, Json } from "./types.js";

function unescape(p: string): string {
  return p.replace(/~1/g, "/").replace(/~0/g, "~");
}
function escape(p: string): string {
  return p.replace(/~/g, "~0").replace(/\//g, "~1");
}

export function getPointer(root: Json, pointer: string): Json {
  if (!pointer || pointer === "/" || pointer === "#") return root;
  const parts = pointer.replace(/^#/, "").split("/").slice(1).map(unescape);
  let cur: Json = root;
  for (const part of parts) {
    if (cur == null) return undefined;
    cur = Array.isArray(cur) ? cur[Number(part)] : cur[part];
  }
  return cur;
}

/** Immutably set the value at a JSON pointer, creating intermediate objects as needed. */
export function setPointer(root: Json, pointer: string, value: Json): Json {
  const parts = pointer.replace(/^#/, "").split("/").slice(1).map(unescape);
  if (parts.length === 0) return value;
  const clone = Array.isArray(root) ? [...root] : { ...(root ?? {}) };
  let cur: Json = clone;
  for (let i = 0; i < parts.length - 1; i++) {
    const key = parts[i];
    const next = cur[key];
    cur[key] = Array.isArray(next) ? [...next] : { ...(next ?? {}) };
    cur = cur[key];
  }
  cur[parts[parts.length - 1]] = value;
  return clone;
}

/** RFC 7396 JSON merge-patch. Kept for the static/demo source; the LIVE layout wire uses RFC 6902
 *  (see applyJsonPatch) — MeshWeaver's DataChangedEvent.Change is a JsonPatch, not a merge-patch. */
export function mergePatch(target: Json, patch: Json): Json {
  if (patch === null || typeof patch !== "object" || Array.isArray(patch)) return patch;
  const out: Json = target && typeof target === "object" && !Array.isArray(target) ? { ...target } : {};
  for (const [k, v] of Object.entries(patch)) {
    if (v === null) delete out[k];
    else out[k] = mergePatch(out[k], v);
  }
  return out;
}

/** One RFC 6902 operation. Layout deltas use add / replace / remove (and occasionally move / copy). */
export interface JsonPatchOp {
  op: "add" | "remove" | "replace" | "move" | "copy" | "test";
  path: string;
  from?: string;
  value?: Json;
}

/**
 * RFC 6902 JSON-Patch — how layout-area deltas ACTUALLY arrive: MeshWeaver serializes
 * `DataChangedEvent.Change` (ChangeType.Patch) as a `JsonPatch` (an array of op/path/value objects),
 * not an RFC 7396 merge-patch. Applied immutably; array `add` inserts (and `/-` appends), `replace`
 * overwrites, `remove` splices — the semantics JsonPatch.Net emits when diffing two layout snapshots.
 */
export function applyJsonPatch(doc: Json, ops: JsonPatchOp[]): Json {
  let out = doc;
  for (const op of ops ?? []) {
    switch (op.op) {
      case "add":
      case "replace":
        out = writeAt(out, op.path, op.op, op.value);
        break;
      case "remove":
        out = writeAt(out, op.path, "remove", undefined);
        break;
      case "move":
        out = writeAt(writeAt(out, op.from!, "remove", undefined), op.path, "add", getPointer(doc, op.from!));
        break;
      case "copy":
        out = writeAt(out, op.path, "add", getPointer(out, op.from!));
        break;
      case "test":
        break; // fold is trusted (server-generated); nothing to assert
    }
  }
  return out;
}

// Immutably apply add / replace / remove at `path`, cloning only the spine down to the target's parent.
function writeAt(root: Json, path: string, kind: "add" | "replace" | "remove", value: Json): Json {
  const parts = path.replace(/^#/, "").split("/").slice(1).map(unescape);
  if (parts.length === 0) return kind === "remove" ? undefined : value;
  const clone = Array.isArray(root) ? [...root] : { ...(root ?? {}) };
  let cur: Json = clone;
  for (let i = 0; i < parts.length - 1; i++) {
    const key = Array.isArray(cur) ? Number(parts[i]) : parts[i];
    const next = cur[key];
    cur[key] = Array.isArray(next) ? [...next] : { ...(next ?? {}) };
    cur = cur[key];
  }
  const last = parts[parts.length - 1];
  if (Array.isArray(cur)) {
    const idx = last === "-" ? cur.length : Number(last);
    if (kind === "add") cur.splice(idx, 0, value);
    else if (kind === "replace") cur[idx] = value;
    else cur.splice(idx, 1);
  } else if (kind === "remove") {
    delete cur[last];
  } else {
    cur[last] = value;
  }
  return clone;
}

export function isBinding(v: Json): v is { $type?: string; pointer: string } {
  return (
    v != null &&
    typeof v === "object" &&
    typeof v.pointer === "string" &&
    /pointer|binding/i.test(String(v.$type ?? "pointer"))
  );
}

/** Resolve a control property: a JsonPointerReference reads from the tree; anything else is literal. */
export function resolve(root: AreaTree, value: Json, dataContext?: string): Json {
  if (isBinding(value)) return getPointer(root, toAbsolute(value.pointer, dataContext));
  return value;
}

/** The absolute pointer a binding writes back to (used by form edits → UpdatePointer). */
export function bindingPointer(value: Json, dataContext?: string): string | undefined {
  return isBinding(value) ? toAbsolute(value.pointer, dataContext) : undefined;
}

function toAbsolute(pointer: string, dataContext?: string): string {
  if (pointer.startsWith("/")) return pointer;
  const base = dataContext && dataContext.startsWith("/") ? dataContext : "";
  return `${base}/${pointer}`;
}

export { escape as escapePointerSegment };
