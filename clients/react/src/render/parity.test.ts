import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { controlRegistry } from "./registry.js";
import { skinRegistry } from "./skins.js";
import { placeholderControlTypes } from "../controls/mesh.js";

// Feature-parity guard: the Fluent React pack must render every control the Blazor portal renders.
//
// The Blazor vocabulary is DERIVED, not hand-listed. It is parsed out of the switch arms in
// src/MeshWeaver.Blazor/BlazorViewRegistry.cs — the single place that decides "this control/skin
// gets this Blazor view". A hand-maintained list is what let Video, SlideShow and the MenuItem skin
// ship on the Blazor side and stay silently missing here: the list never grew, so the test kept
// passing. Reading the C# means adding a control to Blazor fails THIS test until React covers it.

// Vitest runs with cwd = this package root (clients/react), so the repo root is two levels up.
const registryPath = resolve(process.cwd(), "../../src/MeshWeaver.Blazor/BlazorViewRegistry.cs");
const registrySource = readFileSync(registryPath, "utf8");

/**
 * The `$type` names Blazor maps to a view, scraped from the two switch expressions:
 *   `FooControl foo => StandardView<FooControl, FooView>(...)`   → "Foo"
 *   `FooSkin foo => StandardSkinnedView<FooView>(...)`           → "Foo"
 * Interface arms (`IContainerControl`) are dispatch fallbacks, not `$type`s, so they are excluded.
 */
function blazorTypes(suffix: "Control" | "Skin"): string[] {
  const arm = new RegExp(String.raw`^\s*([A-Z]\w*)${suffix}\s+\w+\s*$|^\s*([A-Z]\w*)${suffix}\s+\w+\s*=>`, "gm");
  const names = new Set<string>();
  for (const m of registrySource.matchAll(arm)) {
    const name = m[1] ?? m[2];
    if (name && !name.startsWith("I")) names.add(name);
  }
  return [...names].sort();
}

const BLAZOR_LEAF_CONTROLS = blazorTypes("Control");
const BLAZOR_SKINS = blazorTypes("Skin");

// Controls Blazor dispatches through a path other than BlazorViewRegistry's switch (separate view
// packs: Radzen charts/grids, the Graph pack, the Kernel notebook pack) — parity for these is
// asserted the same way, they just have no arm to scrape.
const EXTERNALLY_PACKED_CONTROLS = [
  "Chart", "CodeSample", "Date", "Dialog", "Exception", "LayoutAreaDefinition", "MeshNodeContentEditor",
  "PivotGrid", "Slider", "ThreadChat", "UserProfile",
];

// Skins the React renderer covers WITHOUT a registry entry, by design. SplitterPane is read by the
// parent SplitterSkin (paneSkinOf → paneSpec) to size each pane, exactly as Blazor's
// FluentMultiSplitterPane does — a registry entry would double-wrap it.
const SKINS_HANDLED_BY_PARENT = ["SplitterPane"];

describe("React ↔ Blazor control parity", () => {
  it("scrapes a plausible vocabulary out of BlazorViewRegistry.cs", () => {
    // Guard the guard: if the C# is refactored into a shape the regex no longer matches, the
    // parity assertions below would vacuously pass on an empty list.
    expect(BLAZOR_LEAF_CONTROLS.length).toBeGreaterThan(40);
    expect(BLAZOR_SKINS.length).toBeGreaterThan(15);
    expect(BLAZOR_LEAF_CONTROLS).toContain("Markdown");
    expect(BLAZOR_SKINS).toContain("LayoutStack");
  });

  it("the Fluent pack registers every Blazor leaf control $type", () => {
    const missing = BLAZOR_LEAF_CONTROLS.filter((t) => !(t in controlRegistry));
    expect(missing, `Missing Blazor controls in the React pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("the Fluent pack registers every Blazor skin $type", () => {
    const missing = BLAZOR_SKINS.filter((t) => !(t in skinRegistry) && !SKINS_HANDLED_BY_PARENT.includes(t));
    expect(missing, `Missing Blazor skins in the React pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("also covers the controls dispatched by the external view packs", () => {
    const missing = EXTERNALLY_PACKED_CONTROLS.filter((t) => !(t in controlRegistry));
    expect(missing, `Missing externally-packed controls: ${missing.join(", ")}`).toEqual([]);
  });

  it("registered controls are React components (functions), not accidental values", () => {
    const all = [...BLAZOR_LEAF_CONTROLS, ...EXTERNALLY_PACKED_CONTROLS];
    const bad = all.filter((t) => t in controlRegistry && typeof controlRegistry[t] !== "function");
    expect(bad, `Non-component control entries: ${bad.join(", ")}`).toEqual([]);
  });

  // RATCHET: the registered-but-placeholder long-tail (controls whose real rendering needs a live
  // mesh service beyond the AreaSource contract). Implementing one for real = remove it from
  // placeholderControlTypes AND from this pinned list. Adding a NEW placeholder fails here — every
  // new control must ship a real implementation.
  it("the placeholder long-tail only ever shrinks", () => {
    // Every mesh control now has a real implementation (documentControls.tsx, nodeTransfer.tsx,
    // fileBrowser.tsx). The placeholder long-tail is EMPTY — no control renders as a bare badge.
    const pinned: string[] = [];
    expect([...placeholderControlTypes].sort()).toEqual(pinned.sort());
  });

  it("the un-placeholdered controls are all real, distinct components", () => {
    const real = ["DocumentSource", "ExportDocument", "NodeExport", "NodeImport", "FileBrowser"].map(
      (key) => {
        expect(typeof controlRegistry[key]).toBe("function");
        return controlRegistry[key];
      },
    );
    // No two of them collapsed to the same component.
    expect(new Set(real).size).toBe(real.length);
  });

  // RATCHET: formerly-thin controls that now have REAL implementations — regressing one to an
  // alias of another control (the old PivotGrid→DataGrid / MeshNodeCollection→Catalog /
  // MeshSearch→SearchBox shortcuts) fails here. Each must stay its own component.
  it("no control regresses to an alias of another (the thin-list stays empty)", () => {
    expect(controlRegistry.PivotGrid).not.toBe(controlRegistry.DataGrid);
    expect(controlRegistry.MeshNodeCollection).not.toBe(controlRegistry.Catalog);
    expect(controlRegistry.MeshSearch).not.toBe(controlRegistry.SearchBox);
    expect(controlRegistry.DiffEditor).not.toBe(controlRegistry.CodeEditor);
    // CodeEditor/Editor and MarkdownEditor/CollaborativeMarkdown intentionally share views
    // (same wire contract); Chart/PivotGrid/LayoutArea are pinned real by their own test files
    // (chart.test.tsx, pivot.test.tsx, embeddedArea.test.tsx).
  });
});
