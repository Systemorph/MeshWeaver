using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Documentation;
public static class DocumentationViewModels
{
    public static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .WithNavMenu((menu, host, _) =>
                menu.WithNavGroup(
                        "Documentation",
                        null,
                        "/article/Documentation/Overview"
                    )
                    .WithNavLink("Articles", "/articles/Documentation")
                    .WithNavLink("Areas", "/app/Documentation/LayoutAreas")
                    //.WithNavLink("Data Model", "/app/Documentation/Model")
            )
        );

}


