using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Northwind.ViewModel;

public static class LayoutTemplates
{
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

    public static object GrowthPercentage(double current, double previous)
    {
        var delta = current - previous;
        var percentage = delta / previous;
        var sign = delta >= 0 ? "+" : "-";
        var color = delta >= 0 ? "green" : "red";
        return Controls.Html($"<span style='color:{color}'>{sign}{percentage:P0}</span>");
    }

}
