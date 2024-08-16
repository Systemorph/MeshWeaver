namespace MeshWeaver.Layout
{
    public record LayoutGridItemSkin : Skin<LayoutGridItemSkin>
    {
        public object AdaptiveRendering { get; init; }
        public object Justify { get; init; }
        public object HiddenWhen { get; init; }
        public object Gap { get; init; }
        public object Lg { get; init; }
        public object Md { get; init; }
        public object Sm { get; init; }
        public object Xl { get; init; }
        public object Xs { get; init; }
        public object Xxl { get; init;}

        public LayoutGridItemSkin WithAdaptiveRendering(object adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };
        public LayoutGridItemSkin WithGap(object gap) => this with { Gap = gap };
        public LayoutGridItemSkin WithLg(object lg) => this with { Lg = lg };
        public LayoutGridItemSkin WithMd(object md) => this with { Md = md };
        public LayoutGridItemSkin WithSm(object sm) => this with { Sm = sm };
        public LayoutGridItemSkin WithXl(object xl) => this with { Xl = xl };
        public LayoutGridItemSkin WithXs(object xs) => this with { Xs = xs };
        public LayoutGridItemSkin WithXxl(object xxl) => this with { Xxl = xxl };

    }
}
