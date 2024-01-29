
namespace OpenSmc.GridModel
{
    public static class GridConst
    {
        public static string AutoSizeColumns = @"event => {
                var allColumnIds = [];
                event.columnApi.getAllGridColumns().forEach(function (column) {
                allColumnIds.push(column.colId);
                });

                event.columnApi.autoSizeColumns(allColumnIds);
        }";

        public static string DefaultValueFormatter = "typeof(value) == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 2 }).format(value) : value";
    }
}
