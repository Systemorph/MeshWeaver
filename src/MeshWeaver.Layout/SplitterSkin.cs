namespace MeshWeaver.Layout
{
    public record SplitterSkin : Skin<SplitterSkin>
    {
        public string BarSize { get; init; }

        public SplitterSkin WithBarSize(string barSize) => this with { BarSize = barSize };

    }
}
