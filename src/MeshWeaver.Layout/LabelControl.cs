namespace MeshWeaver.Layout;
/// <summary>
/// Specifies the weight of the font.
/// /// Represents a label control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/label">Fluent UI Blazor Label documentation</a>.
/// </remarks>
/// <param name="Data">The data associated with the label control.</param>
public record LabelControl(object Data)
    : UiControl<LabelControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Gets or initializes the alignment of the label.
    /// </summary>
    public object Alignment { get; init; }
     /// <summary>
    /// Gets or initializes the color of the label.
    /// </summary>
    public object Color { get; init; }
    /// <summary>
    /// Gets or initializes the disabled state of the label.
    /// </summary>
    public object Disabled { get; init; }
    /// <summary>
    /// Gets or initializes the typography of the label.
    /// </summary>
    public object Typo { get; init; }
    /// <summary>
    /// Gets or initializes the weight of the label.
    /// </summary>
    public object Weight { get; init; }
    /// <summary>
    /// Sets the alignment of the label.
    /// </summary>
    /// <param name="alignment">The alignment to set.</param>
    /// <returns>A new <see cref="LabelControl"/> instance with the specified alignment.</returns>
    public LabelControl WithAlignment(object alignment) => this with {Alignment = alignment};
    /// <summary>
    /// Sets the color of the label.
    /// </summary>
    /// <param name="color">The color to set.</param>
    /// <returns>A new <see cref="LabelControl"/> instance with the specified color.</returns>
    public LabelControl WithColor(object color) => this with {Color = color};
    /// <summary>
    /// Sets the disabled state of the label.
    /// </summary>
    /// <param name="disabled">The disabled state to set.</param>
    /// <returns>A new <see cref="LabelControl"/> instance with the specified disabled state.</returns>
    public LabelControl WithDisabled(object disabled) => this with {Disabled = disabled};
    /// <summary>
    /// Sets the typography of the label.
    /// </summary>
    /// <param name="typo">The typography to set.</param>
    /// <returns>A new <see cref="LabelControl"/> instance with the specified typography.</returns>
    public LabelControl WithTypo(object typo) => this with {Typo = typo};
    /// <summary>
    /// Sets the weight of the label.
    /// </summary>
    /// <param name="weight">The weight to set.</param>
    /// <returns>A new <see cref="LabelControl"/> instance with the specified weight.</returns>
    public LabelControl WithWeight(object weight) => this with {Weight = weight};
}
/// <summary>
/// Represents the typography options for a label.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/typography">Fluent UI Blazor Typography documentation</a>.
/// </remarks>
public enum Typography
{
    Body,
    Subject,
    Header,
    PaneHeader,
    EmailHeader,
    PageTitle,
    HeroTitle,
    H1,
    H2,
    H3,
    H4,
    H5,
    H6
}

public enum FontWeight
{
    Normal,
    Bold,
    Bolder
}
