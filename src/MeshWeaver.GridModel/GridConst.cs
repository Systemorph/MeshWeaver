
namespace MeshWeaver.GridModel
{
    public static class GridConst
    {
        public static string AutoSizeColumns = @"event => {
                var allColumnIds = [];
                event.api.getAllColumns().forEach(function (column) {
                    allColumnIds.push(column.getColId());
                });
                
                event.api.autoSizeColumns(allColumnIds, false);
        }";

        public static string DefaultValueFormatter = "typeof(value) == 'number' ? new Intl.NumberFormat([], { maximumFractionDigits: 2 }).format(value) : value";
    }
}
