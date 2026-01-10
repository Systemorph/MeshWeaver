using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;
/// <summary>
/// Represents a layout area control with customizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/layout">Fluent UI Blazor Layout documentation</a>.
/// </remarks>
/// <param name="Address">The address associated with the layout area control.</param>
/// <param name="Reference">The reference to the layout area.</param>

public record LayoutAreaControl(object Address, LayoutAreaReference Reference)
    : UiControl<LayoutAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Well-known area name for the Catalog view.
    /// </summary>
    public const string CatalogArea = "Catalog";

    /// <summary>
    /// Well-known area name for the Children view (thumbnails without search).
    /// </summary>
    public const string ChildrenArea = "Children";

    /// <summary>
    /// Well-known area name for the NodeTypes view.
    /// </summary>
    public const string NodeTypesArea = "NodeTypes";

    /// <summary>
    /// Creates a LayoutAreaControl for the Catalog area of the specified hub.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <returns>A LayoutAreaControl configured for the Catalog area.</returns>
    public static LayoutAreaControl Catalog(IMessageHub hub)
        => new(hub.Address, new LayoutAreaReference(CatalogArea));

    /// <summary>
    /// Creates a LayoutAreaControl for the Children area of the specified hub.
    /// Shows child nodes as thumbnails without search bar.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <returns>A LayoutAreaControl configured for the Children area.</returns>
    public static LayoutAreaControl Children(IMessageHub hub)
        => new(hub.Address, new LayoutAreaReference(ChildrenArea));

    /// <summary>
    /// Creates a LayoutAreaControl for the NodeTypes area of the specified hub.
    /// Shows NodeType nodes defined at this level.
    /// </summary>
    /// <param name="hub">The message hub.</param>
    /// <returns>A LayoutAreaControl configured for the NodeTypes area.</returns>
    public static LayoutAreaControl NodeTypes(IMessageHub hub)
        => new(hub.Address, new LayoutAreaReference(NodeTypesArea));

    /// <summary>
    /// Gets or initializes the display area of the layout area control.
    /// </summary>
    public object? ProgressMessage { get; init; }
    /// <summary>
    /// Gets or initializes the progress display state of the layout area control.
    /// </summary>
    public object? ShowProgress { get; init; } = true;
    /// <summary>
    /// Sets the display area of the layout area control.
    /// </summary>
    /// <param name="progressMessage">The display area to set.</param>
    /// <returns>A new <see cref="LayoutAreaControl"/> instance with the specified display area.</returns>

    public LayoutAreaControl WithProgressMessage(string progressMessage) => this with { ProgressMessage = progressMessage };

    public virtual bool Equals(LayoutAreaControl? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return base.Equals(other)
               && ProgressMessage == other.ProgressMessage
               && ShowProgress == other.ShowProgress
               && Equals(Reference, other.Reference)
               && Address.Equals(other.Address)
                ;
    }
    /// <summary>
    /// Returns a string that represents the current <see cref="LayoutAreaControl"/>.
    /// </summary>
    /// <returns>A string that represents the current <see cref="LayoutAreaControl"/>.</returns>
    public override string ToString()
    {
        return Reference.ToHref(Address);
    }
    /// <summary>
    /// Serves as the default hash function.
    /// </summary>
    /// <returns>A hash code for the current <see cref="LayoutAreaControl"/>.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            ProgressMessage,
            ShowProgress,
            Reference,
            Address
        );
    }

    public LayoutAreaControl WithShowProgress(bool showProgress)
        => this with { ShowProgress = showProgress };
}
