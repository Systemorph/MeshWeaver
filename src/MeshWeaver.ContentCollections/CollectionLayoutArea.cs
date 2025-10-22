using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Generic layout area for displaying a file browser for any collection.
/// Parses collection and path from the URL format: area:'Collection', Id:'{collection}/{path}'
/// </summary>
public static class CollectionLayoutArea
{
    /// <summary>
    /// Renders a file browser for the specified collection at the given path.
    /// The collection and path are parsed from the host reference ID in format: {collection}/{path}
    /// </summary>
    public static UiControl Collection(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()?.Split("/", StringSplitOptions.RemoveEmptyEntries);
        if (split is null || split.Length < 1)
            return new MarkdownControl("Collection must be specified in format: Collection/Name or Collection/Name/Path");

        var collection = split[0];
        var path = split.Length > 1 ? string.Join('/', split.Skip(1)) : "/";

        var contentService = host.Hub.GetContentService();
        var collectionConfig = contentService.GetCollectionConfig(collection);

        if (collectionConfig == null)
            return new MarkdownControl($"Collection '{collection}' not found.");

        var fileBrowser = new FileBrowserControl(collection);

        fileBrowser = fileBrowser
            .WithCollectionConfiguration(collectionConfig);

        return Controls.Stack
            .WithView(Controls.Title($"Collection: {collectionConfig?.DisplayName ?? collection}", 1))
            .WithView(fileBrowser);
    }
}
