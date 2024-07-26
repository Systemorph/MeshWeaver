using OpenSmc.Documentation;
using OpenSmc.Layout.Domain;
using OpenSmc.Messaging;

namespace OpenSmc.Demo.ViewModel;

public static class DemoDocumentationConfiguration
{
    private const string Overview = nameof(Overview);

    public static ApplicationMenuBuilder AddDocumentationMenu(this ApplicationMenuBuilder builder)
        => builder
            .WithNavLink(Overview, $"{builder.Layout.Hub.Address}/Doc/{Overview}");

    public static MessageHubConfiguration AddDemoDocumentation(
        this MessageHubConfiguration configuration
    ) => configuration
        .AddDocumentation(doc =>
            doc.WithEmbeddedResourcesFrom(typeof(ViewModelStateDemoArea).Assembly,
                source => source
                    .WithDocument(Overview,
                        $"{typeof(ViewModelStateDemoArea).Assembly.GetName().Name}.Markdown.Overview.md")
            )
            );
}
