
namespace MeshWeaver.GridModel
{
    public static class GridConst
    {
        public static string AutoSizeColumns = @"event => {
                var allColumnIds = [];
                var columns = event.api.getColumns ? event.api.getColumns() : event.api.getAllDisplayedColumns();
                if (columns) {
                    columns.forEach(function (column) {
                        allColumnIds.push(column.getColId());
                    });

                    event.api.autoSizeColumns(allColumnIds, false);
                }
        }";

        public static string DefaultValueFormatter = "typeof(value) == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 2 }).format(value) : value";
    }
}
