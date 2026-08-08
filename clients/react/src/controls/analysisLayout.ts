// The analysis views' geometry (#745) — pure, no renderer imports, so EVERY leaf pack (web Fluent,
// React Native) draws a tower and a comparison from the SAME numbers. Same reason `format.ts` is
// split out of `data.tsx`: a module that imports Fluent cannot be reached from a native bundle.
//
// These are faithful ports of `TowerControl.Layout` / `ComparisonBarsControl.Layout` /
// `TowerControl.NavigableHref` in src/MeshWeaver.Layout. Those C# functions are the reference
// semantics — `AnalysisControlsTest.cs` pins them and `analysis.test.tsx` mirrors its cases here, so
// a divergence fails a test rather than producing two subtly different pictures.

import type { Json } from "../area/types.js";

export interface TowerBandWire {
  label?: Json;
  terms?: Json;
  attachment?: Json;
  cover?: Json;
  share?: Json;
  href?: Json;
}

export interface TowerBandPlacement {
  band: TowerBandWire;
  bottomPercent: number;
  heightPercent: number;
}

export interface TowerPlacement {
  top: number;
  retention: number;
  bands: TowerBandPlacement[];
}

export interface ComparisonPairWire {
  label?: Json;
  left?: Json;
  right?: Json;
}

export interface ComparisonRow {
  pair: ComparisonPairWire;
  leftPercent: number | null;
  rightPercent: number | null;
}

export interface ComparisonPlacement {
  max: number;
  rows: ComparisonRow[];
}

export function clamp(v: number, lo: number, hi: number): number {
  return Math.min(Math.max(v, lo), hi);
}

export function num(v: unknown): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
}

/** Null/undefined stays null — an absent side is NOT a zero. */
export function optionalNum(v: unknown): number | null {
  if (v == null) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/** A bound row property is either the array itself or a pointer that resolved to one. */
export function analysisRows<T>(value: Json): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

/** Port of TowerControl.Layout. Null when there is nothing honest to draw. */
export function towerLayout(bands: TowerBandWire[]): TowerPlacement | null {
  if (bands.length === 0) return null;
  const ordered = [...bands].sort((a, b) => num(a.attachment) - num(b.attachment) || num(a.cover) - num(b.cover));
  const top = Math.max(...ordered.map((b) => num(b.attachment) + num(b.cover)));
  if (!(top > 0)) return null;
  return {
    top,
    retention: Math.max(0, Math.min(...ordered.map((b) => num(b.attachment)))),
    bands: ordered.map((band) => ({
      band,
      bottomPercent: clamp(num(band.attachment) / top, 0, 1) * 100,
      heightPercent: clamp(num(band.cover) / top, 0, 1) * 100,
    })),
  };
}

/** Zero, the exhaustion point, and every attachment — collapsed where they would overprint. */
export function towerTicks(layout: TowerPlacement): { percent: number; amount: number }[] {
  const amounts = [...new Set([0, layout.top, ...layout.bands.map((b) => num(b.band.attachment))])]
    .filter((a) => a >= 0 && a <= layout.top)
    .sort((a, b) => a - b);

  const ticks: { percent: number; amount: number }[] = [];
  for (const amount of amounts) {
    const percent = (amount / layout.top) * 100;
    if (ticks.length > 0 && percent - ticks[ticks.length - 1].percent < 4) continue;
    ticks.push({ percent, amount });
  }
  return ticks;
}

/** Port of TowerControl.NavigableHref — a bare mesh path gains its leading slash. */
export function navigableHref(href: string): string | null {
  const trimmed = href.trim();
  if (!trimmed) return null;
  return trimmed.startsWith("/") || trimmed.includes("://") ? trimmed : `/${trimmed}`;
}

/**
 * Port of ComparisonBarsControl.Layout. Null when no side carries a positive value.
 *
 * An absent side stays null rather than becoming 0 — a renderer must be able to tell "we hold
 * nothing" from "we were never told", which is the whole reason the control exists.
 *
 * A NEGATIVE value gets no length. The scale runs from zero upward, so a negative ratio has no
 * direction to be drawn in — and the minimum-visible sliver would LIFT it into a small POSITIVE bar,
 * making the chart state the opposite of its data. The figure is still printed with its sign.
 * Must stay identical to the C# `ComparisonBarsControl.Percent`.
 */
export function comparisonLayout(pairs: ComparisonPairWire[]): ComparisonPlacement | null {
  if (pairs.length === 0) return null;
  const max = Math.max(...pairs.map((p) => Math.max(optionalNum(p.left) ?? 0, optionalNum(p.right) ?? 0)));
  if (!(max > 0)) return null;
  const percent = (v: number | null) =>
    v === null ? null : v < 0 ? 0 : clamp(Math.max(v / max, 0.005), 0, 1) * 100;
  return {
    max,
    rows: pairs.map((pair) => ({
      pair,
      leftPercent: percent(optionalNum(pair.left)),
      rightPercent: percent(optionalNum(pair.right)),
    })),
  };
}
