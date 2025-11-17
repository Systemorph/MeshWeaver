using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using MeshWeaver.GridModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot;
using MeshWeaver.Pivot.Builder.Interfaces;
using MeshWeaver.Pivot.Grouping;
using MeshWeaver.Pivot.Models;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Reporting.Models
{
    public static class GridOptionsMapper
    {
        public static IObservable<UiControl> ToGrid(this IPivotBuilder pivotBuilder) =>
            pivotBuilder.Execute().Select<PivotModel, UiControl>(pivotModel =>
            {
                // Check if there are column groups - only then we need RadzenPivotDataGrid
                var hasColumnGroups = pivotModel.Columns.OfType<ColumnGroup>().Any();

                if (hasColumnGroups)
                {
                    // Get raw data from pivot builder and let Radzen do the pivoting
                    var rawData = GetRawDataFromPivotBuilder(pivotBuilder);
                    var configuration = BuildPivotConfigurationFromBuilder(pivotBuilder, pivotModel);
                    return new PivotGridControl(rawData, configuration);
                }

                // Use DataGridControl for grids without column groups
                return MapToDataGridControl(pivotModel);
            });

        private static bool HasMultiLevelGrouping(PivotModel pivotModel)
        {
            // Check if there's any row grouping - this indicates pivot structure
            var hasRowGrouping = pivotModel.Rows.Any(r => r.RowGroup != null);

            // Check if there are column groups - this indicates column dimensions
            var hasColumnGrouping = pivotModel.Columns.OfType<ColumnGroup>().Any();

            // If we have both row and column grouping, it's a pivot table that needs RadzenPivotDataGrid
            if (hasRowGrouping && hasColumnGrouping)
                return true;

            // Check if there are multiple levels in row dimensions
            if (pivotModel.Rows.Any())
            {
                var firstRow = pivotModel.Rows.First();
                if (firstRow.RowGroup?.Coordinates.Count > 1)
                    return true;
            }

            // Check if there are multiple levels in column dimensions
            if (hasColumnGrouping)
            {
                var firstGroup = pivotModel.Columns.OfType<ColumnGroup>().First();
                if (firstGroup.Coordinates.Count > 1)
                    return true;
            }

            return false;
        }

        public static IObservable<PivotGridControl> ToPivotGrid(this IPivotBuilder pivotBuilder)
        {
            return pivotBuilder.Execute().Select(pivotModel =>
            {
                // Get raw data from pivot builder and let Radzen do the pivoting
                var rawData = GetRawDataFromPivotBuilder(pivotBuilder);
                var configuration = BuildPivotConfigurationFromBuilder(pivotBuilder, pivotModel);
                return new PivotGridControl(rawData, configuration);
            });
        }

        private static object GetRawDataFromPivotBuilder(IPivotBuilder pivotBuilder)
        {
            // Access the raw Objects from the pivot builder using reflection
            var builderType = pivotBuilder.GetType();
            var objectsProperty = builderType.GetProperty("Objects");
            if (objectsProperty == null)
                return Array.Empty<object>();

            var objects = objectsProperty.GetValue(pivotBuilder);

            // Convert to array of objects
            if (objects is System.Collections.IEnumerable enumerable and not string)
            {
                return enumerable.Cast<object>().ToArray();
            }

            // Return as array
            return objects ?? Array.Empty<object>();
        }

        private static PivotConfiguration BuildPivotConfigurationFromBuilder(IPivotBuilder _, PivotModel pivotModel)
        {
            // Extract row and column slice information from the pivot model
            var rowGrouper = pivotModel.Rows.FirstOrDefault()?.RowGroup?.GrouperName;
            var columnGrouper = pivotModel.Columns.OfType<ColumnGroup>().FirstOrDefault()?.GrouperName;

            var rowDimensions = new List<PivotDimension>();
            var columnDimensions = new List<PivotDimension>();
            var aggregates = new List<PivotAggregate>();

            // Add row dimension if present
            if (!string.IsNullOrEmpty(rowGrouper))
            {
                rowDimensions.Add(new PivotDimension
                {
                    Field = rowGrouper,
                    DisplayName = rowGrouper,
                    PropertyPath = rowGrouper,
                    TypeName = "System.Object"
                });
            }

            // Add column dimension if present
            if (!string.IsNullOrEmpty(columnGrouper))
            {
                columnDimensions.Add(new PivotDimension
                {
                    Field = columnGrouper,
                    DisplayName = columnGrouper,
                    PropertyPath = columnGrouper,
                    TypeName = "System.Object"
                });
            }

            // Add default aggregate (Amount)
            aggregates.Add(new PivotAggregate
            {
                Field = "Amount",
                DisplayName = "Amount",
                PropertyPath = "Amount",
                TypeName = "System.Double",
                Function = AggregateFunction.Sum,
                Format = "{0:C}",
                TextAlign = GridModel.TextAlign.Right
            });

            return new PivotConfiguration
            {
                RowDimensions = rowDimensions,
                ColumnDimensions = columnDimensions,
                Aggregates = aggregates,
                ShowRowTotals = true,
                ShowColumnTotals = true
            };
        }

        public static DataGridControl MapToDataGridControl(PivotModel pivotModel)
        {
            var rows = MapToJsonObjects(pivotModel.Rows).ToList();
            var columns = MapToDataGridColumns(pivotModel.Columns);

            // Serialize to JsonElement so DataGridView can deserialize it
            var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(rows);

            var dataGrid = new DataGridControl(jsonElement)
            {
                Columns = columns.ToImmutableList<object>()
            };

            return dataGrid;
        }

        private static IEnumerable<Dictionary<string, object>> MapToDictionaries(PivotModel pivotModel)
        {
            // Get dimension and aggregate information
            var rowDimensionName = pivotModel.Rows.FirstOrDefault()?.RowGroup?.GrouperName;
            var columnGroups = pivotModel.Columns.OfType<ColumnGroup>().ToList();
            var columnDimensionName = columnGroups.FirstOrDefault()?.GrouperName;

            // Get all leaf columns (recursively from ColumnGroup children)
            var dataColumns = GetLeafColumns(pivotModel.Columns).ToList();

            if (string.IsNullOrEmpty(rowDimensionName) || string.IsNullOrEmpty(columnDimensionName) || !dataColumns.Any())
                yield break;

            // Unpivot: For each row, create multiple flat records
            foreach (var row in pivotModel.Rows)
            {
                if (row.RowGroup == null || row.Data == null)
                    continue;

                // Get the row dimension value
                var rowDimensionValue = row.RowGroup.DisplayName ?? row.RowGroup.Id?.ToString() ?? string.Empty;

                // row.Data is already a Dictionary<object, object?> from the pivot processor
                if (row.Data is not System.Collections.IDictionary dataDict)
                    continue;

                // For each column dimension value (e.g., each month), create a flat record
                foreach (var dataColumn in dataColumns)
                {
                    var columnKey = dataColumn.Id;

                    if (columnKey == null || !dataDict.Contains(columnKey))
                        continue;

                    var value = dataDict[columnKey];

                    // Extract the column dimension value from Coordinates (first coordinate is the column dimension)
                    var columnDimensionValue = dataColumn.Coordinates.FirstOrDefault()?.ToString()
                        ?? dataColumn.DisplayName
                        ?? columnKey.ToString()
                        ?? string.Empty;

                    var dict = new Dictionary<string, object>
                    {
                        [rowDimensionName] = rowDimensionValue,
                        [columnDimensionName] = columnDimensionValue,
                        ["Value"] = value ?? 0
                    };

                    yield return dict;
                }
            }
        }

        private static IEnumerable<Column> GetLeafColumns(IReadOnlyCollection<Column> columns)
        {
            foreach (var column in columns)
            {
                if (column is ColumnGroup group)
                {
                    // Recursively get leaf columns from children
                    foreach (var child in GetLeafColumns(group.Children))
                        yield return child;
                }
                else
                {
                    // This is a leaf column
                    yield return column;
                }
            }
        }

        private static IEnumerable<JsonObject> MapToJsonObjects(IReadOnlyCollection<Row> rows)
        {
            foreach (var row in rows)
            {
                var jsonObject = new JsonObject();

                // Add row group display name if it exists
                if (row.RowGroup != null)
                {
                    jsonObject["_rowGroup"] = row.RowGroup.DisplayName;
                }

                // Add data properties
                if (row.Data != null)
                {
                    var dataJson = System.Text.Json.JsonSerializer.SerializeToNode(row.Data);
                    if (dataJson is JsonObject dataObject)
                    {
                        foreach (var prop in dataObject)
                        {
                            jsonObject[prop.Key] = prop.Value;
                        }
                    }
                }

                yield return jsonObject;
            }
        }

        private static IEnumerable<PropertyColumnControl> MapToDataGridColumns(IReadOnlyCollection<Column> columns)
        {
            foreach (var column in columns)
            {
                if (column is ColumnGroup)
                    continue; // Skip column groups for simple grid

                var propertyColumn = new PropertyColumnControl<object>
                {
                    Property = column.Id?.ToString(),
                    Title = column.DisplayName ?? column.Id?.ToString(),
                    Width = "120px",
                    Align = "Right" // Default to right align for numbers
                };

                yield return propertyColumn;
            }

            // Add row group column if we have row grouping
            if (columns.Any())
            {
                yield return new PropertyColumnControl<string>
                {
                    Property = "_rowGroup",
                    Title = "Name",
                    Width = "250px",
                    Frozen = true,
                    Align = "Left"
                };
            }
        }

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
                dataBinding = $"{dataBinding}{coordinate?.ToString()?.Accessor()}";
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
