namespace MeshWeaver.Layout;

/// <summary>
/// A small status pill. With <see cref="Href"/> it is a compact NAVIGATING chip — the control a
/// clickable status chip uses instead of an anchor hand-built in <c>Controls.Html</c> (#6954).
///
/// <para>🚨 Every renderer applies <see cref="LinkHrefs"/> to <see cref="Href"/> exactly as it does
/// for a <see cref="LinkControl"/>: an address whose scheme is not <c>http</c>, <c>https</c>,
/// <c>mailto</c> or <c>tel</c> draws the badge WITHOUT a link, a mesh path navigates inside the
/// portal, and an external address carries <c>rel="noopener noreferrer"</c>. The text and the
/// tooltip are free text the renderer encodes — a caller never escapes them.</para>
///
/// <para>Colors bind to theme tokens: pass a CSS custom property such as
/// <c>var(--accent-fill-rest)</c> or <c>var(--error, #d13438)</c> to <see cref="BackgroundColor"/>
/// and <see cref="Color"/>, so the chip follows light and dark themes.</para>
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/badge">Fluent UI Blazor Badge documentation</a>.
/// </remarks>
/// <param name="Data">The data associated with the badge control.</param>
public record BadgeControl(object Data)
    : UiControl<BadgeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Gets or initializes the appearance of the badge.
    /// </summary>
    public object? Appearance { get; init; }

    /// <summary>
    /// Gets or initializes the background color of the badge.
    /// </summary>
    public object? BackgroundColor { get; init; }

    /// <summary>
    /// Gets or initializes the circular state of the badge.
    /// </summary>
    public object? Circular { get; init; }

    /// <summary>
    /// Gets or initializes the color of the badge.
    /// </summary>
    public object? Color { get; init; }

    /// <summary>
    /// Gets or initializes the dismiss icon of the badge.
    /// </summary>
    public object? DismissIcon { get; init; }

    /// <summary>
    /// Gets or initializes the dismiss title of the badge.
    /// </summary>
    public object? DismissTitle { get; init; }

    /// <summary>
    /// Gets or initializes the fill of the badge.
    /// </summary>
    public object? Fill { get; init; }

    /// <summary>
    /// Gets or initializes the height of the badge.
    /// </summary>
    public object? Height { get; init; }

    /// <summary>
    /// Gets or initializes the width of the badge.
    /// </summary>
    public object? Width { get; init; }

    /// <summary>
    /// Sets the appearance of the badge.
    /// </summary>
    /// <param name="appearance">The appearance to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified appearance.</returns>
    public BadgeControl WithAppearance(object appearance) => this with { Appearance = appearance };

    /// <summary>
    /// Sets the background color of the badge.
    /// </summary>
    /// <param name="backgroundColor">The background color to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified background color.</returns>
    public BadgeControl WithBackgroundColor(object backgroundColor) => this with { BackgroundColor = backgroundColor };

    /// <summary>
    /// Sets the circular state of the badge.
    /// </summary>
    /// <param name="circular">The circular state to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified circular state.</returns>
    public BadgeControl WithCircular(object circular) => this with { Circular = circular };

    /// <summary>
    /// Sets the color of the badge.
    /// </summary>
    /// <param name="color">The color to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified color.</returns>
    public BadgeControl WithColor(object color) => this with { Color = color };

    /// <summary>
    /// Sets the dismiss icon of the badge.
    /// </summary>
    /// <param name="dismissIcon">The dismiss icon to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified dismiss icon.</returns>
    public BadgeControl WithDismissIcon(object dismissIcon) => this with { DismissIcon = dismissIcon };

    /// <summary>
    /// Sets the dismiss title of the badge.
    /// </summary>
    /// <param name="dismissTitle">The dismiss title to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified dismiss title.</returns>
    public BadgeControl WithDismissTitle(object dismissTitle) => this with { DismissTitle = dismissTitle };
    /// <summary>Returns a copy with <paramref name="fill"/> as its fill color token.</summary>
    /// <param name="fill">The fill value to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified fill.</returns>
    public BadgeControl WithFill(object fill) => This with { Fill = fill };

    /// <summary>Returns a copy with <paramref name="height"/> as its CSS height.</summary>
    /// <param name="height">The height value to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified height.</returns>
    public BadgeControl WithHeight(object height) => This with { Height = height };

    /// <summary>Returns a copy with <paramref name="width"/> as its CSS width.</summary>
    /// <param name="width">The width value to set.</param>
    /// <returns>A new <see cref="BadgeControl"/> instance with the specified width.</returns>
    public BadgeControl WithWidth(object width) => This with { Width = width };

    /// <summary>
    /// Where the badge navigates when clicked — a literal or a data-binding pointer. A mesh path is
    /// written as an absolute path (<c>/Doc/GUI</c>), never <c>@/…</c>, and navigates in-app. When
    /// unset the badge is a plain status pill.
    /// </summary>
    public object? Href { get; init; }

    /// <summary>
    /// The hover text, rendered as the element's <c>title</c> — typically the chip's full label when
    /// <see cref="MaxWidth"/> truncates it. A literal or a binding. User-facing: pass a localized string.
    /// </summary>
    public object? Tooltip { get; init; }

    /// <summary>
    /// The widest the badge may grow, as a CSS length (<c>"160px"</c>, <c>"12em"</c>); text past it is
    /// cut with an ellipsis. A literal or a binding. Unbounded when unset.
    /// </summary>
    public object? MaxWidth { get; init; }

    /// <summary>Makes the badge a navigating chip (<see cref="Href"/>).</summary>
    /// <param name="href">The address — an absolute mesh path or an http(s) URL — or a binding.</param>
    /// <returns>A new <see cref="BadgeControl"/> that navigates to <paramref name="href"/>.</returns>
    public BadgeControl WithHref(object href) => This with { Href = href };

    /// <summary>Sets the hover text (<see cref="Tooltip"/>).</summary>
    /// <param name="tooltip">The localized text, or a binding.</param>
    /// <returns>A new <see cref="BadgeControl"/> with the tooltip set.</returns>
    public BadgeControl WithTooltip(object tooltip) => This with { Tooltip = tooltip };

    /// <summary>Bounds the badge's width; longer text ends in an ellipsis (<see cref="MaxWidth"/>).</summary>
    /// <param name="maxWidth">A CSS length, or a binding.</param>
    /// <returns>A new <see cref="BadgeControl"/> with the maximum width set.</returns>
    public BadgeControl WithMaxWidth(object maxWidth) => This with { MaxWidth = maxWidth };
}
