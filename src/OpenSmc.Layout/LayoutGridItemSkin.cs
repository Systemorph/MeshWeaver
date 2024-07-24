namespace OpenSmc.Layout
{
    public record LayoutGridItemSkin
    {
        public bool? AdaptiveRendering { get; init; }
        public JustifyContent? Justify { get; init; }
        public LayoutGridItemHidden HiddenWhen { get; init; }
        public string Gap { get; init; }
        public int? Lg { get; init; }
        public int? Md { get; init; }
        public int? Sm { get; init; }
        public int? Xl { get; init; }
        public int? Xs { get; init; }
        public int? Xxl { get; init;}

        public LayoutGridItemSkin WithAdaptiveRendering(bool adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
        public LayoutGridItemSkin WithGap(string gap) => this with { Gap = gap };
        public LayoutGridItemSkin WithLg(int lg) => this with { Lg = lg };
        public LayoutGridItemSkin WithMd(int md) => this with { Md = md };
        public LayoutGridItemSkin WithSm(int sm) => this with { Sm = sm };
        public LayoutGridItemSkin WithXl(int xl) => this with { Xl = xl };
        public LayoutGridItemSkin WithXs(int xs) => this with { Xs = xs };
        public LayoutGridItemSkin WithXxl(int xxl) => this with { Xxl = xxl };

    }
}
