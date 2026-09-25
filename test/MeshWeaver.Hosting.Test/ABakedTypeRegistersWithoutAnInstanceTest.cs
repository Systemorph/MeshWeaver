using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A dynamic NodeType that is BAKED here must be typeable here, whether or not one of its
/// instances ever activates here</b> (Systemorph/MeshWeaver.Plugins#2180,
/// <c>Doc/Architecture/DynamicContentTypeRegistration</c>).
///
/// <para>A dynamic type's content CLR type registers only as a side effect of its configuration being
/// BUILT, which used to happen only when an instance hub activated. A replica that adopted or baked
/// the type — record stamped, bytes in the store — but never hosted one of its few instances could
/// not type a single node of it: measured on two live portals as <c>Hosting/DeploymentStatus</c> and
/// <c>Hosting/Deployment</c> rendering empty on one replica and not on the other, each with a usable
/// assembly and a clean bake.</para>
///
/// <para>This drives the real pieces, nothing mocked: the batch bake compiles the type through the
/// real Roslyn pipeline WITHOUT activating a hub (the <c>AlreadyBaked</c>-on-another-replica state:
/// bytes and record present, nothing registered), then the registration-only pass runs over the
/// sweep's own enumeration. <b>SHOULD-FAIL-IF</b> the pass is removed or stops building the
/// configuration (the registry answers nothing — the control half proves the bake alone does not
/// register), or if it heals by COMPILING (the record's build stamp would move).</para>
/// </summary>
public class ABakedTypeRegistersWithoutAnInstanceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypeId => $"Gizmo{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";
    private string ContentTypeName => $"GizmoContent{suffix}";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IMeshContentTypeRegistry Registry => Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();

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
                .Emit($"{node.Path} must exist before the sweep reads it", cancellationToken);
    }

    /// <summary>The sweep's enumeration of this one type, waited on until it shows <paramref name="state"/>.</summary>
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
    private async Task<DynamicTypePreWarmer.DynamicTypes> Bake(CancellationToken cancellationToken)
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

        // The build stamp is what the pass reads — wait until the record carries it.
        return await Enumerate(
            d => !string.IsNullOrEmpty(d.LatestAssemblyPath) && d.LastCompiledVersion is not null,
            "the bake's build stamp lands on the NodeType record", cancellationToken);
    }

    /// <summary>
    /// The NodeType's definition read straight from the STORE — the authority, not the lagging
    /// listing and not the NodeType stream (whose first touch may kick a compile, which is exactly
    /// what the "no recompile" half must not provoke).
    /// </summary>
    private async Task<NodeTypeDefinition> Stored(CancellationToken cancellationToken)
    {
        var node = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(TypePath, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("the NodeType row is readable", cancellationToken);
        node.Should().NotBeNull();
        return node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    private async Task<ContentTypeRegistrationOutcome[]> RunPass(
        DynamicTypePreWarmer.DynamicTypes types, CancellationToken cancellationToken)
    {
        var outcomes = await DynamicContentTypeRegistrar
            .RegisterTypes(Mesh, types, TimeSpan.Zero, null)
            .ToArray()
            .Should().Within(PerTypeBudget)
            .Emit("the pass reaches one outcome per type and completes", cancellationToken);
        foreach (var o in outcomes)
            Output.WriteLine("{0}: {1} — {2}", o.TypePath, o.Status, o.Detail ?? "(no detail)");
        return outcomes;
    }

    /// <summary>
    /// 🚨 THE REPRO AND THE FIX. Baked here, never activated here: unregistered. After the pass:
    /// registered — and the NodeType record carries the same build stamp it had before, because the
    /// pass loads the existing bytes and never compiles or writes.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ABakedTypeNobodyActivated_IsRegisteredByThePass_WithoutARecompile()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        var baked = await Bake(ct);
        var before = await Stored(ct);
        before.LatestAssemblyPath.Should().NotBeNullOrEmpty("the store holds the bake's stamp");

        Registry.TryResolveByNodeType(TypePath, out _).Should().BeFalse(
            "CONTROL — the bake compiled the type and stamped its record without building its "
            + "configuration, which is exactly the AlreadyBaked replica state: bytes present, content "
            + "type unknown. If this ever holds, the test no longer measures the gap.");

        var outcomes = await RunPass(baked, ct);

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ContentTypeRegistrationStatus.Registered);
        Registry.TryResolveByNodeType(TypePath, out var registered).Should().BeTrue(
            "the registration-only pass must make the baked type's content typeable on this process");
        registered.Name.Should().Be(ContentTypeName);
        registered.Assembly.IsCollectible.Should().BeTrue(
            "the registered type comes from the baked (collectible) assembly, not a stand-in");

        var after = await Stored(ct);
        after.LatestAssemblyPath.Should().Be(before.LatestAssemblyPath,
            "a pass that healed by COMPILING would have published a new build — the unsafe shape");
        after.LastCompiledVersion.Should().Be(before.LastCompiledVersion);
        after.LastCompileSucceededAt.Should().Be(before.LastCompileSucceededAt);

        (await RunPass(baked, ct)).Should().ContainSingle()
            .Which.Status.Should().Be(ContentTypeRegistrationStatus.AlreadyRegistered,
                "a type already registered is not probed again");
    }

    /// <summary>
    /// 🚨 CONTROL — a type with no usable build is SKIPPED, never compiled: registering it would need
    /// the compile chain, which is the first access's job and never this pass's.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ATypeWithNoBuild_IsSkipped_NotCompiled()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        var unbaked = await Enumerate(_ => true, "the sweep enumerates the unbaked type", ct);

        var outcome = (await RunPass(unbaked, ct)).Should().ContainSingle().Which;

        outcome.Status.Should().Be(ContentTypeRegistrationStatus.NotBaked);
        Registry.TryResolveByNodeType(TypePath, out _).Should().BeFalse();
        (await Stored(ct)).LatestAssemblyPath.Should().BeNullOrEmpty("the pass compiled nothing");
    }
}
