namespace MeshWeaver.Layout;

/// <summary>
/// A container control that renders its child areas as labelled property editors,
/// each wrapped with a <see cref="PropertySkin"/> item skin.
/// </summary>
public record EditorControl()
    : ContainerControlWithItemSkin<EditorControl, EditorSkin, PropertySkin>(ModuleSetup.ModuleName,
        ModuleSetup.ApiVersion, new())
{
    /// <summary>Returns a default <see cref="PropertySkin"/> for the given named area.</summary>
    /// <param name="namedArea">The named child area being wrapped.</param>
    /// <returns>A new default <see cref="PropertySkin"/> instance.</returns>
    protected override PropertySkin CreateItemSkin(NamedAreaControl namedArea) => new();
}

/// <summary>Skin record for <see cref="EditorControl"/>; carries no additional style properties.</summary>
public record EditorSkin : Skin<EditorSkin>;
