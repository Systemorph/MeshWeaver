namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents the skin for a layout grid item with customizable properties.
    /// </summary>
    public record LayoutGridItemSkin : Skin<LayoutGridItemSkin>
    {
        /// <summary>
        /// Gets or initializes the adaptive rendering state of the layout grid item.
        /// </summary>
        public object AdaptiveRendering { get; init; }

        /// <summary>
        /// Gets or initializes the justification of the layout grid item.
        /// </summary>
        public object Justify { get; init; }

        /// <summary>
        /// Gets or initializes the hidden state of the layout grid item based on conditions.
        /// </summary>
        public object HiddenWhen { get; init; }

        /// <summary>
        /// Gets or initializes the gap between items in the layout grid.
        /// </summary>
        public object Gap { get; init; }

        /// <summary>
        /// Gets or initializes the large screen size configuration for the layout grid item.
        /// </summary>
        public object Lg { get; init; }

        /// <summary>
        /// Gets or initializes the medium screen size configuration for the layout grid item.
        /// </summary>
        public object Md { get; init; }

        /// <summary>
        /// Gets or initializes the small screen size configuration for the layout grid item.
        /// </summary>
        public object Sm { get; init; }

        /// <summary>
        /// Gets or initializes the extra large screen size configuration for the layout grid item.
        /// </summary>
        public object Xl { get; init; }

        /// <summary>
        /// Gets or initializes the extra small screen size configuration for the layout grid item.
        /// </summary>
        public object Xs { get; init; }

        /// <summary>
        /// Gets or initializes the extra extra large screen size configuration for the layout grid item.
        /// </summary>
        public object Xxl { get; init; }

        /// <summary>
        /// Sets the adaptive rendering state of the layout grid item.
        /// </summary>
        /// <param name="adaptiveRendering">The adaptive rendering state to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified adaptive rendering state.</returns>
        public LayoutGridItemSkin WithAdaptiveRendering(object adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };

        /// <summary>
        /// Sets the gap between items in the layout grid.
        /// </summary>
        /// <param name="gap">The gap to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified gap.</returns>
        public LayoutGridItemSkin WithGap(object gap) => this with { Gap = gap };

        /// <summary>
        /// Sets the large screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="lg">The large screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified large screen size configuration.</returns>
        public LayoutGridItemSkin WithLg(object lg) => this with { Lg = lg };

        /// <summary>
        /// Sets the medium screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="md">The medium screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified medium screen size configuration.</returns>
        public LayoutGridItemSkin WithMd(object md) => this with { Md = md };

        /// <summary>
        /// Sets the small screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="sm">The small screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified small screen size configuration.</returns>
        public LayoutGridItemSkin WithSm(object sm) => this with { Sm = sm };

        /// <summary>
        /// Sets the extra large screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="xl">The extra large screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified extra large screen size configuration.</returns>
        public LayoutGridItemSkin WithXl(object xl) => this with { Xl = xl };

        /// <summary>
        /// Sets the extra small screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="xs">The extra small screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified extra small screen size configuration.</returns>
        public LayoutGridItemSkin WithXs(object xs) => this with { Xs = xs };

        /// <summary>
        /// Sets the extra extra large screen size configuration for the layout grid item.
        /// </summary>
        /// <param name="xxl">The extra extra large screen size configuration to set.</param>
        /// <returns>A new <see cref="LayoutGridItemSkin"/> instance with the specified extra extra large screen size configuration.</returns>
        public LayoutGridItemSkin WithXxl(object xxl) => this with { Xxl = xxl };
    }
}