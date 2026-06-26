namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Implemented by objects that can convert themselves into a UI control for rendering in the layout system.
/// </summary>
public interface IRenderableObject
{
    /// <summary>Converts this object to its corresponding UI control representation.</summary>
    /// <returns>The <see cref="UiControl"/> that renders this object.</returns>
    UiControl ToControl();
}
