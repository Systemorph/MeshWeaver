using System.ComponentModel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Layout areas for content collections functionality
/// </summary>
public static class FileBrowserLayoutAreas
{

    /// <summary>
    /// FileBrowser layout area for displaying and managing files in a collection
    /// </summary>
    [Browsable(false)]
    public static UiControl FileBrowser(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()?.Split("/");
        if (split is null || split.Length < 1)
            return new MarkdownControl("Collection must be specified");

        var collection = ContentCollectionsExtensions.DecodeCollectionName(split[0]);
        var path = split.Length > 1 ? $"/{string.Join('/', split.Skip(1))}" : "/";

        var contentService = host.Hub.GetContentService();
        var collectionConfig = contentService.GetCollectionConfig(collection);

        var fileBrowser = new FileBrowserControl(collection)
            .WithPath(path)
            .WithNodePath(host.Hub.Address.ToString())
            .WithUrlBasePath(
                $"/{host.Hub.Address}/{ContentCollectionsExtensions.FileBrowserAreaName}/{ContentCollectionsExtensions.EncodeCollectionName(collection)}");

        if (collectionConfig != null)
        {
            fileBrowser = fileBrowser
                .WithCollectionConfiguration(collectionConfig)
                .WithCollectionInfo(collectionConfig.SourceType, collectionConfig.BasePath, collectionConfig.Settings);
            // Only auto-create the collection root — never a URL-supplied sub-path
            // (a mistyped deep link must not create folders).
            if (path == "/")
                fileBrowser = fileBrowser.CreatePath();
        }

        return fileBrowser;
    }
}
