// ChartControl on Chart.js — the SAME wire contract the Blazor side binds
// (src/MeshWeaver.Layout/Chart/ChartControl.cs + ChartSeries.cs, rendered by RadzenChartView):
//   { title, subtitle, showLegend, legendPosition ("Top"|"Bottom"|"Left"|"Right"), labels: string[],
//     series: [{ $type: "bar"|"column"|"line"|"pie"|"doughnut"|"radar"|"polar"|"scatter"|"bubble",
//                data, label, backgroundColor, borderColor, borderWidth, hidden, tension, fill,
//                pointRadius, cutout, barPercentage, categoryPercentage }],
//     isStacked, disableAnimation, width (default "100%"), height (default "400px"),
//     categoryAxisLabelAngle (default -45) }
// ($type discriminators are the JsonDerivedType tags on ChartSeries; class names are tolerated.)
//
// The mapping to a Chart.js config is a PURE function (chartConfigFor) pinned by tests; the view
// dynamically imports chart.js/auto (client-only, code-split — the lib bundle stays lean) and
// falls back to an empty canvas in hosts without a 2D canvas (jsdom, SSR).

import type { ReactNode } from "react";
import { useEffect, useMemo, useRef } from "react";
import { Text } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { str } from "./common.js";

interface WireSeries {
  $type?: string;
  data?: Json[];
  label?: Json;
  backgroundColor?: Json;
  borderColor?: Json;
  borderWidth?: Json;
  hidden?: Json;
  tension?: Json;
  fill?: Json;
  pointRadius?: Json;
  cutout?: Json;
  barPercentage?: Json;
  categoryPercentage?: Json;
  [key: string]: Json;
}

export interface ChartInputs {
  title?: Json;
  subtitle?: Json;
  showLegend?: Json;
  legendPosition?: Json;
  labels?: Json;
  series?: Json;
  isStacked?: Json;
  disableAnimation?: Json;
  categoryAxisLabelAngle?: Json;
}

/** The default series palette (matches the hand-rolled chart this replaces; Blazor lets Radzen pick). */
export const chartPalette = ["#0f6cbd", "#107c10", "#d83b01", "#5c2e91", "#008272", "#c239b3", "#986f0b"];

/** Wire `$type` → Chart.js chart type. Tolerates the class names ("BarSeries") besides the tags. */
export function chartTypeFor(seriesType: string | undefined): { type: string; horizontal: boolean } {
  const t = (seriesType ?? "").toLowerCase().replace(/series$/, "");
  switch (t) {
    case "bar":
      return { type: "bar", horizontal: true }; // Blazor's BarSeries = horizontal bars
    case "column":
      return { type: "bar", horizontal: false };
    case "line":
      return { type: "line", horizontal: false };
    case "pie":
      return { type: "pie", horizontal: false };
    case "doughnut":
      return { type: "doughnut", horizontal: false };
    case "radar":
      return { type: "radar", horizontal: false };
    case "polar":
      return { type: "polarArea", horizontal: false };
    case "scatter":
      return { type: "scatter", horizontal: false };
    case "bubble":
      return { type: "bubble", horizontal: false };
    default:
      return { type: "bar", horizontal: false };
  }
}

const CIRCULAR = new Set(["pie", "doughnut", "polarArea"]);

function seriesData(s: WireSeries): Json[] {
  const raw = Array.isArray(s.data) ? s.data : Array.isArray(s.values) ? (s.values as Json[]) : [];
  return raw.map((v) => (v != null && typeof v === "object" ? v : Number(v)));
}

/** Build the Chart.js config for the resolved ChartControl props. Pure — pinned by chart.test.tsx. */
export function chartConfigFor(p: ChartInputs): Json {
  const labels = (Array.isArray(p.labels) ? p.labels : []).map(str);
  const series = (Array.isArray(p.series) ? (p.series as WireSeries[]) : []).filter((s) => s != null);
  const kinds = series.map((s) => chartTypeFor(s.$type));
  const base = kinds[0] ?? { type: "bar", horizontal: false };
  const circular = CIRCULAR.has(base.type);
  const mixed = kinds.some((k) => k.type !== base.type);

  const datasets = series.map((s, i) => {
    const kind = kinds[i];
    const data = seriesData(s);
    // Circular charts color per-slice; cartesian per-dataset. Wire colors win when present.
    const autoColor = CIRCULAR.has(kind.type) ? data.map((_, j) => chartPalette[j % chartPalette.length]) : chartPalette[i % chartPalette.length];
    const ds: Record<string, Json> = {
      label: s.label != null ? str(s.label) : undefined,
      data,
      backgroundColor: s.backgroundColor ?? autoColor,
      borderColor: s.borderColor ?? (kind.type === "line" || kind.type === "radar" ? autoColor : undefined),
      borderWidth: s.borderWidth,
      hidden: s.hidden === true ? true : undefined,
      tension: s.tension != null ? Number(s.tension) : undefined,
      fill: s.fill != null ? s.fill : undefined,
      pointRadius: s.pointRadius != null ? Number(s.pointRadius) : undefined,
      cutout: s.cutout != null ? s.cutout : undefined,
      barPercentage: s.barPercentage != null ? Number(s.barPercentage) : undefined,
      categoryPercentage: s.categoryPercentage != null ? Number(s.categoryPercentage) : undefined,
    };
    if (mixed && kind.type !== base.type) ds.type = kind.type;
    for (const k of Object.keys(ds)) if (ds[k] === undefined) delete ds[k];
    return ds;
  });

  const title = str(p.title);
  const subtitle = str(p.subtitle);
  const angle = p.categoryAxisLabelAngle != null ? Math.abs(Number(p.categoryAxisLabelAngle)) : 45;
  const stacked = p.isStacked === true;

  const config: Record<string, Json> = {
    type: base.type,
    data: { labels, datasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      ...(base.horizontal ? { indexAxis: "y" } : {}),
      ...(p.disableAnimation === true ? { animation: false } : {}),
      plugins: {
        title: { display: title.length > 0, text: title },
        subtitle: { display: subtitle.length > 0, text: subtitle },
        legend: {
          display: p.showLegend === true,
          position: str(p.legendPosition).toLowerCase() || "top",
        },
      },
      ...(circular
        ? {}
        : {
            scales: {
              x: { stacked, ticks: { maxRotation: angle, minRotation: 0, autoSkip: true } },
              y: { stacked, beginAtZero: true },
            },
          }),
    },
  };
  return config;
}

interface ChartLike {
  destroy(): void;
}

export function ChartView({ control }: { control: UiControl }): ReactNode {
  const title = str(useResolve(control.title));
  const subtitle = useResolve(control.subtitle);
  const labels = useResolve(control.labels);
  const series = useResolve(control.series);
  const showLegend = useResolve(control.showLegend);
  const legendPosition = useResolve(control.legendPosition);
  const isStacked = useResolve(control.isStacked);
  const disableAnimation = useResolve(control.disableAnimation);
  const angle = useResolve(control.categoryAxisLabelAngle);
  const width = str(useResolve(control.width)) || "100%";
  const height = str(useResolve(control.height)) || "400px";

  const config = useMemo(
    () =>
      chartConfigFor({
        title,
        subtitle,
        labels,
        series,
        showLegend,
        legendPosition,
        isStacked,
        disableAnimation,
        categoryAxisLabelAngle: angle,
      }),
    [title, subtitle, labels, series, showLegend, legendPosition, isStacked, disableAnimation, angle],
  );
  const configKey = useMemo(() => JSON.stringify(config), [config]);

  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    let live = true;
    let chart: ChartLike | undefined;
    import("chart.js/auto")
      .then(({ default: Chart }) => {
        if (!live) return;
        const ctx = canvas.getContext?.("2d");
        if (!ctx) return; // no 2D canvas (jsdom / SSR) — the empty canvas stands in
        chart = new Chart(ctx as never, JSON.parse(configKey)) as unknown as ChartLike;
      })
      .catch(() => undefined);
    return () => {
      live = false;
      chart?.destroy();
    };
  }, [configKey]);

  const hasData = Array.isArray(series) && series.length > 0;
  if (!hasData)
    return (
      <Text italic size={200}>
        No chart data available
      </Text>
    );
  return (
    <div style={{ position: "relative", width, height }}>
      <canvas ref={canvasRef} role="img" aria-label={title || "Chart"} />
    </div>
  );
}

export const chartControls = {
  Chart: ChartView,
};
