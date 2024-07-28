namespace OpenSmc.Layout
{
    public record SplitterControl : LayoutStackControl
    {
        public string BarSize { get; init; }

        public SplitterControl WithBarSize(string barSize) => this with { BarSize = barSize };

    }
}
