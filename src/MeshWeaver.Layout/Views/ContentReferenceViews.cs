using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Views;

/// <summary>
/// Provides the Data layout area for unified data references (data: prefix).
/// </summary>
public static class ContentReferenceViews
{
    /// <summary>
    /// Adds the Data layout area for unified data references.
    /// Note: The Content layout area is handled by ContentLayoutArea in MeshWeaver.ContentCollections.
    /// </summary>
    public static LayoutDefinition AddDataReferenceView(this LayoutDefinition layout)
        => layout.WithView(ctx => ctx.Area == "Data", DataContentView);

    /// <summary>
    /// Renders data content references as JSON in a markdown code block.
    /// The host.Reference.Id contains the full path like "data:app/Northwind/Orders/10248"
    /// </summary>
    [Browsable(false)]
    private static IObservable<UiControl?> DataContentView(
        LayoutAreaHost host,
        RenderingContext ctx)
    {
        var path = host.Reference.Id?.ToString();
        if (string.IsNullOrEmpty(path))
            return Observable.Return<UiControl?>(Controls.Html("<div class='error'>No data path specified</div>"));

        // Create a UnifiedReference and get the stream
        var unifiedRef = new UnifiedReference(path);
        var stream = host.Workspace.GetStream(unifiedRef);

        if (stream == null)
            return Observable.Return<UiControl?>(Controls.Html($"<div class='error'>Unable to get stream for: {path}</div>"));

        // Bind the data stream and render as JSON in a markdown code block
        return stream
            .Select(changeItem => RenderDataAsJson(changeItem?.Value, host.Hub.JsonSerializerOptions));
    }

    private static UiControl? RenderDataAsJson(object? data, JsonSerializerOptions options)
    {
        if (data == null)
            return Controls.Html("<div class='data-loading'>Loading data...</div>");

        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(options)
            {
                WriteIndented = true
            });

            // Render as a markdown code block with JSON syntax highlighting
            var markdown = $"```json\n{json}\n```";
            return Controls.Markdown(markdown);
        }
        catch (Exception ex)
        {
            return Controls.Html($"<div class='error'>Error serializing data: {ex.Message}</div>");
        }
    }
}
