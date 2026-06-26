
namespace MeshWeaver.GridModel
{
    /// <summary>Reusable JavaScript snippets used as grid event handlers and formatters.</summary>
    public static class GridConst
    {
        /// <summary>Grid-ready handler that auto-sizes every displayed column to fit its content.</summary>
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

        /// <summary>Default value formatter that renders numbers with up to two fraction digits and leaves other values untouched.</summary>
        public static string DefaultValueFormatter = "typeof(value) == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 2 }).format(value) : value";
    }
}
