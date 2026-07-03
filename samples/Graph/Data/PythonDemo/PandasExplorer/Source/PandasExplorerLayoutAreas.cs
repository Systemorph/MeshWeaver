// <meshweaver>
// Id: PandasExplorerLayoutAreas
// DisplayName: Pandas Explorer Layout Areas
// </meshweaver>

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

/// <summary>
/// The INTERACTIVE frontend for the Python <c>py/pandas</c> participant
/// (<c>clients/python/meshweaver/examples/pandas_node.py</c>). A toolbar of real framework buttons +
/// a text input DRIVE a live <c>pandas.DataFrame</c> that lives in a Python process — fed from a CSV
/// file kept in mesh content (<see cref="PandasExplorer.SourcePath"/>) — and the grid below shows the
/// frame's current state as a genuine <see cref="DataGridControl"/> — never markdown or an HTML string.
/// <para>
/// Everything is reactive end-to-end: a button flips a trigger in the layout-area data store, the
/// grid sub-area re-observes it and POSTS the matching <see cref="PandasCommand"/> to <c>py/pandas</c>,
/// and the returned <see cref="DataGridControl"/> replaces the grid. When NO Python participant is
/// attached (the default in prod / CI), the post fails or times out and the area degrades to an
/// informative notice — it never hangs and never shows a raw error.
/// </para>
/// </summary>
public static class PandasExplorerLayoutAreas
{
    /// <summary>Stable mesh address of the Python participant holding the DataFrame.</summary>
    private static readonly Address PandasParticipant = new("py", "pandas");

    /// <summary>The single interactive area name.</summary>
    public const string ExplorerArea = "Explorer";

    // Layout-area data-store keys.
    private const string TriggerId = "pandasViewCommand";      // which read-only view to render next
    private const string GroupByColumnId = "pandasGroupByColumn"; // bound to the text input
    private const string DefaultGroupBy = "region";

    // Bounded wait for the Python round-trip: long enough for a real participant, short enough that
    // the no-node path degrades promptly instead of hanging.
    private static readonly TimeSpan BackendTimeout = TimeSpan.FromSeconds(8);

    private const string Intro =
        "This grid is rendered by a **Python** process — a `pandas.DataFrame` held live in the "
        + "`py/pandas` mesh participant, fed from a **CSV file kept in mesh content** "
        + "(`PythonDemo/SalesData` by default). *Load* makes the participant read that node over the "
        + "mesh and parse it with `pandas.read_csv`; the other buttons post `PandasCommand` messages "
        + "that mutate and analyse the frame, and the grid re-renders from whatever Python returns.";

    private const string NoNodeText =
        "**No Python pandas node attached.**\n\n"
        + "This area drives a live `pandas.DataFrame` held in a Python process. Start the participant "
        + "and it appears here automatically:\n\n"
        + "```bash\npython -m meshweaver.examples.pandas_node \\\n"
        + "    --url https://memex.meshweaver.cloud --token mw_… --address py/pandas\n```";

    /// <summary>Registers the interactive Explorer view.</summary>
    public static LayoutDefinition AddPandasExplorerLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView(ExplorerArea, Explorer);

    /// <summary>
    /// The Explorer area: a static header + toolbar, and a reactive grid sub-area that talks to the
    /// Python participant. The toolbar renders once; only the grid re-renders as commands flow.
    /// </summary>
    public static IObservable<UiControl?> Explorer(LayoutAreaHost host, RenderingContext _)
    {
        // Seed the group-by input once (so the "Group by" button always has a column to read).
        host.UpdateData(GroupByColumnId, DefaultGroupBy);

        return Observable.Return<UiControl?>(
            Controls.Stack
                .WithView(Controls.Title("Pandas Explorer", 2), "title")
                .WithView(Controls.Markdown(Intro), "intro")
                .WithView(BuildToolbar(host), "toolbar")
                .WithView((h, _) => GridRegion(h), "grid"));
    }

    // ---- the reactive grid ------------------------------------------------------------------------

    /// <summary>
    /// Observes the current view-command trigger and renders the corresponding grid. <c>Switch</c>
    /// cancels an in-flight request when a newer command arrives, so a click storm never piles up
    /// round-trips.
    /// </summary>
    private static IObservable<UiControl> GridRegion(LayoutAreaHost host) =>
        host.GetDataStream<PandasViewCommand>(TriggerId)
            .StartWith(PandasViewCommand.Render())
            .Where(view => view is not null)
            .Select(view => RenderGrid(host, view!))
            .Switch();

    /// <summary>
    /// Posts one grid-producing <see cref="PandasCommand"/> to <c>py/pandas</c> and maps the response
    /// to a control. Shows a loading placeholder first; a timeout / delivery failure / unexpected
    /// response all degrade to the informative no-node notice.
    /// </summary>
    private static IObservable<UiControl> RenderGrid(LayoutAreaHost host, PandasViewCommand view)
    {
        var delivery = host.Hub.Post(view.ToCommand(), o => o.WithTarget(PandasParticipant));
        if (delivery is null)
            return Observable.Return(NoNode());

        return host.Hub.Observe(delivery)
            .Select(response => response.Message is DataGridControl grid
                ? GridView(view, grid)
                : NoNode("The Python participant returned an unexpected response."))
            .Take(1)
            .Timeout(BackendTimeout)
            .Catch((Exception _) => Observable.Return(NoNode()))
            .StartWith(Loading(view));
    }

    private static UiControl GridView(PandasViewCommand view, DataGridControl grid) =>
        Controls.Stack
            .WithView(Controls.Markdown($"**Live DataFrame** — {view.Caption()}"), "status")
            .WithView(grid, "table");

    private static UiControl Loading(PandasViewCommand view) =>
        Controls.Stack
            .WithView(Controls.Markdown($"_Asking the Python participant to render the {view.Caption()}…_"), "status")
            .WithView(Controls.Progress("Contacting py/pandas…", 0), "table");

    private static UiControl NoNode(string? detail = null) =>
        Controls.Stack
            .WithView(Controls.Markdown(detail is null ? NoNodeText : $"{NoNodeText}\n\n_{detail}_"), "status")
            // A disabled/empty grid keeps the layout stable until a participant attaches.
            .WithView(Controls.DataGrid(Array.Empty<object>()), "table");

    // ---- the controls (the point) -----------------------------------------------------------------

    private static UiControl BuildToolbar(LayoutAreaHost host)
    {
        var groupByColumn = (new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("group-by column")
                .WithImmediate(true) with
            {
                DataContext = LayoutAreaReference.GetDataPointer(GroupByColumnId),
            }).WithStyle("min-width: 160px;");

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin: 8px 0 16px 0;")
            // load / append MUTATE the held frame, then refresh the grid.
            .WithView(Button("Load sales CSV", Appearance.Accent,
                ctx => LoadFromContent(ctx.Host)), "load")
            .WithView(Button("Append 2 rows", Appearance.Neutral,
                ctx => Mutate(ctx.Host, PandasCommand.AppendTwo())), "append")
            // group-by reads the text input, then renders a grouped view.
            .WithView(Button("Group by", Appearance.Neutral, ctx => GroupBy(ctx.Host)), "groupby")
            .WithView(groupByColumn, "groupByColumn")
            // analytical views — read-only, do not mutate the frame.
            .WithView(Button("Rolling mean (3)", Appearance.Neutral,
                ctx => View(ctx.Host, PandasViewCommand.Rolling("sales", 3))), "rolling")
            .WithView(Button("Describe", Appearance.Neutral,
                ctx => View(ctx.Host, PandasViewCommand.Describe())), "describe")
            .WithView(Button("Refresh", Appearance.Neutral,
                ctx => View(ctx.Host, PandasViewCommand.Render())), "refresh")
            .WithView(Button("Reset", Appearance.Neutral,
                ctx => Mutate(ctx.Host, PandasCommand.Reset())), "reset");
    }

    private static ButtonControl Button(string text, string appearance, Action<UiActionContext> onClick) =>
        Controls.Button(text)
            .WithAppearance(appearance)
            .WithClickAction(ctx =>
            {
                onClick(ctx);
                return Task.CompletedTask;
            });

    /// <summary>Flip the trigger to a new read-only view — the grid sub-area re-renders it.</summary>
    private static void View(LayoutAreaHost host, PandasViewCommand view) =>
        host.UpdateData(TriggerId, view);

    /// <summary>
    /// Load the CSV file kept in mesh content into the Python-held frame: resolve the source path from
    /// the explorer node's content (<see cref="PandasExplorer.SourcePath"/>), then post a <c>load</c>
    /// command carrying only the PATH — the participant reads the node over the mesh itself, so the
    /// data never travels through the frontend.
    /// </summary>
    private static void LoadFromContent(LayoutAreaHost host) =>
        SourcePath(host).Subscribe(path => Mutate(host, PandasCommand.LoadFrom(path)));

    /// <summary>
    /// The explorer node's <see cref="PandasExplorer.SourcePath"/> — one snapshot off the per-node
    /// hub's own node stream (the same read the framework's default node areas use). Falls back to
    /// <see cref="PandasExplorer.DefaultSourcePath"/> when the area is hosted without a node (tests,
    /// ad-hoc layout hosts) so the button stays functional everywhere.
    /// </summary>
    private static IObservable<string> SourcePath(LayoutAreaHost host)
    {
        var nodeStream = host.Workspace.GetStream<MeshNode>();
        if (nodeStream is null)
            return Observable.Return(PandasExplorer.DefaultSourcePath);

        var hubPath = host.Hub.Address.ToString();
        return nodeStream
            .Select(nodes => ExtractExplorer(nodes?.FirstOrDefault(n => n.Path == hubPath))?.SourcePath)
            .Select(path => string.IsNullOrWhiteSpace(path) ? PandasExplorer.DefaultSourcePath : path!)
            .Take(1)
            .Timeout(BackendTimeout)
            .Catch((Exception _) => Observable.Return(PandasExplorer.DefaultSourcePath));
    }

    /// <summary>
    /// Extracts the typed content from the MeshNode, handling both the typed record and the raw
    /// JsonElement shape (content arrives as JSON before the type is bound).
    /// </summary>
    private static PandasExplorer? ExtractExplorer(MeshNode? node)
    {
        if (node?.Content is PandasExplorer explorer)
            return explorer;

        if (node?.Content is JsonElement json)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<PandasExplorer>(json.GetRawText(), options);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Read the current group-by column from the input, then render the grouped view.</summary>
    private static void GroupBy(LayoutAreaHost host) =>
        host.GetDataStream<string>(GroupByColumnId)
            .Take(1)
            .Subscribe(column =>
                View(host, PandasViewCommand.GroupBy(
                    string.IsNullOrWhiteSpace(column) ? DefaultGroupBy : column!.Trim())));

    /// <summary>
    /// Post a mutation (<c>load</c> / <c>append</c> / <c>reset</c>) and, once the participant acks,
    /// refresh the grid so it reflects the new frame state. Race-free: the render only fires after
    /// the mutation is applied. On failure the trigger still flips to render, which shows the notice.
    /// </summary>
    private static void Mutate(LayoutAreaHost host, PandasCommand mutation)
    {
        var delivery = host.Hub.Post(mutation, o => o.WithTarget(PandasParticipant));
        if (delivery is null)
        {
            View(host, PandasViewCommand.Render());
            return;
        }

        host.Hub.Observe(delivery)
            .Take(1)
            .Timeout(BackendTimeout)
            .Subscribe(
                _ => View(host, PandasViewCommand.Render()),
                _ => View(host, PandasViewCommand.Render()));
    }
}
