import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { rnPack, rnPlaceholderControlTypes } from "./rnPack";

// Feature-parity guard for the React Native leaf pack — the RN twin of the web pack's
// clients/react/src/render/parity.test.ts.
//
// The Blazor vocabulary is DERIVED, not hand-listed. It is parsed out of the switch arms in
// src/MeshWeaver.Blazor/BlazorViewRegistry.cs — the single place that decides "this control/skin gets
// this Blazor view". A hand-maintained list is exactly what let ten controls and thirteen skins ship
// on the Blazor side and stay silently missing here: RN had no ratchet at all, so nothing failed.
// Reading the C# means adding a control to Blazor fails THIS test until the RN pack covers it.

// Vitest runs with cwd = this package root (clients/react-native), so the repo root is two levels up.
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
// asserted the same way, they just have no arm to scrape. Kept identical to the web pack's list so
// the two ratchets cannot drift apart.
const EXTERNALLY_PACKED_CONTROLS = [
  "Chart", "CodeSample", "Date", "Dialog", "Exception", "LayoutAreaDefinition", "MeshNodeContentEditor",
  "PivotGrid", "Slider", "ThreadChat", "UserProfile",
];

// Skins the RN renderer covers WITHOUT a registry entry, by design. SplitterPane is read by the
// parent Splitter skin (it sizes each pane), exactly as Blazor's FluentMultiSplitterPane does — a
// registry entry would double-wrap it.
const SKINS_HANDLED_BY_PARENT = ["SplitterPane"];

describe("React Native ↔ Blazor control parity", () => {
  it("scrapes a plausible vocabulary out of BlazorViewRegistry.cs", () => {
    // Guard the guard: if the C# is refactored into a shape the regex no longer matches, the parity
    // assertions below would vacuously pass on an empty list.
    expect(BLAZOR_LEAF_CONTROLS.length).toBeGreaterThan(40);
    expect(BLAZOR_SKINS.length).toBeGreaterThan(15);
    expect(BLAZOR_LEAF_CONTROLS).toContain("Markdown");
    expect(BLAZOR_SKINS).toContain("LayoutStack");
  });

  it("the RN pack registers every Blazor leaf control $type", () => {
    const missing = BLAZOR_LEAF_CONTROLS.filter((t) => !(t in rnPack.controls));
    expect(missing, `Missing Blazor controls in the RN pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("the RN pack registers every Blazor skin $type", () => {
    const missing = BLAZOR_SKINS.filter((t) => !(t in rnPack.skins) && !SKINS_HANDLED_BY_PARENT.includes(t));
    expect(missing, `Missing Blazor skins in the RN pack: ${missing.join(", ")}`).toEqual([]);
  });

  it("also covers the controls dispatched by the external view packs", () => {
    const missing = EXTERNALLY_PACKED_CONTROLS.filter((t) => !(t in rnPack.controls));
    expect(missing, `Missing externally-packed controls: ${missing.join(", ")}`).toEqual([]);
  });

  it("registered controls are React components (functions), not accidental values", () => {
    const all = [...BLAZOR_LEAF_CONTROLS, ...EXTERNALLY_PACKED_CONTROLS];
    const bad = all.filter((t) => t in rnPack.controls && typeof rnPack.controls[t] !== "function");
    expect(bad, `Non-component control entries: ${bad.join(", ")}`).toEqual([]);
  });

  it("registered skins are React components (functions), not accidental values", () => {
    const bad = BLAZOR_SKINS.filter((t) => t in rnPack.skins && typeof rnPack.skins[t] !== "function");
    expect(bad, `Non-component skin entries: ${bad.join(", ")}`).toEqual([]);
  });

  // RATCHET: the registered-but-placeholder long-tail. Implementing one for real = remove it from
  // rnPlaceholderControlTypes. Adding a NEW placeholder fails here — every new control must ship a
  // real implementation, the same rule the web pack lives under.
  it("the placeholder long-tail only ever shrinks", () => {
    // The live-ops controls (ThreadChat, MeshSearch, MeshNodeCollection, Appearance) now render
    // against the connected mesh via useMeshOps, exactly as the web pack does. Nothing renders as a
    // bare labelled badge any more.
    const pinned: string[] = [];
    expect([...rnPlaceholderControlTypes].sort()).toEqual(pinned.sort());
  });

  // RATCHET: controls that must stay their OWN component. Collapsing one to an alias of another is
  // how a "port" silently becomes a stub — DataGrid standing in for PivotGrid renders numbers with
  // no aggregation, and nothing fails.
  it("no control regresses to an alias of another", () => {
    expect(rnPack.controls.PivotGrid).not.toBe(rnPack.controls.DataGrid);
    expect(rnPack.controls.MeshNodeCollection).not.toBe(rnPack.controls.Catalog);
    expect(rnPack.controls.MeshSearch).not.toBe(rnPack.controls.SearchBox);
    expect(rnPack.controls.DiffEditor).not.toBe(rnPack.controls.CodeEditor);
    expect(rnPack.controls.NodeImport).not.toBe(rnPack.controls.NodeExport);
    expect(rnPack.controls.Video).not.toBe(rnPack.controls.SlideShow);
    // CodeEditor/Editor and MarkdownEditor/CollaborativeMarkdown intentionally share views (same
    // wire contract), matching the web pack.
  });

  it("keeps a fallback so an unknown $type degrades instead of crashing", () => {
    expect(typeof rnPack.fallback).toBe("function");
    expect(typeof rnPack.defaultContainer).toBe("function");
  });
});
