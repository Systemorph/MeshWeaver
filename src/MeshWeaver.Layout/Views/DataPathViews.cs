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

    private const int MaxTruncatedLines = 100;

    /// <summary>
    /// Adds the $Data layout area for unified data references.
    /// For self-reference (empty path), returns a JSON representation of the current data context.
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
        if (string.IsNullOrEmpty(localPath))
            return Observable.Return(Controls.Html("<div class='muted'>No data path specified</div>"));

        var pathReference = new DataPathReference(localPath);

        // Use DataPathReference which handles both entity and collection paths
        ISynchronizationStream? stream;
        try
        {
            stream = host.Workspace.GetStream(pathReference);
        }
        catch (InvalidOperationException)
        {
            return Observable.Return(Controls.Html($"<div class='error'>Unable to create stream for: {localPath}</div>"));
        }

        if (stream == null)
            return Observable.Return(Controls.Html($"<div class='error'>Unable to get stream for: {localPath}</div>"));

        // State key for tracking whether to show full content (using string "true"/"false" since GetDataStream requires reference type)
        var showFullKey = $"showFull_{localPath?.Replace("/", "_")}";

        // Combine data stream with state stream to react to both changes
        var showFullStream = host.GetDataStream<DataViewState>(showFullKey).StartWith(new DataViewState());
        var dataStream = (IObservable<ChangeItem<object>>)stream;

        return dataStream.CombineLatest<ChangeItem<object>, DataViewState?, UiControl>(showFullStream,
            (changeItem, state) =>
            {
                var showFull = state?.ShowFull ?? false;
                var fullJson = SerializeToJson(changeItem?.Value, host.Hub.JsonSerializerOptions);
                if (string.IsNullOrEmpty(fullJson))
                    return Controls.Html("<div class='muted'>No data</div>");

                var lines = fullJson.Split('\n');
                var isTruncated = !showFull && lines.Length > MaxTruncatedLines;

                var displayJson = isTruncated
                    ? string.Join('\n', lines.Take(MaxTruncatedLines)) + "\n..."
                    : fullJson;

                var markdown = Controls.Markdown($"```json\n{displayJson}\n```")
                    .WithStyle(style => style
                        .WithWidth("100%")
                        .WithMaxHeight("400px")
                        .WithOverflow("auto"));

                if (isTruncated)
                {
                    return Controls.Stack
                        .WithView(markdown)
                        .WithView(Controls.Button($"Load All ({lines.Length} lines)")
                            .WithClickAction(_ =>
                            {
                                host.UpdateData(showFullKey, new DataViewState { ShowFull = true });
                                return Task.CompletedTask;
                            }));
                }

                return markdown;
            });
    }

    /// <summary>
    /// State class for tracking data view display mode.
    /// </summary>
    private class DataViewState
    {
        public bool ShowFull { get; init; }
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
