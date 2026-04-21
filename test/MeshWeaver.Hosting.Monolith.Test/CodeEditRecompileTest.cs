using System;
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
/// Regression: before the <c>_Source/</c>-aware NodeTypeService invalidator and
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
                    // being unchanged because only a _Source child was edited).
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

    [Fact(Timeout = 60000)]
    public async Task CodeEdit_AfterRecycle_RecompilesAndServesNewVersion()
    {
        var ct = new CancellationTokenSource(45.Seconds()).Token;

        // 1. Create the NodeType with a Code source returning V1.
        await NodeFactory.CreateNodeAsync(new MeshNode("CodeEditType", TestPartition)
        {
            Name = "Code Edit Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Regression test for edit-then-recycle recompile.",
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", CodeEditLayoutAreas.Overview))",
                ShowChildrenInDetails = false,
            }
        }, ct);

        await NodeFactory.CreateNodeAsync(new MeshNode("code", $"{TestPartition}/CodeEditType/_Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = CodeV1, Language = "csharp" }
        }, ct);

        // 2. Create an instance and evaluate its Overview area.
        await NodeFactory.CreateNodeAsync(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        }, ct);

        var v1 = await ReadOverviewAsync(InstancePath, ct);
        v1.Should().Contain("MARKER_V1", "initial compile must use the V1 source");

        // 3. Update the Code source with V2 (same path, new body).
        MeshNode? codeNode = null;
        await foreach (var n in NodeFactory.QueryAsync<MeshNode>(
            $"path:{TestPartition}/CodeEditType/_Source/code", ct: ct).WithCancellation(ct))
        {
            codeNode = n;
            break;
        }
        codeNode.Should().NotBeNull();
        await NodeFactory.UpdateNodeAsync(codeNode! with
        {
            Content = new CodeConfiguration { Code = CodeV2, Language = "csharp" }
        }, ct);

        // Sanity check: persistence must observe V2 before we invalidate + reread.
        // Poll because InMemoryPersistence can propagate async.
        var persistedOk = false;
        for (var i = 0; i < 50; i++)
        {
            MeshNode? probe = null;
            await foreach (var n in NodeFactory.QueryAsync<MeshNode>(
                $"path:{TestPartition}/CodeEditType/_Source/code", ct: ct).WithCancellation(ct))
            {
                probe = n;
                break;
            }
            var cf = probe?.Content as CodeConfiguration;
            var jsonCf = probe?.Content switch
            {
                CodeConfiguration c => c.Code,
                JsonElement je when je.TryGetProperty("code", out var cProp) => cProp.GetString(),
                _ => null
            };
            if (jsonCf != null && jsonCf.Contains("MARKER_V2"))
            {
                persistedOk = true;
                break;
            }
            await Task.Delay(50, ct);
        }
        persistedOk.Should().BeTrue("persistence must return V2 content before we force a recompile");

        // 4. Trigger the full production recycle path: invalidate NodeTypeService
        //    caches AND dispose the instance grain. The new NodeTypeService
        //    MeshChangeFeed subscriber also fires automatically when the _Source
        //    child was updated above, but call InvalidateCache explicitly so the
        //    test is deterministic.
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        nodeTypeService.InvalidateCache(NodeTypePath);

        // Delete + recreate the instance so the next read goes through the full
        // activation path (no chance the stale hub lingers in the routing cache).
        await NodeFactory.DeleteNodeAsync(InstancePath, ct);
        await Task.Delay(100, ct);
        await NodeFactory.CreateNodeAsync(new MeshNode("instance1", $"{TestPartition}/CodeEditType")
        {
            Name = "Instance 1",
            NodeType = NodeTypePath,
        }, ct);

        Output.WriteLine("=== After invalidation + delete+recreate, reading Overview for V2 ===");

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

        return ((HtmlControl)control).Data?.ToString() ?? string.Empty;
    }
}
