import { describe, expect, it } from "vitest";
import React from "react";
import TestRenderer, { type ReactTestRendererJSON } from "react-test-renderer";
import { RegistryProvider, ScopeProvider, RenderArea, StaticAreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./rnPack";
import { chartModelFor, chartSvg, slicePath } from "./rnData";

type Json = ReactTestRendererJSON;

function renderTree(tree: AreaTree): Json {
  let r!: TestRenderer.ReactTestRenderer;
  TestRenderer.act(() => {
    r = TestRenderer.create(
      <RegistryProvider pack={rnPack}>
        <ScopeProvider source={new StaticAreaSource(tree)} area="main">
          <RenderArea areaKey="main" />
        </ScopeProvider>
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

// ---- Chart ---------------------------------------------------------------------------------

describe("chartModelFor — the wire → drawable normalization", () => {
  it("collapses the Chart.js type zoo onto the three drawable families", () => {
    expect(chartModelFor({ series: [{ $type: "ColumnDataSet" }] }).family).toBe("bar");
    expect(chartModelFor({ series: [{ $type: "BarDataSet" }] }).family).toBe("bar");
    expect(chartModelFor({ series: [{ $type: "LineDataSet" }] }).family).toBe("line");
    expect(chartModelFor({ series: [{ $type: "AreaDataSet" }] }).family).toBe("line");
    expect(chartModelFor({ series: [{ $type: "PieDataSet" }] }).family).toBe("pie");
    expect(chartModelFor({ series: [{ $type: "DoughnutDataSet" }] }).family).toBe("pie");
  });

  it("reads values off either `data` or `values`, coercing non-numbers to 0", () => {
    expect(chartModelFor({ series: [{ data: [1, "2", null] }] }).series[0].values).toEqual([1, 2, 0]);
    expect(chartModelFor({ series: [{ values: [3, 4] }] }).series[0].values).toEqual([3, 4]);
  });

  it("unwraps a {x,y} point to its y value", () => {
    expect(chartModelFor({ series: [{ data: [{ x: 1, y: 7 }] }] }).series[0].values).toEqual([7]);
  });

  it("honours a wire colour and otherwise assigns distinct palette entries per series", () => {
    const m = chartModelFor({ series: [{ backgroundColor: "#abc" }, {}, {}] });
    expect(m.series[0].color).toBe("#abc");
    expect(m.series[1].color).not.toBe(m.series[2].color);
  });

  it("labels an unlabelled series positionally rather than leaving the legend blank", () => {
    expect(chartModelFor({ series: [{}, {}] }).series.map((s) => s.label)).toEqual(["Series 1", "Series 2"]);
  });
});

describe("slicePath", () => {
  it("sets the large-arc flag only past a half turn", () => {
    expect(slicePath(0, 0, 10, 0, Math.PI / 2)).toContain("A 10 10 0 0 1");
    expect(slicePath(0, 0, 10, 0, Math.PI * 1.5)).toContain("A 10 10 0 1 1");
  });
  it("closes the wedge back to the centre", () => {
    expect(slicePath(5, 5, 4, 0, 1).startsWith("M 5 5 L")).toBe(true);
    expect(slicePath(5, 5, 4, 0, 1).endsWith("Z")).toBe(true);
  });
});

describe("chartSvg", () => {
  const model = (over: Partial<ReturnType<typeof chartModelFor>> = {}) => ({
    ...chartModelFor({ labels: ["a", "b"], series: [{ $type: "ColumnDataSet", data: [1, 2] }] }),
    ...over,
  });

  it("emits one rect per bar datapoint", () => {
    const svg = chartSvg(model(), 400);
    expect(svg.match(/<rect /g) ?? []).toHaveLength(2);
  });

  it("emits a polyline (not rects) for a line series", () => {
    const svg = chartSvg(chartModelFor({ labels: ["a", "b"], series: [{ $type: "LineDataSet", data: [1, 2] }] }), 400);
    expect(svg).toContain("<polyline");
    expect(svg).not.toContain("<rect");
  });

  it("emits one path per pie slice", () => {
    const svg = chartSvg(chartModelFor({ series: [{ $type: "PieDataSet", data: [1, 1, 2] }] }), 400);
    // 3 slices; the baseline <path> of the cartesian body must NOT be there.
    expect(svg.match(/<path /g) ?? []).toHaveLength(3);
  });

  it("escapes label text so a stray angle bracket cannot break the document", () => {
    const svg = chartSvg(chartModelFor({ labels: ['<a href="x">'], series: [{ data: [1] }] }), 400);
    expect(svg).toContain("&lt;a href=&quot;x&quot;&gt;");
    expect(svg).not.toContain('<a href="x">');
  });

  it("declares the svg namespace and the requested size", () => {
    const svg = chartSvg(model(), 321);
    expect(svg).toContain('xmlns="http://www.w3.org/2000/svg"');
    expect(svg).toContain('width="321"');
  });
});

describe("Chart control", () => {
  it("renders the SVG document plus a legend entry per series", () => {
    const tree: AreaTree = {
      areas: {
        main: {
          $type: "Chart",
          title: "Revenue",
          labels: ["Q1", "Q2"],
          series: [
            { $type: "ColumnDataSet", label: "2025", data: [10, 20] },
            { $type: "ColumnDataSet", label: "2026", data: [15, 25] },
          ],
        },
      },
    };
    const j = renderTree(tree);
    const svgs = [...walk(j)].filter((n) => n.type === "SvgXml");
    expect(svgs).toHaveLength(1);
    expect(String(svgs[0].props.xml)).toContain("<rect");
    const text = allText(j);
    expect(text).toContain("Revenue");
    expect(text).toContain("2025");
    expect(text).toContain("2026");
  });

  it("says so instead of drawing an empty axis when there is no series", () => {
    expect(allText(renderTree({ areas: { main: { $type: "Chart" } } }))).toContain("No chart data");
  });
});

// ---- PivotGrid -----------------------------------------------------------------------------

describe("PivotGrid control", () => {
  const tree: AreaTree = {
    areas: {
      main: {
        $type: "PivotGrid",
        data: [
          { region: "West", year: "2025", amount: 10 },
          { region: "West", year: "2026", amount: 5 },
          { region: "East", year: "2025", amount: 20 },
        ],
        configuration: {
          rowDimensions: [{ field: "region" }],
          columnDimensions: [{ field: "year" }],
          aggregates: [{ field: "amount", displayName: "Amount", function: "Sum", format: "N0" }],
        },
      },
    },
  };

  it("aggregates through the SHARED computePivot — cells, row totals and grand total", () => {
    const text = allText(renderTree(tree));
    expect(text).toContain("West");
    expect(text).toContain("East");
    expect(text).toContain("2025");
    expect(text).toContain("2026");
    expect(text).toContain("15"); // West row total: 10 + 5
    expect(text).toContain("20"); // East 2025 cell
    expect(text).toContain("35"); // grand total
  });

  // The measure-header rule, ported from the web pack: a measure row appears only when several
  // aggregates share a column group, or when there are no column dimensions (the measures ARE the
  // columns). A single aggregate under column dims must NOT repeat its name over every group.
  it("omits the measure row for a single aggregate under column dimensions", () => {
    expect(allText(renderTree(tree))).not.toContain("Amount");
  });

  it("shows the measure names when there are no column dimensions", () => {
    const noCols: AreaTree = {
      areas: {
        main: {
          $type: "PivotGrid",
          data: [{ region: "West", amount: 10 }],
          configuration: {
            rowDimensions: [{ field: "region" }],
            aggregates: [{ field: "amount", displayName: "Amount", function: "Sum", format: "N0" }],
          },
        },
      },
    };
    expect(allText(renderTree(noCols))).toContain("Amount");
  });

  it("shows the measure names when several aggregates share a column group", () => {
    const twoAggs: AreaTree = {
      areas: {
        main: {
          $type: "PivotGrid",
          data: [{ region: "West", year: "2025", amount: 10, units: 2 }],
          configuration: {
            rowDimensions: [{ field: "region" }],
            columnDimensions: [{ field: "year" }],
            aggregates: [
              { field: "amount", displayName: "Amount", function: "Sum", format: "N0" },
              { field: "units", displayName: "Units", function: "Sum", format: "N0" },
            ],
          },
        },
      },
    };
    const text = allText(renderTree(twoAggs));
    expect(text).toContain("Amount");
    expect(text).toContain("Units");
  });

  it("reports a missing aggregate config rather than rendering an empty grid", () => {
    const bare: AreaTree = { areas: { main: { $type: "PivotGrid", data: [], configuration: {} } } };
    expect(allText(renderTree(bare))).toContain("No pivot aggregates configured");
  });

  it("is its own component — never an alias of DataGrid", () => {
    expect(rnPack.controls.PivotGrid).not.toBe(rnPack.controls.DataGrid);
  });
});
