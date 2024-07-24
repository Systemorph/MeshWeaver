namespace OpenSmc.Layout
{
    public record SplitterPaneSkin : Skin<SplitterPaneSkin>
    {
        public bool Collapsible { get; init; }
        public bool Collapsed { get; init; }

        public string Max { get; init; }
        public string Min { get; init; }
        public bool Resizable { get; init; } = true;

        public string Size { get; init; }

        public SplitterPaneSkin WithCollapsible(bool collapsible) => this with { Collapsible = collapsible };
        public SplitterPaneSkin WithCollapsed(bool collapsed) => this with { Collapsed = collapsed };
        public SplitterPaneSkin WithMax(string max) => this with { Max = max };
        public SplitterPaneSkin WithMin(string min) => this with { Min = min };
        public SplitterPaneSkin WithResizable(bool resizable) => this with { Resizable = resizable };
        public SplitterPaneSkin WithSize(string size) => this with { Size = size };

    }
}
