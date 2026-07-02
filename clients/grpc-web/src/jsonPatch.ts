// Minimal RFC 6902 JSON-patch application — the shape the mesh's sync streams deliver for
// ChangeType.Patch (JsonSynchronizationStream.ToJsonPatch): an ARRAY of {op, path, value, from}.
// Node streams (MeshNodeReference) patch at the ROOT of the node JSON (CreateSingleObjectPatch),
// so segments are plain property names / array indices (RFC 6901-escaped).

export type Json = unknown;

export interface PatchOperation {
  op: string;
  path: string;
  value?: Json;
  from?: string;
}

function unescapeSegment(seg: string): string {
  return seg.replace(/~1/g, "/").replace(/~0/g, "~");
}

function splitPointer(pointer: string): string[] {
  if (!pointer) return [];
  return pointer.split("/").slice(1).map(unescapeSegment);
}

function getAt(root: Json, parts: string[]): Json {
  let cur: Json = root;
  for (const part of parts) {
    if (cur == null || typeof cur !== "object") return undefined;
    cur = Array.isArray(cur) ? cur[Number(part)] : (cur as Record<string, Json>)[part];
  }
  return cur;
}

/** Immutable set — RFC 6902 semantics: on arrays, `add` INSERTS at the index ("-" appends). */
function setAt(root: Json, parts: string[], value: Json, insert: boolean): Json {
  if (parts.length === 0) return value;
  const [head, ...rest] = parts;
  if (Array.isArray(root)) {
    const clone = [...root];
    const idx = head === "-" ? clone.length : Number(head);
    if (rest.length === 0) {
      if (insert) clone.splice(idx, 0, value);
      else clone[idx] = value;
    } else {
      clone[idx] = setAt(clone[idx], rest, value, insert);
    }
    return clone;
  }
  const clone: Record<string, Json> = { ...((root ?? {}) as Record<string, Json>) };
  clone[head] = rest.length === 0 ? value : setAt(clone[head], rest, value, insert);
  return clone;
}

/** Immutable remove — splices arrays, deletes object properties; missing paths are tolerated. */
function removeAt(root: Json, parts: string[]): Json {
  if (parts.length === 0) return {};
  if (root == null || typeof root !== "object") return root;
  const [head, ...rest] = parts;
  if (Array.isArray(root)) {
    const clone = [...root];
    const idx = Number(head);
    if (rest.length === 0) clone.splice(idx, 1);
    else clone[idx] = removeAt(clone[idx], rest);
    return clone;
  }
  const clone: Record<string, Json> = { ...(root as Record<string, Json>) };
  if (rest.length === 0) delete clone[head];
  else clone[head] = removeAt(clone[head], rest);
  return clone;
}

/** Apply an RFC 6902 patch array immutably ("test" and unknown ops are folding no-ops). */
export function applyJsonPatch(state: Json, ops: PatchOperation[]): Json {
  let next: Json = state;
  for (const op of ops) {
    const parts = splitPointer(op.path);
    switch (op.op) {
      case "add":
      case "replace":
        next = setAt(next, parts, op.value, op.op === "add");
        break;
      case "remove":
        next = removeAt(next, parts);
        break;
      case "move": {
        const from = splitPointer(op.from ?? "");
        const moved = getAt(next, from);
        next = removeAt(next, from);
        next = setAt(next, parts, moved, true);
        break;
      }
      case "copy":
        next = setAt(next, parts, getAt(next, splitPointer(op.from ?? "")), true);
        break;
      default:
        break;
    }
  }
  return next;
}
