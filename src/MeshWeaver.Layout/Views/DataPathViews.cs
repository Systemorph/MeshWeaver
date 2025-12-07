using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Views;

/// <summary>
/// Provides the $Data layout area for unified data references (data: prefix).
/// </summary>
public static class DataPathViews
{
    /// <summary>
    /// Area name for data references. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string DataAreaName = "$Data";

    /// <summary>
    /// Adds the $Data layout area for unified data references.
    /// </summary>
    public static LayoutDefinition AddDataReferenceView(this LayoutDefinition layout)
        => layout.WithView(ctx => ctx.Area == DataAreaName, DataContentView);

    /// <summary>
    /// Renders data content references as JSON in a markdown code block.
    /// The host.Reference.Id contains the data path like "Orders/10248"
    /// </summary>
    [Browsable(false)]
    private static IObservable<UiControl> DataContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        var localPath = host.Reference.Id?.ToString();
        var pathReference = new DataPathReference(localPath ?? string.Empty);



        // Use DataPathReference which handles both entity and collection paths
        var stream = host.Workspace.GetStream(pathReference);
        if (stream == null)
            return Observable.Return(Controls.Html($"<div class='error'>Unable to get stream for: {localPath}</div>"));

        return stream
                 .Select(changeItem =>
                 {
                     var data = SerializeToJson(changeItem?.Value, host.Hub.JsonSerializerOptions);
                     return Controls.Markdown($"```json\n{data}\n```");
                 });


    }

    private static string? SerializeToJson(object? data, JsonSerializerOptions options)
    {
        if (data == null)
            return null;

        try
        {
            return JsonSerializer.Serialize(data, new JsonSerializerOptions(options)
            {
                WriteIndented = true
            });
        }
        catch
        {
            return null;
        }
    }
}
