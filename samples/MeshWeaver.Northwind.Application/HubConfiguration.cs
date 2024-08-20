using MeshWeaver.Application;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.AgGrid;
using MeshWeaver.Blazor.ChartJs;
using MeshWeaver.Messaging;
using MeshWeaver.Demo.ViewModel;
using MeshWeaver.MeshBrowser.ViewModel;
using MeshWeaver.Northwind.Model;
using MeshWeaver.Northwind.ViewModel;

namespace MeshWeaver.Northwind.Application;

public static class HubConfiguration
{
    /// <summary>
    /// Defines the configuration for the Northwind application's messaging hubs.
    /// </summary>
    /// <remarks>
    /// This static class provides an extension method to configure various aspects of the Northwind application's messaging infrastructure, including Blazor components, ChartJs, AgGrid, and various Northwind domain-specific entities like employees, orders, suppliers, products, customers, and reference data.
    /// </remarks>
    /// <returns>
    /// The enriched <c>MessageHubConfiguration</c> instance, allowing for fluent configuration.
    /// </returns>
    /// <param name="configuration">The message hub configuration to be enriched with Northwind-specific settings.</param>
    /// <remarks>
    /// This method adds support for Blazor components, ChartJs, AgGrid, and configures the application to include various Northwind domain entities such as employees, orders, suppliers, products, customers, and reference data.
    /// </remarks>
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
            )
            .WithHostedHub(
                new ApplicationAddress("MeshBrowser", "dev"),
                application =>
                    application
                        .AddMeshBrowserViewModels()
            )
            .WithHostedHub(
                new ApplicationAddress("Demo", "dev"),
                application =>
                    application
                        .AddDemoViewModels()
            );
    }
}
