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

describe("MeshSearch — the home design (scope tabs, Icons grid, SortByAccess, grouped sections)", () => {
  const appRows = [
    { path: "u1/_App/alpha", id: "alpha", name: "Alpha", nodeType: "InstalledApp", mainNode: "store/Alpha", icon: "🅰" },
    { path: "u1/_App/beta", id: "beta", name: "Beta", nodeType: "InstalledApp", mainNode: "store/Beta", icon: "🅱" },
  ];

  it("renders the scope-tab strip for 2+ tabs; switching swaps the query while the term stays", async () => {
    const search = vi.fn(async (query: string) =>
      query.startsWith("nodeType:Pin") ? [nodes[0]] : query.startsWith("nodeType:All") ? [nodes[1]] : [],
    );
    const ops = { ...fakeOps([]), search } as unknown as MeshOps;
    view(
      {
        $type: "MeshSearch",
        hiddenQuery: "nodeType:All",
        visibleQuery: "laptop",
        scopeTabs: [
          { label: "All", query: "nodeType:All" },
          { label: "Pinned", query: "nodeType:Pin" },
        ],
      },
      ops,
    );
    expect(await screen.findByText("Second story")).toBeTruthy();
    // The strip renders both tabs; the first is active.
    const tabs = screen.getAllByRole("tab");
    expect(tabs.map((el) => el.textContent)).toEqual(["All", "Pinned"]);
    expect(tabs[0].getAttribute("aria-selected")).toBe("true");
    fireEvent.click(tabs[1]);
    expect(await screen.findByText("First story")).toBeTruthy();
    // The scope's query replaced the base — with the typed term still appended (shared search bar).
    await waitFor(() => expect(search).toHaveBeenCalledWith("nodeType:Pin laptop", undefined));
  });

  it("a SINGLE scope tab renders no strip but still applies its settings (Icons + NavigateToMainNode + row-only select)", async () => {
    const search = vi.fn(async () => appRows);
    const ops = { ...fakeOps([]), search } as unknown as MeshOps;
    view(
      {
        $type: "MeshSearch",
        hiddenQuery: "path:u1/_App scope:children nodeType:InstalledApp",
        showSearchBox: false,
        scopeTabs: [
          {
            label: "Apps",
            query: "path:u1/_App scope:children nodeType:InstalledApp",
            renderMode: "Icons",
            navigateToMainNode: true,
          },
        ],
      },
      ops,
    );
    expect(await screen.findByText("Alpha")).toBeTruthy();
    expect(screen.queryByRole("tab")).toBeNull(); // one tab ⇒ no strip
    // Row-only: the icon grid must never pull content over the wire.
    expect(search).toHaveBeenCalledWith(
      "path:u1/_App scope:children nodeType:InstalledApp select:path,id,namespace,name,nodeType,icon,mainNode",
      undefined,
    );
    // A tile navigates to the row's mainNode (the APP), never the record.
    expect(screen.getByText("Alpha").closest("a")?.getAttribute("href")).toBe("/store/Alpha");
  });

  it("SortByAccess orders most-recently-used first from the viewer's access log, keeping never-opened items", async () => {
    const search = vi.fn(async (query: string) =>
      query.includes("_UserActivity")
        ? [{ id: "store_Beta", lastModified: "2026-08-20T10:00:00Z", path: "u1/_UserActivity/store_Beta", nodeType: "UserActivity" }]
        : appRows,
    );
    const ops = { ...fakeOps([]), search, userId: "u1" } as unknown as MeshOps;
    view(
      {
        $type: "MeshSearch",
        hiddenQuery: "path:u1/_App scope:children nodeType:InstalledApp",
        showSearchBox: false,
        scopeTabs: [
          {
            label: "Apps",
            query: "path:u1/_App scope:children nodeType:InstalledApp",
            renderMode: "Icons",
            navigateToMainNode: true,
            sortByAccess: true,
          },
        ],
      },
      ops,
    );
    // Beta was opened (its TARGET store/Beta is in the access log, mangled '/'→'_'); Alpha never —
    // Beta leads, Alpha stays (a source:accessed INNER JOIN would have dropped it).
    await waitFor(() => {
      const labels = screen
        .getAllByText(/Alpha|Beta/)
        .map((el) => el.textContent)
        .filter((t): t is string => t === "Alpha" || t === "Beta");
      expect(labels).toEqual(["Beta", "Alpha"]);
    });
    expect(search).toHaveBeenCalledWith(
      "namespace:u1/_UserActivity nodeType:UserActivity select:path,id,namespace,name,nodeType,lastModified sort:LastModified-desc limit:500",
      undefined,
      500,
    );
  });

  it("Grouped mode buckets by nodeType with counts, biggest group first under groupByFrequency", async () => {
    const rows = [
      { path: "a/1", name: "One", nodeType: "Story" },
      { path: "a/2", name: "Two", nodeType: "Space" },
      { path: "a/3", name: "Three", nodeType: "Space" },
    ];
    const ops = fakeOps(rows);
    view(
      {
        $type: "MeshSearch",
        hiddenQuery: "is:main",
        renderMode: "Grouped",
        groupByFrequency: true,
        showSearchBox: false,
        grouping: { groupByProperty: "NodeType" },
      },
      ops,
    );
    expect(await screen.findByText("Space (2)")).toBeTruthy();
    expect(screen.getByText("Story (1)")).toBeTruthy();
    // Biggest group leads.
    const headers = screen.getAllByText(/\(\d\)$/).map((el) => el.textContent);
    expect(headers).toEqual(["Space (2)", "Story (1)"]);
  });

  it("a newline-joined UNION hidden query issues each leg separately and merges deduped", async () => {
    const search = vi.fn(async (query: string) =>
      query.startsWith("namespace: ") || query === "namespace:"
        ? [{ path: "Doc", name: "DocRoot", nodeType: "Group" }, { path: "Both", name: "BothFirst", nodeType: "Group" }]
        : [{ path: "u1/x", name: "OwnItem", nodeType: "Group" }, { path: "Both", name: "BothDup", nodeType: "Group" }],
    );
    const ops = { ...fakeOps([]), search } as unknown as MeshOps;
    view(
      { $type: "MeshSearch", hiddenQuery: "namespace: is:main\nnamespace:u1 is:main", showSearchBox: false },
      ops,
    );
    expect(await screen.findByText("DocRoot")).toBeTruthy();
    expect(screen.getByText("OwnItem")).toBeTruthy();
    expect(search).toHaveBeenCalledTimes(2);
    expect(screen.getAllByText("BothFirst")).toHaveLength(1); // deduped by path, first leg wins
    expect(screen.queryByText("BothDup")).toBeNull();
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
