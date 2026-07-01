import type { ReactNode } from "react";
import { useState } from "react";
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
import { ChevronDown20Regular, ChevronRight20Regular } from "@fluentui/react-icons";
import type { Json, UiControl } from "../area/types.js";
import { useResolve } from "../area/context.js";
import { ControlRenderer } from "../render/ControlRenderer.js";
import { str } from "./common.js";

type Row = Record<string, Json> & { __id: string };

/** .NET-style numeric formatting ("N0", "C2", "P1"; "{0:N2}" tolerated). Shared with PivotGrid. */
export function formatValue(v: Json, format?: string): string {
  if (v == null) return "";
  const f = format?.replace(/^\{0:(.+)\}$/, "$1");
  if (f && typeof v === "number") {
    const m = /^([NCP])(\d+)?$/i.exec(f);
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

// ---- Catalog ------------------------------------------------------------------------------------
// CatalogControl wire (src/MeshWeaver.Layout/Catalog/CatalogControl.cs):
//   { groups: [{ key, label, emoji, order, isExpanded, items: UiControl[], totalCount }],
//     collapsibleSections, showCounts, sectionGap, cardHeight, ... }
// Each item is a full UiControl tree — rendered through the registry, exactly like Blazor's
// CatalogView dispatches each item view.

interface CatalogGroupWire {
  key?: string;
  label?: string;
  emoji?: string;
  order?: number;
  isExpanded?: boolean;
  items?: UiControl[];
  totalCount?: number;
}

function CatalogSection({ group, collapsible, showCounts, cardHeight }: { group: CatalogGroupWire; collapsible: boolean; showCounts: boolean; cardHeight: number }): ReactNode {
  const [open, setOpen] = useState(group.isExpanded !== false);
  const items = Array.isArray(group.items) ? group.items : [];
  const count = group.totalCount || items.length;
  return (
    <section>
      <div
        role={collapsible ? "button" : undefined}
        onClick={collapsible ? () => setOpen((o) => !o) : undefined}
        style={{ display: "flex", alignItems: "center", gap: 6, cursor: collapsible ? "pointer" : "default", padding: "4px 0" }}
      >
        {collapsible ? (open ? <ChevronDown20Regular /> : <ChevronRight20Regular />) : null}
        <Text weight="semibold" size={400}>
          {group.emoji ? `${group.emoji} ` : ""}
          {str(group.label ?? group.key)}
        </Text>
        {showCounts ? (
          <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
            ({count})
          </Text>
        ) : null}
      </div>
      {open ? (
        <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))" }}>
          {items.map((item, i) => (
            <div key={i} style={{ minHeight: cardHeight }}>
              <ControlRenderer control={item} />
            </div>
          ))}
        </div>
      ) : null}
    </section>
  );
}

function CatalogView({ control }: { control: UiControl }): ReactNode {
  const groups = (useResolve(control.groups) as CatalogGroupWire[]) ?? [];
  const legacyItems = useResolve(control.data);
  const collapsible = control.collapsibleSections !== false;
  const showCounts = control.showCounts !== false;
  const sectionGap = Number(control.sectionGap ?? 16) || 16;
  const cardHeight = Number(control.cardHeight ?? 140) || 140;

  if (Array.isArray(groups) && groups.length > 0) {
    const ordered = [...groups].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: sectionGap }}>
        {ordered.map((g, i) => (
          <CatalogSection key={g.key ?? i} group={g} collapsible={collapsible} showCounts={showCounts} cardHeight={cardHeight} />
        ))}
      </div>
    );
  }

  // Legacy/demo shape: a flat `data` array of {name,title,description} records.
  const arr = Array.isArray(legacyItems) ? legacyItems : [];
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
  Catalog: CatalogView,
};
