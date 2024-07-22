using OpenSmc.Documentation;
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
        public static ApplicationMenuBuilder AddDocumentationMenu(this ApplicationMenuBuilder builder)
            => builder
                .WithNavLink(Overview, $"{builder.Layout.Hub.Address}/Doc/{Overview}");

        /// <summary>
        /// Represents the configuration for the MessageHub.
        /// </summary>
        public static MessageHubConfiguration AddNorthwindDocumentation(
            this MessageHubConfiguration configuration
        ) => configuration
            .AddDocumentation(doc =>
                doc.WithEmbeddedResourcesFrom(typeof(NorthwindDashboardArea).Assembly,
                    source => source
                        .WithDocument(Overview,
                            $"{typeof(NorthwindDashboardArea).Assembly.GetName().Name}.Markdown.Overview.md")
                )
                );
    }
}
