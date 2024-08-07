using MeshWeaver.Pivot.Models;

namespace MeshWeaver.Reporting.Models
{
    public record GridRow
    {
        public RowGroup RowGroup { get; set; }
        public object Data { get; }
        public object Style { get; set; }

        public GridRow(RowGroup rowGroup, object row)
        {
            RowGroup = rowGroup;
            Data = row;
        }
    }
}
