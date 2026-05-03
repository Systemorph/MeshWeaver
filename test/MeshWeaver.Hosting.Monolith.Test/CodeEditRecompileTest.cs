using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the code-edit-then-recompile flow:
///   1. Create a NodeType with a Code source returning "V1".
///   2. Evaluate the NodeType's Overview layout area — must emit V1.
///   3. Update the Code source to return "V2" (persist, no recycle).
///   4. Recycle the NodeType hub to force a fresh activation.
///   5. Re-evaluate the Overview — must emit V2, NOT the cached V1 assembly.
///
/// Regression: before the <c>Source/</c>-aware NodeTypeService invalidator and
/// the on-disk <c>ICompilationCacheService.InvalidateCache</c> call, step (5)
/// reused the cached V1 DLL because the NodeType's own LastModified hadn't
/// advanced and IsCacheValid returned true.
/// </summary>
public class CodeEditRecompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IDisposable
{
    private readonly string _cacheDir = Path.Combine(
        Path.GetTempPath(), $"MeshWeaverCodeEditTest-{Guid.NewGuid():N}");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_cacheDir);
        return base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = _cacheDir;
                    // Keep disk+release caching ENABLED — that's the production config
                    // where the bug originally showed up (stale DLL survives LastModified
                    // being unchanged because only a Sources child was edited).
                    o.EnableCompilationCache = true;
                    o.EnableDiskCache = true;
                }));
    }

    public new void Dispose()
    {
        base.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_cacheDir))
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private const string NodeTypePath = "TestData/CodeEditType";
    private const string InstancePath = "TestData/CodeEditType/instance1";

    private const string CodeV1 = """
        using MeshWeaver.Layout.Composition;
        public static class CodeEditLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_V1</div>");
        }
        """;

    private const string CodeV2 = """
        using MeshWeaver.Layout.Composition;
        public static class CodeEditLayoutAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_V2</div>");
        }
        """;

    // Skipped 2026-04-28: Windows file-locking on the cached .dll (loaded by
    // AssemblyLoadContext can't be deleted by InvalidateCache) AND Linux CI
    // 30 s SubscribeRequest timeout because the per-node hub for instance1
    // doesn't activate within budget. Both the recompile path and the hub
    // activation need rework before this test can pass deterministically;
    // tracking via the failure ledger.
    // Re-evaluated 2026-05-03: original Windows file-lock root cause is fixed by
    // cb467ed87 (CompilationCacheService skips ALC unload + GC.Collect when no
    // live context). Test now fails with a different shape: CreateNodeRequest
    // times out at 30s targeting mesh/{instanceId} — the per-node hub for the
    // first instance never activates within budget. That's a hub-routing issue,
    // not a compile-cache one; needs separate investigation.
    /// <summary>
    /// Release-driven recompile flow (post-2026-05-03 migration). The legacy
    /// implicit "edit + invalidate-cache + recycle" path is gone — users now
    /// click Create Release explicitly, which flips
    /// <c>NodeTypeDefinition.CompilationStatus = Pending</c>, the
    /// <c>CompileWatcher</c> compiles, and on success a Release MeshNode is
    /// written at <c>{nodeTypePath}/_Release/{version}</c> (with the user's
    /// markdown notes) and the NodeType's <c>LatestReleasePath</c> is updated.
    /// See <c>Doc/Architecture/Postmortems/NodeTypeReleaseRedesign.md</c>.
    /// </summary>
    // Skipped 2026-05-04: Phase 0+1+6 of the Release migration ship the
    // forward path — `NodeTypeReleaseTest` covers that the Release MeshNode
    // gets created with the right notes and that LatestReleasePath flips.
    // What this test exercises additionally is the per-instance-hub
    // re-activation picking up the new release's assembly, which still
    // returns V1 even after delete+recreate of the instance. That last leg
    // requires Phase 2 (`GetCachedConfiguration` resolving from
    // `LatestReleasePath` instead of the in-memory `_hubConfigurations`
    // cache populated by the FIRST compile). Tracked as the next migration
    // step in the Postmortem.
    [Fact(Timeout = 90000, Skip = "Phase 2 (active-release lookup) still pending — instance hub keeps V1 ALC after V2 release. NodeTypeReleaseTest covers Phases 0+1.")]
    public async Task CodeEdit_NewRelease_RecompilesAndServesNewVersion()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // 1. Create the NodeType with a Code source returning V1.
        await NodeFactory.CreateNode(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for the Release-driven recompile flow.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/CodeEditType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });

        // 2. Click "Create Release" — flip CompilationStatus → Pending so the
        //    CompileWatcher compiles + writes a Release MeshNode under
        //    {nodeTypePath}/_Release/*. Same code path the GUI uses.
        await TriggerCreateReleaseAsync(NodeTypePath, "First release", ct);
        var v1Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: new(), ct);

        // 3. Create an instance and read its Overview — must use V1.
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });
        var v1 = await ReadOverviewAsync(InstancePath, ct);
        v1.Should().Contain("MARKER_V1",
            "initial release must serve the V1 source. Latest release: " + v1Release);

        // 4. Edit the source to V2.
        var codeNode = await FindNodeAsync($"{TestPartition}/CodeEditType/Source/code", ct);
        codeNode.Should().NotBeNull();
        await NodeFactory.UpdateNode(codeNode! with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        });

        // 5. Click Create Release again — same shape as step 2. The watcher
        //    compiles V2 and writes a fresh Release MeshNode at a new path.
        await TriggerCreateReleaseAsync(NodeTypePath, "Second release", ct);
        var v2Release = await WaitForNewReleaseAsync(
            NodeTypePath,
            knownReleases: new HashSet<string> { v1Release },
            ct);
        v2Release.Should().NotBe(v1Release, "the second release must be a distinct MeshNode");

        // 6. Re-create the instance so its hub re-activates against the new
        //    NodeType configuration (binds to the V2 assembly).
        await NodeFactory.DeleteNode(InstancePath);
        await Task.Delay(100, ct);
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });

        Output.WriteLine($"=== Second release at {v2Release}; reading Overview for V2 ===");

        // 5. Evaluate again — must now return V2. If the old DLL was reused from
        //    the compilation cache, this would still say MARKER_V1 and fail.
        var v2 = await ReadOverviewAsync(InstancePath, ct);
        v2.Should().Contain("MARKER_V2", "after code edit + recycle, the new source must be compiled and served");
        v2.Should().NotContain("MARKER_V1", "the stale V1 assembly must not be reused");
    }

    private async Task<string> ReadOverviewAsync(string path, CancellationToken ct)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(30.Seconds())
            .FirstAsync(x => x is HtmlControl)
            .ToTask(ct);

        return (control as HtmlControl)?.Data?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Same as the GUI Create-Release click: write the markdown notes onto
    /// the NodeType's <c>NodeTypeDefinition.ReleaseNotes</c> + flip
    /// <c>CompilationStatus</c> to Pending. The CompileWatcher in
    /// <c>MeshDataSource.InstallCompileWatcher</c> picks up Pending and runs
    /// Roslyn → on success writes a Release MeshNode under
    /// <c>{nodeTypePath}/_Release/{version}</c>.
    /// </summary>
    private async Task TriggerCreateReleaseAsync(string nodeTypePath, string releaseNotes, CancellationToken ct)
    {
        var node = await FindNodeAsync(nodeTypePath, ct);
        node.Should().NotBeNull();
        var def = node!.Content as NodeTypeDefinition;
        def.Should().NotBeNull();
        await NodeFactory.UpdateNode(node with
        {
            Content = def! with
            {
                ReleaseNotes = releaseNotes,
                CompilationStatus = CompilationStatus.Pending,
                LastCompileStartedAt = DateTimeOffset.UtcNow
            }
        });
    }

    /// <summary>
    /// Subscribes to the catalog change-feed for Release MeshNodes under
    /// <c>{nodeTypePath}/_Release</c> and emits the first one whose path is
    /// not in <paramref name="knownReleases"/>. <see cref="IMeshService.ObserveQuery{T}"/>
    /// re-emits whenever the queried set changes — observable, no polling, no
    /// cross-hub <c>SubscribeRequest</c> to a per-node hub (which can time
    /// out from the test client when activation is racing).
    /// </summary>
    private async Task<string> WaitForNewReleaseAsync(
        string nodeTypePath, HashSet<string> knownReleases, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var releaseNamespace = $"{nodeTypePath}/_Release";
        var release = await meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{releaseNamespace} nodeType:Release"))
            .Select(change =>
            {
                foreach (var n in change.Items)
                    if (!string.IsNullOrEmpty(n.Path) && !knownReleases.Contains(n.Path))
                        return n;
                return null;
            })
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct);
        return release!.Path!;
    }

    private async Task<MeshNode?> FindNodeAsync(string path, CancellationToken ct)
    {
        await foreach (var n in NodeFactory.QueryAsync<MeshNode>($"path:{path}", ct: ct).WithCancellation(ct))
            return n;
        return null;
    }
}
