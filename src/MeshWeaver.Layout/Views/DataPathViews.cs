using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Views;

/// <summary>
/// Provides the $Data layout area for unified data references.
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
        => layout
            .WithView(ctx => ctx.Area == DataAreaName, DataContentView);

    /// <summary>
    /// Renders data content references as JSON in a markdown code block.
    /// The host.Reference.Id contains the data path like "Orders/10248"
    /// For empty path (self-reference), uses the default data reference (Content).
    ///
    /// <para>A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the markdown block
    /// and the "Load all" button are declared at once and bound by pointer to
    /// <c>/data/{viewId}</c>; <see cref="FeedDataView"/> projects the referenced data into that
    /// slot. The first emission is the page — a loading line until the data stream answers —
    /// and every later change of the data reaches the same controls.</para>
    /// </summary>
    [Browsable(false)]
    private static UiControl DataContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        var localPath = host.Reference.Id?.ToString();
        var suffix = localPath?.Replace("/", "_") ?? "self";
        var viewId = $"dataView_{suffix}";
        var showFullKey = $"showFull_{suffix}";

        // The feed starts when the template is BUILT into the area (the same seam Template.Bind
        // uses), so its subscription belongs to the rendered area and ends with it.
        return BuildTemplate(viewId, showFullKey)
            .WithBuildup((h, c, store) =>
            {
                FeedDataView(h, c, localPath, viewId, showFullKey);
                return new(store, [], null);
            });
    }

    /// <summary>
    /// The $Data area's control tree. Reads nothing: every value is a pointer into
    /// <c>/data/{viewId}</c> (<see cref="DataViewModel"/>), so the tree renders before any data
    /// has arrived and follows each later projection.
    /// </summary>
    /// <param name="viewId">The data slot <see cref="FeedDataView"/> writes.</param>
    /// <param name="showFullKey">The data slot the "Load all" button flips.</param>
    /// <returns>The template.</returns>
    internal static StackControl BuildTemplate(string viewId, string showFullKey)
    {
        var dataContext = LayoutAreaReference.GetDataPointer(viewId);
        var markdown = new MarkdownControl(new JsonPointerReference(DataViewModel.MarkdownPointer))
        {
            DataContext = dataContext,
        }.WithStyle(style => style
            .WithWidth("100%")
            .WithMaxHeight("400px")
            .WithOverflow("auto"));

        var loadAll = Controls.Button(new JsonPointerReference(DataViewModel.LoadAllLabelPointer))
            .WithClickAction(c =>
            {
                c.Host.UpdateData(showFullKey, new DataViewState { ShowFull = true });
                return Task.CompletedTask;
            }) with
        {
            DataContext = dataContext,
            // Hidden until the projection reports that the JSON was cut.
            Style = new JsonPointerReference(DataViewModel.LoadAllStylePointer),
        };

        return Controls.Stack.WithView(markdown).WithView(loadAll);
    }

    /// <summary>
    /// The data half of the $Data area: subscribes the referenced data (or the default data
    /// reference for a self-reference) together with the "show full" flag and writes the
    /// projection into <c>/data/{viewId}</c>. Builds no control. The subscription lives as
    /// long as the area.
    /// </summary>
    private static void FeedDataView(
        LayoutAreaHost host, RenderingContext ctx, string? localPath, string viewId, string showFullKey)
    {
        var options = host.Hub.JsonSerializerOptions;
        var noData = host.Localize("data.none");
        var showFullStream = host.GetDataStream<DataViewState>(showFullKey).StartWith(new DataViewState());

        // The loading line is the first value of the SAME subscription that later carries the
        // data, so it can never overwrite a value that arrived first.
        var projection = OpenData(host, localPath) is { } data
            ? data.CombineLatest(showFullStream, (value, state) =>
                    Project(value, state?.ShowFull ?? false, options, noData,
                        lines => host.Localize("data.loadAll", lines)))
                .DistinctUntilChanged()
            : Observable.Return(DataViewModel.Message(string.IsNullOrEmpty(localPath)
                ? host.Localize("data.noDefaultReference")
                : host.Localize("data.streamUnavailable", localPath)));

        host.RegisterForDisposal(ctx.Area, projection
            .StartWith(DataViewModel.Message(host.Localize("ui.mdLoading")))
            .Subscribe(
                view => host.UpdateData(viewId, view),
                ex =>
                {
                    host.Hub.ServiceProvider.GetRequiredService<ILogger<DataViewState>>()
                        .LogWarning(ex, "$Data could not read {Path}", localPath ?? "(default reference)");
                    host.UpdateData(viewId, DataViewModel.Message(host.Localize("data.failed", ex.Message)));
                }));
    }

    /// <summary>The referenced data: the default data reference for a self-reference, else the
    /// <see cref="DataPathReference"/> stream; <c>null</c> when neither can be opened.</summary>
    private static IObservable<object?>? OpenData(LayoutAreaHost host, string? localPath)
    {
        if (string.IsNullOrEmpty(localPath))
            return host.Workspace.DataContext.DefaultDataReferenceFactory?.Invoke(host.Workspace);

        // DataPathReference handles both entity and collection paths. A path the workspace cannot
        // open is reported in the slot (data.streamUnavailable), not thrown.
        //
        // 🚨 Ask the map the read itself uses (the #5065 rule in DomainLayoutAreas.Catalog). A first
        // segment that is neither a virtual path nor a collection a data source maps does not
        // return null from GetStream — it throws `ArgumentException: Collections X are not mapped
        // to any source` three frames down, out of the render, and the viewer got the framework's
        // render-failure sentence instead of this area's own line.
        var dataContext = host.Workspace.DataContext;
        var prefix = localPath.Split('/', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (prefix is null
            || (!dataContext.VirtualPaths.ContainsKey(prefix) && dataContext.GetTypeSource(prefix) is null))
            return null;

        ISynchronizationStream? stream;
        try
        {
            stream = host.Workspace.GetStream(new DataPathReference(localPath));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return stream is null ? null : ((IObservable<ChangeItem<object>>)stream).Select(ci => ci?.Value);
    }

    private static DataViewModel Project(
        object? data, bool showFull, JsonSerializerOptions options, string noData, Func<int, string> loadAllLabel)
    {
        var fullJson = SerializeToJson(data, options);
        if (string.IsNullOrEmpty(fullJson))
            return DataViewModel.Message(noData);

        var lines = fullJson.Split('\n');
        var isTruncated = !showFull && lines.Length > MaxTruncatedLines;
        var displayJson = isTruncated
            ? string.Join('\n', lines.Take(MaxTruncatedLines)) + "\n..."
            : fullJson;

        return new DataViewModel(
            $"```json\n{displayJson}\n```",
            isTruncated ? loadAllLabel(lines.Length) : "",
            isTruncated ? "" : DataViewModel.Hidden);
    }

    /// <summary>
    /// The projection the $Data template binds to — one slot per area instance.
    /// </summary>
    /// <param name="Markdown">The markdown shown in the block (a JSON fence, or a message).</param>
    /// <param name="LoadAllLabel">The "Load all" button's label.</param>
    /// <param name="LoadAllStyle">The button's style: hidden unless the JSON was truncated.</param>
    internal record DataViewModel(string Markdown, string LoadAllLabel, string LoadAllStyle)
    {
        internal const string MarkdownPointer = "markdown";
        internal const string LoadAllLabelPointer = "loadAllLabel";
        internal const string LoadAllStylePointer = "loadAllStyle";
        internal const string Hidden = "display: none;";

        internal static DataViewModel Message(string markdown) => new(markdown, "", Hidden);
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
