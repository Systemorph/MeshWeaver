using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Layout area for displaying article catalog
/// </summary>
public static class ArticlesLayoutArea
{
    /// <summary>
    /// Articles layout area that displays the article catalog for a specific collection.
    /// By default, shows articles from the current hub address only.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>An Article Catalog control</returns>
    public static UiControl? Articles(LayoutAreaHost host, RenderingContext context)
    {

        return new ArticleCatalogControl() { Addresses = new[] { host.Hub.Address } };
    }
}
