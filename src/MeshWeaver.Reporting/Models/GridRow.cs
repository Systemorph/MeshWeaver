using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Reporting.Models
{
    public record GridRow
    {
        public RowGroup? RowGroup { get; set; } = null!;
        public object Data { get; } = null!;
        public object Style { get; set; } = null!;

        public GridRow(RowGroup? rowGroup, object? row)
        {
            RowGroup = rowGroup;
            Data = row ?? new object();
        }
    }
}
