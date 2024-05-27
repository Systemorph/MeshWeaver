using OpenSmc.Layout;
using OpenSmc.Messaging;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind;

public static class NorthwindViews
{
    public static MessageHubConfiguration AddNorthwindViews(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(layout => layout.WithView(nameof(Dashboard), Main()));
    }

    private static LayoutStackControl Main() =>
        Stack().WithSkin(Skin.MainWindow).WithView(SideMenu());

    private static LayoutStackControl SideMenu() =>
        Stack()
            .WithSkin(Skin.SideMenu)
            .WithView(
                Stack()
                    .WithSkin(Skin.VerticalPanel)
                    .WithView(
                        Menu("Dashboard")
                    // .WithClickAction(c => c.Layout.Render(MainContent, Dashboard())                            )
                    )
            );

    public static UiControl Dashboard()
    {
        return Stack()
            .WithSkin(Skin.VerticalPanel)
            .WithView(
                Stack()
                    .WithSkin(Skin.HorizontalPanel)
                    .WithView(
                        Stack()
                            .WithSkin(Skin.VerticalPanel)
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("Total Orders"))
                                    .WithView(HtmlView("100"))
                            )
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("Total Customers"))
                                    .WithView(HtmlView("100"))
                            )
                    )
                    .WithView(
                        Stack()
                            .WithSkin(Skin.VerticalPanel)
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("Total Products"))
                                    .WithView(HtmlView("100"))
                            )
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(HtmlView("Total Employees"))
                                    .WithView(HtmlView("100"))
                            )
                    )
            );
    }


}
