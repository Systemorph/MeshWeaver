using MeshWeaver.Pivot.Models;
using System.Text.Json.Serialization;

namespace MeshWeaver.Reporting.Models
{
    public record GridRow
    {
        public RowGroup? RowGroup { get; set; } = null!;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; init; }

        public object Style { get; set; } = null!;

        public GridRow(RowGroup? rowGroup, object? row)
        {
            RowGroup = rowGroup;
            Data = row;
        }
    }
}
