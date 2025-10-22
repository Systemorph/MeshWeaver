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

        var collection = split[0];
        var path = split.Length > 1 ? string.Join('/', split.Skip(1)) : "/";

        var contentService = host.Hub.GetContentService();
        var collectionConfig = contentService.GetCollectionConfig(collection);

        var fileBrowser = new FileBrowserControl(collection);

        if (collectionConfig != null)
        {
            fileBrowser = fileBrowser
                .WithCollectionConfiguration(collectionConfig)
                .CreatePath();
        }

        return fileBrowser;
    }
}
