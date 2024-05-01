using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
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

    private static object Dashboard() =>
        Stack()
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

    private const string MainContent = nameof(MainContent);
}
