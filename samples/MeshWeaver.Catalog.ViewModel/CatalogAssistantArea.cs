using MeshWeaver.Assistant;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Catalog.ViewModel;

public static class CatalogAssistantArea
{
    private const string AssistantSystemMessage = @"
You are a helpful assistant which helps people find hotels.
";

    private static IReadOnlyCollection<string> suggestions =
    [
        "Can you recommend a few hotels near the ocean with beach access and good views",
        "Hotels with best SPA services"
    ];

    public static LayoutDefinition AddCatalogAssistant(this LayoutDefinition layout)
    {
        return layout
            .WithView(nameof(Assistant), Assistant)
            ;
    }

    private static object Assistant(this LayoutAreaHost host, RenderingContext context) => 
        new AssistantControl()
            .WithSystemMessage(AssistantSystemMessage)
            .WithSuggestions(suggestions);
}
