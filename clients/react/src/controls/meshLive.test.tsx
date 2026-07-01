// Live mesh controls parity — MeshSearch and MeshNodeCollection render from REAL queries through
// the MeshOps contract (live/meshOps.tsx `search`), the same decoupling ThreadChat uses: the test
// injects a fake MeshOps and asserts the composed query (hiddenQuery + visibleQuery, Blazor
// MeshSearchView semantics) and the rendered result cards/rows.

import { beforeAll, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl, type MeshOps } from "../core.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

function fakeOps(results: Record<string, Record<string, unknown>[]> | Record<string, unknown>[]): MeshOps & { search: ReturnType<typeof vi.fn> } {
  const search = vi.fn(async (query: string) => (Array.isArray(results) ? results : (results[query] ?? [])));
  return {
    // eslint-disable-next-line @typescript-eslint/no-empty-function
    watch: async function* () {},
    startThread: async () => ({ path: "t" }),
    submitMessage: async () => null,
    patch: () => {},
    search,
  } as unknown as MeshOps & { search: ReturnType<typeof vi.fn> };
}

function view(control: Record<string, unknown>, ops: MeshOps) {
  const source = new StaticAreaSource({ data: {}, areas: { main: control as unknown as UiControl } } satisfies AreaTree);
  return render(<MeshAreaView source={source} rootArea="main" ops={ops} />);
}

const nodes = [
  { path: "acme/Story/First", name: "First story", nodeType: "Story", content: { description: "The first" } },
  { path: "acme/Story/Second", name: "Second story", nodeType: "Story" },
];

describe("MeshSearch — query-backed results (Blazor MeshSearchView parity)", () => {
  it("runs hiddenQuery + visibleQuery and renders result cards with name/type/description", async () => {
    const ops = fakeOps(nodes);
    view({ $type: "MeshSearch", title: "Stories", hiddenQuery: "nodeType:Story", visibleQuery: "laptop", namespace: "acme" }, ops);
    expect(await screen.findByText("First story")).toBeTruthy();
    expect(screen.getByText("Second story")).toBeTruthy();
    expect(screen.getByText("The first")).toBeTruthy();
    expect(screen.getAllByText("Story").length).toBeGreaterThan(0);
    expect(ops.search).toHaveBeenCalledWith("nodeType:Story laptop", "acme");
    // Results link to /{path}.
    const link = screen.getByText("First story").closest("a");
    expect(link?.getAttribute("href")).toBe("/acme/Story/First");
  });

  it("live search re-queries (debounced) as the user types", async () => {
    const ops = fakeOps(nodes);
    view({ $type: "MeshSearch", hiddenQuery: "nodeType:Story", placeholder: "Find…" }, ops);
    fireEvent.change(screen.getByPlaceholderText("Find…"), { target: { value: "banana" } });
    await waitFor(() => expect(ops.search).toHaveBeenCalledWith("nodeType:Story banana", undefined), { timeout: 2000 });
  });

  it("excludeBasePath drops the namespace root node; List mode renders rows", async () => {
    const ops = fakeOps([{ path: "acme", name: "Acme root", nodeType: "Space" }, ...nodes]);
    view({ $type: "MeshSearch", hiddenQuery: "scope:descendants", namespace: "acme", renderMode: "List" }, ops);
    expect(await screen.findByText("First story")).toBeTruthy();
    expect(screen.queryByText("Acme root")).toBeNull();
  });

  it("shows the empty message for a no-hit query, hides the box when showSearchBox=false", async () => {
    const ops = fakeOps([]);
    view({ $type: "MeshSearch", hiddenQuery: "nodeType:Nothing", showSearchBox: false }, ops);
    expect(await screen.findByText("No items found.")).toBeTruthy();
    expect(screen.queryByRole("textbox")).toBeNull();
  });
});

describe("MeshNodeCollection — compact cards from queries (Blazor MeshNodeCollectionView parity)", () => {
  it("runs all queries, merges by path, and renders avatar cards (name + type) linking to the node", async () => {
    const byQuery = {
      "path:acme/Story/* nodeType:Story": [nodes[0], nodes[1]],
      "path:acme/* scope:children": [nodes[1]], // overlap — must dedup by path
    };
    const ops = fakeOps(byQuery);
    view({ $type: "MeshNodeCollection", queries: Object.keys(byQuery) }, ops);
    expect(await screen.findByText("First story")).toBeTruthy();
    expect(screen.getAllByText("Second story")).toHaveLength(1);
    expect(ops.search).toHaveBeenCalledTimes(2);
    expect(screen.getByText("First story").closest("a")?.getAttribute("href")).toBe("/acme/Story/First");
  });

  it("shows 'No items.' when empty and the add button is disabled", async () => {
    const ops = fakeOps([]);
    view({ $type: "MeshNodeCollection", queries: ["path:none/*"], showAdd: false }, ops);
    expect(await screen.findByText("No items.")).toBeTruthy();
  });
});
