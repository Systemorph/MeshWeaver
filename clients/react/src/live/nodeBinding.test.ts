import { describe, expect, it, vi } from "vitest";
import { createStore } from "./nodeBinding.js";
import type { MeshOps } from "./meshOps.js";

// The store's lifecycle contract, pinned per the #2129 review:
//  * the LAST unsubscribe must CLOSE the watch iterator (its finally is where the transport
//    releases the server subscription — a stranded next() leaks it until the next emission);
//  * a FAILED watch must evict its entry so the next subscriber re-pumps (a cached dead entry
//    turns one transient disconnect into permanently stale controls);
//  * the optimistic merge follows RFC 7396: null DELETES a member, it is never stored.

function opsWith(watch: MeshOps["watch"], patch = vi.fn()): MeshOps {
  return { watch, patch, startThread: vi.fn(), submitMessage: vi.fn() } as unknown as MeshOps;
}

/** A hand-driven async iterable: push() feeds the pending next(); returned tracks teardown. */
function manualWatch() {
  let resolve: ((r: IteratorResult<unknown>) => void) | null = null;
  let reject: ((e: unknown) => void) | null = null;
  const state = { returned: false, pending: 0 };
  const iterable: AsyncIterable<unknown> = {
    [Symbol.asyncIterator]: () => ({
      next: () =>
        new Promise<IteratorResult<unknown>>((res, rej) => {
          state.pending++;
          resolve = res;
          reject = rej;
        }),
      return: () => {
        state.returned = true;
        return Promise.resolve({ done: true, value: undefined } as IteratorResult<unknown>);
      },
    }),
  };
  return {
    iterable,
    state,
    push: (v: unknown) => resolve?.({ done: false, value: v }),
    fail: (e: unknown) => reject?.(e),
  };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

describe("node-binding store lifecycle", () => {
  it("closes the watch iterator on the LAST unsubscribe", async () => {
    const w = manualWatch();
    const store = createStore(opsWith(() => w.iterable as AsyncIterable<never>));
    const off1 = store.subscribe("acme/Node", () => {});
    const off2 = store.subscribe("acme/Node", () => {});
    w.push({ path: "acme/Node", content: { a: 1 } });
    await tick();
    off1();
    expect(w.state.returned).toBe(false); // a reader remains — stream stays open
    off2();
    await tick();
    expect(w.state.returned).toBe(true); // last reader gone — return() ran the transport's finally
  });

  it("evicts a FAILED entry so the next subscriber re-pumps", async () => {
    let calls = 0;
    const w2 = manualWatch();
    const store = createStore(
      opsWith((path) => {
        calls++;
        if (calls === 1) return { [Symbol.asyncIterator]: () => ({ next: () => Promise.reject(new Error("blip")) }) } as AsyncIterable<never>;
        return w2.iterable as AsyncIterable<never>;
      }),
    );
    const off = store.subscribe("acme/Node", () => {});
    await tick(); // the rejection lands, the dead entry is evicted
    const notified = vi.fn();
    const off2 = store.subscribe("acme/Node", notified);
    w2.push({ path: "acme/Node", content: { a: 2 } });
    await tick();
    expect(calls).toBe(2); // a fresh watch was opened — not the cached dead entry
    expect(store.get("acme/Node")).toMatchObject({ content: { a: 2 } });
    expect(notified).toHaveBeenCalled();
    off(); off2();
  });

  it("a stale unsubscribe never tears down a NEWER entry under the same path", async () => {
    let calls = 0;
    const w2 = manualWatch();
    const store = createStore(
      opsWith(() => {
        calls++;
        if (calls === 1) return { [Symbol.asyncIterator]: () => ({ next: () => Promise.reject(new Error("blip")) }) } as AsyncIterable<never>;
        return w2.iterable as AsyncIterable<never>;
      }),
    );
    const offDead = store.subscribe("acme/Node", () => {});
    await tick(); // evicted
    store.subscribe("acme/Node", () => {}); // the LIVE second entry
    offDead(); // must not delete/close the live entry
    w2.push({ path: "acme/Node", content: { a: 3 } });
    await tick();
    expect(store.get("acme/Node")).toMatchObject({ content: { a: 3 } });
    expect(w2.state.returned).toBe(false);
  });

  it("optimistic write: null DELETES the member (RFC 7396), matching the authoritative echo", async () => {
    const w = manualWatch();
    const patch = vi.fn();
    const store = createStore(opsWith(() => w.iterable as AsyncIterable<never>, patch));
    store.subscribe("acme/Node", () => {});
    w.push({ path: "acme/Node", content: { keep: "x", drop: "y" } });
    await tick();
    store.write("acme/Node", { content: { drop: null } });
    const snap = store.get("acme/Node") as { content: Record<string, unknown> };
    expect(snap.content.keep).toBe("x");
    expect("drop" in snap.content).toBe(false); // deleted, not null
    expect(patch).toHaveBeenCalledWith("acme/Node", { content: { drop: null } }); // the wire keeps null — that IS the delete
  });
});
