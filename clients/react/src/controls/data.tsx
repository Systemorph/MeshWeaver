import type { ReactNode } from "react";
import {
  Card,
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  Text,
  createTableColumn,
  type TableColumnDefinition,
} from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { ControlRenderer } from "../render/ControlRenderer.js";
import { str } from "./common.js";

type Row = Record<string, Json> & { __id: string };

function formatValue(v: Json, format?: string): string {
  if (v == null) return "";
  if (format && typeof v === "number") {
    const m = /^([NCP])(\d+)?$/i.exec(format);
    if (m) {
      const digits = m[2] ? Number(m[2]) : m[1].toUpperCase() === "N" ? 0 : 2;
      const n = v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
      if (m[1].toUpperCase() === "C") return `$${n}`;
      if (m[1].toUpperCase() === "P") return `${(v * 100).toFixed(digits)}%`;
      return n;
    }
  }
  return str(v);
}

function buildColumns(columnDefs: UiControl[]): TableColumnDefinition<Row>[] {
  return columnDefs.map((col, i) => {
    const property = str(col.property ?? col.sortBy ?? `col${i}`);
    const title = str(col.title ?? property);
    const format = col.format != null ? String(col.format) : col.displayFormat != null ? String(col.displayFormat) : undefined;
    return createTableColumn<Row>({
      columnId: property || String(i),
      compare: (a, b) => str(a[property]).localeCompare(str(b[property]), undefined, { numeric: true }),
      renderHeaderCell: () => title,
      renderCell: (row) =>
        col.$type === "TemplateColumn" && col.template ? <ControlRenderer control={col.template as UiControl} /> : formatValue(row[property], format),
    });
  });
}

function DataGridView({ control }: { control: UiControl }): ReactNode {
  const rawRows = useResolve(control.data);
  const rows: Row[] = (Array.isArray(rawRows) ? rawRows : []).map((r, i) => ({ ...(r ?? {}), __id: String(i) }));
  const columnDefs = (Array.isArray(control.columns) ? control.columns : []) as UiControl[];
  const columns = buildColumns(columnDefs);
  if (columns.length === 0) return <Text italic>No columns</Text>;
  return (
    <DataGrid
      items={rows}
      columns={columns}
      sortable={control.sortable !== false}
      resizableColumns={control.resizableColumns !== false}
      getRowId={(item) => (item as Row).__id}
      style={{ width: "100%" }}
    >
      <DataGridHeader>
        <DataGridRow>{({ renderHeaderCell }) => <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>}</DataGridRow>
      </DataGridHeader>
      <DataGridBody<Row>>
        {({ item, rowId }) => (
          <DataGridRow<Row> key={rowId}>{({ renderCell }) => <DataGridCell>{renderCell(item)}</DataGridCell>}</DataGridRow>
        )}
      </DataGridBody>
    </DataGrid>
  );
}

function CatalogView({ control }: { control: UiControl }): ReactNode {
  const items = useResolve(control.data);
  const arr = Array.isArray(items) ? items : [];
  return (
    <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))" }}>
      {arr.map((it, i) => (
        <Card key={i} style={{ padding: 12 }}>
          <Text weight="semibold">{str(it?.name ?? it?.title ?? it?.id ?? `Item ${i + 1}`)}</Text>
          {it?.description ? <Text size={200}>{str(it.description)}</Text> : null}
        </Card>
      ))}
    </div>
  );
}

export const dataControls = {
  DataGrid: DataGridView,
  PivotGrid: DataGridView,
  Catalog: CatalogView,
  MeshNodeCollection: CatalogView,
};
