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

    /// <summary>
    /// Transparent-recompile flow (the canonical UI shape post-2026-05-04):
    /// users edit code in the editor, hit Save, and the new release ships
    /// automatically — no explicit "Create Release" click needed.
    ///
    /// <para>The plumbing: every <c>UpdateNode</c> on a MeshNode under
    /// <c>{NodeTypePath}/Source/...</c> fires an <c>IMeshChangeFeed</c> event
    /// that <c>NodeTypeService</c> picks up; the handler calls
    /// <c>TryTriggerRecompile(owning)</c>, which flips
    /// <c>NodeTypeDefinition.CompilationStatus = Pending</c> on the owning
    /// NodeType. The CompileWatcher (in <c>MeshDataSource.InstallCompileWatcher</c>)
    /// reacts to Pending, runs Roslyn, and on success writes a fresh Release
    /// MeshNode under <c>{nodeTypePath}/_Release/{version}</c> + flips
    /// <c>LatestReleasePath</c>. The same plumbing services the FIRST compile
    /// (when the source node is created) — no manual trigger needed for the
    /// initial release either.</para>
    ///
    /// <para>UI parity: this is exactly what happens when a user types in the
    /// Code editor and clicks Save — the Save handler posts a
    /// <c>DataChangeRequest</c> on the source MeshNode, which fires the same
    /// MeshChangeFeed event. The "Create Release" button (still wired in
    /// <c>NodeTypeLayoutAreas.cs</c>) is now redundant for normal edits;
    /// it remains as an explicit re-release trigger.</para>
    /// </summary>
    // 2026-05-04: Transparent recompile is desirable but the source-edit →
    // CompilationStatus = Pending trigger needs a one-shot fire-and-forget
    // primitive that doesn't leak a SubscribeRequest. workspace.UpdateMeshNode
    // for a remote node opens a long-standing GetRemoteStream subscription
    // that the change-feed handler can't tear down — every test that mutates
    // a node under {NodeTypePath}/Source/... left a pending callback at
    // dispose time. Tracked as task #47 (build-then-activate routing); when
    // that ships, the recompile trigger can post a true DataChangeRequest
    // and we can un-skip this test.
    [Fact(Timeout = 90000, Skip = "Pending transparent-recompile primitive (task #47) — UpdateMeshNode on remote leaks SubscribeRequest")]
    public async Task CodeEdit_NewRelease_RecompilesAndServesNewVersion()
    {
        var ct = new CancellationTokenSource(60.Seconds()).Token;

        // 1. Create the NodeType.
        await NodeFactory.CreateNode(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for the transparent-recompile flow.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. Create the V1 source. The MeshChangeFeed Created event triggers
        //    the transparent recompile path → CompilationStatus = Pending →
        //    CompileWatcher → V1 release. NO TriggerCreateReleaseAsync.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/CodeEditType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });

        var v1Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: new(), ct);
        Output.WriteLine($"=== V1 release transparently created at {v1Release} ===");

        // 3. Create an instance and read its Overview — must use V1.
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });
        var v1 = await ReadOverviewAsync(InstancePath, ct);
        v1.Should().Contain("MARKER_V1",
            "initial release must serve the V1 source. Latest release: " + v1Release);

        // 4. Edit the source to V2 — same UpdateNode call the editor's Save
        //    button posts. The MeshChangeFeed Updated event flips Pending,
        //    CompileWatcher recompiles, V2 release ships automatically.
        var codeNode = await FindNodeAsync($"{TestPartition}/CodeEditType/Source/code", ct);
        codeNode.Should().NotBeNull();
        await NodeFactory.UpdateNode(codeNode! with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        });

        var v2Release = await WaitForNewReleaseAsync(
            NodeTypePath,
            knownReleases: new HashSet<string> { v1Release },
            ct);
        v2Release.Should().NotBe(v1Release, "the second release must be a distinct MeshNode");
        Output.WriteLine($"=== V2 release transparently created at {v2Release} ===");

        // 5. Re-create the instance so its hub re-activates against the new
        //    release's ALC. UI parity: in the GUI the user navigates away and
        //    back (or refreshes) — same effect as delete+recreate at the data
        //    layer.
        await NodeFactory.DeleteNode(InstancePath);
        await Task.Delay(100, ct);
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });

        // 6. Evaluate again — must now return V2.
        var v2 = await ReadOverviewAsync(InstancePath, ct);
        v2.Should().Contain("MARKER_V2", "after transparent recompile + instance re-activation, the new source must be served");
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
