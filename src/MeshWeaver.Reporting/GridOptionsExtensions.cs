using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MeshWeaver.GridModel;
using MeshWeaver.Reporting.Models;

namespace MeshWeaver.Reporting
{
    // TODO V10: move to the builder (2021/12/08, Ekaterina Mishina)
    public class GridRowSelector(string dimension)
    {
        private readonly IList<Func<GridRow, bool>> orConditions = new List<Func<GridRow, bool>>();

        public void Level(params int[] levels)
        {
            orConditions.Add(row => levels.Contains(row.RowGroup.Coordinates.Count - 1));
        }

        public bool TrueFor(GridRow gridRow)
        {
            var ret = Regex
                .Match(
                    gridRow.RowGroup.GrouperName,
                    $"{dimension}[0-9]{{0,}}$",
                    RegexOptions.IgnoreCase
                )
                .Success;

            return orConditions.Count == 0 ? ret : ret && orConditions.Any(x => x(gridRow));
        }

        public void SystemNames(string[] systemNames)
        {
            orConditions.Add(row => systemNames.Contains(row.RowGroup.Coordinates.Last()));
        }
    }

    public static class GridOptionsExtensions
    {
        public static GridOptions AutoHeight(this GridOptions gridOptions)
        {
            return gridOptions with { DomLayout = "autoHeight", };
        }

        public static GridOptions HideRowValuesForDimension(
            this GridOptions gridOptions,
            string dimension,
            Func<GridRowSelector, GridRowSelector>? rowSelectorFunc = null
        )
        {
            var gridRowSelector = new GridRowSelector(dimension);

            if (rowSelectorFunc != null)
                gridRowSelector = rowSelectorFunc(gridRowSelector);

            return gridOptions with
            {
                RowData = gridOptions
                    .RowData.Select(row =>
                    {
                        var gridRow = (GridRow)row;
                        return gridRowSelector.TrueFor(gridRow)
                            ? new GridRow(gridRow.RowGroup, null)
                            : row;
                    })
                    .ToImmutableList()
            };
        }

        public static GridRowSelector ForSystemName(
            this GridRowSelector rowSelector,
            params string[] systemNames
        )
        {
            rowSelector.SystemNames(systemNames);
            return rowSelector;
        }

        public static GridRowSelector ForLevel(
            this GridRowSelector rowSelector,
            params int[] levels
        )
        {
            rowSelector.Level(levels);
            return rowSelector;
        }

        public static GridOptions WithColumns(
            this GridOptions gridOptions,
            Func<IReadOnlyCollection<ColDef>, IReadOnlyCollection<ColDef>> func
        )
        {
            return gridOptions with { ColumnDefs = func(gridOptions.ColumnDefs) };
        }

        public static GridOptions WithAutoGroupColumn(
            this GridOptions gridOptions,
            Func<ColDef, ColDef> definitionModifier
        )
        {
            return gridOptions with
            {
                AutoGroupColumnDef = definitionModifier(gridOptions.AutoGroupColumnDef ?? new ColDef())
            };
        }

        public static GridOptions WithDefaultColumn(
            this GridOptions def,
            Func<ColDef, ColDef> definitionModifier
        )
        {
            return def with { DefaultColDef = definitionModifier(def.DefaultColDef ?? new ColDef()) };
        }

        public static GridOptions WithDefaultColumnGroup(
            this GridOptions def,
            Func<ColGroupDef, ColGroupDef> definitionModifier
        )
        {
            return def with { DefaultColGroupDef = definitionModifier(def.DefaultColGroupDef ?? new ColGroupDef()) };
        }

        public static GridOptions WithRowStyle(this GridOptions def, CellStyle style)
        {
            return def with { RowStyle = style };
        }

        public static IReadOnlyCollection<ColDef> Modify(
            this IReadOnlyCollection<ColDef> definitions,
            Func<ColDef, bool> definitionFilter,
            Func<ColDef, ColDef> definitionModifier
        )
        {
            var modifiedDefinitions = definitions
                .Select(def => def.ModifyDefinition(definitionFilter, definitionModifier))
                .ToList();
            return modifiedDefinitions;
        }

        private static ColDef ModifyDefinition(
            this ColDef def,
            Func<ColDef, bool> definitionFilter,
            Func<ColDef, ColDef> definitionModifier
        )
        {
            if (def is ColGroupDef cg)
            {
                var modifiedColumnGroup = definitionFilter(cg)
                        ? (ColGroupDef)definitionModifier(cg)
                        : cg;
                return modifiedColumnGroup with
                {
                    Children = cg
                        .Children.Select(c =>
                            c.ModifyDefinition(definitionFilter, definitionModifier)
                        )
                        .ToImmutableList()
                };
            }

            return definitionFilter(def)
                ? definitionModifier(def)
                : def;
        }

        public static IReadOnlyCollection<ColDef> Modify(
            this IReadOnlyCollection<ColDef> definitions,
            string? systemNameRegex,
            Func<ColDef, ColDef> definitionModifier
        )
        {
            return definitions.Modify(
                x =>
                    Regex
                        .Match(
                            x is ColGroupDef g ? g.GroupId.ToString()! : x.ColId.ToString()!,
                            systemNameRegex ?? string.Empty,
                            RegexOptions.IgnoreCase
                        )
                        .Success,
                definitionModifier
            );
        }

        // https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Intl/NumberFormat/NumberFormat
        public static ColDef WithFormat(this ColDef def, string formatter)
        {
            return def with { ValueFormatter = formatter };
        }

        public static ColDef WithNumberFormat(
            this ColDef def,
            string locales,
            string? options = null
        )
        {
            options ??= "{ maximumFractionDigits: 2 }";

            var valueFormatter =
                $"typeof(value) == 'number' ? new Intl.NumberFormat('{locales}', {options}).format(value) : value";

            return def with
            {
                ValueFormatter = valueFormatter
            };
        }

        public static ColDef WithWidth(this ColDef def, int width)
        {
            return def with { Width = width };
        }

        public static ColDef WithDisplayName(this ColDef def, string name)
        {
            return def with { HeaderName = name };
        }

        public static ColDef AsTotal(this ColDef def)
        {
            return def with { CellStyle = Highlight("#f3f3f3") };
        }

        public static ColDef WithStyle(this ColDef def, CellStyle style)
        {
            return def with { CellStyle = style };
        }

        public static ColDef Highlighted(this ColDef def)
        {
            return def with { CellStyle = Highlight("#eaf3f9") };
        }

        public static ColDef HighlightNegativeValues(this ColDef def)
        {
            return def with
            {
                CellStyle =
                    @"function(params){
                if (params.value < 0) {
                    return {
                        'textAlign': 'right',
                        'color': '#F75435'
                    }
                }
                return { 'textAlign': 'right' }
                }"
            };
        }

        public static GridOptions WithRows(
            this GridOptions gridOptions,
            Func<IReadOnlyCollection<GridRow>, IReadOnlyCollection<GridRow>> func
        )
        {
            return gridOptions with
            {
                RowData = func(gridOptions.RowData.Select(x => (GridRow)x).ToImmutableList())
            };
        }

        public static IReadOnlyCollection<GridRow> Modify(
            this IReadOnlyCollection<GridRow> rows,
            string systemNameRegex,
            Func<GridRow, GridRow> rowModifier
        )
        {
            return rows.Modify(
                x =>
                    x.RowGroup != null
                    && Regex
                        .Match(
                            x.RowGroup.Id?.ToString() ?? string.Empty,
                            systemNameRegex ?? string.Empty,
                            RegexOptions.IgnoreCase
                        )
                        .Success,
                rowModifier
            );
        }

        public static IReadOnlyCollection<GridRow> Modify(
            this IReadOnlyCollection<GridRow> rows,
            Func<GridRow, bool> rowFilter,
            Func<GridRow, GridRow> rowModifier
        )
        {
            var rowsWithModifiedDefinitions = rows.Select(row =>
                {
                    if (rowFilter(row))
                        return rowModifier(row);
                    return row;
                })
                .ToList();
            return rowsWithModifiedDefinitions;
        }

        public static GridRow WithDisplayName(this GridRow row, string name)
        {
            return row with { RowGroup = row.RowGroup with { DisplayName = name } };
        }

        public static GridRow AsSubTotal(this GridRow def)
        {
            return def with { Style = Highlight("#f3f3f3") };
        }

        public static GridRow AsTotal(this GridRow def)
        {
            return def with { Style = Highlight("#cce3ff") };
        }

        // TODO V10: replace by object/string or define more user friendly styling options (2022/03/15, Ekaterina Mishina)
        public static GridRow WithStyle(this GridRow def, CellStyle style)
        {
            return def with { Style = style };
        }

        public static GridRow Highlighted(this GridRow def)
        {
            return def with { Style = Highlight("#eaf3f9") };
        }

        public static object Highlight(string backgroundColor, string fontWeight = "medium")
        {
            return new { backgroundColor, fontWeight };
        }
    }
}
