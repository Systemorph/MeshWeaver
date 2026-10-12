namespace MeshWeaver.Layout;

/// <summary>
/// A compact on/off button bound to a boolean — pressed when the value is true. Shows an icon (any
/// form an icon control accepts: a Fluent icon name, a URL, an SVG, or a glyph such as an emoji);
/// <see cref="FormControlBase{T}.Label"/> is its tooltip and accessible name. Use it where a row of
/// labelled switches would take too much room, e.g. one toggle per source above a list.
/// </summary>
/// <param name="Data">The boolean data binding for the pressed state.</param>
public record ToggleButtonControl(object Data)
    : FormControlBase<ToggleButtonControl>(Data)
{
    /// <summary>The icon the button shows.</summary>
    public object? Icon { get; init; }

    /// <summary>Returns a copy showing <paramref name="icon"/>.</summary>
    /// <param name="icon">An icon name, URL, SVG markup or glyph.</param>
    public ToggleButtonControl WithIcon(object icon) => this with { Icon = icon };
}
