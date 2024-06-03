using System.Reactive.Linq;
using OpenSmc.Data;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Northwind.Domain;
using static OpenSmc.Layout.Controls;

namespace OpenSmc.Northwind.ViewModel;

public static class NorthwindViewModels
{
    public static MessageHubConfiguration AddNorthwindViewModels(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddLayout(
            layout => layout
                .WithView(nameof(Dashboard), Dashboard)
                .WithView(nameof(OrderSummary), _ => OrderSummary())
                .WithView(nameof(ProductSummary), _ => ProductSummary())
            .WithView(nameof(CustomerSummary), _ => CustomerSummary())
            .WithView(nameof(SupplierSummary), _ => SupplierSummary())
                .WithView(nameof(NavigationMenu), _ => NavigationMenu())
        );
    }

    private static object NavigationMenu()
    {
        return null; // TODO V10: Create a structure which allows me to return a container with NavLinks (03.06.2024, Roland Bürgi)
    }


    public static object Dashboard(LayoutArea layoutArea)
    {
        return Stack()
            .WithOrientation(Orientation.Vertical)
            .WithView(Html("<h1>Northwind Dashboard</h1>"))
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(OrderSummary())
                .WithView(ProductSummary())
            )
            .WithView(Stack().WithOrientation(Orientation.Horizontal)
                .WithView(CustomerSummary())
                .WithView(SupplierSummary())
            )
            ;

    }

    private static LayoutStackControl SupplierSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Supplier Summary</h2>"));

    private static LayoutStackControl CustomerSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Customer Summary</h2>"));

    private static LayoutStackControl ProductSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Product Summary</h2>"));

    private static LayoutStackControl OrderSummary() =>
        Stack().WithOrientation(Orientation.Vertical)
            .WithView(Html("<h2>Order Summary</h2>"))
            .WithView(area => area.Workspace.GetObservable<Order>()
                .Select(x =>
                x
                    .OrderByDescending(y => y.OrderDate)
                    .Take(5)
                    .Select(order => new OrderSummaryItem(area.Workspace.GetData<Customer>(order.CustomerId)?.ContactName, area.Workspace.GetData<OrderDetails>().Count(d => d.OrderId == order.OrderId), order.OrderDate))
                    .ToArray()
                    .ToDataGrid(conf => 
                    conf
                    .WithColumn(o => o.Customer)
                    .WithColumn(o => o.Products)
                    .WithColumn(o => o.Purchased, column => column.WithFormat("yyyy-MM-dd"))
                    )));



}
