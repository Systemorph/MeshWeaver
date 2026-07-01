import { describe, it, expect } from "vitest";
import { getPointer, setPointer, mergePatch, resolve, bindingPointer } from "./pointer.js";

describe("JSON pointer (RFC 6901)", () => {
  const root = { data: { name: "Ada", nested: { x: 1 }, list: [10, 20] } };

  it("reads nested objects and array indices", () => {
    expect(getPointer(root, "/data/name")).toBe("Ada");
    expect(getPointer(root, "/data/nested/x")).toBe(1);
    expect(getPointer(root, "/data/list/1")).toBe(20);
    expect(getPointer(root, "/")).toBe(root);
    expect(getPointer(root, "/data/missing")).toBeUndefined();
  });

  it("setPointer is immutable and creates intermediate path", () => {
    const next = setPointer(root, "/data/name", "Grace");
    expect(next).not.toBe(root);
    expect(getPointer(next, "/data/name")).toBe("Grace");
    expect(getPointer(root, "/data/name")).toBe("Ada"); // original untouched
    expect(getPointer(setPointer({}, "/a/b/c", 5), "/a/b/c")).toBe(5);
  });
});

describe("mergePatch (RFC 7396)", () => {
  it("merges, replaces, and deletes (null removes a key)", () => {
    const target = { a: 1, b: { c: 2, d: 3 }, e: 4 };
    const patch = { b: { c: 20, d: null }, e: null, f: 5 };
    expect(mergePatch(target, patch)).toEqual({ a: 1, b: { c: 20 }, f: 5 });
  });
});

describe("binding resolution", () => {
  const root = { data: { name: "Ada" } };

  it("resolves a JsonPointerReference and passes literals through", () => {
    expect(resolve(root, { $type: "JsonPointerReference", pointer: "/data/name" })).toBe("Ada");
    expect(resolve(root, "hello")).toBe("hello");
    expect(resolve(root, 42)).toBe(42);
    expect(resolve(root, null)).toBe(null);
  });

  it("bindingPointer returns the write-back pointer for bindings only", () => {
    expect(bindingPointer({ $type: "JsonPointerReference", pointer: "/data/name" })).toBe("/data/name");
    expect(bindingPointer("literal")).toBeUndefined();
  });
});
