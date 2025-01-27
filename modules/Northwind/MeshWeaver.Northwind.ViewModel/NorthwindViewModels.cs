using MeshWeaver.Articles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Northwind.ViewModel
{
    /// <summary>
    /// Provides a centralized registration mechanism for all Northwind application views and configurations. This static class facilitates the addition of various Northwind-specific views and documentation to the application's MessageHub configuration.
    /// </summary>
    public static class NorthwindViewModels
    {
        /// <summary>
        /// Registers all Northwind views and configurations to the provided MessageHub configuration.
        /// </summary>
        /// <param name="configuration">The MessageHub configuration to be enhanced with Northwind views and settings.</param>
        /// <returns>The updated MessageHub configuration with Northwind views and documentation added.</returns>
        /// <remarks>
        /// This method sequentially adds dashboard, product summary, orders summary, customer summary, and supplier summary views to the application layout. It also configures the application menu and default views, and includes Northwind-specific documentation.
        /// </remarks>
        public static MessageHubConfiguration AddNorthwindViewModels(
            this MessageHubConfiguration configuration
        )
        {
            return configuration
                    .AddNorthwindDocumentation()
                    .AddDomainViews()
                    .AddLayout(layout =>
                        layout
                            .AddAnnualReport()
                            .AddDashboard()
                            .AddProductsSummary()
                            .AddOrdersSummary()
                            .AddCustomerSummary()
                            .AddSupplierSummary()
                            .WithNavMenu((menu, host, _) =>
                                menu.WithNavGroup(
                                    "Northwind",
                                    group => group.WithUrl("article/Northwind/Overview")
                                        .WithNavLink("Articles", "articles/Northwind")
                                        .WithNavLink("Areas", "app/Northwind/LayoutAreas")
                                        .WithNavLink("Data Model", "app/Northwind/Model")

                                )
                            )

                    )
                ;
        }

    }
}
