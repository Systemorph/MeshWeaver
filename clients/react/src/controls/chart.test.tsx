// ChartControl → Chart.js mapping parity. The wire contract is ChartControl + ChartSeries
// (src/MeshWeaver.Layout/Chart) — series discriminated by $type ("bar"|"column"|"line"|"pie"|…) —
// and the SAME payload must fold into a Chart.js config that mirrors what the Blazor side renders:
// column = vertical bars, bar = horizontal (indexAxis y), line tension/fill, doughnut cutout,
// stacked scales, legend/title plumbing.

import { beforeAll, describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource } from "../core.js";
import { chartConfigFor, chartTypeFor } from "./chart.js";

beforeAll(() => {
  if (!window.matchMedia)
    window.matchMedia = ((q: string) =>
      ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false, onchange: null })) as unknown as typeof window.matchMedia;
});

describe("chartTypeFor — wire $type discriminators", () => {
  it("maps every ChartSeries discriminator to its Chart.js type", () => {
    expect(chartTypeFor("column")).toEqual({ type: "bar", horizontal: false });
    expect(chartTypeFor("bar")).toEqual({ type: "bar", horizontal: true }); // BarSeries = horizontal
    expect(chartTypeFor("line").type).toBe("line");
    expect(chartTypeFor("pie").type).toBe("pie");
    expect(chartTypeFor("doughnut").type).toBe("doughnut");
    expect(chartTypeFor("radar").type).toBe("radar");
    expect(chartTypeFor("polar").type).toBe("polarArea");
    expect(chartTypeFor("scatter").type).toBe("scatter");
    expect(chartTypeFor("bubble").type).toBe("bubble");
  });

  it("tolerates serialized class names besides the JsonDerivedType tags", () => {
    expect(chartTypeFor("LineSeries").type).toBe("line");
    expect(chartTypeFor("DoughnutSeries").type).toBe("doughnut");
  });
});

describe("chartConfigFor — the ChartControl wire payload", () => {
  const labels = ["Q1", "Q2", "Q3"];

  it("builds a column (vertical bar) chart with title, legend and labels", () => {
    const cfg = chartConfigFor({
      title: "Revenue",
      showLegend: true,
      legendPosition: "Bottom",
      labels,
      series: [{ $type: "column", label: "2025", data: [1, 2, 3] }],
    });
    expect(cfg.type).toBe("bar");
    expect(cfg.options.indexAxis).toBeUndefined();
    expect(cfg.data.labels).toEqual(labels);
    expect(cfg.data.datasets[0]).toMatchObject({ label: "2025", data: [1, 2, 3] });
    expect(cfg.options.plugins.title).toEqual({ display: true, text: "Revenue" });
    expect(cfg.options.plugins.legend).toEqual({ display: true, position: "bottom" });
  });

  it("renders BarSeries horizontally (indexAxis y) — the Radzen bar/column split", () => {
    const cfg = chartConfigFor({ labels, series: [{ $type: "bar", data: [1, 2, 3] }] });
    expect(cfg.type).toBe("bar");
    expect(cfg.options.indexAxis).toBe("y");
  });

  it("maps line series props (tension, fill = area chart, pointRadius)", () => {
    const cfg = chartConfigFor({
      labels,
      series: [{ $type: "line", data: [4, 5, 6], tension: 0.4, fill: true, pointRadius: 2 }],
    });
    expect(cfg.type).toBe("line");
    expect(cfg.data.datasets[0]).toMatchObject({ tension: 0.4, fill: true, pointRadius: 2 });
  });

  it("pie/doughnut: circular charts get per-slice colors and no cartesian scales", () => {
    const cfg = chartConfigFor({ labels, series: [{ $type: "doughnut", data: [1, 2, 3], cutout: "60%" }] });
    expect(cfg.type).toBe("doughnut");
    expect(cfg.options.scales).toBeUndefined();
    expect(Array.isArray(cfg.data.datasets[0].backgroundColor)).toBe(true);
    expect(cfg.data.datasets[0].backgroundColor).toHaveLength(3);
    expect(cfg.data.datasets[0].cutout).toBe("60%");
  });

  it("isStacked stacks both axes; categoryAxisLabelAngle rotates ticks; disableAnimation kills animation", () => {
    const cfg = chartConfigFor({
      labels,
      isStacked: true,
      disableAnimation: true,
      categoryAxisLabelAngle: -45,
      series: [
        { $type: "column", label: "A", data: [1, 2, 3] },
        { $type: "column", label: "B", data: [4, 5, 6] },
      ],
    });
    expect(cfg.options.scales.x.stacked).toBe(true);
    expect(cfg.options.scales.y.stacked).toBe(true);
    expect(cfg.options.scales.x.ticks.maxRotation).toBe(45);
    expect(cfg.options.animation).toBe(false);
  });

  it("mixed cartesian series get per-dataset types (column base + line overlay)", () => {
    const cfg = chartConfigFor({
      labels,
      series: [
        { $type: "column", data: [1, 2, 3] },
        { $type: "line", data: [2, 2, 2] },
      ],
    });
    expect(cfg.type).toBe("bar");
    expect(cfg.data.datasets[0].type).toBeUndefined();
    expect(cfg.data.datasets[1].type).toBe("line");
  });

  it("scatter/bubble pass point objects through untouched", () => {
    const cfg = chartConfigFor({ series: [{ $type: "bubble", data: [{ x: 1, y: 2, r: 3 }] }] });
    expect(cfg.type).toBe("bubble");
    expect(cfg.data.datasets[0].data).toEqual([{ x: 1, y: 2, r: 3 }]);
  });

  it("wire colors win over the palette; hidden series stay hidden", () => {
    const cfg = chartConfigFor({
      labels,
      series: [{ $type: "column", data: [1], backgroundColor: "#123456", hidden: true }],
    });
    expect(cfg.data.datasets[0].backgroundColor).toBe("#123456");
    expect(cfg.data.datasets[0].hidden).toBe(true);
  });
});

describe("ChartView — DOM rendering", () => {
  it("renders an accessible canvas sized by width/height from a realistic payload", () => {
    const source = new StaticAreaSource({
      data: {},
      areas: {
        main: {
          $type: "Chart",
          title: "Sales by quarter",
          labels: ["Q1", "Q2"],
          series: [{ $type: "column", label: "2026", data: [10, 20] }],
          height: "240px",
        },
      },
    });
    render(<MeshAreaView source={source} rootArea="main" />);
    const canvas = screen.getByRole("img", { name: "Sales by quarter" });
    expect(canvas.tagName).toBe("CANVAS");
    expect((canvas.parentElement as HTMLElement).style.height).toBe("240px");
  });

  it("shows the no-data message when the series are empty (Blazor parity)", () => {
    const source = new StaticAreaSource({ data: {}, areas: { main: { $type: "Chart", series: [] } } });
    render(<MeshAreaView source={source} rootArea="main" />);
    expect(screen.getByText("No chart data available")).toBeTruthy();
  });
});
