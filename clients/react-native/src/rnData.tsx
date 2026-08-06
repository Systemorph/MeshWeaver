// PivotGrid and Chart — the two data leaves the RN pack lacked entirely.
//
// PivotGrid runs the SHARED cross-tab model (computePivot / formatCell, exported from
// @meshweaver/react/core) — the same pure functions the web pack renders, so a total is aggregated
// by ONE implementation on both platforms rather than a native re-derivation that drifts.
//
// Chart draws with react-native-svg rather than Chart.js (a canvas library with no native
// renderer), covering the cartesian and circular families the wire actually carries: bar/column,
// line/area, and pie/doughnut.

import { useMemo, useState } from "react";
import { View, Text, ScrollView, StyleSheet, useWindowDimensions } from "react-native";
import { SvgXml } from "react-native-svg";
import {
  useLocalize,
  computePivot,
  formatCell,
  formatValue,
  groupsAt,
  str,
  useResolve,
  type ControlComponent,
  type PivotConfigWire,
} from "@meshweaver/react/core";

const s = str;

// ── PivotGrid ────────────────────────────────────────────────────────────────

const PivotGrid: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const data = useResolve(control.data);
  const config = useResolve(control.configuration) as PivotConfigWire | undefined;
  const pivot = useMemo(() => computePivot(data, config), [data, config]);
  const { rowDims, colDims, aggregates, colKeys, rows, columnTotals } = pivot;

  if (aggregates.length === 0) return <Text style={styles.muted}>{t("pivot.noAggregates")}</Text>;

  const showRowTotals = config?.showRowTotals !== false;
  const aggCount = aggregates.length;
  // A measure header row is needed when several aggregates share a column group, or when there are
  // no column dimensions at all (the measures ARE the columns) — same rule as the web pack.
  const showMeasureRow = aggCount > 1 || colDims.length === 0;

  return (
    <ScrollView horizontal>
      <View>
        {/* nested column-group header rows, outermost first */}
        {colDims.map((dim, level) => (
          <View key={`h${level}`} style={styles.row}>
            {rowDims.map((_, i) => (
              <View key={`sp${i}`} style={[styles.cell, styles.headerCell]} />
            ))}
            {groupsAt(colKeys, level).map((g, i) => (
              <View key={`g${i}`} style={[styles.cell, styles.headerCell, { minWidth: 110 * g.span * aggCount }]}>
                <Text style={styles.headerText}>{g.label}</Text>
              </View>
            ))}
            {showRowTotals ? <View style={[styles.cell, styles.headerCell]} /> : null}
          </View>
        ))}
        {/* the measure row + the row-dimension titles */}
        <View style={styles.row}>
          {rowDims.map((d, i) => (
            <View key={`rd${i}`} style={[styles.cell, styles.headerCell]}>
              <Text style={styles.headerText}>{s(d.displayName ?? d.field)}</Text>
            </View>
          ))}
          {showMeasureRow
            ? colKeys.flatMap((_, ci) =>
                aggregates.map((a, ai) => (
                  <View key={`m${ci}-${ai}`} style={[styles.cell, styles.headerCell]}>
                    <Text style={styles.headerText}>{s(a.displayName ?? a.field)}</Text>
                  </View>
                )),
              )
            : colKeys.map((_, ci) => <View key={`m${ci}`} style={[styles.cell, styles.headerCell]} />)}
          {showRowTotals
            ? aggregates.map((a, ai) => (
                <View key={`t${ai}`} style={[styles.cell, styles.headerCell]}>
                  <Text style={styles.headerText}>{t("common.total")}</Text>
                </View>
              ))
            : null}
        </View>
        {/* body */}
        {rows.map((r, ri) => (
          <View key={`r${ri}`} style={styles.row}>
            {r.keys.map((k, ki) => (
              <View key={`k${ki}`} style={styles.cell}>
                <Text style={styles.body}>{k}</Text>
              </View>
            ))}
            {r.cells.map((v, ci) => (
              <View key={`c${ci}`} style={styles.cell}>
                <Text style={styles.numeric}>{formatCell(v, aggregates[ci % aggCount]?.format)}</Text>
              </View>
            ))}
            {showRowTotals
              ? r.totals.map((v, ti) => (
                  <View key={`rt${ti}`} style={[styles.cell, styles.totalCell]}>
                    <Text style={styles.numeric}>{formatCell(v, aggregates[ti]?.format)}</Text>
                  </View>
                ))
              : null}
          </View>
        ))}
        {/* column totals */}
        {columnTotals ? (
          <View style={[styles.row, styles.totalRow]}>
            {rowDims.map((_, i) => (
              <View key={`tl${i}`} style={styles.cell}>
                {i === 0 ? <Text style={styles.headerText}>{t("common.total")}</Text> : null}
              </View>
            ))}
            {columnTotals.cells.map((v, ci) => (
              <View key={`ct${ci}`} style={styles.cell}>
                <Text style={styles.numeric}>{formatCell(v, aggregates[ci % aggCount]?.format)}</Text>
              </View>
            ))}
            {showRowTotals
              ? columnTotals.grand.map((v, gi) => (
                  <View key={`gt${gi}`} style={[styles.cell, styles.totalCell]}>
                    <Text style={styles.numeric}>{formatCell(v, aggregates[gi]?.format)}</Text>
                  </View>
                ))
              : null}
          </View>
        ) : null}
      </View>
    </ScrollView>
  );
};

// ── Chart ────────────────────────────────────────────────────────────────────

export const chartPalette = ["#0f6cbd", "#c50f1f", "#0e700e", "#c19c00", "#8764b8", "#00838f", "#d83b01", "#5c2e91"];

export type ChartFamily = "bar" | "line" | "pie";

export interface ChartSeries {
  label: string;
  values: number[];
  color: string;
}

export interface ChartModel {
  family: ChartFamily;
  horizontal: boolean;
  labels: string[];
  series: ChartSeries[];
  title: string;
  stacked: boolean;
}

/**
 * Normalize the ChartControl wire into what a native renderer needs. Chart.js's own type zoo
 * collapses onto three drawable families here: `column`/`bar` → bar (horizontal for `bar`),
 * `line`/`area`/`radar`/`scatter` → line, `pie`/`doughnut`/`polar` → pie. Pure — unit-tested.
 */
export function chartModelFor(p: {
  labels?: unknown;
  series?: unknown;
  title?: unknown;
  isStacked?: unknown;
}): ChartModel {
  const labels = (Array.isArray(p.labels) ? p.labels : []).map(s);
  const wire = (Array.isArray(p.series) ? (p.series as Record<string, unknown>[]) : []).filter((x) => x != null);
  const first = s(wire[0]?.$type).toLowerCase();
  const family: ChartFamily = /pie|doughnut|polar/.test(first)
    ? "pie"
    : /line|area|radar|scatter|bubble/.test(first)
      ? "line"
      : "bar";
  const horizontal = /(^|\.)bar/.test(first) && !/column/.test(first);
  const series: ChartSeries[] = wire.map((w, i) => {
    const raw = Array.isArray(w.data) ? w.data : Array.isArray(w.values) ? (w.values as unknown[]) : [];
    return {
      label: s(w.label) || `Series ${i + 1}`,
      values: raw.map((v) => {
        const n = Number(v != null && typeof v === "object" ? (v as Record<string, unknown>).y : v);
        return Number.isFinite(n) ? n : 0;
      }),
      color: s(w.backgroundColor) || s(w.borderColor) || chartPalette[i % chartPalette.length],
    };
  });
  return { family, horizontal, labels, series, title: s(p.title), stacked: p.isStacked === true };
}

/** A pie/doughnut slice path (SVG arc) for [start, end] radians. */
export function slicePath(cx: number, cy: number, r: number, start: number, end: number): string {
  const x0 = cx + r * Math.cos(start);
  const y0 = cy + r * Math.sin(start);
  const x1 = cx + r * Math.cos(end);
  const y1 = cy + r * Math.sin(end);
  const large = end - start > Math.PI ? 1 : 0;
  return `M ${cx} ${cy} L ${x0} ${y0} A ${r} ${r} 0 ${large} 1 ${x1} ${y1} Z`;
}

const CHART_HEIGHT = 220;
const PAD = { left: 44, right: 12, top: 16, bottom: 28 };

function esc(t: string): string {
  return t.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/**
 * Draw the model as an SVG document.
 *
 * react-native-svg 15 ships no `elements/index.d.ts`, so its shape components (Rect/Path/G/…) are
 * not reachable as named typed imports under `moduleResolution: Bundler` — only `SvgXml` is. That
 * is why the pack has always drawn vectors (the Icon leaf included) by handing an SVG document to
 * SvgXml, and the chart does the same. Pure, so the geometry is unit-tested without a renderer.
 */
export function chartSvg(model: ChartModel, width: number): string {
  const plotW = width - PAD.left - PAD.right;
  const plotH = CHART_HEIGHT - PAD.top - PAD.bottom;
  const body = model.family === "pie" ? pieSvg(model, width) : cartesianSvg(model, plotW, plotH);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${CHART_HEIGHT}" viewBox="0 0 ${width} ${CHART_HEIGHT}">${body}</svg>`;
}

function cartesianSvg(model: ChartModel, plotW: number, plotH: number): string {
  const all = model.series.flatMap((se) => se.values);
  const max = Math.max(1, ...all);
  const min = Math.min(0, ...all);
  const span = max - min || 1;
  const y = (v: number) => PAD.top + plotH - ((v - min) / span) * plotH;
  const count = Math.max(1, model.labels.length || model.series[0]?.values.length || 1);
  const slot = plotW / count;
  const out: string[] = [];

  // baseline + max gridline label
  out.push(`<path d="M ${PAD.left} ${y(min)} H ${PAD.left + plotW}" stroke="#c7c7c7" stroke-width="1" fill="none"/>`);
  out.push(`<text x="4" y="${y(max) + 4}" font-size="10" fill="#616161">${esc(formatValue(max, "N0"))}</text>`);

  if (model.family === "bar") {
    const bw = (slot * 0.7) / model.series.length;
    model.series.forEach((se, si) =>
      se.values.forEach((v, i) => {
        const x = PAD.left + i * slot + slot * 0.15 + si * bw;
        const top = Math.min(y(v), y(0));
        const h = Math.abs(y(v) - y(0)) || 1;
        out.push(`<rect x="${x}" y="${top}" width="${bw}" height="${h}" rx="2" fill="${esc(se.color)}"/>`);
      }),
    );
  } else {
    model.series.forEach((se) => {
      const pts = se.values.map((v, i) => `${PAD.left + i * slot + slot / 2},${y(v)}`).join(" ");
      out.push(`<polyline points="${pts}" fill="none" stroke="${esc(se.color)}" stroke-width="2"/>`);
    });
  }

  model.labels.forEach((l, i) => {
    out.push(
      `<text x="${PAD.left + i * slot + slot / 2}" y="${PAD.top + plotH + 16}" font-size="10" fill="#616161" text-anchor="middle">${esc(l)}</text>`,
    );
  });
  return out.join("");
}

function pieSvg(model: ChartModel, width: number): string {
  // A circular chart plots ONE series, coloured per slice (the Chart.js rule the web pack follows).
  const values = model.series[0]?.values ?? [];
  const total = values.reduce((a, b) => a + Math.abs(b), 0) || 1;
  const cx = width / 2;
  const cy = CHART_HEIGHT / 2;
  const r = Math.min(cx, cy) - 12;
  let angle = -Math.PI / 2;
  return values
    .map((v, i) => {
      const sweep = (Math.abs(v) / total) * Math.PI * 2;
      const d = slicePath(cx, cy, r, angle, angle + sweep);
      angle += sweep;
      return `<path d="${d}" fill="${chartPalette[i % chartPalette.length]}"/>`;
    })
    .join("");
}

const Chart: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const { width: winWidth } = useWindowDimensions();
  const model = chartModelFor({
    labels: useResolve(control.labels),
    series: useResolve(control.series ?? control.data),
    title: useResolve(control.title),
    isStacked: useResolve(control.isStacked),
  });
  const width = Math.max(240, Math.min(winWidth - 32, 640));

  if (model.series.length === 0) return <Text style={styles.muted}>{t("chart.noData")}</Text>;

  return (
    <View style={{ gap: 6 }}>
      {model.title ? <Text style={styles.chartTitle}>{model.title}</Text> : null}
      <SvgXml xml={chartSvg(model, width)} width={width} height={CHART_HEIGHT} />
      <View style={styles.legend}>
        {model.series.map((se) => (
          <View key={se.label} style={styles.legendItem}>
            <View style={[styles.legendSwatch, { backgroundColor: se.color }]} />
            <Text style={styles.muted}>{se.label}</Text>
          </View>
        ))}
      </View>
    </View>
  );
};

export const rnDataControls: Record<string, ControlComponent> = {
  PivotGrid,
  Chart,
};

const styles = StyleSheet.create({
  body: { fontSize: 13, color: "#242424" },
  muted: { fontSize: 12, color: "#616161" },
  numeric: { fontSize: 13, color: "#242424", textAlign: "right" },
  row: { flexDirection: "row" },
  cell: { minWidth: 110, padding: 8, borderWidth: StyleSheet.hairlineWidth, borderColor: "#e1e1e1" },
  headerCell: { backgroundColor: "#f5f5f5" },
  headerText: { fontSize: 13, fontWeight: "700", color: "#242424" },
  totalCell: { backgroundColor: "#fafafa" },
  totalRow: { backgroundColor: "#fafafa" },
  chartTitle: { fontSize: 15, fontWeight: "600", color: "#242424" },
  legend: { flexDirection: "row", flexWrap: "wrap", gap: 12 },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 6 },
  legendSwatch: { width: 10, height: 10, borderRadius: 2 },
});
