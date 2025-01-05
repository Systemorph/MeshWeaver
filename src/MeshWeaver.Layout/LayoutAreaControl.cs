using MeshWeaver.Data;
using MeshWeaver.Utils;

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

public record LayoutAreaControl(string AddressType, string AddressId, LayoutAreaReference Reference)
    : UiControl<LayoutAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{


    /// <summary>
    /// Gets or initializes the display area of the layout area control.
    /// </summary>
    public object DisplayArea { get; init; } 
    /// <summary>
    /// Gets or initializes the progress display state of the layout area control.
    /// </summary>
    public object ShowProgress { get; init; } = true;
    /// <summary>
    /// Sets the display area of the layout area control.
    /// </summary>
    /// <param name="displayArea">The display area to set.</param>
    /// <returns>A new <see cref="LayoutAreaControl"/> instance with the specified display area.</returns>

    public LayoutAreaControl WithDisplayArea(string displayArea) => this with { DisplayArea = displayArea };

    public virtual bool Equals(LayoutAreaControl other)
    {
        if(other is null) return false;
        if(ReferenceEquals(this, other)) return true;

        return base.Equals(other)
               && DisplayArea == other.DisplayArea
               && ShowProgress == other.ShowProgress 
               && Equals(Reference, other.Reference)
               && AddressType == other.AddressType
                && AddressId == other.AddressId;
        ;
    }
/// <summary>
    /// Returns a string that represents the current <see cref="LayoutAreaControl"/>.
    /// </summary>
    /// <returns>A string that represents the current <see cref="LayoutAreaControl"/>.</returns>
    public override string ToString()
    {
        return Reference.ToAppHref(AddressType, AddressId);
    }
 /// <summary>
    /// Serves as the default hash function.
    /// </summary>
    /// <returns>A hash code for the current <see cref="LayoutAreaControl"/>.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            DisplayArea,
            ShowProgress,
            Reference,
            AddressType.ToString(),
            AddressId.ToString()
        );
    }
}
