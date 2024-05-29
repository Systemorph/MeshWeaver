using System.Reactive.Linq;
using OpenSmc.Data;
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
        return configuration.AddLayout(layout => layout.WithView(nameof(Dashboard), Dashboard()));
    }


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
                                    .WithView(Html("<h1>Total Orders</h1>")))
                                    .OrdersDashboardTable()
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(Html("Total Customers"))
                                    .WithView(Html("100"))
                            )
                    )
                    .WithView(
                        Stack()
                            .WithSkin(Skin.VerticalPanel)
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(Html("Total Products"))
                                    .WithView(Html("100"))
                            )
                            .WithView(
                                Stack()
                                    .WithSkin(Skin.HorizontalPanel)
                                    .WithView(Html("Total Employees"))
                                    .WithView(Html("100"))
                            )
                        )
                    );
    }

    private static LayoutStackControl OrdersDashboardTable(this LayoutStackControl stack)
    {
        return stack.WithView(area => area.Workspace.GetObservable<Order>().Select(x => x.OrderByDescending(y => y.OrderDate).Take(5)).DistinctUntilChanged());
    }


}
