using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.GridModel;
using MeshWeaver.Pivot;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Reporting.Models
{
    public static class GridOptionsMapper
    {
        public static IObservable<GridControl> ToGrid(this IPivotBuilder pivotBuilder) =>
            pivotBuilder.Execute().Select(x => x.ToGrid());

        public static GridControl ToGrid(this PivotModel pivotModel) =>
            new(MapToGridOptions(pivotModel));

        public static GridOptions MapToGridOptions(PivotModel pivotModel)
        {
            var gridColumns = MapToColDef(pivotModel.Columns);

            var gridRows = MapToGridRows(pivotModel.Rows);

            var gridOptions = new GridOptions { ColumnDefs = gridColumns, RowData = gridRows };

            gridOptions = gridOptions.DefaultGridOptions();

            if (pivotModel.HasRowGrouping)
                gridOptions = gridOptions.AsTreeModel();

            return gridOptions;
        }

        public static List<ColDef> MapToColDef(IReadOnlyCollection<Column> columns)
        {
            var gridColumns = new List<ColDef>();

            foreach (var column in columns)
            {
                if (column is ColumnGroup pivotColumnGroup)
                {
                    gridColumns.Add(pivotColumnGroup.TransformToColGroupDef());
                    continue;
                }

                gridColumns.Add(column.TransformToColDef());
            }

            return gridColumns;
        }

        public static List<object> MapToGridRows(IReadOnlyCollection<Row> rows)
        {
            var gridRows = new List<object>();

            foreach (var row in rows)
                gridRows.Add(new GridRow(row.RowGroup, row.Data));

            return gridRows;
        }

        public static ColDef TransformToColDef(this ItemWithCoordinates item)
        {
            return new()
            {
                ColId = item.Id,
                HeaderName = item.DisplayName,
                ValueGetter = ValueGetter(item)
            };
        }

        private static string ValueGetter(ItemWithCoordinates item)
        {
            var dataBinding = $"{GridBindings.Data}";
            var ret = dataBinding;
            foreach (var coordinate in item.Coordinates)
            {
                dataBinding = $"{dataBinding}{coordinate.ToString().Accessor()}";
                ret += " && " + dataBinding;
            }
            return ret;
        }

        public static ColGroupDef TransformToColGroupDef(this ColumnGroup item)
        {
            IImmutableList<ColDef> children = item
                .Children.Select(x =>
                    x is ColumnGroup g ? g.TransformToColGroupDef() : x.TransformToColDef()
                )
                .ToImmutableList();

            var show =
                item.Coordinates.Last() == IPivotGrouper<object, ColumnGroup>.TotalGroup.Id
                    ? "closed"
                    : "open";

            var colGroupDef = new ColGroupDef
            {
                ColumnGroupShow = show,
                GroupId = item.Id,
                HeaderName = item.DisplayName,
                Children = children
            };

            return colGroupDef;
        }

        private static GridOptions DefaultGridOptions(this GridOptions options)
        {
            var gridOptions = options.WithDefaultSettings() with
            {
                GetRowStyle = GridBindings.GetRowStyle
            };
            return gridOptions;
        }

        private static GridOptions AsTreeModel(this GridOptions options)
        {
            var gridOptions = options with
            {
                Components = options.Components.SetItem(
                    GridBindings.DisplayNameGroupColumnRenderer.Name,
                    GridBindings.DisplayNameGroupColumnRenderer.Component
                ),
                TreeData = true,
                GetDataPath = GridBindings.GetDataPath,
                AutoGroupColumnDef = new ColDef
                {
                    HeaderName = PivotConst.DefaultGroupName,
                    ColId = PivotConst.DefaultGroupName,
                    Resizable = true,
                    CellStyle = new CellStyle { TextAlign = "left" },
                    CellRendererParams = new CellRendererParams
                    {
                        SuppressCount = true,
                        InnerRenderer = GridBindings.DisplayNameGroupColumnRenderer.Name
                    }
                }
            };
            return gridOptions;
        }
    }
}
