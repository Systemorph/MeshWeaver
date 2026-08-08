namespace MeshWeaver.Layout;

/// <summary>
/// One tile of a <see cref="KpiStripControl"/> — a headline figure with its caption.
/// </summary>
/// <param name="Label">The caption above the figure (e.g. "Subject premium").</param>
/// <param name="Value">
/// The figure itself, ALREADY formatted for display (e.g. "12.4m EUR", "78.2%"). The strip renders
/// it verbatim: a KPI's formatting is domain knowledge (currency, precision, unit) that belongs
/// with the caller, not with the tile.
/// </param>
/// <param name="Hint">Optional small print under the figure (basis, as-of date, caveat).</param>
public record KpiItem(string Label, string Value, string? Hint = null);

/// <summary>
/// The KPI strip — a row of compact tiles carrying a view's headline figures, wrapping onto further
/// rows when the viewport is narrow.
///
/// <para>It is an ordinary leaf control: put it in a <c>Controls.Stack</c>, a
/// <c>Controls.LayoutGrid</c> cell or a card exactly like a <c>DataGrid</c> — it brings no layout
/// mechanism of its own beyond the wrap of its own tiles.</para>
///
/// <para><see cref="Items"/> is either a concrete <c>IEnumerable&lt;KpiItem&gt;</c> or a
/// data-binding reference resolving to one, so a strip can be bound to a live data section.</para>
/// </summary>
/// <param name="Items">The tiles, or a data-binding reference resolving to them.</param>
public record KpiStripControl(object Items)
    : UiControl<KpiStripControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The minimum width a tile may shrink to before the strip wraps, as a CSS length.
    /// Null renders the view default (<c>150px</c>).
    /// </summary>
    public object? MinTileWidth { get; init; }

    /// <summary>Returns a copy showing <paramref name="items"/> instead.</summary>
    /// <param name="items">The tiles, or a data-binding reference resolving to them.</param>
    /// <returns>A new <see cref="KpiStripControl"/> with the specified items.</returns>
    public KpiStripControl WithItems(object items) => this with { Items = items };

    /// <summary>Returns a copy whose tiles wrap below <paramref name="minTileWidth"/>.</summary>
    /// <param name="minTileWidth">A CSS length, e.g. <c>"200px"</c>.</param>
    /// <returns>A new <see cref="KpiStripControl"/> with the specified minimum tile width.</returns>
    public KpiStripControl WithMinTileWidth(object minTileWidth) => this with { MinTileWidth = minTileWidth };
}
