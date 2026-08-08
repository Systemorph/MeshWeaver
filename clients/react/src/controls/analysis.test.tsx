import { describe, expect, it } from "vitest";
import { render } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource, type AreaTree, type UiControl } from "../core.js";
import { comparisonLayout, navigableHref, towerLayout } from "./analysis.js";

// The analysis views (#745). Two things are worth pinning here:
//
//   1. The GEOMETRY ports. TowerControl.Layout / ComparisonBarsControl.Layout in
//      src/MeshWeaver.Layout are the reference semantics, and AnalysisControlsTest.cs pins them on
//      the C# side. These cases mirror that test's cases exactly, so a divergence between the two
//      renderers shows up as a failing assertion rather than as two subtly different pictures.
//   2. The ABSENT SIDE. A missing value must read as words, never as a zero-length bar — the whole
//      reason the comparison view exists.

const view = (control: UiControl): string => {
  const tree: AreaTree = { data: {}, areas: { main: control } };
  const { container } = render(<MeshAreaView source={new StaticAreaSource(tree)} rootArea="main" />);
  return container.textContent ?? "";
};

describe("tower layout (port of TowerControl.Layout)", () => {
  it("stacks consecutive layers edge to edge, ordered by attachment", () => {
    const layout = towerLayout([
      { label: "Layer 2", attachment: 10_000_000, cover: 15_000_000 },
      { label: "Layer 1", attachment: 3_000_000, cover: 7_000_000 },
    ])!;

    expect(layout.top).toBe(25_000_000);
    expect(layout.retention).toBe(3_000_000);
    expect(layout.bands.map((b) => b.band.label)).toEqual(["Layer 1", "Layer 2"]);
    expect(layout.bands[0].bottomPercent).toBeCloseTo(12, 9);
    expect(layout.bands[0].heightPercent).toBeCloseTo(28, 9);
    // Layer 1 exhausts exactly where layer 2 attaches.
    expect(layout.bands[0].bottomPercent + layout.bands[0].heightPercent).toBeCloseTo(
      layout.bands[1].bottomPercent,
      9,
    );
    expect(layout.bands[1].bottomPercent + layout.bands[1].heightPercent).toBeCloseTo(100, 9);
  });

  it("is null when there is nothing honest to draw", () => {
    expect(towerLayout([])).toBeNull();
    expect(towerLayout([{ label: "Empty", attachment: 0, cover: 0 }])).toBeNull();
  });

  it("normalizes a band href to a navigable target", () => {
    expect(navigableHref("Acme/Deal/Layer1")).toBe("/Acme/Deal/Layer1");
    expect(navigableHref("/Acme/Deal/Layer1")).toBe("/Acme/Deal/Layer1");
    expect(navigableHref("https://example.com/x")).toBe("https://example.com/x");
    expect(navigableHref("   ")).toBeNull();
  });

  it("says so in words when there is no structure", () => {
    expect(view({ $type: "Tower", bands: [] })).toContain("No structure to draw");
  });
});

describe("comparison layout (port of ComparisonBarsControl.Layout)", () => {
  it("sizes both series against one shared scale", () => {
    const layout = comparisonLayout([
      { label: "Paid", left: 100, right: 50 },
      { label: "Outstanding", left: 200, right: 200 },
    ])!;

    expect(layout.max).toBe(200);
    expect(layout.rows[0].leftPercent).toBeCloseTo(50, 9);
    expect(layout.rows[0].rightPercent).toBeCloseTo(25, 9);
    expect(layout.rows[1].leftPercent).toBeCloseTo(100, 9);
  });

  it("keeps an absent side distinguishable from a reported zero", () => {
    const layout = comparisonLayout([
      { label: "Only ours", left: null, right: 400 },
      { label: "Zero on the left", left: 0, right: 400 },
    ])!;

    expect(layout.rows[0].leftPercent).toBeNull();
    expect(layout.rows[1].leftPercent).toBeGreaterThan(0);
    expect(layout.rows[1].leftPercent!).toBeLessThan(1);
  });

  it("is null when no side carries a positive value", () => {
    expect(comparisonLayout([])).toBeNull();
    expect(comparisonLayout([{ label: "Nothing", left: null, right: null }])).toBeNull();
    expect(comparisonLayout([{ label: "All zero", left: 0, right: 0 }])).toBeNull();
  });

  it("renders an absent side as words, never as a bar", () => {
    const text = view({
      $type: "ComparisonBars",
      leftLegend: "reported",
      rightLegend: "ours",
      pairs: [{ label: "Outstanding", left: 90_000, right: null }],
    });

    expect(text).toContain("reported 90,000");
    expect(text).toContain("not on this side");
  });
});

describe("KPI strip", () => {
  it("renders a tile per item, hint included", () => {
    const text = view({
      $type: "KpiStrip",
      items: [
        { label: "Premium", value: "12.4m" },
        { label: "Combined ratio", value: "94.1%", hint: "before commission" },
      ],
    });

    expect(text).toContain("Premium");
    expect(text).toContain("12.4m");
    expect(text).toContain("before commission");
  });

  it("says so in words when there are no figures", () => {
    expect(view({ $type: "KpiStrip", items: [] })).toContain("No figures to show");
  });
});
