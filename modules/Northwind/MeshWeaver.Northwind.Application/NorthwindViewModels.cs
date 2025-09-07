﻿using MeshWeaver.GridModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Northwind.Application
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
                    .AddGridModel()
                    .AddLayout(layout =>
                        layout
                            .AddDomainViews()
                            .AddAnnualReport()
                            .AddDashboard()
                            .AddOrdersSummary()
                            .AddCustomerSummary()
                            .AddSupplierSummary()
                            .AddOrdersOverview()
                            .AddProductAnalysis()
                            .AddCustomerAnalysis()
                            .AddSalesGeography()
                            .AddEmployeePerformance()
                            .AddInventoryAnalysis()
                            .AddTimeSeriesAnalysis()
                            .AddDetailedReports()
                    )
                ;
        }

    }
}
