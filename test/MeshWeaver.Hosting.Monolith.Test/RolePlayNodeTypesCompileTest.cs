using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Proves the RolePlay node types (Story / Scenery) — authored as a node repo in the
/// MeshWeaver.Plugins repo (RolePlay/{Type}.json + RolePlay/{Type}/Source/*.cs) — compile LIVE on the
/// mesh (Roslyn first-build → <see cref="CompilationStatus.Ok"/>). The Story view references
/// MeshWeaver.AI (StartThread / ThreadNodeType) and the platform IIconGenerator, so this also guards
/// that those references resolve in a runtime-compiled node type.
/// </summary>
public class RolePlayNodeTypesCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string CacheDirectory =
        Path.Combine(Path.GetTempPath(), "MeshWeaverRolePlayNodeTypeTests", ".mesh-cache");

    // Force the MeshWeaver.AI assembly into the load context so the runtime compile (which references
    // it from the Story Source) finds it in the trusted-platform-assemblies reference set.
    private static readonly Type AiAnchor = typeof(MeshWeaver.AI.ThreadNodeType);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(new MeshNode("RolePlay") { Name = "Role Play", NodeType = "Space", State = MeshNodeState.Active })
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = CacheDirectory;
                    o.EnableDiskCache = true;
                });
                return services;
            });

    [Theory(Timeout = 180_000)]
    [InlineData("Story")]
    [InlineData("Scenery")]
    public async Task RolePlayNodeType_CompilesLive(string type)
    {
        _ = AiAnchor; // ensure the anchor (and thus MeshWeaver.AI) is referenced
        var repo = LocateRolePlayRepo();
        var typePath = $"RolePlay/{type}";

        // The NodeType node — from the real RolePlay/{Type}.json in the plugins repo.
        var nodeJson = await File.ReadAllTextAsync(Path.Combine(repo, $"{type}.json"));
        var node = JsonSerializer.Deserialize<MeshNode>(nodeJson, Mesh.JsonSerializerOptions)!;
        await NodeFactory.CreateNode(node with { State = MeshNodeState.Active }).FirstAsync().ToTask();

        // Its Source/*.cs AND Test/*.cs children — created as Code nodes under {typePath}/Source and
        // {typePath}/Test, the subtrees the NodeType's default Sources+Tests queries compile TOGETHER
        // (so a broken in-node test fails the type's compile — which is what we want to catch).
        foreach (var sub in new[] { "Source", "Test" })
        {
            var dir = Path.Combine(repo, type, sub);
            if (!Directory.Exists(dir))
                continue;
            foreach (var cs in Directory.EnumerateFiles(dir, "*.cs"))
            {
                var id = Path.GetFileNameWithoutExtension(cs);
                await NodeFactory.CreateNode(new MeshNode(id, $"{typePath}/{sub}")
                {
                    Name = id,
                    NodeType = "Code",
                    State = MeshNodeState.Active,
                    Content = new CodeConfiguration { Code = await File.ReadAllTextAsync(cs), Language = "csharp" }
                }).FirstAsync().ToTask();
            }
        }

        var settled = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(150.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);

        var def = (NodeTypeDefinition)settled.Content!;
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"RolePlay/{type} must compile live; error: {def.CompilationError}");
    }

    private static string LocateRolePlayRepo([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "plugins", "RolePlay")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException($"plugins/RolePlay not found above {here}");
        return Path.Combine(dir.FullName, "plugins", "RolePlay");
    }
}
