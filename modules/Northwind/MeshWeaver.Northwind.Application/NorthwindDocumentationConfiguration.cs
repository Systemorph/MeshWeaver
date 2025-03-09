using MeshWeaver.Messaging;

namespace MeshWeaver.Northwind.Application
{
    /// <summary>
    /// Provides configuration options for the Northwind documentation.
    /// </summary>
    public static class NorthwindDocumentationConfiguration
    {
        private const string Overview = nameof(Overview);

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
            // .AddLayout(layout => layout.AddDocumentationMenuForAssemblies(typeof(NorthwindDocumentationConfiguration).Assembly))
            ;
    }
}
