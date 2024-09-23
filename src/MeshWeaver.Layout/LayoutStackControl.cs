using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;
/// <summary>
/// Represents a layout stack control with cust/// omizable properties.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/layoutstack">Fluent UI Blazor LayoutStack documentation</a>.
/// </remarks>

public record LayoutStackControl()
    : ContainerControl<LayoutStackControl, LayoutStackSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion,
        new());
/// <summary>
/// Represents the skin for a layout stack control.
/// </summary>
/// <remarks>
/// For more information, visit the 
/// <a href="https://www.fluentui-blazor.net/layoutstack">Fluent UI Blazor LayoutStack documentation</a>.
/// </remarks>

public record LayoutStackSkin : Skin<LayoutStackSkin>
/// <summary>
    /// Gets or initializes the horizontal alignment of the layout stack.
    /// </summary>
{
    public object HorizontalAlignment { get; init; }

/// <summary>
    /// Gets or initializes the vertical alignment of the layout stack.
    /// </summary>
    public object VerticalAlignment { get; init; }
    /// <summary>
    /// Gets or initializes the horizontal gap between elements in the layout stack.
    /// </summary>
    public object HorizontalGap { get; init; }
    /// <summary>
    /// Gets or initializes the vertical gap between elements in the layout stack.
    /// </summary>
    public object VerticalGap { get; init; }
    /// <summary>
    /// Gets or initializes the orientation of the layout stack.
    /// </summary>
    public object Orientation { get; init; } = Layout.Orientation.Vertical;
    /// <summary>
    /// Gets or initializes the wrap state of the layout stack.
    /// </summary>
    public object Wrap { get; init; }
    /// <summary>
    /// Gets or initializes the width of the layout stack.
    /// </summary>
    public object Width { get; init; }
    /// <summary>
    /// Gets or initializes the height of the layout stack.
    /// </summary>
    public object Height { get; init; }
    /// <summary>
    /// Sets the horizontal alignment of the layout stack.
    /// </summary>
    /// <param name="horizontalAlignment">The horizontal alignment to set.</param>
    /// <returns>A new <see cref="LayoutStackSkin"/> instance with the specified horizontal alignment.</returns>
    public LayoutStackSkin WithHorizontalAlignment(object horizontalAlignment)
        => This with { HorizontalAlignment = horizontalAlignment };
        /// <summary>
    /// Sets the vertical alignment of the layout stack.
    /// </summary>
    /// <param name="verticalAlignment">The vertical alignment to set.</param>
    /// <returns>A new <see cref="LayoutStackSkin"/> instance with the specified vertical alignment.</returns>
    public LayoutStackSkin WithVerticalAlignment(object verticalAlignment)
        => This with { VerticalAlignment = verticalAlignment };
    /// <summary>
    /// Sets the horizontal gap between elements in the layout stack.
    /// </summary>
    /// <param name="horizontalGap">The horizontal gap to set.</param>
    /// <returns>A new <see cref="LayoutStackSkin"/> instance with the specified horizontal gap.</returns>
    
    public LayoutStackSkin WithHorizontalGap(object horizontalGap)
        => This with { HorizontalGap = horizontalGap };
        /// <summary>
    /// Sets the vertical gap between elements in the layout stack.
    /// </summary>
    /// <param name="verticalGap">The vertical gap to set.</param>
    /// <returns>A new <see cref="LayoutStackSkin"/> instance with the specified vertical gap.</returns>
    public LayoutStackSkin WithVerticalGap(object verticalGap)
        => This with { VerticalGap = verticalGap };
    
    public LayoutStackSkin WithOrientation(object orientation)
        => This with { Orientation = orientation };
    public LayoutStackSkin WithWrap(object wrap)
        => This with { Wrap = wrap };
    public LayoutStackSkin WithWidth(object width)
        => This with { Width = width };
    public LayoutStackSkin WithHeight(object height) => This with { Height = height };

}
public static class LayoutStackExtensions
{
     /// <summary>
    /// Sets the horizontal alignment of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="horizontalAlignment">The horizontal alignment to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified horizontal alignment.</returns>
    public static LayoutStackControl WithHorizontalAlignment(this LayoutStackControl control, object horizontalAlignment)
        => control.WithSkin(skin => skin.WithHorizontalAlignment(horizontalAlignment));
        /// <summary>
    /// Sets the vertical alignment of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="verticalAlignment">The vertical alignment to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified vertical alignment.</returns>

    public static LayoutStackControl WithVerticalAlignment(this LayoutStackControl control, object verticalAlignment)
        => control.WithSkin(skin => skin.WithVerticalAlignment(verticalAlignment));
/// <summary>
    /// Sets the horizontal gap between elements in the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="horizontalGap">The horizontal gap to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified horizontal gap.</returns>
    public static LayoutStackControl WithHorizontalGap(this LayoutStackControl control, object horizontalGap)
        => control.WithSkin(skin => skin.WithHorizontalGap(horizontalGap));

 /// <summary>
    /// Sets the vertical gap between elements in the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="verticalGap">The vertical gap to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified vertical gap.</returns>
    public static LayoutStackControl WithVerticalGap(this LayoutStackControl control, object verticalGap)
        => control.WithSkin(skin => skin.WithVerticalGap(verticalGap));
        /// <summary>
    /// Sets the orientation of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="orientation">The orientation to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified orientation.</returns>

    public static LayoutStackControl WithOrientation(this LayoutStackControl control, object orientation)
        => control.WithSkin(skin => skin.WithOrientation(orientation));
/// <summary>
    /// Sets the wrap state of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="wrap">The wrap state to set.</param>
    public static LayoutStackControl WithWrap(this LayoutStackControl control, object wrap)
        => control.WithSkin(skin => skin.WithWrap(wrap));

/// <summary>
    /// Sets the width of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="width">The width to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified width.</returns>
    public static LayoutStackControl WithWidth(this LayoutStackControl control, object width)
        => control.WithSkin(skin => skin.WithWidth(width));
/// <summary>
    /// Sets the height of the layout stack control.
    /// </summary>
    /// <param name="control">The layout stack control.</param>
    /// <param name="height">The height to set.</param>
    /// <returns>A new <see cref="LayoutStackControl"/> instance with the specified height.</returns>
    public static LayoutStackControl WithHeight(this LayoutStackControl control, object height)
        => control.WithSkin(skin => skin.WithHeight(height));
}
