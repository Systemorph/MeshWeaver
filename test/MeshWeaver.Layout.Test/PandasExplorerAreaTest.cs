using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Records every <see cref="PandasCommand"/> a fake <c>py/pandas</c> responder receives, so a test can
/// assert which command a button click posted. Mesh-scoped singleton (dies with the mesh — no static
/// state, no <c>Clear()</c>). A ReplaySubject so an assertion subscribing after the fact still sees it.
/// </summary>
public sealed class PandasCommandRecorder
{
    private readonly ReplaySubject<PandasCommand> commands = new();

    /// <summary>Every command the fake participant has received.</summary>
    public IObservable<PandasCommand> Commands => commands;

    /// <summary>Records a received command.</summary>
    public void Record(PandasCommand command) => commands.OnNext(command);
}

/// <summary>
/// Drives the interactive PandasExplorer frontend (<c>PandasExplorerLayoutAreas</c>) against an
/// in-process fake <c>py/pandas</c> participant that mirrors the Python node's contract:
/// <c>PandasCommand</c> in → <see cref="DataGridControl"/> out. Proves the area renders the grid FROM
/// the backend response, and that a button click posts the correct <c>PandasCommand</c> to <c>py/pandas</c>.
/// </summary>
public class PandasExplorerAreaTest : HubTestBase
{
    private const string Area = PandasExplorerLayoutAreas.ExplorerArea;

    public PandasExplorerAreaTest(ITestOutputHelper output) : base(output)
    {
        // Shared across the host (which drives the area) and the fake py/pandas responder hub.
        Services.AddSingleton<PandasCommandRecorder>();
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(PandasCommand))
            .AddLayout(layout => layout.AddPandasExplorerLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    // Route py/pandas to a hosted hub that stands in for the Python participant.
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .WithRoutes(routes => routes.RouteAddressToHostedHub("py", c => c
                .WithTypes(typeof(PandasCommand))
                .AddLayout(x => x)
                .WithHandler<PandasCommand>(RespondWithGrid)
                .WithPostingIdentity(PostingIdentity.System)));

    private static IMessageDelivery RespondWithGrid(IMessageHub hub, IMessageDelivery<PandasCommand> request)
    {
        hub.ServiceProvider.GetRequiredService<PandasCommandRecorder>().Record(request.Message);
        // Mirror the Python node: grid/analytical commands reply AS a DataGridControl; mutations reply
        // with an ack — here we always reply with a grid, which is enough to drive the frontend.
        hub.Post(SampleGrid(), o => o.ResponseFor(request));
        return request.Processed();
    }

    // The exact idiom from PandasDataGridWireTest — a real DataGridControl with typed, formatted columns.
    private static DataGridControl SampleGrid() =>
        Controls.DataGrid(new object[]
            {
                new { month = "Jan", region = "EMEA", sales = 120.0, units = 12L },
                new { month = "Feb", region = "EMEA", sales = 135.5, units = 14L },
                new { month = "Mar", region = "EMEA", sales = 128.0, units = 13L },
                new { month = "Apr", region = "APAC", sales = 98.0, units = 9L },
                new { month = "May", region = "APAC", sales = 143.0, units = 15L },
                new { month = "Jun", region = "APAC", sales = 150.0, units = 16L },
            })
            .WithColumn(new PropertyColumnControl<string> { Property = "month" }.WithTitle("Month"))
            .WithColumn(new PropertyColumnControl<string> { Property = "region" }.WithTitle("Region"))
            .WithColumn(new PropertyColumnControl<double> { Property = "sales" }.WithTitle("Sales").WithFormat("N2"))
            .WithColumn(new PropertyColumnControl<long> { Property = "units" }.WithTitle("Units").WithFormat("N0"));

    [HubFact]
    public async Task Area_renders_the_DataGrid_from_the_backend_response()
    {
        var reference = new LayoutAreaReference(Area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        // The grid the area shows is the DataGridControl the backend returned — 4 typed columns.
        var grid = await stream.GetControlStream($"{Area}/grid/table")
            .Where(c => c is DataGridControl { Columns.Count: 4 })
            .Select(c => (DataGridControl)c!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        Assert.Equal(4, grid.Columns.Count);

        // The initial render actually contacted the participant with a `render` command.
        var recorder = ServiceProvider.GetRequiredService<PandasCommandRecorder>();
        await recorder.Commands.Where(c => c.Command == "render")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();
    }

    [HubFact]
    public async Task Load_button_posts_load_with_the_content_file_path()
    {
        var reference = new LayoutAreaReference(Area);
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        await stream.GetControlStream($"{Area}/toolbar/load")
            .Where(c => c is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var recorder = ServiceProvider.GetRequiredService<PandasCommandRecorder>();
        client.Post(new ClickedEvent($"{Area}/toolbar/load", stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));

        // The click posts `load` carrying the PATH of the CSV file kept in content — never the data
        // itself; the participant reads the node over the mesh. Hosted without a node instance, the
        // area falls back to the default source path.
        var load = await recorder.Commands.Where(c => c.Command == "load")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();
        Assert.Equal(PandasExplorer.DefaultSourcePath, load.Path);
        Assert.Null(load.Data);
    }

    [HubFact]
    public async Task GroupBy_button_posts_a_groupby_PandasCommand_to_py_pandas()
    {
        var reference = new LayoutAreaReference(Area);
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        // Wait for the toolbar button to render before clicking it.
        await stream.GetControlStream($"{Area}/toolbar/groupby")
            .Where(c => c is not null).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var recorder = ServiceProvider.GetRequiredService<PandasCommandRecorder>();
        client.Post(new ClickedEvent($"{Area}/toolbar/groupby", stream.StreamId),
            o => o.WithTarget(CreateHostAddress()));

        // The click drove a real groupby command, carrying the seeded group-by column.
        var groupBy = await recorder.Commands.Where(c => c.Command == "groupby")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(20)).ToTask();
        Assert.Equal("region", groupBy.By);
        Assert.Equal("sum", groupBy.Agg);
    }
}

/// <summary>
/// The graceful no-participant path: with NO <c>py/pandas</c> routed (the default in prod / CI), the
/// area posts, the delivery fails / times out, and the grid degrades to an informative notice — it
/// never hangs and never surfaces a raw error.
/// </summary>
public class PandasExplorerNoParticipantTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = PandasExplorerLayoutAreas.ExplorerArea;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(PandasCommand))
            .AddLayout(layout => layout.AddPandasExplorerLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task No_participant_degrades_to_the_informative_notice()
    {
        var reference = new LayoutAreaReference(Area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);

        var notice = await stream.GetControlStream($"{Area}/grid/status")
            .Where(c => c is MarkdownControl m && (m.Markdown?.ToString() ?? "").Contains("No Python pandas node"))
            .Select(c => (MarkdownControl)c!)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        Assert.Contains("py/pandas", notice.Markdown?.ToString() ?? "");
    }
}
