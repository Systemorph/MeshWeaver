// Pins the nested-`@@`-embed stability contract: createGrpcEmbeddedFactory must return a STABLE,
// CACHING factory. The bug it fixes — an inline factory recreated each render — re-subscribed every
// embed on every re-render (AddressAreaEmbed's effect depends on the factory identity), so doc-page
// `@@` regions rendered non-deterministically ("different sections each refresh"). Caching by
// (address, area, id) guarantees the same embed returns the SAME started source.

import { describe, expect, it, vi } from "vitest";
import { createGrpcEmbeddedFactory } from "./grpcSource.js";
import type { MeshConnectionLike } from "./grpcSource.js";

function fakeConnection(): MeshConnectionLike & { watch: ReturnType<typeof vi.fn> } {
  const watch = vi.fn(async function* () {
    // no frames — the source starts (proving watch was called) and idles
  });
  return { watch, post: vi.fn() };
}

describe("createGrpcEmbeddedFactory — stable, caching embed factory", () => {
  it("returns the SAME source for the same (address, area, id) — one subscription per embed", () => {
    const conn = fakeConnection();
    const factory = createGrpcEmbeddedFactory(conn);
    const a = factory("acme/Doc", { area: "Overview" });
    const b = factory("acme/Doc", { area: "Overview" });
    expect(a.source).toBe(b.source);
    expect(a.rootArea).toBe("Overview");
    // Started exactly once (one gRPC-web watch), not re-subscribed on the repeat call.
    expect(conn.watch).toHaveBeenCalledTimes(1);
  });

  it("returns DISTINCT sources for different embeds", () => {
    const conn = fakeConnection();
    const factory = createGrpcEmbeddedFactory(conn);
    const overview = factory("acme/Doc", { area: "Overview" });
    const data = factory("acme/Doc", { area: "Data" });
    const otherNode = factory("acme/Other", { area: "Overview" });
    expect(overview.source).not.toBe(data.source);
    expect(overview.source).not.toBe(otherNode.source);
    expect(conn.watch).toHaveBeenCalledTimes(3);
  });

  it("keys on the id too (item-area cards) and roots at the default area for an empty ref", () => {
    const conn = fakeConnection();
    const factory = createGrpcEmbeddedFactory(conn);
    const first = factory("acme/Pin", { area: "PinnedThumbnail", id: "1" });
    const second = factory("acme/Pin", { area: "PinnedThumbnail", id: "2" });
    expect(first.source).not.toBe(second.source);
    const def = factory("acme/Node", {});
    expect(def.rootArea).toBe("");
  });
});
