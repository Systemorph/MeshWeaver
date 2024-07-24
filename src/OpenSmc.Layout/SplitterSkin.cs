namespace OpenSmc.Layout
{
    public record SplitterSkin : Skin<SplitterSkin>
    {
        public string BarSize { get; init; }
        public string Width { get; init; }
        public string Height { get; init; }
        public Orientation? Orientation { get; init; }
        public SplitterSkin WithOrientation(Orientation orientation) => this with { Orientation = orientation };

        public SplitterSkin WithBarSize(string barSize) => this with { BarSize = barSize };

        public SplitterSkin WithWidth(string width) => this with { Width = width };

        public SplitterSkin WithHeight(string height) => this with { Height = height };
    }
}
