namespace MeshWeaver.Pivot.Models
{
    public class PivotModel
    {
        public IReadOnlyCollection<Column> Columns { get; init; }
        public IReadOnlyCollection<Row> Rows { get; init; }

        public bool HasRowGrouping { get; init; }
        public PivotModel(IReadOnlyCollection<Column> columns, IReadOnlyCollection<Row> rows, bool hasRowGrouping = false)
        {
            Columns = columns;
            Rows = rows;
            HasRowGrouping = hasRowGrouping;
        }
    }
}
