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
/// Proves the migrated Edu node types (Course / Module / Exercise) — authored as node repos in the
/// MeshWeaver.Plugins repo (Edu/{Type}.json + Edu/{Type}/Source/*.cs) — compile LIVE on the mesh
/// (Roslyn first-build → <see cref="CompilationStatus.Ok"/>). They install the same way a GitSync
/// import would: create the NodeType node + its Source Code children; the mesh compiles them. No app
/// rebuild, no NuGet — the education types as self-compiling dynamic node types.
/// </summary>
public class EduNodeTypesCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string CacheDirectory =
        Path.Combine(Path.GetTempPath(), "MeshWeaverEduNodeTypeTests", ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSpaceType()
            .AddMeshNodes(new MeshNode("Edu") { Name = "Education", NodeType = "Space", State = MeshNodeState.Active })
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
    [InlineData("Course")]
    [InlineData("Module")]
    [InlineData("Exercise")]
    public async Task EduNodeType_CompilesLive(string type)
    {
        var repo = LocateEduRepo();
        var typePath = $"Edu/{type}";

        // The NodeType node — from the real Edu/{Type}.json in the plugins repo.
        var nodeJson = await File.ReadAllTextAsync(Path.Combine(repo, $"{type}.json"));
        var node = JsonSerializer.Deserialize<MeshNode>(nodeJson, Mesh.JsonSerializerOptions)!;
        await NodeFactory.CreateNode(node with { State = MeshNodeState.Active }).FirstAsync().ToTask();

        // Its Source/*.cs AND Test/*.cs children — created as Code nodes under {typePath}/Source and
        // {typePath}/Test, the subtrees the NodeType's default Sources+Tests queries compile TOGETHER
        // (so a broken in-node test would fail the type's compile — which is what we want to catch).
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
            $"Edu/{type} must compile live; error: {def.CompilationError}");
    }

    private static string LocateEduRepo([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "plugins", "Edu")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException($"plugins/Edu not found above {here}");
        return Path.Combine(dir.FullName, "plugins", "Edu");
    }
}
