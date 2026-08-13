using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
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
/// 🚨 A CACHED SYNCED QUERY MUST ANSWER THE QUERIES IT WAS ASKED — issue #1311.
///
/// <para><b>The defect.</b> <c>workspace.GetQuery(id, queries)</c> is a get-or-create cache keyed
/// on <c>id</c> ALONE: on a hit it returns the existing entry and DISCARDS the <c>queries</c>
/// argument entirely. Every caller therefore has to guarantee that its id fully determines its
/// query set — and the compiler's does not. <see cref="NodeSources.CacheId"/> is
/// <c>nodetype-sources:{nodeTypePath}</c>, while the queries come from
/// <see cref="NodeTypeDefinition.Sources"/>/<see cref="NodeTypeDefinition.Tests"/>, which an
/// author edits at will.</para>
///
/// <para><b>What that costs.</b> Adding <c>shared=@Other/Type/Source</c> to a NodeType on a
/// RUNNING portal cannot take effect: the entry materialised before the edit keeps serving the
/// OLD query set, so the shared files are never in the source snapshot. The snapshot is
/// <see cref="SourceSnapshot.IsEstablished"/> and NON-EMPTY (the type's own sources are all
/// there), so it wins <c>RaceSourceSnapshot</c> instantly — measured at <c>0ms</c> on memex —
/// and the correct, wider answer from <c>DirectSourceProbe</c> (which recomputes its queries
/// every call and needs a ~1s quiet window) never gets a look-in. Roslyn is handed a SHORT set
/// and emits a completely genuine-looking <c>CS0103 — LessonLayoutAreas does not exist</c> about
/// code that is fine, and the NodeType parks.</para>
///
/// <para><b>Why every gate was green.</b> The failure needs a cache entry that PREDATES the
/// sources edit. A fresh mesh (the ACR gate), a repo-side compile-check, or any process whose
/// first materialisation already saw the new declaration all resolve it correctly — which is
/// also why <c>Chess/GambitHunt</c> read Ok on the same mesh where <c>Edu/Module</c> parked.
/// The defect is invisible to anything that does not edit sources on a warm process.</para>
///
/// <para><b>This is not the same bug as #1218.</b> There the snapshot was short because a leg
/// resolved as <c>Anonymous</c>; the fix made the probe read as System. Here the set is
/// established AND authorised AND still too narrow, because the queries that produced it are
/// stale. Same lethal shape — a partial source set that looks like a verdict about the code.</para>
/// </summary>
public class SyncedQueryCacheQuerySetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// The mechanism, isolated: the SAME cache id asked for a WIDER query set must not be served
    /// the narrower cached answer. Deterministic — no compile, no race, no timing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetQuery_AskedForAWiderQuerySet_DoesNotServeTheNarrowerCachedAnswer()
    {
        await MeshService.CreateNode(CodeNode("qs/Own/Source", "own", "// own"))
            .Should().Within(30.Seconds()).Emit();
        await MeshService.CreateNode(CodeNode("qs/Lib/Source", "lib", "// lib"))
            .Should().Within(30.Seconds()).Emit();

        var workspace = Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
        const string cacheId = "test-sources:qs/Own";
        const string ownQuery = "namespace:qs/Own/Source scope:subtree nodeType:Code";
        const string libQuery = "namespace:qs/Lib/Source scope:subtree nodeType:Code";

        // Materialise the entry with the NARROW set — the state a running portal is in before
        // the author adds a shared= source.
        var narrow = await workspace.GetQuery(cacheId, ownQuery)
            .Should().Within(30.Seconds()).Match(s => s.Any(n => n.Path == "qs/Own/Source/own"));
        narrow.Select(n => n.Path).Should().NotContain("qs/Lib/Source/lib",
            "the narrow query genuinely does not select the library — otherwise the widening " +
            "below would prove nothing");

        // The SAME id, now asked for one more query. Before the fix this returns the cached
        // narrow entry and the added query is silently never executed.
        var wide = await workspace.GetQuery(cacheId, ownQuery, libQuery)
            .Should().Within(30.Seconds()).Match(s => s.Any(n => n.Path == "qs/Lib/Source/lib"));

        wide.Select(n => n.Path).Should().Contain("qs/Own/Source/own",
            "widening a cached query must not lose what it already matched");
    }

    /// <summary>
    /// The reported symptom end-to-end (#1311): a NodeType that compiles, then gains a
    /// <c>shared=@Other/Source</c> entry, must compile against the shared file. Before the fix
    /// the second compile re-uses the first compile's frozen source query set and fails with a
    /// phantom <c>CS0103</c> naming a symbol that is right there in the mesh.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AddingASharedSourceToAWarmNodeType_IsVisibleToTheNextCompile()
    {
        const string nodeTypePath = "type/SharedConsumer";
        const string libNamespace = "lib/SharedLib/Source";

        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = "SharedConsumer",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<SharedConsumer>()"
            },
            State = MeshNodeState.Active
        };
        await MeshService.CreateNode(typeNode).Should().Within(30.Seconds()).Emit();
        await MeshService.CreateNode(CodeNode($"{nodeTypePath}/Source", "model",
                "public record SharedConsumer { public string Title { get; init; } = string.Empty; }"))
            .Should().Within(30.Seconds()).Emit();

        var compilationService = Mesh.ServiceProvider
            .GetRequiredService<IMeshNodeCompilationService>();

        // FIRST compile — this is what warms `nodetype-sources:type/SharedConsumer` with the
        // DEFAULT (own-subtree) query set.
        var first = await compilationService.CompileAndGetConfigurations(typeNode)
            .Should().Within(60.Seconds()).Emit();
        first!.AssemblyLocation.Should().NotBeNullOrEmpty(
            $"the baseline compile must succeed; log: {Flatten(first)}");

        // The author now publishes a library and points the type at it — exactly the
        // MeshWeaver.Plugins#432 change that parked Edu/Module.
        await MeshService.CreateNode(CodeNode(libNamespace, "helper",
                "public static class SharedConsumerHelper { public static int Answer => 42; }"))
            .Should().Within(30.Seconds()).Emit();

        var withShared = typeNode with
        {
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<SharedConsumer>()",
                Sources = ["namespace:Source scope:subtree", $"shared=@{libNamespace}"]
            }
        };

        var second = await compilationService.CompileAndGetConfigurations(withShared)
            .Should().Within(60.Seconds()).Emit();

        second!.CompiledSources.Should().NotBeNull();
        second.CompiledSources!.Keys.Should().Contain($"{libNamespace}/helper",
            "the shared= source must be in the snapshot the compile consumed. Serving the " +
            "pre-edit cache entry hands Roslyn the type's OWN sources only, and the phantom " +
            $"CS0103 that follows names code that is right there in the mesh. Log: {Flatten(second)}");
        second.AssemblyLocation.Should().NotBeNullOrEmpty(
            $"the compile must succeed against the widened source set; log: {Flatten(second)}");
    }

    private static MeshNode CodeNode(string ns, string name, string code) =>
        new(name, ns)
        {
            NodeType = "Code",
            Name = name,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
            State = MeshNodeState.Active
        };

    private static string Flatten(NodeCompilationResult? result) =>
        result?.Log is null ? "(no log)" : string.Join(" | ", result.Log.Messages.Select(m => m.Message));
}
