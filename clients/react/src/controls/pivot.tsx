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
import { str } from "./common.js";
// The pure cross-tab model lives in pivotModel.ts (no renderer imports) so the React Native pack
// shares ONE aggregation implementation with this one. Re-exported below for back-compat.
import { computePivot, formatCell, groupsAt, type PivotConfigWire } from "./pivotModel.js";
export {
  computePivot,
  formatCell,
  groupsAt,
} from "./pivotModel.js";
export type {
  PivotAggregateWire,
  PivotConfigWire,
  PivotDimensionWire,
  PivotResult,
  PivotRowOut,
} from "./pivotModel.js";

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
