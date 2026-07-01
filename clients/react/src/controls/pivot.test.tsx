// PivotGridControl parity — a REAL cross-tab from the wire contract
// (src/MeshWeaver.Layout/Pivot/PivotGridControl.cs + PivotConfiguration.cs), not a DataGrid alias:
// rows grouped by rowDimensions, columns by columnDimensions, measures aggregated per cell with
// Sum/Average/Count/Min/Max, row + column totals, .NET number formats.

import { beforeAll, describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource } from "../core.js";
import { computePivot, formatCell, type PivotConfigWire } from "./pivot.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

// Rows arrive camelCase on the wire while PropertyPath keeps the C# property name — computePivot
// must bridge that (the same convention DataGrid columns use).
const rows = [
  { product: "Apples", region: "East", amount: 100, quantity: 2 },
  { product: "Apples", region: "West", amount: 50, quantity: 1 },
  { product: "Bananas", region: "East", amount: 30, quantity: 3 },
  { product: "Apples", region: "East", amount: 20, quantity: 4 },
];

const config: PivotConfigWire = {
  rowDimensions: [{ field: "Product", displayName: "Product", propertyPath: "Product" }],
  columnDimensions: [{ field: "Region", displayName: "Region", propertyPath: "Region" }],
  aggregates: [{ field: "Amount", displayName: "Amount", propertyPath: "Amount", function: "Sum", format: "N0" }],
  showRowTotals: true,
  showColumnTotals: true,
};

describe("computePivot", () => {
  it("cross-tabs rows × columns with summed cells, row totals and column totals", () => {
    const p = computePivot(rows, config);
    expect(p.colKeys).toEqual([["East"], ["West"]]);
    expect(p.rows.map((r) => r.keys[0])).toEqual(["Apples", "Bananas"]);
    const apples = p.rows[0];
    expect(apples.cells).toEqual([120, 50]); // East, West
    expect(apples.totals).toEqual([170]);
    const bananas = p.rows[1];
    expect(bananas.cells).toEqual([30, null]); // no Bananas/West data → null
    expect(bananas.totals).toEqual([30]);
    expect(p.columnTotals?.cells).toEqual([150, 50]);
    expect(p.columnTotals?.grand).toEqual([200]);
  });

  it("supports Average / Count / Min / Max (string enum names and ordinals)", () => {
    const aggs = (fns: unknown[]) =>
      computePivot(rows, {
        rowDimensions: [{ field: "Product", propertyPath: "Product" }],
        aggregates: fns.map((f, i) => ({ field: "Amount", propertyPath: "Amount", displayName: `A${i}`, function: f as never })),
      });
    const p = aggs(["Average", "Count", "Min", "Max"]);
    const apples = p.rows[0];
    // Apples amounts: 100, 50, 20
    expect(apples.cells).toEqual([170 / 3, 3, 20, 100]);
    // Ordinals (pre-string-enum wire): 1 = Average
    expect(aggs([1]).rows[0].cells).toEqual([170 / 3]);
  });

  it("multiple aggregates render measures innermost (colKeys × aggregates)", () => {
    const p = computePivot(rows, {
      ...config,
      aggregates: [
        { field: "Amount", displayName: "Amount", propertyPath: "Amount", function: "Sum" },
        { field: "Quantity", displayName: "Qty", propertyPath: "Quantity", function: "Sum" },
      ],
    });
    // [East×Amount, East×Qty, West×Amount, West×Qty]
    expect(p.rows[0].cells).toEqual([120, 6, 50, 1]);
  });

  it("respects Descending sortOrder on a dimension", () => {
    const p = computePivot(rows, {
      ...config,
      rowDimensions: [{ field: "Product", propertyPath: "Product", sortOrder: "Descending" }],
    });
    expect(p.rows.map((r) => r.keys[0])).toEqual(["Bananas", "Apples"]);
  });

  it("no column dimensions → measures are the columns; empty data → no rows", () => {
    const p = computePivot(rows, { rowDimensions: config.rowDimensions, aggregates: config.aggregates });
    expect(p.colKeys).toEqual([[]]);
    expect(p.rows[0].cells).toEqual([170]);
    expect(computePivot([], config).rows).toEqual([]);
  });
});

describe("formatCell — .NET format strings", () => {
  // Expected values via toLocaleString so the assertion is host-locale-agnostic.
  const loc = (v: number, digits: number) => v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
  it("formats N0 / N2 / {0:N2} and blanks nulls", () => {
    expect(formatCell(1234.5, "N0")).toBe(loc(1234.5, 0));
    expect(formatCell(1234.5, "N2")).toBe(loc(1234.5, 2));
    expect(formatCell(1234.5, "{0:N2}")).toBe(loc(1234.5, 2));
    expect(formatCell(null, "N0")).toBe("");
  });
});

describe("PivotGridView — DOM rendering from a realistic payload", () => {
  function pivotTree(overrides: Record<string, unknown> = {}) {
    return {
      data: { rows },
      areas: {
        main: {
          $type: "PivotGrid",
          data: { $type: "JsonPointerReference", pointer: "/data/rows" },
          configuration: config,
          ...overrides,
        },
      },
    };
  }

  it("renders the cross-tab: dimension headers, column groups, cells and totals", () => {
    render(<MeshAreaView source={new StaticAreaSource(pivotTree())} rootArea="main" />);
    const table = screen.getByRole("table");
    const text = table.textContent ?? "";
    expect(text).toContain("Product");
    expect(text).toContain("East");
    expect(text).toContain("West");
    expect(text).toContain("Apples");
    expect(text).toContain("120"); // Apples/East sum, N0
    expect(text).toContain("170"); // Apples row total
    expect(text).toContain("200"); // grand total
    expect(screen.getAllByText("Total").length).toBeGreaterThanOrEqual(2); // header + totals row
  });

  it("pages rows when showPager is set", () => {
    render(<MeshAreaView source={new StaticAreaSource(pivotTree({ showPager: true, pageSize: 1 }))} rootArea="main" />);
    expect(screen.getByText("Page 1 of 2")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Next" })).toBeTruthy();
  });
});
