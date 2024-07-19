using OpenSmc.Application;
using OpenSmc.Blazor;
using OpenSmc.Blazor.AgGrid;
using OpenSmc.Blazor.ChartJs;
using OpenSmc.Layout;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Model;
using OpenSmc.Northwind.ViewModel;

namespace OpenSmc.Northwind.Application;

public static class HubConfiguration
{
    public static MessageHubConfiguration ConfigureNorthwindHubs(
        this MessageHubConfiguration configuration
    )
    {
        // TODO V10: add pre-compiled statement to remove all northwind related config (05.06.2024, Alexander Kravets)
        return configuration
            .AddBlazor(x => 
                x.AddChartJs()
                    .AddAgGrid()
            )
            .WithHostedHub(
                new ApplicationAddress("Northwind", "dev"),
                application =>
                    application
                        .AddNorthwindViewModels()
                        .AddNorthwindEmployees()
                        .AddNorthwindOrders()
                        .AddNorthwindSuppliers()
                        .AddNorthwindProducts()
                        .AddNorthwindCustomers()
                        .AddNorthwindReferenceData()
            );


    }
}
