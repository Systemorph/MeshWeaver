using MeshWeaver.Documentation.LayoutAreas;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Documentation;

/// <summary>
/// Main entry point to the Document module View Models.
/// By adding a mesh node or a hosted hub,
/// you can install the views from the Documentation module.
/// </summary>
public static class DocumentationViewModels
{
    /// <summary>
    /// Use this configuration to add the views of the documentation module to the hub.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public static MessageHubConfiguration AddDocumentation(MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .AddCounter()
        );

}


