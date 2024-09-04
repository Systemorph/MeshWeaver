using MeshWeaver.Assistant;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Catalog.ViewModel;

public static class CatalogAssistantArea
{

    public static LayoutDefinition AddCatalogAssistant(this LayoutDefinition layout)
    {
        return layout
            .WithView(nameof(Assistant), Assistant)
            ;
    }

    private static object Assistant(this LayoutAreaHost host, RenderingContext context) => 
        new AssistantControl();
}
