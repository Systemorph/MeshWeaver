using OpenSmc.Layout;
using OpenSmc.Messaging;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind;

public static class NorthwindViewConfiguration
{
    public static MessageHubConfiguration AddNorthwindViews(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(layout => layout.WithInitialState(Main()));
    }

    private static LayoutStackControl Main() =>
        Stack().WithSkin(Skin.MainWindow).WithView(SideMenu());
}
