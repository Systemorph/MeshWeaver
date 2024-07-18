using OpenSmc.Documentation;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Northwind.ViewModel
{
    public static class NorthwindDocumentationConfiguration
    {
        private const string Overview = nameof(Overview);
        
        public static ApplicationMenuBuilder DocumentationMenu(this ApplicationMenuBuilder builder)
            => builder
                .WithNavLink(Overview, $"{builder.Layout.Hub.Address}/Doc/{Overview}");

        public static MessageHubConfiguration AddNorthwindDocumentation(
            this MessageHubConfiguration configuration
        ) => configuration
            .AddDocumentation(doc =>
                doc.WithEmbeddedResourcesFrom(typeof(NorthwindLayoutAreas).Assembly,
                    source => source
                        .WithDocument(Overview,
                            $"{typeof(NorthwindLayoutAreas).Assembly.GetName().Name}.Markdown.Overview.md")
                )
                );
    }
}
