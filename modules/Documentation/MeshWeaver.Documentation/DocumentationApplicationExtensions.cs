using MeshWeaver.ContentCollections;
using MeshWeaver.Documentation.LayoutAreas;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Documentation;

/// <summary>
/// Main entry point to the Document module View Models.
/// By adding a mesh node or a hosted hub,
/// you can install the views from the Documentation module.
/// </summary>
public static class DocumentationApplicationExtensions
{
    /// <summary>
    /// This method adds the views defined in the documentation module to
    /// the Documentation hub.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public static MessageHubConfiguration ConfigureDocumentation(MessageHubConfiguration config)
        => config
            .AddArticles()
            .AddEmbeddedResourceContentCollection("Documentation", typeof(DocumentationApplicationAttribute).Assembly, "Markdown")
            .AddFileSystemContentCollection("Blog", sp =>
                sp.GetRequiredService<IConfiguration>().GetSection("Blog")["BasePath"]
                ?? Path.Combine(Directory.GetCurrentDirectory(), "Blog"))
            .AddLayout(layout => layout
                .AddCounter()
                .AddCalculator()
                .AddDistributionStatistics()
                .AddProgress()
                .AddFileBrowser()
                .WithThumbnailBasePath("/app/Documentation/static/Documentation/thumbnails")
            );

}


