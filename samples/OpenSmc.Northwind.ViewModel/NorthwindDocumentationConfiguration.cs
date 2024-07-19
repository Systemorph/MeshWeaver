using OpenSmc.Documentation;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Northwind.ViewModel
{
    public static class NorthwindDocumentationConfiguration
    {
        private const string Overview = nameof(Overview);
        
        public static ApplicationMenuBuilder AddDocumentationMenu(this ApplicationMenuBuilder builder)
            => builder
                .WithNavLink(Overview, $"{builder.Layout.Hub.Address}/Doc/{Overview}");

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
