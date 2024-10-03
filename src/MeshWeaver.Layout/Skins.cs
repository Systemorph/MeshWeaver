namespace MeshWeaver.Layout;

/// <summary>
/// Provides static properties to access various skins.
/// </summary>
public static class Skins
{
    /// <summary>
    /// Gets the skin for body content.
    /// </summary>
    public static BodyContentSkin BodyContent => new();

    /// <summary>
    /// Gets the skin for layout.
    /// </summary>
    public static LayoutSkin Layout => new();

    /// <summary>
    /// Gets the skin for header.
    /// </summary>
    public static HeaderSkin Header => new();

    /// <summary>
    /// Gets the skin for footer.
    /// </summary>
    public static FooterSkin Footer => new();

    /// <summary>
    /// Gets the skin for card.
    /// </summary>
    public static CardSkin Card => new();
}
