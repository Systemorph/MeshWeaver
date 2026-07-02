// PivotGridControl — a REAL pivot (cross-tab), the React mirror of the Blazor RadzenPivotGridView.
// Wire contract (src/MeshWeaver.Layout/Pivot/PivotGridControl.cs + PivotConfiguration.cs):
//   { data: Row[], configuration: {
//       rowDimensions:    [{ field, displayName, propertyPath, width, sortOrder }],
//       columnDimensions: [{ field, displayName, propertyPath, sortOrder }],
//       aggregates:       [{ field, displayName, propertyPath, function: "Sum"|"Average"|"Count"|"Min"|"Max",
//                            format, textAlign, sortOrder }],
//       showRowTotals, showColumnTotals, pageSize },
//     showPager, pageSize }
// The pivot computation is a PURE function (computePivot) pinned by pivot.test.tsx; the component
// renders the result as a themed table (nested column-group header rows, measures innermost,
// row/column totals, optional pager).

import type { CSSProperties, ReactNode } from "react";
import { useMemo, useState } from "react";
import { Button, Text } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { formatValue } from "./data.js";
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

// ---- rendering ----------------------------------------------------------------------------------

const cellBorder = "1px solid var(--colorNeutralStroke2)";
const headerStyle: CSSProperties = {
  border: cellBorder,
  padding: "6px 10px",
  background: "var(--colorNeutralBackground3)",
  fontWeight: 600,
  textAlign: "left",
  whiteSpace: "nowrap",
};
const cellStyle: CSSProperties = { border: cellBorder, padding: "6px 10px", textAlign: "right", fontVariantNumeric: "tabular-nums" };
const dimCellStyle: CSSProperties = { border: cellBorder, padding: "6px 10px", textAlign: "left" };

/** ".NET-style" number format for pivot cells: agg.format may be "N2" or "{0:N2}". */
export function formatCell(v: number | null, format?: string): string {
  if (v == null) return "";
  const f = format?.replace(/^\{0:(.+)\}$/, "$1");
  return formatValue(v, f || "N2");
}

/** Group consecutive column tuples that share a prefix up to `level` — for header colSpans. */
function groupsAt(colKeys: string[][], level: number): { label: string; span: number }[] {
  const groups: { label: string; span: number; prefix: string }[] = [];
  for (const key of colKeys) {
    const prefix = key.slice(0, level + 1).join(SEP);
    const prev = groups[groups.length - 1];
    if (prev && prev.prefix === prefix) prev.span += 1;
    else groups.push({ label: key[level] ?? "", span: 1, prefix });
  }
  return groups;
}

export function PivotGridView({ control }: { control: UiControl }): ReactNode {
  const data = useResolve(control.data);
  const config = useResolve(control.configuration) as PivotConfigWire | undefined;
  const pivot = useMemo(() => computePivot(data, config), [data, config]);
  const showPager = control.showPager === true;
  const pageSize = Number(control.pageSize ?? config?.pageSize ?? 50) || 50;
  const [page, setPage] = useState(0);

  const { rowDims, colDims, aggregates, colKeys, rows, columnTotals } = pivot;
  if (aggregates.length === 0)
    return (
      <Text italic size={200}>
        No pivot aggregates configured
      </Text>
    );

  const showRowTotals = config?.showRowTotals !== false && colDims.length > 0;
  const showMeasureRow = aggregates.length > 1 || colDims.length === 0;
  const headerRowCount = colDims.length + (showMeasureRow ? 1 : 0) || 1;
  const aggCount = aggregates.length;

  const pageCount = showPager ? Math.max(1, Math.ceil(rows.length / pageSize)) : 1;
  const current = Math.min(page, pageCount - 1);
  const visibleRows = showPager ? rows.slice(current * pageSize, (current + 1) * pageSize) : rows;

  return (
    <div style={{ overflowX: "auto", width: "100%" }}>
      <table role="table" style={{ borderCollapse: "collapse", minWidth: "100%", fontSize: 13 }}>
        <thead>
          {/* One header row per column dimension: grouped labels spanning their leaves. */}
          {colDims.map((dim, level) => (
            <tr key={`cd${level}`}>
              {level === 0
                ? rowDims.map((rd) => (
                    <th key={rd.field} rowSpan={headerRowCount} style={{ ...headerStyle, width: rd.width }}>
                      {rd.displayName ?? rd.field}
                    </th>
                  ))
                : null}
              {groupsAt(colKeys, level).map((g, i) => (
                <th key={i} colSpan={g.span * aggCount} style={{ ...headerStyle, textAlign: "center" }}>
                  {g.label}
                </th>
              ))}
              {level === 0 && showRowTotals ? (
                <th rowSpan={headerRowCount} colSpan={aggCount} style={{ ...headerStyle, textAlign: "center" }}>
                  Total
                </th>
              ) : null}
            </tr>
          ))}
          {showMeasureRow ? (
            <tr>
              {colDims.length === 0
                ? rowDims.map((rd) => (
                    <th key={rd.field} style={{ ...headerStyle, width: rd.width }}>
                      {rd.displayName ?? rd.field}
                    </th>
                  ))
                : null}
              {colKeys.map((ck, i) =>
                aggregates.map((agg) => (
                  <th key={`${i}:${agg.field}`} style={{ ...headerStyle, textAlign: "right" }}>
                    {agg.displayName ?? agg.field}
                  </th>
                )),
              )}
            </tr>
          ) : null}
        </thead>
        <tbody>
          {visibleRows.map((r, ri) => (
            <tr key={ri}>
              {r.keys.map((k, ki) => (
                <td key={ki} style={dimCellStyle}>
                  {k}
                </td>
              ))}
              {r.cells.map((c, ci) => (
                <td key={ci} style={cellStyle}>
                  {formatCell(c, aggregates[ci % aggCount]?.format)}
                </td>
              ))}
              {showRowTotals
                ? r.totals.map((t, ti) => (
                    <td key={`t${ti}`} style={{ ...cellStyle, fontWeight: 600 }}>
                      {formatCell(t, aggregates[ti]?.format)}
                    </td>
                  ))
                : null}
            </tr>
          ))}
          {columnTotals ? (
            <tr>
              <td colSpan={Math.max(1, rowDims.length)} style={{ ...dimCellStyle, fontWeight: 600 }}>
                Total
              </td>
              {columnTotals.cells.map((c, ci) => (
                <td key={ci} style={{ ...cellStyle, fontWeight: 600 }}>
                  {formatCell(c, aggregates[ci % aggCount]?.format)}
                </td>
              ))}
              {showRowTotals
                ? columnTotals.grand.map((g, gi) => (
                    <td key={`g${gi}`} style={{ ...cellStyle, fontWeight: 700 }}>
                      {formatCell(g, aggregates[gi]?.format)}
                    </td>
                  ))
                : null}
            </tr>
          ) : null}
        </tbody>
      </table>
      {showPager && pageCount > 1 ? (
        <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "8px 0" }}>
          <Button size="small" disabled={current === 0} onClick={() => setPage(current - 1)}>
            Previous
          </Button>
          <Text size={200}>
            Page {current + 1} of {pageCount}
          </Text>
          <Button size="small" disabled={current >= pageCount - 1} onClick={() => setPage(current + 1)}>
            Next
          </Button>
        </div>
      ) : null}
    </div>
  );
}

export const pivotControls = {
  PivotGrid: PivotGridView,
};
