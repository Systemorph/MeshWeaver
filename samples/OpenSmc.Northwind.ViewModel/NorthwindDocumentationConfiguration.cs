using System.Reflection;
using OpenSmc.Documentation;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// Provides configuration options for the Northwind documentation.
    /// </summary>
     public static class NorthwindDocumentationConfiguration
    {
        private const string Overview = nameof(Overview);

        /// <summary>
        /// Represents a builder for creating application menus.
        /// </summary>
        /// <param name="layout">The layout definition to which the documentation menu item will be added.</param>
        /// <returns>The updated application menu builder with the Northwind documentation menu item added.</returns>
        /// <remarks>
        /// This method adds a documentation menu item for the Northwind overview to the application's main menu.
        /// It creates a navigation link for the Northwind overview documentation, using the application's layout hub address to construct the URL.
        /// </remarks>

        /// <summary>
        /// Represents the configuration for the MessageHub.
        /// </summary>
        /// <param name="configuration">The MessageHub configuration to which the Northwind documentation will be added.</param>
        /// <returns>The updated MessageHub configuration with Northwind documentation resources included.</returns>
        /// <remarks>
        /// This method configures the MessageHub to include documentation for the Northwind application by embedding resources from the NorthwindDashboardArea assembly.
        /// It adds Northwind-specific documentation to the MessageHub, making it available application-wide.
        /// It specifies the use of embedded resources for documentation content, particularly targeting the "Overview" document within the NorthwindDashboardArea assembly.
        /// </remarks>
        public static MessageHubConfiguration AddNorthwindDocumentation(
            this MessageHubConfiguration configuration
        ) => configuration
            .AddDocumentation()
            .AddLayout(layout => layout.AddDocumentationMenu(typeof(NorthwindDocumentationConfiguration).Assembly))
            ;
    }
}
