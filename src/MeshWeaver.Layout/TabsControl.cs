namespace MeshWeaver.Layout;

/// <summary>
/// Represents a tabs control with customizable properties.
/// </summary>
public record TabsControl() :
    ContainerControlWithItemSkin<TabsControl, TabsSkin, TabSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, new())
{
    /// <summary>
    /// Creates a new instance of <see cref="TabSkin"/> for the specified named area.
    /// </summary>
    /// <param name="namedArea">The named area control.</param>
    /// <returns>A new instance of <see cref="TabSkin"/>.</returns>
    protected override TabSkin CreateItemSkin(NamedAreaControl namedArea)
    {
        return new TabSkin(namedArea.Id ?? "");
    }
}

/// <summary>
/// Represents the skin for a tabs control with customizable properties.
/// </summary>
public record TabsSkin : Skin<TabsSkin>
{
    /// <summary>
    /// Gets or initializes the ID of the active tab.
    /// </summary>
    public object? ActiveTabId { get; init; }

    /// <summary>
    /// Gets or sets the orientation of the tabs.
    /// </summary>
    public object? Orientation { get; set; }

    /// <summary>
    /// Gets or sets the height of the tabs.
    /// </summary>
    public object? Height { get; set; }

    /// <summary>
    /// Sets the ID of the active tab.
    /// </summary>
    /// <param name="activeTabId">The ID of the active tab to set.</param>
    /// <returns>A new instance of <see cref="TabsSkin"/> with the specified active tab ID.</returns>
    public TabsSkin WithActiveTabId(object activeTabId)
        => this with { ActiveTabId = activeTabId };

    /// <summary>
    /// Sets the orientation of the tabs.
    /// </summary>
    /// <param name="orientation">The orientation to set.</param>
    /// <returns>A new instance of <see cref="TabsSkin"/> with the specified orientation.</returns>
    public TabsSkin WithOrientation(object orientation)
        => this with { Orientation = orientation };

    /// <summary>
    /// Sets the height of the tabs.
    /// </summary>
    /// <param name="height">The height to set.</param>
    /// <returns>A new instance of <see cref="TabsSkin"/> with the specified height.</returns>
    public TabsSkin WithHeight(object height)
        => this with { Height = height };
}

/// <summary>
/// Represents the skin for an individual tab with a label.
/// </summary>
/// <param name="Label">The label of the tab.</param>
public record TabSkin(object Label) : Skin<TabSkin>
{
    /// <summary>
    /// Sets the tab label.
    /// </summary>
    /// <param name="label">The label to set.</param>
    /// <returns>A new instance of <see cref="TabSkin"/> with the specified label.</returns>
    public TabSkin WithLabel(object label)
        => this with { Label = label };
}
