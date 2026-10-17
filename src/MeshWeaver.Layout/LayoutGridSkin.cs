namespace MeshWeaver.Layout;


/// <summary>
/// Represents a layout grid control with customizable properties.
/// </summary>
public record LayoutGridControl() : ContainerControlWithItemSkin<LayoutGridControl, LayoutGridSkin, LayoutGridItemSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Creates a new instance of <see cref="LayoutGridItemSkin"/> for the specified named area.
    /// </summary>
    /// <param name="namedArea">The named area control.</param>
    /// <returns>A new instance of <see cref="LayoutGridItemSkin"/>.</returns>
    protected override LayoutGridItemSkin CreateItemSkin(NamedAreaControl namedArea)
    {
        return new();
    }

    /// <summary>
    /// Switches the grid to TILE FLOW: every child becomes a tile at least
    /// <paramref name="minTileWidth"/> wide, as many tiles as fit fill each row, and the rest wrap —
    /// a responsive card grid with no breakpoints to declare. Shorthand for
    /// <c>WithSkin(s =&gt; s.WithMinTileWidth(minTileWidth))</c>; see <see cref="LayoutGridSkin.MinTileWidth"/>.
    /// </summary>
    /// <param name="minTileWidth">A CSS length, e.g. <c>"280px"</c>.</param>
    /// <returns>A new <see cref="LayoutGridControl"/> in tile-flow mode.</returns>
    public LayoutGridControl WithMinTileWidth(object minTileWidth) => WithSkin(s => s.WithMinTileWidth(minTileWidth));
}

/// <summary>
/// Represents the skin for a layout grid control with customizable properties.
/// </summary>
public record LayoutGridSkin : Skin<LayoutGridSkin>
{
    /// <summary>
    /// Gets or initializes the adaptive rendering state of the layout grid.
    /// </summary>
    public object? AdaptiveRendering { get; init; }

    /// <summary>
    /// Gets or initializes the justification of the layout grid.
    /// </summary>
    public object? Justify { get; init; }

    /// <summary>
    /// Gets or initializes the spacing between items in the layout grid.
    /// </summary>
    public object? Spacing { get; init; }

    /// <summary>
    /// The minimum width of a tile, as a CSS length (e.g. <c>"280px"</c>) — set, it switches the grid
    /// to TILE FLOW. Every child is then a tile of at least this width; as many tiles as fit share a
    /// row in equal columns, and the rest wrap onto further rows (CSS
    /// <c>repeat(auto-fill, minmax(min(100%, w), 1fr))</c> on the web, the equivalent computed column
    /// count on native). A tile never exceeds the container, so on a narrow screen it is one column.
    /// The per-item breakpoint spans (<see cref="LayoutGridItemSkin.Xs"/> …) do not apply in this mode.
    /// The gap between tiles is <see cref="Spacing"/> × 4px, 12px when unset. Null (the default)
    /// keeps the 12-column breakpoint grid.
    /// </summary>
    public object? MinTileWidth { get; init; }

    /// <summary>
    /// Sets the adaptive rendering state of the layout grid.
    /// </summary>
    /// <param name="adaptiveRendering">The adaptive rendering state to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified adaptive rendering state.</returns>
    public LayoutGridSkin WithAdaptiveRendering(object adaptiveRendering) => this with { AdaptiveRendering = adaptiveRendering };

    /// <summary>
    /// Sets the justification of the layout grid.
    /// </summary>
    /// <param name="justify">The justification to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified justification.</returns>
    public LayoutGridSkin WithJustify(object justify) => this with { Justify = justify };

    /// <summary>
    /// Sets the spacing between items in the layout grid.
    /// </summary>
    /// <param name="spacing">The spacing to set.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> instance with the specified spacing.</returns>
    public LayoutGridSkin WithSpacing(object spacing) => this with { Spacing = spacing };

    /// <summary>
    /// Switches the grid to tile flow with tiles of at least <paramref name="minTileWidth"/>; see <see cref="MinTileWidth"/>.
    /// </summary>
    /// <param name="minTileWidth">A CSS length, e.g. <c>"280px"</c>.</param>
    /// <returns>A new <see cref="LayoutGridSkin"/> in tile-flow mode.</returns>
    public LayoutGridSkin WithMinTileWidth(object minTileWidth) => this with { MinTileWidth = minTileWidth };
}
