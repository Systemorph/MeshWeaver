// <meshweaver>
// Id: NorthwindHelpers
// DisplayName: Northwind Helpers
// </meshweaver>

using System.Globalization;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// UI helper methods for Northwind views.
/// </summary>
public static class NorthwindHelpers
{
    /// <summary>
    /// Creates a value box with title, icon, value, and optional growth indicator.
    /// </summary>
    public static UiControl ValueBox(string title, Icon icon, string value, UiControl? growth) =>
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
    /// Creates a growth percentage indicator.
    /// </summary>
    public static UiControl GrowthPercentage(double current, double previous)
    {
        var delta = current - previous;
        var percentage = delta / previous;
        var sign = delta >= 0 ? "+" : "-";
        var color = delta >= 0 ? "green" : "red";
        return Controls.Html($"<span style='color:{color}'>{sign}{percentage:P0}</span>");
    }

    /// <summary>
    /// Formats a number with K/M/B suffix.
    /// </summary>
    public static string ToSuffixFormat(this double num, string format = "0.##")
    {
        switch (num)
        {
            case > 999999999:
            case < -999999999:
                return num.ToString("0,,,.###B", CultureInfo.InvariantCulture);
            case > 999999:
            case < -999999:
                return num.ToString("0,,.##M", CultureInfo.InvariantCulture);
            case > 999:
            case < -999:
                return num.ToString("0,.#K", CultureInfo.InvariantCulture);
            default:
                return num.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Formats an integer with K/M/B suffix.
    /// </summary>
    public static string ToSuffixFormat(this int num) =>
        ((double)num).ToSuffixFormat("0");
}
