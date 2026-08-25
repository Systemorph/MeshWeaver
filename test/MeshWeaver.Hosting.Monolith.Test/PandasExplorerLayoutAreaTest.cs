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
///
/// <para>🚨 <b>Every wait goes through <see cref="Rendered"/>, which FOLLOWS the redirect</b> — see
/// its remarks. A wait that merely sat on one subscription was #2155.</para>
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

    /// <summary>
    /// How many overlay redirects one wait will follow before giving up. One is the expected
    /// maximum (the overlay heals once per compile); the extra hops cover a sweep in which the
    /// type is recompiled again while we are re-subscribing. Bounded on purpose — an instance
    /// stuck in an overlay↔heal loop must fail this test, not spin in it.
    /// </summary>
    private const int MaxRedirectHops = 3;

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
        // No ping: subscribing to the layout area activates the per-node hub AND triggers the cold
        // Roslyn compile of the Pandas NodeType Source. The budget below covers that.

        // 1. The Explorer area renders a Stack composing title + intro + toolbar + grid sub-areas.
        //    The composition is INCREMENTAL: the root Stack can emit with only the first children
        //    attached (title + intro + toolbar) and the grid landing a frame later — a loaded CI
        //    runner sampled exactly that 3-child intermediate frame (PR #1970's run). So the wait
        //    matches the COMPLETE composition, never the first Stack-shaped emission; the Within
        //    budget still guards the hang this test exists for.
        var root = await Rendered(ExplorerArea)
            .Should().Within(RenderBudget).Match(c => c is StackControl s && s.Areas.Count >= 4);

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
        var toolbar = await Rendered($"{ExplorerArea}/toolbar")
            .Should().Within(RenderBudget).Match(c => c is StackControl);

        var toolbarStack = toolbar!.Should().BeOfType<StackControl>().Subject;
        var buttonAreas = toolbarStack.Areas.Select(a => a.Id?.ToString()).ToArray();
        Output.WriteLine($"Toolbar sub-areas: {string.Join(", ", buttonAreas)}");
        foreach (var expected in new[] { "load", "append", "groupby", "rolling", "describe", "refresh", "reset" })
            buttonAreas.Should().Contain(expected, $"the toolbar exposes the '{expected}' button");

        // Each named button area resolves to a real ButtonControl carrying its label.
        var loadButton = await Rendered($"{ExplorerArea}/toolbar/load")
            .Should().Within(RenderBudget).Match(c => c is ButtonControl);
        loadButton.Should().BeOfType<ButtonControl>()
            .Which.Data!.ToString().Should().Contain("Load", "the primary action loads the sales CSV");

        // 3. THE POINT: with no py/pandas participant attached, the grid MUST degrade to the
        //    "No Python pandas node attached" notice WITHIN the budget — not hang, not raw error.
        //    The grid area resolves to a NoNode() stack whose 'status' markdown carries the notice.
        var gridStatus = await Rendered($"{ExplorerArea}/grid/status")
            .Where(c => c is MarkdownControl)
            .Should().Within(RenderBudget).Match(c =>
                ((MarkdownControl)c!).Markdown?.ToString()?
                    .Contains("No Python pandas node attached", StringComparison.Ordinal) == true);

        var notice = gridStatus.Should().BeOfType<MarkdownControl>().Subject;
        Output.WriteLine($"Grid degraded to: {notice.Markdown}");
        notice.Markdown!.ToString().Should().Contain("No Python pandas node attached",
            "with no participant the grid degrades to the informative no-node notice — it must never hang");
    }

    /// <summary>
    /// The Explorer area's control stream for <paramref name="areaPath"/>, <b>following the
    /// compilation-in-progress overlay's redirect the way a GUI client does</b>.
    ///
    /// <para>🚨 <b>Why a plain subscription is not enough (#2155).</b> When the Pandas NodeType's
    /// cold Roslyn compile outlives <c>NodeTypeEnrichmentHelpers.InFlightOverlayGrace</c> (5 s — a
    /// loaded CI runner routinely does), the LiveFrame instance activates against the
    /// compilation-in-progress overlay, which serves <c>NodeTypeLayoutAreas.CompileProgressView</c>
    /// on EVERY area. When the build lands <c>Ok</c> that view emits a
    /// <see cref="RedirectControl"/> — and the SAME event recycles the instance hub
    /// (<c>WithOverlaySelfHeal</c> posts a <c>DisposeRequest</c> to it) so the next access
    /// re-enriches against the now-usable type. A subscriber that just keeps waiting is therefore
    /// attached to a hub that has gone away: the redirect is the LAST thing it ever sees. That is
    /// exactly what #2155 recorded — "Last of 3 emission(s) was: RedirectControl … Href =
    /// /Doc/DataMesh/PythonPandasNode/PandasExplorer/LiveFrame/Explorer" after 50 s.</para>
    ///
    /// <para>Following it is what a real client does — the Blazor <c>DispatchView</c> answers a
    /// <see cref="RedirectControl"/> with <c>NavigationManager.NavigateTo(href)</c>, i.e. a fresh
    /// area subscription. A fresh <see cref="HubTestBase.GetClient"/> is this test's navigation: it
    /// is a brand-new client hub, so its workspace opens a brand-new remote stream rather than
    /// handing back the orphaned one out of <c>Workspace._remoteStreamCache</c> (which is keyed on
    /// owner + reference + identity, and would otherwise return the same dead stream).</para>
    ///
    /// <para>No timer, no retry, no widened bound: the redirect IS the event, and each hop is
    /// driven by receiving one. <see cref="MaxRedirectHops"/> bounds it so an instance stuck in an
    /// overlay↔heal loop still fails inside <see cref="RenderBudget"/>.</para>
    /// </summary>
    /// <param name="areaPath">Area (or sub-area) path to observe, e.g. <c>Explorer/toolbar</c>.</param>
    /// <param name="client">Client hub to subscribe from; a fresh one is created per hop.</param>
    /// <param name="hopsLeft">Remaining redirects this wait may follow.</param>
    private IObservable<UiControl?> Rendered(
        string areaPath, IMessageHub? client = null, int hopsLeft = MaxRedirectHops) =>
        Observable.Defer(() =>
                (client ?? GetClient(c => c.AddData(data => data)))
                .GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    new Address(LiveFramePath), new LayoutAreaReference(ExplorerArea))
                .GetControlStream(areaPath))
            .Where(c => c is not null)
            .SelectMany(c => c is not RedirectControl
                ? Observable.Return(c)
                : hopsLeft > 0
                    // Navigate: a fresh client ⇒ a fresh subscription ⇒ a fresh activation of the
                    // instance hub, which re-enriches against the now-compiled NodeType.
                    ? Observable.Defer(() =>
                    {
                        Output.WriteLine(
                            $"[{areaPath}] compile-progress overlay redirected to "
                            + $"{(c as RedirectControl)!.Href} — re-subscribing "
                            + $"({hopsLeft - 1} hop(s) left)");
                        return Rendered(areaPath, client: null, hopsLeft - 1);
                    })
                    : Observable.Empty<UiControl?>());
}
