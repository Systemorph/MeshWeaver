// Wire folding for the mesh's EntityStore snapshots — shared by the live gRPC source (folds every
// Full frame) and by SSR consumers that fetch one Full frame over REST (POST /api/mesh/render-area)
// and seed a StaticAreaSource with it.
//
// WIRE (pinned against the server source, src/MeshWeaver.Data*/):
//   - EntityStore collections serialize as InstanceCollections: instance keys are JSON-ENCODED
//     property names (`"Content"` arrives as the property `"\"Content\""`) plus a leading `$type`
//     collection marker (InstanceCollectionConverter). Area lookups use PLAIN names
//     (NamedArea.area → areas["Content"]), so keys are decoded at fold time; control/data VALUES
//     stay wire-faithful — their internal binding pointers keep the encoding and decode at
//     resolution time (pointer.ts decodePointerSegment).

import type { AreaTree, Json } from "../area/types.js";

/**
 * Fold a wire EntityStore snapshot into the renderer's {areas,data} tree: decode the JSON-encoded
 * instance keys of each collection and drop the `$type` collection markers, so lookups
 * (areas["Content"], decoded binding pointers) see plain keys. Control/data VALUES stay
 * wire-faithful — their internal binding pointers keep the encoding and decode at resolution.
 */
export function normalizeEntityStore(store: Json): AreaTree {
  if (store == null || typeof store !== "object" || Array.isArray(store)) return (store ?? {}) as AreaTree;
  const out: Record<string, Json> = {};
  for (const [collection, value] of Object.entries(store as Record<string, Json>)) {
    if (collection === "$type") continue;
    out[collection] = normalizeCollection(value);
  }
  return out;
}

/** Decode one InstanceCollection: JSON-encoded instance keys → plain, `$type` marker dropped. */
export function normalizeCollection(value: Json): Json {
  if (value == null || typeof value !== "object" || Array.isArray(value)) return value;
  const instances: Record<string, Json> = {};
  for (const [key, item] of Object.entries(value as Record<string, Json>)) {
    if (key === "$type") continue; // the collection-name marker, not an instance
    instances[decodeWireKey(key)] = item;
  }
  return instances;
}

/** InstanceCollection keys arrive JSON-encoded (`"home"` → property `"\"home\""`); decode strings. */
export function decodeWireKey(key: string): string {
  if (key.length >= 2 && key.startsWith('"') && key.endsWith('"')) {
    try {
      const parsed: unknown = JSON.parse(key);
      if (typeof parsed === "string") return parsed;
    } catch {
      /* not JSON-encoded — keep the raw key */
    }
  }
  return key;
}
