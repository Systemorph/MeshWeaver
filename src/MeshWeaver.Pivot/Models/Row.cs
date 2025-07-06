namespace MeshWeaver.Pivot.Models
{
    public record Row
    {
        public Row(RowGroup? rowGroup, object row)
        {
            RowGroup = rowGroup;
            Data = row;
        }

        public RowGroup? RowGroup { get; set; }

        public object Data { get; }
    }
}
