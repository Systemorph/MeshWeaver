using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// Aggregate layer to register all Northwind views in one go.
    /// </summary>
    public static class NorthwindViewModelsRegistry
    {
        /// <summary>
        /// Adds all the northwind views.
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static MessageHubConfiguration AddNorthwindViewModels(
            this MessageHubConfiguration configuration
        )
        {
            return configuration.AddLayout(layout =>
                        layout.AddDashboard()
                            .AddProductsSummary()
                            .AddOrdersSummary()
                            .AddCustomerSummary()
                            .AddSupplierSummary()
                            .ConfigureApplication(views => views
                                .WithMenu(menu => menu
                                    .AddDocumentationMenu()
                                    .AddRegisteredViews()
                                    .AddTypesCatalogs()
                                )
                                .DefaultViews()
                            )
                    )
                    .AddNorthwindDocumentation()
                ;
        }

    }
}
