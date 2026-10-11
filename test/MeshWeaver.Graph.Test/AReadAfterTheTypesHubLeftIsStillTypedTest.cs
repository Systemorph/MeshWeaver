using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A replica that typed a dynamic NodeType keeps typing it after the type's hub is torn down</b>
/// (Systemorph/MeshWeaver.Plugins#2799, residual 2).
///
/// <para><b>The incident.</b> memex-cloud, 2026-10-10: the outgoing pod
/// <c>6dc5db759b-786n6</c> (booted 19:51Z) logged fifteen
/// <c>MeshNodeStreamCache.GetStream: Content for Ops/Status/… stayed an untyped JsonElement</c>
/// lines between 20:34:53Z and 20:36:03Z — after another replica of its generation stopped
/// (20:34:49Z) and before its own host stopped (20:36:10Z) — and then
/// <c>content for nodeType Hosting/DeploymentStatus was NEVER resolvable on this replica — 15
/// read(s) degraded</c>. It had typed that content for 45 minutes. In the roll its per-node hubs
/// were torn down (<c>giving up on owner Hosting/DeploymentStatus — 4 DISTINCT owner activations
/// have refused this stream</c>), hub teardown unloads the type's collectible contexts
/// (<c>UnloadNodeContexts</c>), and the mesh-wide registry dropped the type's entries the moment
/// the unload BEGAN — while the generation was still loaded and its standing watches were still
/// being served. The on-demand registration (#6411) could not put it back: its verdict is kept for
/// the process and said <c>Registered</c>.</para>
///
/// <para>This drives the real pieces, nothing mocked: the type is baked without an instance hub,
/// registered through the production read seam's own service, and its contexts are unloaded
/// through the same <see cref="ICompilationCacheService.UnloadNodeContexts"/> call a hub's disposal
/// makes. <b>SHOULD-FAIL-IF</b> the registry removes an unloading generation outright: the read after
/// the teardown then answers a <see cref="JsonElement"/> (the kept <c>Registered</c> verdict is
/// replayed over a registry that no longer answers), which is the incident.</para>
/// </summary>
public class AReadAfterTheTypesHubLeftIsStillTypedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypeId => $"Gauge{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";
    private string ContentTypeName => $"GaugeContent{suffix}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<ContentTypeOnDemandRegistration>());

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IMeshContentTypeRegistry Registry => Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
    private ContentTypeOnDemandRegistration OnDemand => Mesh.ServiceProvider.GetRequiredService<ContentTypeOnDemandRegistration>();

    /// <summary>Plain serializer options for the read seam — the reader's own, as a cache caller's are.</summary>
    private static JsonSerializerOptions ReadOptions => new(JsonSerializerDefaults.Web);

    private MeshNode Instance => new($"Item{suffix}", TestPartition)
    {
        NodeType = TypePath,
        State = MeshNodeState.Active,
        Content = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"$type":"{{ContentTypeName}}","label":"a standing watch's next emission"}"""),
    };

    private async Task Seed(CancellationToken cancellationToken)
    {
        foreach (var node in new[]
                 {
                     new MeshNode(TypeId, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = TypeId,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition
                         {
                             Configuration = $"config => config.WithContentType<{ContentTypeName}>()",
                         },
                     },
                     new MeshNode("Main", $"{TypePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration
                         {
                             Language = "csharp",
                             Code = $"public record {ContentTypeName} {{ public string? Label {{ get; init; }} }}",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{node.Path} must exist before the bake reads it", cancellationToken);
    }

    private async Task<DynamicTypePreWarmer.DynamicTypes> Enumerate(
        Func<NodeTypeDefinition, bool> state, string because, CancellationToken cancellationToken)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem())
                .Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.TryGetValue(TypePath, out var d) && d is not null && state(d))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(because, cancellationToken);

    /// <summary>Bakes the type the way the gated sweep does — a direct compile, no hub activation.</summary>
    private async Task Bake(CancellationToken cancellationToken)
    {
        var enumerated = await Enumerate(_ => true, "the sweep enumerates the type", cancellationToken);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batched discovery pass must establish the source set", cancellationToken);
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], sets.TryGetValue(TypePath, out var set) ? set : [],
                PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1))
            .Emit("BakeOne always reaches exactly one outcome", cancellationToken);
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");
        await Enumerate(
            d => !string.IsNullOrEmpty(d.LatestAssemblyPath) && d.LastCompiledVersion is not null,
            "the bake's build stamp lands on the NodeType record", cancellationToken);
    }

    private async Task RegisterOnDemand(CancellationToken cancellationToken)
    {
        var outcome = await OnDemand.EnsureRegistered(Mesh, TypePath)
            .Should().Within(PerTypeBudget).Emit("the on-demand attempt reaches one outcome", cancellationToken);
        outcome.Status.Should().Be(ContentTypeRegistrationStatus.Registered, outcome.Detail ?? "(no detail)");
        Registry.TryResolveByNodeType(TypePath, out _).Should().BeTrue("the replica types this NodeType now");
    }

    /// <summary>What a hub's disposal does to the type's collectible contexts.</summary>
    private void TearDownTheTypesContexts() =>
        Mesh.ServiceProvider.GetRequiredService<ICompilationCacheService>()
            .UnloadNodeContexts(CodeConventions.SanitizeNodeName(TypePath));

    private Task<MeshNode> Read(CancellationToken cancellationToken)
        => MeshNodeStreamCache
            .TypeStream(Observable.Return(Instance), ReadOptions, NullLogger.Instance, Registry,
                degradations: null, ensureRegistered: nodeType => OnDemand.EnsureRegistered(Mesh, nodeType))
            .FirstAsync()
            .Should().Within(PerTypeBudget).Emit("the read answers", cancellationToken);

    /// <summary>
    /// The incident: the type's hub is torn down while a reader still holds content of the type.
    /// The next emission of the standing watch must be typed.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AReadAfterTheTypesContextsBeganUnloading_IsStillTyped()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Bake(ct);
        await RegisterOnDemand(ct);

        // What the node-stream cache holds for a standing watch: the content it last emitted.
        var lastEmission = (await Read(ct)).Content;
        lastEmission.Should().NotBeOfType<JsonElement>("CONTROL — before the teardown the read is typed");
        var contentType = lastEmission!.GetType();
        contentType.Assembly.IsCollectible.Should().BeTrue("the content is typed from the baked build");

        TearDownTheTypesContexts();

        var afterTeardown = (await Read(ct)).Content;
        afterTeardown.Should().NotBeOfType<JsonElement>(
            "the generation is still loaded — the old build must keep typing reads until a newer one "
            + "is bound; on main the registry dropped it when the unload began and this read is untyped");
        afterTeardown!.GetType().Should().BeSameAs(contentType);
        GC.KeepAlive(lastEmission);
    }
}
