// The PURE pivot model — the cross-tab computation, the .NET-style cell formatter and the
// header-grouping helper, with NO renderer imports.
//
// It used to live inside pivot.tsx next to the Fluent table that renders it, which meant any other
// leaf pack (React Native) could not reach it without pulling @fluentui/react-components into a
// native bundle. Extracting it keeps ONE aggregation implementation shared by every pack rather
// than a second, drifting copy per platform. pivot.tsx re-exports these, so the web pack's public
// surface is unchanged and pivot.test.tsx keeps pinning the same functions.

import type { Json } from "../area/types.js";
import { formatValue } from "./format.js";
import { str } from "./common.js";

export interface PivotDimensionWire {
  field: string;
  displayName?: string;
  propertyPath?: string;
  width?: string;
  sortOrder?: Json;
}

export interface PivotAggregateWire extends PivotDimensionWire {
  function?: Json;
  format?: string;
  textAlign?: Json;
}

export interface PivotConfigWire {
  rowDimensions?: PivotDimensionWire[];
  columnDimensions?: PivotDimensionWire[];
  aggregates?: PivotAggregateWire[];
  showRowTotals?: boolean;
  showColumnTotals?: boolean;
  pageSize?: number;
}

export interface PivotRowOut {
  /** One value per row dimension. */
  keys: string[];
  /** One value per leaf column (colKeys × aggregates, aggregates innermost); null = no data. */
  cells: (number | null)[];
  /** One value per aggregate — the row's total across all column groups. */
  totals: (number | null)[];
}

export interface PivotResult {
  rowDims: PivotDimensionWire[];
  colDims: PivotDimensionWire[];
  aggregates: PivotAggregateWire[];
  /** Distinct column-dimension tuples, sorted (each level per its sortOrder). */
  colKeys: string[][];
  rows: PivotRowOut[];
  /** Per leaf column when showColumnTotals; the trailing entries are the grand totals per aggregate. */
  columnTotals: { cells: (number | null)[]; grand: (number | null)[] } | null;
}

interface Acc {
  sum: number;
  count: number;
  min: number;
  max: number;
}

function newAcc(): Acc {
  return { sum: 0, count: 0, min: Number.POSITIVE_INFINITY, max: Number.NEGATIVE_INFINITY };
}

function accumulate(acc: Acc, v: number): void {
  acc.sum += v;
  acc.count += 1;
  if (v < acc.min) acc.min = v;
  if (v > acc.max) acc.max = v;
}

/** Aggregate function names arrive as strings (hub string-enum converter); ordinals tolerated. */
function applyFn(acc: Acc | undefined, fn: Json): number | null {
  if (!acc || acc.count === 0) return null;
  const f = typeof fn === "number" ? ["Sum", "Average", "Count", "Min", "Max"][fn] : str(fn) || "Sum";
  switch (f) {
    case "Average":
      return acc.sum / acc.count;
    case "Count":
      return acc.count;
    case "Min":
      return acc.min;
    case "Max":
      return acc.max;
    default:
      return acc.sum;
  }
}

/** Read a row property — the wire serializes rows camelCase while PropertyPath keeps the C# name. */
function readProp(row: Record<string, Json>, path: string): Json {
  let cur: Json = row;
  for (const part of path.split(".")) {
    if (cur == null || typeof cur !== "object") return undefined;
    const camel = part.length > 0 ? part[0].toLowerCase() + part.slice(1) : part;
    cur = part in cur ? cur[part] : cur[camel];
  }
  return cur;
}

function compareValues(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true });
}

function sortTuples(tuples: string[][], dims: PivotDimensionWire[]): string[][] {
  return [...tuples].sort((a, b) => {
    for (let i = 0; i < dims.length; i++) {
      const desc = str(dims[i]?.sortOrder) === "Descending" || dims[i]?.sortOrder === 1;
      const c = compareValues(a[i] ?? "", b[i] ?? "");
      if (c !== 0) return desc ? -c : c;
    }
    return 0;
  });
}

const SEP = "\u0001"; // non-printable separator: tuple values cannot collide with composite keys

/** Compute the cross-tab. Pure — pinned by pivot.test.tsx. */
export function computePivot(data: Json, config: PivotConfigWire | undefined): PivotResult {
  const rows: Record<string, Json>[] = (Array.isArray(data) ? data : []).filter((r) => r != null && typeof r === "object");
  const rowDims = config?.rowDimensions ?? [];
  const colDims = config?.columnDimensions ?? [];
  const aggregates = config?.aggregates?.length ? config.aggregates : [];

  const rowKeySet = new Map<string, string[]>();
  const colKeySet = new Map<string, string[]>();
  // cell accumulators per (rowKey, colKey, aggregate)
  const cells = new Map<string, Acc>();

  for (const row of rows) {
    const rTuple = rowDims.map((d) => str(readProp(row, d.propertyPath ?? d.field)));
    const cTuple = colDims.map((d) => str(readProp(row, d.propertyPath ?? d.field)));
    const rKey = rTuple.join(SEP);
    const cKey = cTuple.join(SEP);
    if (!rowKeySet.has(rKey)) rowKeySet.set(rKey, rTuple);
    if (!colKeySet.has(cKey)) colKeySet.set(cKey, cTuple);
    aggregates.forEach((agg, ai) => {
      const raw = readProp(row, agg.propertyPath ?? agg.field);
      const v = raw == null || raw === "" ? NaN : Number(raw);
      if (Number.isNaN(v)) return;
      for (const key of [
        `${rKey}${SEP}|${cKey}${SEP}|${ai}`, // cell
        `${rKey}${SEP}|*${SEP}|${ai}`, // row total
        `*${SEP}|${cKey}${SEP}|${ai}`, // column total
        `*${SEP}|*${SEP}|${ai}`, // grand total
      ]) {
        let acc = cells.get(key);
        if (!acc) cells.set(key, (acc = newAcc()));
        accumulate(acc, v);
      }
    });
  }

  const colKeys = sortTuples([...colKeySet.values()], colDims);
  const rowKeys = sortTuples([...rowKeySet.values()], rowDims);

  const outRows: PivotRowOut[] = rowKeys.map((rTuple) => {
    const rKey = rTuple.join(SEP);
    const cellsOut: (number | null)[] = [];
    for (const cTuple of colKeys)
      aggregates.forEach((agg, ai) => {
        cellsOut.push(applyFn(cells.get(`${rKey}${SEP}|${cTuple.join(SEP)}${SEP}|${ai}`), agg.function));
      });
    const totals = aggregates.map((agg, ai) => applyFn(cells.get(`${rKey}${SEP}|*${SEP}|${ai}`), agg.function));
    return { keys: rTuple, cells: cellsOut, totals };
  });

  let columnTotals: PivotResult["columnTotals"] = null;
  if (config?.showColumnTotals !== false) {
    const cellsOut: (number | null)[] = [];
    for (const cTuple of colKeys)
      aggregates.forEach((agg, ai) => {
        cellsOut.push(applyFn(cells.get(`*${SEP}|${cTuple.join(SEP)}${SEP}|${ai}`), agg.function));
      });
    const grand = aggregates.map((agg, ai) => applyFn(cells.get(`*${SEP}|*${SEP}|${ai}`), agg.function));
    columnTotals = { cells: cellsOut, grand };
  }

  return { rowDims, colDims, aggregates, colKeys, rows: outRows, columnTotals };
}

// ---- formatting / header grouping -----------------------------------------------------------

/** ".NET-style" number format for pivot cells: agg.format may be "N2" or "{0:N2}". */
export function formatCell(v: number | null, format?: string): string {
  if (v == null) return "";
  const f = format?.replace(/^\{0:(.+)\}$/, "$1");
  return formatValue(v, f || "N2");
}

/** Group consecutive column tuples that share a prefix up to `level` — for header colSpans. */
export function groupsAt(colKeys: string[][], level: number): { label: string; span: number }[] {
  const groups: { label: string; span: number; prefix: string }[] = [];
  for (const key of colKeys) {
    const prefix = key.slice(0, level + 1).join(SEP);
    const prev = groups[groups.length - 1];
    if (prev && prev.prefix === prefix) prev.span += 1;
    else groups.push({ label: key[level] ?? "", span: 1, prefix });
  }
  return groups;
}

