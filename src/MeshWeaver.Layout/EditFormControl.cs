namespace MeshWeaver.Layout;

/// <summary>
/// Represents an edit form control with customizable properties.
/// </summary>
public record EditFormControl()
    : ContainerControlWithItemSkin<EditFormControl, EditFormSkin, PropertySkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Creates a new instance of <see cref="PropertySkin"/> for the specified named area.
    /// </summary>
    /// <param name="namedArea">The named area control.</param>
    /// <returns>A new instance of <see cref="PropertySkin"/>.</returns>
    protected override PropertySkin CreateItemSkin(NamedAreaControl namedArea)
        => new();
}

/// <summary>
/// Represents the skin for an edit form control.
/// </summary>
public record EditFormSkin : Skin<EditFormSkin>;

/// <summary>
/// Represents the skin for an item in an edit form control.
/// </summary>
public record PropertySkin : Skin<PropertySkin>
{
    /// <summary>
    /// Gets or initializes the description of the item.
    /// </summary>
    public object Description { get; init; }

    /// <summary>
    /// Gets or initializes the name of the item.
    /// </summary>
    public object Name { get; init; }

    /// <summary>
    /// Gets or initializes the label of the item.
    /// </summary>
    public object Label { get; set; }
}
