import { describe, expect, it } from "vitest";
import {
  combineFieldPointer,
  fieldPointerOf,
  isNodeBoundPointer,
  nodeFieldPatch,
  parseMeshNodeDataContext,
  resolveNodeField,
} from "./meshNodeBinding.js";

// The wire contract is the SERVER's: LayoutAreaReference.GetMeshNodeDataContext produces these
// strings and MeshNodeBindingExtensions resolves them. These cases pin the client twin against
// contexts taken off the wire (`rbuergi` → `cmJ1ZXJnaQ`, base64url, unpadded).

const RBUERGI = "/$meshNode/cmJ1ZXJnaQ/c";                 // content-bound, no sub-path
const RBUERGI_NODE = "/$meshNode/cmJ1ZXJnaQ/n";            // whole-node (metadata) bound
const WITH_SUB = "/$meshNode/cmJ1ZXJnaQ/c/Y29tcG9zZXI";    // sub-path "composer"

const node = {
  path: "rbuergi",
  name: "Roland Buergi",
  description: "the node's own description",
  content: { fullName: "Roland Buergi", email: "rbuergi@systemorph.com", composer: { harness: "default" } },
};

describe("the node-bound DataContext encoding", () => {
  it("decodes the path, the target and the sub-path", () => {
    expect(parseMeshNodeDataContext(RBUERGI)).toEqual({ nodePath: "rbuergi", bindContent: true, subPath: undefined });
    expect(parseMeshNodeDataContext(RBUERGI_NODE)).toEqual({ nodePath: "rbuergi", bindContent: false, subPath: undefined });
    expect(parseMeshNodeDataContext(WITH_SUB)).toEqual({ nodePath: "rbuergi", bindContent: true, subPath: "composer" });
  });

  it("is null for an ordinary /data context, so the hot path branches cheaply", () => {
    expect(parseMeshNodeDataContext("/data/user-form")).toBeNull();
    expect(parseMeshNodeDataContext(undefined)).toBeNull();
    expect(parseMeshNodeDataContext("/$meshNode/cmJ1ZXJnaQ")).toBeNull(); // too few segments
  });

  it("an ABSOLUTE pointer is a layout-area path and is never node-bound", () => {
    expect(isNodeBoundPointer(RBUERGI, "fullName")).toBe(true);
    expect(isNodeBoundPointer(RBUERGI, "/data/editState_x")).toBe(false);
    expect(isNodeBoundPointer("/data/x", "fullName")).toBe(false);
  });

  it("recovers the field a control's absolute pointer addressed", () => {
    expect(fieldPointerOf(RBUERGI, `${RBUERGI}/fullName`)).toBe("fullName");
    expect(fieldPointerOf(RBUERGI, "/data/other")).toBeNull();
    expect(combineFieldPointer("composer", "harness")).toBe("composer/harness");
  });
});

describe("reading a field off the node", () => {
  it("binds content fields (`c`) and node fields (`n`) to their own roots", () => {
    expect(resolveNodeField(node, parseMeshNodeDataContext(RBUERGI)!, "fullName")).toBe("Roland Buergi");
    expect(resolveNodeField(node, parseMeshNodeDataContext(RBUERGI_NODE)!, "description")).toBe("the node's own description");
  });

  it("is case-insensitive — PascalCase DTO pointers and camelCase content pointers both bind", () => {
    expect(resolveNodeField(node, parseMeshNodeDataContext(RBUERGI)!, "FullName")).toBe("Roland Buergi");
    expect(resolveNodeField(node, parseMeshNodeDataContext(RBUERGI_NODE)!, "Description")).toBe("the node's own description");
  });

  it("nests under the context's sub-path", () => {
    expect(resolveNodeField(node, parseMeshNodeDataContext(WITH_SUB)!, "harness")).toBe("default");
  });

  it("an absent field, node or content is undefined — never a throw", () => {
    expect(resolveNodeField(node, parseMeshNodeDataContext(RBUERGI)!, "nope")).toBeUndefined();
    expect(resolveNodeField(undefined, parseMeshNodeDataContext(RBUERGI)!, "fullName")).toBeUndefined();
    expect(resolveNodeField({ path: "x" }, parseMeshNodeDataContext(RBUERGI)!, "fullName")).toBeUndefined();
  });
});

describe("writing a field back", () => {
  it("touches ONLY the edited field, under content for a `c` context", () => {
    expect(nodeFieldPatch(node, parseMeshNodeDataContext(RBUERGI)!, "fullName", "Ada")).toEqual({ content: { fullName: "Ada" } });
  });

  it("patches node fields at the root for an `n` context", () => {
    expect(nodeFieldPatch(node, parseMeshNodeDataContext(RBUERGI_NODE)!, "description", "d")).toEqual({ description: "d" });
  });

  it("nests through the sub-path", () => {
    expect(nodeFieldPatch(node, parseMeshNodeDataContext(WITH_SUB)!, "harness", "x"))
      .toEqual({ content: { composer: { harness: "x" } } });
  });

  // 🚨 The patch must land on the key the node ALREADY uses. Adding a casing variant would leave
  // the node holding both `fullName` and `FullName`, and the reader would keep seeing the old one.
  it("matches an existing key case-insensitively rather than adding a variant", () => {
    expect(nodeFieldPatch(node, parseMeshNodeDataContext(RBUERGI)!, "FullName", "Ada")).toEqual({ content: { fullName: "Ada" } });
  });

  it("creates the intermediate object when the sub-path is not there yet", () => {
    expect(nodeFieldPatch({ path: "x", content: {} }, parseMeshNodeDataContext(WITH_SUB)!, "harness", "x"))
      .toEqual({ content: { composer: { harness: "x" } } });
  });
});

describe("RFC 6901 escaped segments — the server's SplitPointer unescapes, so must we", () => {
  const escNode = { path: "x", content: { "a/b": { "c~d": "v" } } };
  const ctx = parseMeshNodeDataContext(RBUERGI)!;

  it("reads a field whose name contains / (~1) or ~ (~0)", () => {
    expect(resolveNodeField(escNode, ctx, "a~1b/c~0d")).toBe("v");
  });

  it("patches through escaped segments onto the real keys", () => {
    expect(nodeFieldPatch(escNode, ctx, "a~1b/c~0d", "w")).toEqual({ content: { "a/b": { "c~d": "w" } } });
  });
});
