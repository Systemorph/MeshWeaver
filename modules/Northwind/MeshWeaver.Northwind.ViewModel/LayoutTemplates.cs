using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides layout templates for the Northwind view model.
/// </summary>
public static class LayoutTemplates
{
    /// <summary>
    /// Creates a value box with a title, icon, value, and growth indicator.
    /// </summary>
    /// <param name="title">The title of the value box.</param>
    /// <param name="icon">The icon to display in the value box.</param>
    /// <param name="value">The value to display in the value box.</param>
    /// <param name="growth">The growth indicator to display in the value box.</param>
    /// <returns>An object representing the value box.</returns>
    public static object ValueBox(string title, Icon icon, string value, object growth) =>
        Controls.Stack
            .WithSkin(skin => skin.WithOrientation(Orientation.Horizontal))
            .WithHorizontalGap(10)
            .WithView(Controls.Icon(icon).WithWidth("48px"))
            .WithView(Controls.Stack
                .WithVerticalGap(10)
                .WithView(Controls.Stack.WithView(Controls.H3(title)).WithView(growth)
                    .WithOrientation(Orientation.Horizontal).WithHorizontalGap(5))
                .WithView(Controls.H2(value))
            );

    /// <summary>
    /// Creates a growth percentage indicator based on current and previous values.
    /// </summary>
    /// <param name="current">The current value.</param>
    /// <param name="previous">The previous value.</param>
    /// <returns>An object representing the growth percentage indicator.</returns>
    public static object GrowthPercentage(double current, double previous)
    {
        var delta = current - previous;
        var percentage = delta / previous;
        var sign = delta >= 0 ? "+" : "-";
        var color = delta >= 0 ? "green" : "red";
        return Controls.Html($"<span style='color:{color}'>{sign}{percentage:P0}</span>");
    }
}
