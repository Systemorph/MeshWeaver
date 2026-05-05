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
/// End-to-end tests for the explicit compile pipeline:
///   CreateReleaseRequest → IsUpToDate check → CompileWatcher → Release node.
///
/// The automatic MeshChangeFeed → TryTriggerRecompile path has been removed.
/// All compilation is now triggered explicitly via CreateReleaseRequest.
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
    private const string Instance2Path = "TestData/CodeEditType/instance2";

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
    /// Explicit compile flow (the new canonical shape post-2026-05-05):
    ///   1. Create NodeType + Code source.
    ///   2. Send CreateReleaseRequest → should trigger compilation (not IsUpToDate).
    ///   3. Wait for V1 release node.
    ///   4. CreateReleaseRequest again → should return AlreadyUpToDate = true.
    ///   5. Modify source code to V2.
    ///   6. CreateReleaseRequest → should re-compile (sources changed).
    ///   7. Wait for V2 release node.
    ///   8. Create fresh instance → must serve V2 layout.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task CodeEdit_ExplicitRelease_IsUpToDate_RecompilesOnSourceChange()
    {
        var ct = new CancellationTokenSource(75.Seconds()).Token;

        // 1. Create the NodeType.
        await NodeFactory.CreateNode(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for the explicit compile flow.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. Create the V1 source.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/CodeEditType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });

        // 3. Explicitly trigger V1 compilation.
        var v1Response = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        v1Response.Success.Should().BeTrue("CreateReleaseRequest should succeed");
        v1Response.AlreadyUpToDate.Should().BeFalse("first compile has no CompiledSources yet");

        var v1Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: [], ct);
        Output.WriteLine($"=== V1 release at {v1Release} ===");

        // 4. CreateReleaseRequest again without changes → AlreadyUpToDate.
        var dupResponse = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        dupResponse.AlreadyUpToDate.Should().BeTrue("sources unchanged since V1 compile");

        // 5. Create an instance and verify it serves V1.
        await NodeFactory.CreateNode(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        });
        var v1Html = await ReadOverviewAsync(InstancePath, ct);
        v1Html.Should().Contain("MARKER_V1", "V1 release must be served");

        // 6. Modify the source to V2. Live remote stream — path is known, no
        // index lag (per CqrsAndContentAccess.md).
        var sourceClient = GetClient(c => c.AddData());
        var codeNode = await sourceClient.GetWorkspace()
            .GetMeshNodeStream($"{TestPartition}/CodeEditType/Source/code")
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);
        await NodeFactory.UpdateNode(codeNode with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        });

        // 7. Explicitly trigger V2 compilation — sources changed, should recompile.
        var v2Response = await SendCreateReleaseAsync(NodeTypePath, force: false, ct);
        v2Response.Success.Should().BeTrue("CreateReleaseRequest should succeed after source change");
        v2Response.AlreadyUpToDate.Should().BeFalse("source was modified, must recompile");

        var v2Release = await WaitForNewReleaseAsync(NodeTypePath, knownReleases: [v1Release], ct);
        v2Release.Should().NotBe(v1Release, "second release must be distinct");
        Output.WriteLine($"=== V2 release at {v2Release} ===");

        // 8. Create a fresh instance and verify it serves V2.
        await NodeFactory.CreateNode(new MeshNode("instance2", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 2",
            NodeType = NodeTypePath,
        });
        // Wait for the V2 marker explicitly: the per-instance hub may render a stale
        // (V1) snapshot first while the new release's HubConfiguration is still
        // propagating, then re-render once V2 is wired. Plain ReadOverviewAsync
        // takes the first HtmlControl emission and would race that pre-V2 tick.
        var v2Html = await ReadOverviewMatchingAsync(Instance2Path,
            html => html.Contains("MARKER_V2"), ct);
        v2Html.Should().Contain("MARKER_V2", "V2 release must be served after recompile");
        v2Html.Should().NotContain("MARKER_V1", "stale V1 assembly must not be reused");
    }

    /// <summary>
    /// Pin to a historical release via <see cref="NodeTypeDefinition.RequestedReleasePath"/>:
    ///   1. Compile V1, capture V1 release path.
    ///   2. Modify source to V2, compile V2 (V2 is now the latest release).
    ///   3. Pin <c>RequestedReleasePath</c> to V1 on the NodeType.
    ///   4. Create a fresh instance — must serve V1 (the pinned release), not V2 (latest).
    ///   5. Clear the pin → fresh instance must serve V2 again.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task NodeType_RequestedReleasePath_PinsToHistoricalRelease()
    {
        var ct = new CancellationTokenSource(75.Seconds()).Token;

        // 1. Create the NodeType.
        await NodeFactory.CreateNode(new MeshNode("PinType", TestPartition)
        {
            Name = "Pin Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for RequestedReleasePath pinning.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        });

        // 2. V1 source + compile.
        await NodeFactory.CreateNode(new MeshNode("code", $"{TestPartition}/PinType/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        });
        var pinTypePath = $"{TestPartition}/PinType";
        var v1Resp = await SendCreateReleaseAsync(pinTypePath, force: false, ct);
        v1Resp.Success.Should().BeTrue();
        var v1Release = await WaitForNewReleaseAsync(pinTypePath, knownReleases: [], ct);
        Output.WriteLine($"=== Pinned-test V1 release at {v1Release} ===");

        // 3. Modify to V2 + compile (V2 becomes latest).
        var codeNode = await FindNodeAsync($"{TestPartition}/PinType/Source/code", ct);
        codeNode.Should().NotBeNull();
        await NodeFactory.UpdateNode(codeNode! with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        });
        var v2Resp = await SendCreateReleaseAsync(pinTypePath, force: false, ct);
        v2Resp.Success.Should().BeTrue();
        v2Resp.AlreadyUpToDate.Should().BeFalse();
        var v2Release = await WaitForNewReleaseAsync(pinTypePath, knownReleases: [v1Release], ct);
        v2Release.Should().NotBe(v1Release);
        Output.WriteLine($"=== Pinned-test V2 release at {v2Release} ===");

        // 4. Pin to V1 release on the NodeTypeDefinition.
        var nodeTypeNode = await FindNodeAsync(pinTypePath, ct);
        nodeTypeNode.Should().NotBeNull();
        var def = nodeTypeNode!.Content as NodeTypeDefinition;
        def.Should().NotBeNull();
        await NodeFactory.UpdateNode(nodeTypeNode with
        {
            Content = def! with { RequestedReleasePath = v1Release }
        });

        // Wait for the pin write to propagate before creating the instance —
        // otherwise the per-instance hub's GetCompilationPathRequest can read
        // a stale def with no pin set. Live remote stream (path is known) —
        // not ObserveQuery, which is index-lagged.
        var reader = GetClient(c => c.AddData());
        await reader.GetWorkspace().GetMeshNodeStream(pinTypePath)
            .Where(n => n?.Content is NodeTypeDefinition d && d.RequestedReleasePath == v1Release)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

        // 5. Fresh instance — pinned path means V1 must be served even though V2 is latest.
        await NodeFactory.CreateNode(new MeshNode("pinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Pinned Instance",
            NodeType = pinTypePath,
        });
        var pinnedHtml = await ReadOverviewMatchingAsync(
            $"{TestPartition}/PinType/pinnedInstance",
            html => html.Contains("MARKER_V1") || html.Contains("MARKER_V2"),
            ct);
        pinnedHtml.Should().Contain("MARKER_V1",
            "RequestedReleasePath pins to V1 — instance must serve V1 even though V2 is latest");
        pinnedHtml.Should().NotContain("MARKER_V2",
            "pinned release V1 must not leak V2's body");

        // 6. Clear the pin → fresh instance serves V2 (latest) again.
        nodeTypeNode = await FindNodeAsync(pinTypePath, ct);
        def = nodeTypeNode!.Content as NodeTypeDefinition;
        await NodeFactory.UpdateNode(nodeTypeNode with
        {
            Content = def! with { RequestedReleasePath = null }
        });
        await reader.GetWorkspace().GetMeshNodeStream(pinTypePath)
            .Where(n => n?.Content is NodeTypeDefinition d && d.RequestedReleasePath == null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

        await NodeFactory.CreateNode(new MeshNode("unpinnedInstance", $"{TestPartition}/PinType")
        {
            Name = "Unpinned Instance",
            NodeType = pinTypePath,
        });
        var unpinnedHtml = await ReadOverviewMatchingAsync(
            $"{TestPartition}/PinType/unpinnedInstance",
            html => html.Contains("MARKER_V1") || html.Contains("MARKER_V2"),
            ct);
        unpinnedHtml.Should().Contain("MARKER_V2",
            "after clearing RequestedReleasePath, fresh instance must serve the latest release (V2)");
    }

    private async Task<CreateReleaseResponse> SendCreateReleaseAsync(
        string nodeTypePath, bool force, CancellationToken ct)
    {
        var reader = GetClient(c => c.AddData());
        var response = await reader
            .Observe(new CreateReleaseRequest(Force: force), o => o.WithTarget(new Address(nodeTypePath)))
            .Select(d => d.Message)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);
        // Wait for compile to complete (status = Ok or Error) before returning.
        // Live remote stream (GetMeshNodeStream(path)) — NOT ObserveQuery, which
        // is index-lagged and can miss the post-compile tick (per the CQRS
        // feedback note + Doc/Architecture/CqrsAndContentAccess.md). Path is
        // known here, so the live stream is the right primitive.
        if (!response.AlreadyUpToDate && response.Success)
        {
            await reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
                .Where(n => n?.Content is NodeTypeDefinition def
                    && (def.CompilationStatus == CompilationStatus.Ok
                        || def.CompilationStatus == CompilationStatus.Error))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(45))
                .ToTask(ct);
        }
        return response;
    }

    private Task<string> ReadOverviewAsync(string path, CancellationToken ct)
        => ReadOverviewMatchingAsync(path, _ => true, ct);

    /// <summary>
    /// Reads the Overview area and waits for an <see cref="HtmlControl"/> whose
    /// data matches <paramref name="matches"/>. Used by V2 reads where the per-node
    /// hub may emit a stale (V1) snapshot first while the new release's
    /// HubConfiguration is still propagating — taking <c>FirstAsync(x is HtmlControl)</c>
    /// would race the first stale emission and fail the assertion before the
    /// V2-bound re-render lands.
    /// </summary>
    private async Task<string> ReadOverviewMatchingAsync(string path, Func<string, bool> matches, CancellationToken ct)
    {
        var client = GetClient(c => c.AddData());
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(30.Seconds())
            .FirstAsync(x => x is HtmlControl h && matches(h.Data?.ToString() ?? string.Empty))
            .ToTask(ct);

        return (control as HtmlControl)?.Data?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Waits for a fresh <c>_Release</c> MeshNode whose path differs from any in
    /// <paramref name="knownReleases"/>. Reads <see cref="NodeTypeDefinition.LatestReleasePath"/>
    /// off the live <see cref="GetMeshNodeStream"/> — atomic with the post-compile
    /// status flip, so by the time CompilationStatus settles to Ok the new path
    /// is already on the NodeType. Avoids the lagged <c>ObserveQuery</c> namespace
    /// scan over <c>_Release/*</c>.
    /// </summary>
    private async Task<string> WaitForNewReleaseAsync(
        string nodeTypePath, HashSet<string> knownReleases, CancellationToken ct)
    {
        var reader = GetClient(c => c.AddData());
        var node = await reader.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Where(n => n?.Content is NodeTypeDefinition def
                && !string.IsNullOrEmpty(def.LatestReleasePath)
                && !knownReleases.Contains(def.LatestReleasePath!))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(50))
            .ToTask(ct);
        return ((NodeTypeDefinition)node.Content!).LatestReleasePath!;
    }

    /// <summary>
    /// Live, path-known read of a single MeshNode. Replaces the lagged
    /// <c>NodeFactory.QueryAsync($"path:{path}")</c> pattern: queries are
    /// eventually consistent and routinely miss the just-written value (per
    /// CqrsAndContentAccess.md). Path is known here, so the live remote stream
    /// is the right primitive.
    /// </summary>
    private async Task<MeshNode?> FindNodeAsync(string path, CancellationToken ct)
    {
        var reader = GetClient(c => c.AddData());
        return await reader.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);
    }
}
