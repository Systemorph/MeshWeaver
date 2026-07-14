using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Render-safety test for the Python pandas sample's C# frontend — the live-compiled
/// <c>Doc/DataMesh/PythonPandasNode/PandasExplorer</c> NodeType and its <c>Explorer</c> layout area
/// (<c>PandasExplorerLayoutAreas</c>). The area drives a Python <c>py/pandas</c> participant; in prod
/// and CI NO such participant is attached, and the contract is that the grid then DEGRADES to a
/// "No Python pandas node attached" notice <em>within the backend timeout</em> — it must never hang
/// and never show a raw error.
///
/// <para>This exercises the REAL embedded Source: <c>AddDocumentation()</c> registers the whole
/// <c>Doc</c> partition (the Pandas NodeType, its two <c>Source/*.cs</c> files, and the
/// <c>LiveFrame</c> instance ship as embedded resources under <c>MeshWeaver.Documentation.Data</c>),
/// so the mesh compiles the actual production frontend with Roslyn and renders the actual instance —
/// mirroring <see cref="CessionLayoutAreaTest"/>. No compiled test double.</para>
///
/// <para>Every assertion runs under a hard wall-clock budget: if the grid sub-area hung instead of
/// degrading, the <c>Within(...)</c> bound trips and the test FAILS loudly rather than blocking CI —
/// which is the exact production hazard being guarded.</para>
/// </summary>
public class PandasExplorerLayoutAreaTest : MonolithMeshTestBase
{
    /// <summary>The live explorer instance shipped in the Doc partition (see LiveFrame.json).</summary>
    private const string LiveFramePath = "Doc/DataMesh/PythonPandasNode/PandasExplorer/LiveFrame";

    /// <summary>The single interactive area the NodeType exposes (its default area).</summary>
    private const string ExplorerArea = "Explorer";

    /// <summary>
    /// Wall-clock budget for the whole render. The area's own backend timeout is 8s, so 50s is
    /// generous head-room for the cold Roslyn compile of the NodeType Source on top of the degrade —
    /// yet still a HARD ceiling, so a genuine hang (the regression this guards) fails the test.
    /// </summary>
    private static readonly TimeSpan RenderBudget = TimeSpan.FromSeconds(50);

    private readonly string _cacheDirectory;

    public PandasExplorerLayoutAreaTest(ITestOutputHelper output) : base(output)
    {
        // Per-test-class cache dir — a stale, prior-process DLL in the shared bin/.mesh-cache can lock
        // on Windows and wedge the compile (same rationale as CessionLayoutAreaTest).
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverPandasExplorerTests");
        Directory.CreateDirectory(_cacheDirectory);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            // AddDocumentation registers the embedded-resource "Doc" partition (which holds the Pandas
            // NodeType + Source + LiveFrame instance); partition-routing persistence is required for
            // that provider to actually serve reads.
            .AddPartitionedInMemoryPersistence()
            .AddDocumentation()
            .ConfigureServices(services =>
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = _cacheDirectory))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            .AddGraph()
            .AddMeshNodes(TestUsers.PublicAdminAccess());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_cacheDirectory))
            try { Directory.Delete(_cacheDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact(Timeout = 120000)]
    public async Task Explorer_NoPythonNode_RendersToolbarAndDegradesGridWithoutHanging()
    {
        var client = GetClient(c => c.AddData(data => data));
        var address = new Address(LiveFramePath);

        // No ping: subscribing to the layout area activates the per-node hub AND triggers the cold
        // Roslyn compile of the Pandas NodeType Source. The budget below covers that.
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                address, new LayoutAreaReference(ExplorerArea));

        // 1. The Explorer area renders a Stack composing title + intro + toolbar + grid sub-areas.
        var root = await stream.GetControlStream(ExplorerArea)
            .Where(c => c is not null)
            .Should().Within(RenderBudget).Match(c => c is StackControl);

        Output.WriteLine($"Explorer root control: {root!.GetType().Name}");
        var stack = root.Should().BeOfType<StackControl>().Subject;

        var areaIds = stack.Areas.Select(a => a.Id?.ToString()).ToArray();
        Output.WriteLine($"Explorer sub-areas: {string.Join(", ", areaIds)}");
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(4,
            "Explorer composes title + intro + toolbar + grid");
        areaIds.Should().Contain("toolbar", "the interactive toolbar is a named sub-area");
        areaIds.Should().Contain("grid", "the reactive grid is a named sub-area");

        // 2. The toolbar resolves to a horizontal Stack whose named sub-areas are the real buttons —
        //    the point of the sample is genuine framework controls, not an HTML string.
        var toolbar = await stream.GetControlStream($"{ExplorerArea}/toolbar")
            .Where(c => c is not null)
            .Should().Within(RenderBudget).Match(c => c is StackControl);

        var toolbarStack = toolbar!.Should().BeOfType<StackControl>().Subject;
        var buttonAreas = toolbarStack.Areas.Select(a => a.Id?.ToString()).ToArray();
        Output.WriteLine($"Toolbar sub-areas: {string.Join(", ", buttonAreas)}");
        foreach (var expected in new[] { "load", "append", "groupby", "rolling", "describe", "refresh", "reset" })
            buttonAreas.Should().Contain(expected, $"the toolbar exposes the '{expected}' button");

        // Each named button area resolves to a real ButtonControl carrying its label.
        var loadButton = await stream.GetControlStream($"{ExplorerArea}/toolbar/load")
            .Where(c => c is not null)
            .Should().Within(RenderBudget).Match(c => c is ButtonControl);
        loadButton.Should().BeOfType<ButtonControl>()
            .Which.Data!.ToString().Should().Contain("Load", "the primary action loads the sales CSV");

        // 3. THE POINT: with no py/pandas participant attached, the grid MUST degrade to the
        //    "No Python pandas node attached" notice WITHIN the budget — not hang, not raw error.
        //    The grid area resolves to a NoNode() stack whose 'status' markdown carries the notice.
        var gridStatus = await stream.GetControlStream($"{ExplorerArea}/grid/status")
            .Where(c => c is MarkdownControl)
            .Should().Within(RenderBudget).Match(c =>
                ((MarkdownControl)c!).Markdown?.ToString()?
                    .Contains("No Python pandas node attached", StringComparison.Ordinal) == true);

        var notice = gridStatus.Should().BeOfType<MarkdownControl>().Subject;
        Output.WriteLine($"Grid degraded to: {notice.Markdown}");
        notice.Markdown!.ToString().Should().Contain("No Python pandas node attached",
            "with no participant the grid degrades to the informative no-node notice — it must never hang");
    }
}
