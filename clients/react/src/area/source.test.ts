import { describe, it, expect, vi } from "vitest";
import { StaticAreaSource } from "./source.js";

describe("StaticAreaSource", () => {
  it("optimistically applies update events and notifies subscribers", () => {
    const source = new StaticAreaSource({ data: { name: "Ada" } });
    const listener = vi.fn();
    const unsubscribe = source.subscribe(listener);

    source.emit({ kind: "update", area: "x", pointer: "/data/name", value: "Grace" });

    expect(source.getState().data?.name).toBe("Grace");
    expect(listener).toHaveBeenCalledTimes(1);
    expect(source.events).toHaveLength(1);

    unsubscribe();
    source.emit({ kind: "update", area: "x", pointer: "/data/name", value: "Z" });
    expect(listener).toHaveBeenCalledTimes(1); // no longer notified after unsubscribe
  });

  it("applyPatch merges via RFC 7396", () => {
    const source = new StaticAreaSource({ data: { a: 1, b: 2 } });
    source.applyPatch({ data: { b: null, c: 3 } });
    expect(source.getState().data).toEqual({ a: 1, c: 3 });
  });

  it("records click/blur events without mutating state", () => {
    const source = new StaticAreaSource({ areas: {} });
    const before = source.getState();
    source.emit({ kind: "click", area: "btn" });
    source.emit({ kind: "blur", area: "field" });
    expect(source.getState()).toBe(before); // unchanged reference
    expect(source.events).toMatchObject([{ kind: "click", area: "btn" }, { kind: "blur", area: "field" }]);
  });
});
