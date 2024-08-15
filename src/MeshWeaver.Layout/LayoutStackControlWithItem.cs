using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

public record LayoutStackControl()
    : ContainerControl<LayoutStackControl, LayoutStackSkin>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion,
        new());

public record LayoutStackSkin : Skin<LayoutStackSkin>
{
    public object HorizontalAlignment { get; init; }

    public object VerticalAlignment { get; init; }
    public object HorizontalGap { get; init; }
    public object VerticalGap { get; init; }
    public object Orientation { get; init; } = Layout.Orientation.Vertical;
    public object Wrap { get; init; }
    public object Width { get; init; }
    public object Height { get; init; }
    public LayoutStackSkin WithHorizontalAlignment(object horizontalAlignment)
        => This with { HorizontalAlignment = horizontalAlignment };
    public LayoutStackSkin WithVerticalAlignment(object verticalAlignment)
        => This with { VerticalAlignment = verticalAlignment };
    public LayoutStackSkin WithHorizontalGap(object horizontalGap)
        => This with { HorizontalGap = horizontalGap };
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
    public static LayoutStackControl WithHorizontalAlignment(this LayoutStackControl control, object horizontalAlignment)
        => control.WithSkin(skin => skin.WithHorizontalAlignment(horizontalAlignment));

    public static LayoutStackControl WithVerticalAlignment(this LayoutStackControl control, object verticalAlignment)
        => control.WithSkin(skin => skin.WithVerticalAlignment(verticalAlignment));

    public static LayoutStackControl WithHorizontalGap(this LayoutStackControl control, object horizontalGap)
        => control.WithSkin(skin => skin.WithHorizontalGap(horizontalGap));

    public static LayoutStackControl WithVerticalGap(this LayoutStackControl control, object verticalGap)
        => control.WithSkin(skin => skin.WithVerticalGap(verticalGap));

    public static LayoutStackControl WithOrientation(this LayoutStackControl control, object orientation)
        => control.WithSkin(skin => skin.WithOrientation(orientation));

    public static LayoutStackControl WithWrap(this LayoutStackControl control, object wrap)
        => control.WithSkin(skin => skin.WithWrap(wrap));

    public static LayoutStackControl WithWidth(this LayoutStackControl control, object width)
        => control.WithSkin(skin => skin.WithWidth(width));

    public static LayoutStackControl WithHeight(this LayoutStackControl control, object height)
        => control.WithSkin(skin => skin.WithHeight(height));
}
