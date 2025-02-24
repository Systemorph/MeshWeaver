namespace MeshWeaver.Domain;


/// <summary>
/// Represents an icon. We link to the fluent icons from Microsoft
/// <see ref="https://fluentui-explorer.azurewebsites.net/icon-explorer"/>
/// </summary>
/// <param name="Provider"></param>
/// <param name="Id"></param>
public record Icon(string Provider, string Id)
{
    public IconSize Size { get; init; } = IconSize.Size24;

    public Icon WithSize(IconSize size) => this with { Size = size };

    public IconVariant Variant { get; init; } = IconVariant.Regular;
    public Icon WithVariant(IconVariant variant) => this with { Variant = variant };

}

/// <summary>
/// The size of the icon.
/// </summary>
public enum IconSize
{
    /// <summary>
    /// Icon size 10x10
    /// </summary>
    Size10 = 10,
    /// <summary>
    /// Icon size 12x12
    /// </summary>
    Size12 = 12,
    /// <summary>
    /// Icon size 16x16
    /// </summary>
    Size16 = 16,
    /// <summary>
    /// Icon size 20x20
    /// </summary>
    Size20 = 20,
    /// <summary>
    /// Icon size 24x24
    /// </summary>
    Size24 = 24,
    /// <summary>
    /// Icon size 28x28
    /// </summary>
    Size28 = 28,
    /// <summary>
    /// Icon size 32x32
    /// </summary>
    Size32 = 32,
    /// <summary>
    /// Icon size 48x48
    /// </summary>
    Size48 = 48,

    /// <summary>
    /// Custom size included in the SVG content.
    /// </summary>
    Custom = 0
}

/// <summary>
/// The icon variant.
/// </summary>
public enum IconVariant
{
    /// <summary>
    /// Regular variant of icons.
    /// </summary>
    Regular,
    /// <summary>
    /// Filled variant of icons.
    /// </summary>
    Filled
}
