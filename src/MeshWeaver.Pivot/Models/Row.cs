namespace MeshWeaver.Pivot.Models
{
    public record Row(RowGroup? RowGroup, object? Data)
    {
        public RowGroup? RowGroup { get; set; } = RowGroup;
    }
}
