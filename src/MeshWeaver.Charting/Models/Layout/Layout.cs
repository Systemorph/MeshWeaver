namespace MeshWeaver.Charting.Models.Layout
{
    // https://www.chartjs.org/docs/3.5.1/configuration/layout.html
    public record Layout
    {
        public object Padding { get; init; }

        public Layout WithPadding(PaddingObject padding) => new() { Padding = padding };

        public Layout WithPadding(int padding) => new() { Padding = padding };

        public Layout WithPaddingObject(PaddingObject paddingObject) => new() { Padding = paddingObject };
    }
}