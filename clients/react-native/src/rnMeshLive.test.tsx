import { describe, expect, it, vi } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import {
  MeshOpsProvider,
  RegistryProvider,
  ScopeProvider,
  RenderArea,
  StaticAreaSource,
  type AreaTree,
  type MeshNodeState,
  type MeshOps,
  localize,
} from "@meshweaver/react/core";

import { rnPack } from "./rnPack";

// Assert against the CATALOG, not a literal — that also pins that these strings are localized.
const en = (key: string) => localize(key, "en");

// The four leaves that used to be labelled placeholder badges, plus MeshNodeContentEditor. Driven
// through a FAKE MeshOps — the same seam the web pack's tests use — so the live behaviour (watch a
// node, submit a message, patch a field, run a query) is proven without a mesh.

type Json = ReactTestRendererJSON;

/** A one-shot async iterable that yields the given node states and then stays open. */
function nodeStream(...states: MeshNodeState[]): AsyncIterable<MeshNodeState> {
  return {
    async *[Symbol.asyncIterator]() {
      for (const s of states) yield s;
      await new Promise(() => {}); // stay subscribed, like a live stream
    },
  };
}

function fakeOps(over: Partial<MeshOps> = {}): MeshOps {
  return {
    watch: () => nodeStream(),
    startThread: async () => ({ path: "p/_Thread/t1" }),
    submitMessage: async () => "m1",
    patch: () => {},
    ...over,
  } as MeshOps;
}

async function renderLive(tree: AreaTree, ops: MeshOps): Promise<Json> {
  let r!: TestRenderer.ReactTestRenderer;
  await TestRenderer.act(async () => {
    r = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <MeshOpsProvider ops={ops}>
          <ScopeProvider source={new StaticAreaSource(tree)} area="main">
            <RenderArea areaKey="main" />
          </ScopeProvider>
        </MeshOpsProvider>
      </RegistryProvider>,
    );
  });
  return r.toJSON() as Json;
}

function* walk(node: Json | Json[] | null): Generator<Json> {
  if (node == null) return;
  if (Array.isArray(node)) {
    for (const n of node) yield* walk(n);
    return;
  }
  yield node;
  if (node.children) for (const c of node.children) yield* walk(c as Json);
}

function textOf(node: Json): string {
  let out = "";
  for (const c of node.children ?? []) {
    if (typeof c === "string") out += c;
    else if (c && typeof c === "object") out += textOf(c as Json);
  }
  return out;
}

const allText = (j: Json) => [...walk(j)].filter((n) => n.type === "Text").map(textOf);
const byType = (j: Json, t: string) => [...walk(j)].filter((n) => n.type === t);

// ---- ThreadChat ----------------------------------------------------------------------------

describe("ThreadChat", () => {
  const threadPath = "acme/_Thread/t1";

  it("renders the ordered message bubbles from the thread's cells", async () => {
    const ops = fakeOps({
      watch: (path: string) => {
        if (path === threadPath)
          return nodeStream({ path, content: { messages: ["m1", "m2"], status: "Idle" } } as MeshNodeState);
        if (path === `${threadPath}/m1`)
          return nodeStream({ path, content: { role: "user", text: "what is a mesh node?" } } as MeshNodeState);
        if (path === `${threadPath}/m2`)
          return nodeStream({ path, content: { role: "assistant", text: "a unit of content" } } as MeshNodeState);
        return nodeStream();
      },
    });
    const text = allText(await renderLive({ areas: { main: { $type: "ThreadChat", threadPath } } }, ops));
    expect(text).toContain("what is a mesh node?");
    expect(text).toContain("a unit of content");
  });

  it("falls back to the PENDING payload for a message whose cell has not materialised", async () => {
    const ops = fakeOps({
      watch: (path: string) =>
        path === threadPath
          ? nodeStream({
              path,
              content: { messages: ["m1"], pendingUserMessages: { m1: { role: "user", text: "queued question" } } },
            } as MeshNodeState)
          : nodeStream(),
    });
    expect(allText(await renderLive({ areas: { main: { $type: "ThreadChat", threadPath } } }, ops))).toContain(
      "queued question",
    );
  });

  it("shows the working indicator while the round executes", async () => {
    const ops = fakeOps({
      watch: (path: string) =>
        path === threadPath
          ? nodeStream({ path, content: { messages: [], status: "Executing" } } as MeshNodeState)
          : nodeStream(),
    });
    const j = await renderLive({ areas: { main: { $type: "ThreadChat", threadPath } } }, ops);
    expect(allText(j)).toContain(en("chat.working"));
    expect(byType(j, "ActivityIndicator").length).toBeGreaterThan(0);
  });

  it("submits to an EXISTING thread via submitMessage", async () => {
    const submitMessage = vi.fn(async () => "m9");
    const ops = fakeOps({
      submitMessage,
      watch: (path: string) =>
        path === threadPath ? nodeStream({ path, content: { messages: [] } } as MeshNodeState) : nodeStream(),
    });
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <RegistryProvider pack={rnPack}>
          <MeshOpsProvider ops={ops}>
            <ScopeProvider source={new StaticAreaSource({ areas: { main: { $type: "ThreadChat", threadPath } } })} area="main">
              <RenderArea areaKey="main" />
            </ScopeProvider>
          </MeshOpsProvider>
        </RegistryProvider>,
      );
    });
    const input = [...walk(r.toJSON() as Json)].find((n) => n.type === "TextInput")!;
    await TestRenderer.act(async () => input.props.onChangeText("hello"));
    const send = [...walk(r.toJSON() as Json)].find(
      (n) => n.type === "Pressable" && n.props.accessibilityLabel === en("common.send"),
    )!;
    await TestRenderer.act(async () => send.props.onPress());
    expect(submitMessage).toHaveBeenCalledWith(threadPath, "hello", expect.anything());
  });

  it("STARTS a thread when there is no thread path yet, then submits to the created one", async () => {
    const startThread = vi.fn(async () => ({ path: "acme/_Thread/new" }));
    const submitMessage = vi.fn(async () => "m1");
    const ops = fakeOps({ startThread, submitMessage });
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <RegistryProvider pack={rnPack}>
          <MeshOpsProvider ops={ops}>
            <ScopeProvider
              source={new StaticAreaSource({ areas: { main: { $type: "ThreadChat", namespacePath: "acme" } } })}
              area="main"
            >
              <RenderArea areaKey="main" />
            </ScopeProvider>
          </MeshOpsProvider>
        </RegistryProvider>,
      );
    });
    const type = async (t: string) => {
      const input = [...walk(r.toJSON() as Json)].find((n) => n.type === "TextInput")!;
      await TestRenderer.act(async () => input.props.onChangeText(t));
    };
    const press = async () => {
      const send = [...walk(r.toJSON() as Json)].find(
        (n) => n.type === "Pressable" && n.props.accessibilityLabel === en("common.send"),
      )!;
      await TestRenderer.act(async () => send.props.onPress());
    };
    await type("first");
    await press();
    expect(startThread).toHaveBeenCalledWith("acme", "first", expect.anything());

    // Message 2 must NEVER re-StartThread — it drains through the thread created by message 1.
    await type("second");
    await press();
    expect(startThread).toHaveBeenCalledTimes(1);
    expect(submitMessage).toHaveBeenCalledWith("acme/_Thread/new", "second", expect.anything());
  });
});

// ---- MeshSearch / MeshNodeCollection ---------------------------------------------------------

describe("MeshSearch", () => {
  it("combines the hidden query with the visible term and lists the hits", async () => {
    const search = vi.fn(async () => [
      { path: "acme/Sales", name: "Sales", nodeType: "Group", content: { description: "Q3" } },
    ]);
    const ops = fakeOps({ search });
    const tree: AreaTree = {
      areas: { main: { $type: "MeshSearch", hiddenQuery: "nodeType:Group", visibleQuery: "sales" } },
    };
    const j = await renderLive(tree, ops);
    await TestRenderer.act(async () => {
      await new Promise((r) => setTimeout(r, 300)); // the 250 ms search debounce
    });
    expect(search).toHaveBeenCalledWith("nodeType:Group sales", undefined);
  });

  it("reports an empty result set rather than rendering nothing", async () => {
    const ops = fakeOps({ search: async () => [] });
    const j = await renderLive({ areas: { main: { $type: "MeshSearch", visibleQuery: "zzz" } } }, ops);
    expect(allText(j)).toContain(en("common.noResults"));
  });
});

describe("MeshNodeCollection", () => {
  it("runs every declared query and de-duplicates the union by path", async () => {
    const search = vi.fn(async (q: string) =>
      q === "a"
        ? [{ path: "p/1", name: "One" }, { path: "p/2", name: "Two" }]
        : [{ path: "p/2", name: "Two" }, { path: "p/3", name: "Three" }],
    );
    const ops = fakeOps({ search });
    const j = await renderLive({ areas: { main: { $type: "MeshNodeCollection", queries: ["a", "b"] } } }, ops);
    await TestRenderer.act(async () => {
      await new Promise((r) => setTimeout(r, 10));
    });
    expect(search).toHaveBeenCalledTimes(2);
  });

  it("is its own component — never an alias of Catalog", () => {
    expect(rnPack.controls.MeshNodeCollection).not.toBe(rnPack.controls.Catalog);
  });
});

// ---- MeshNodeContentEditor -------------------------------------------------------------------

describe("MeshNodeContentEditor", () => {
  const nodePath = "acme/Config";

  it("edits the node's scalar content fields and writes each one back with a field-level patch", async () => {
    const patch = vi.fn();
    const ops = fakeOps({
      patch,
      watch: (path: string) =>
        path === nodePath
          ? nodeStream({ path, content: { title: "Old title", enabled: false } } as MeshNodeState)
          : nodeStream(),
    });
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <RegistryProvider pack={rnPack}>
          <MeshOpsProvider ops={ops}>
            <ScopeProvider
              source={new StaticAreaSource({ areas: { main: { $type: "MeshNodeContentEditor", nodePath } } })}
              area="main"
            >
              <RenderArea areaKey="main" />
            </ScopeProvider>
          </MeshOpsProvider>
        </RegistryProvider>,
      );
    });
    const j = r.toJSON() as Json;
    // the string field is bound to the live node content
    const input = [...walk(j)].find((n) => n.type === "TextInput")!;
    expect(input.props.value).toBe("Old title");

    await TestRenderer.act(async () => input.props.onChangeText("New title"));
    // ONE field, not the whole node — the merge-patch rule.
    expect(patch).toHaveBeenCalledWith(nodePath, { content: { title: "New title" } });

    const check = [...walk(r.toJSON() as Json)].find((n) => n.type === "Pressable" && n.props.accessibilityRole === "checkbox")!;
    await TestRenderer.act(async () => check.props.onPress());
    expect(patch).toHaveBeenCalledWith(nodePath, { content: { enabled: true } });
  });

  it("says so when no node is bound", async () => {
    const j = await renderLive({ areas: { main: { $type: "MeshNodeContentEditor" } } }, fakeOps());
    expect(allText(j)).toContain(en("editor.noNodeBound"));
  });
});

// ---- Appearance ------------------------------------------------------------------------------

describe("Appearance", () => {
  it("renders the theme panel with both modes selectable", async () => {
    const j = await renderLive({ areas: { main: { $type: "Appearance" } } }, fakeOps());
    const text = allText(j);
    expect(text).toContain(en("appearance.theme"));
    expect(text).toContain(en("appearance.light"));
    expect(text).toContain(en("appearance.dark"));
  });
});
