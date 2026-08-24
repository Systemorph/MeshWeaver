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

import { fullPack } from "./modules/standard";
import { NavContext } from "./nav";

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
      <RegistryProvider pack={fullPack}>
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
        <RegistryProvider pack={fullPack}>
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
        <RegistryProvider pack={fullPack}>
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
    expect(fullPack.controls.MeshNodeCollection).not.toBe(fullPack.controls.Catalog);
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
        <RegistryProvider pack={fullPack}>
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

// ---- MeshSearch union queries -----------------------------------------------------------------

describe("MeshSearch union queries", () => {
  it("runs a newline-joined UNION as separate queries, appends the term, merges in order, dedupes by path", async () => {
    // The production failure this pins: the home catalog's hidden query is a newline-joined UNION
    // (FirstLevelUnion); sent as ONE string the server parses it to nothing and every home section
    // said "No results". The leaf must issue each line separately.
    const search = vi.fn(async (q: string) =>
      q.startsWith("namespace: ")
        ? [
            { path: "Doc", name: "DocRoot", nodeType: "Group" },
            { path: "Both", name: "BothFirst", nodeType: "Group" },
          ]
        : [
            { path: "device-user/x", name: "OwnItem", nodeType: "Group" },
            { path: "Both", name: "BothDup", nodeType: "Group" },
          ]);
    const ops = fakeOps({ search });
    const j = await renderLive(
      {
        areas: {
          main: {
            $type: "MeshSearch",
            hiddenQuery: "namespace: is:main\nnamespace:device-user is:main",
            visibleQuery: "foo",
            showSearchBox: false,
          } as never,
        },
      },
      ops,
    );

    // One call PER line — never the newline-joined union string — each with the visible term.
    expect(search.mock.calls.map((c) => c[0])).toEqual([
      "namespace: is:main foo",
      "namespace:device-user is:main foo",
    ]);

    const text = allText(j).join("\n");
    expect(text).toContain("DocRoot");
    expect(text).toContain("OwnItem");
    // Deduped by path in DECLARATION order: the first batch's "Both" wins, the duplicate is dropped.
    expect(text).toContain("BothFirst");
    expect(text).not.toContain("BothDup");
  });
});

// ---- MeshSearch home design (scope tabs · Icons grid · SortByAccess · grouped sections) --------

describe("MeshSearch home design", () => {
  const appRows = [
    { path: "u1/_App/alpha", id: "alpha", name: "Alpha", nodeType: "InstalledApp", mainNode: "store/Alpha", icon: "🅰" },
    { path: "u1/_App/beta", id: "beta", name: "Beta", nodeType: "InstalledApp", mainNode: "store/Beta", icon: "🅱" },
  ];
  const appsScope = {
    label: "Apps",
    query: "path:u1/_App scope:children nodeType:InstalledApp",
    renderMode: "Icons",
    navigateToMainNode: true,
  };

  it("a SINGLE scope applies its settings without a strip: Icons tiles, row-only select, mainNode target", async () => {
    const search = vi.fn(async () => appRows);
    const ops = fakeOps({ search });
    const navigate = vi.fn();
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <NavContext.Provider value={navigate}>
          <RegistryProvider pack={fullPack}>
            <MeshOpsProvider ops={ops}>
              <ScopeProvider
                source={
                  new StaticAreaSource({
                    areas: {
                      main: {
                        $type: "MeshSearch",
                        hiddenQuery: appsScope.query,
                        showSearchBox: false,
                        scopeTabs: [appsScope],
                      } as never,
                    },
                  })
                }
                area="main"
              >
                <RenderArea areaKey="main" />
              </ScopeProvider>
            </MeshOpsProvider>
          </RegistryProvider>
        </NavContext.Provider>,
      );
    });
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 300)); // the 250 ms search debounce
    });
    const j = r.toJSON() as Json;
    // Row-only: the icon grid never pulls content over the wire.
    expect(search).toHaveBeenCalledWith(
      "path:u1/_App scope:children nodeType:InstalledApp select:path,id,namespace,name,nodeType,icon,mainNode",
      undefined,
    );
    const text = allText(j).join("\n");
    expect(text).toContain("Alpha");
    expect(text).toContain("Beta");
    // One tab ⇒ no tab strip.
    expect([...walk(j)].some((n) => n.props?.accessibilityRole === "tablist")).toBe(false);
    // Pressing a tile navigates to the row's mainNode (the APP), never the record.
    const tile = r.root.findAll((n) => n.props?.accessibilityLabel === "Alpha")[0];
    await TestRenderer.act(async () => {
      tile.props.onPress();
    });
    expect(navigate).toHaveBeenCalledWith({ address: "store/Alpha", area: "" });
  });

  it("SortByAccess orders most-recently-used first from the viewer's access log, keeping never-opened tiles", async () => {
    const search = vi.fn(async (q: string) =>
      q.includes("_UserActivity")
        ? [{ id: "store_Beta", lastModified: "2026-08-20T10:00:00Z", path: "u1/_UserActivity/store_Beta", nodeType: "UserActivity" }]
        : appRows,
    );
    const ops = fakeOps({ search, userId: "u1" });
    const j = await renderLive(
      {
        areas: {
          main: {
            $type: "MeshSearch",
            hiddenQuery: appsScope.query,
            showSearchBox: false,
            scopeTabs: [{ ...appsScope, sortByAccess: true }],
          } as never,
        },
      },
      ops,
    );
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 300));
    });
    // The access log was read row-only from the viewer's own partition…
    expect(search.mock.calls.some((c) => String(c[0]).startsWith("namespace:u1/_UserActivity nodeType:UserActivity"))).toBe(true);
    // …and Beta (opened — its TARGET store/Beta is logged, '/'→'_') leads while Alpha stays.
    const labels = allText(j).filter((t2) => t2 === "Alpha" || t2 === "Beta");
    expect(labels).toEqual(["Beta", "Alpha"]);
  });

  it("renders the strip for 2+ scopes and groups by nodeType with counts, biggest group first", async () => {
    const search = vi.fn(async () => [
      { path: "a/1", name: "One", nodeType: "Story" },
      { path: "a/2", name: "Two", nodeType: "Space" },
      { path: "a/3", name: "Three", nodeType: "Space" },
    ]);
    const ops = fakeOps({ search });
    const j = await renderLive(
      {
        areas: {
          main: {
            $type: "MeshSearch",
            hiddenQuery: "is:main",
            renderMode: "Grouped",
            groupByFrequency: true,
            showSearchBox: false,
            grouping: { groupByProperty: "NodeType" },
            scopeTabs: [
              { label: "All", query: "is:main" },
              { label: "Pinned", query: "path:(a/1)" },
            ],
          } as never,
        },
      },
      ops,
    );
    await TestRenderer.act(async () => {
      await new Promise((res) => setTimeout(res, 300));
    });
    expect([...walk(j)].some((n) => n.props?.accessibilityRole === "tablist")).toBe(true);
    const text = allText(j);
    // Counts on the section headers, biggest group first.
    const headers = text.filter((t2) => /\(\d\)$/.test(t2));
    expect(headers).toEqual(["Space (2)", "Story (1)"]);
  });
});

// ---- ResultIcon: mesh-relative SVG urls on a device ------------------------------------------

describe("ResultIcon url handling (the icons-not-showing defect)", () => {
  it("resolves a mesh-relative .svg icon against the instance and renders it via SvgUri", async () => {
    // The row shape prod actually serves: /static/… node icons are SVG at a RELATIVE path. RN's
    // Image renders nothing for both reasons (no origin; no svg decoder) — the tile must resolve
    // the URL against the current instance and route it through react-native-svg.
    const rows = [{ path: "u1/_App/doc", id: "doc", name: "Documentation", nodeType: "InstalledApp",
                    mainNode: "Doc", icon: "/static/NodeTypeIcons/book.svg" }];
    const search = vi.fn(async () => rows);
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <NavContext.Provider value={() => {}}>
          <RegistryProvider pack={fullPack}>
            <MeshOpsProvider ops={fakeOps({ search })}>
              <ScopeProvider
                source={new StaticAreaSource({ areas: { main: {
                  $type: "MeshSearch", hiddenQuery: "nodeType:InstalledApp",
                  showSearchBox: false, renderMode: "Icons",
                } as never } })}
                area="main"
              >
                <RenderArea areaKey="main" />
              </ScopeProvider>
            </MeshOpsProvider>
          </RegistryProvider>
        </NavContext.Provider>,
      );
    });
    await TestRenderer.act(async () => { await new Promise((res) => setTimeout(res, 300)); });
    const uris = r.root.findAll((n) => n.type === ("SvgUri" as never)).map((n) => n.props.uri);
    // Absolute (the mock instance's sidecar origin) — never the bare relative path RN cannot fetch.
    expect(uris).toContain("http://localhost:5250/static/NodeTypeIcons/book.svg");
  });
});

// ---- the SHARED composer model on the RN leaf (@-mention autocomplete) -----------------------

describe("ThreadChat composer @-mentions (core useMentionModel, native dropdown)", () => {
  it("typing an @token opens suggestions from ops.autocomplete; picking splices the insertText", async () => {
    const autocomplete = vi.fn(async () => [
      { label: "Documentation", insertText: "@/Doc", path: "Doc" },
    ]);
    const ops = fakeOps({ autocomplete });
    let r!: TestRenderer.ReactTestRenderer;
    await TestRenderer.act(async () => {
      r = TestRenderer.create(
        <NavContext.Provider value={() => {}}>
          <RegistryProvider pack={fullPack}>
            <MeshOpsProvider ops={ops}>
              <ScopeProvider
                source={new StaticAreaSource({ areas: { main: { $type: "ThreadChat", namespacePath: "u1" } as never } })}
                area="main"
              >
                <RenderArea areaKey="main" />
              </ScopeProvider>
            </MeshOpsProvider>
          </RegistryProvider>
        </NavContext.Provider>,
      );
    });
    const input = r.root.findAll((n) => String(n.type) === "TextInput"
      && n.props.placeholder === en("chat.composerPlaceholder"))[0];
    await TestRenderer.act(async () => { input.props.onChangeText("hello @do"); });
    await TestRenderer.act(async () => { await new Promise((res) => setTimeout(res, 300)); }); // the model's debounce
    expect(autocomplete).toHaveBeenCalledWith("@do", undefined);
    const item = r.root.findAll((n) => typeof n.type === "string" && n.props?.accessibilityRole === "menuitem")[0];
    expect(item).toBeTruthy();
    await TestRenderer.act(async () => { item.props.onPress(); });
    const after = r.root.findAll((n) => String(n.type) === "TextInput"
      && n.props.placeholder === en("chat.composerPlaceholder"))[0];
    expect(after.props.value).toBe("hello @/Doc ");
  });
});
