using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#6195 — <b>an instance activation on a replica below its type's package
/// dependency floor never asks for a SHARED recompile.</b>
///
/// <para><b>The defect.</b> The hydration of an existing build is held to the floor
/// (<see cref="DependencyFloorUnmetException"/>), and both extraction sites of
/// <c>ApplyStreamResult</c> folded that refusal into the loader's verdict and sent it through
/// <c>RefuseUnloadableBind</c> to <c>TriggerRecompileAndRetry</c> — which flips the type's ONE shared
/// record from <c>Ok</c> to <c>Pending</c>. During a mixed-generation roll every cold activation on
/// a below-floor replica did that, so a healthy type was rebuilt over and over on whichever replica
/// owned it, although no rebuild can give the ACTIVATING replica the dependency it lacks. The
/// framework-stale branch requested the same recompile for the same replica.</para>
///
/// <para><b>What must hold.</b> The instance serves the no-code-change diagnosis naming the unmet
/// requirement, and the type's record is untouched. Each case runs with the recompile budget
/// UNSPENT (<c>recompileAttempts: 0</c>), which is exactly when the old path flipped the record.
/// NEGATIVE CONTROL: the identical bytes bind for a type whose floor is met.</para>
/// </summary>
public class ABelowFloorReplicaRequestsNoSharedRecompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Every type whose path carries this marker is below its floor on this "replica".</summary>
    private const string BelowMarker = "BelowFloor";

    private const string Unmet =
        "package 'Hosting' requires AI@^1.40.0, but this replica runs 'MeshWeaver.AI' (package 'AI') "
        + "at 1.39.8, which does not satisfy it";

    /// <summary>The floor seam's double: unmet for <see cref="BelowMarker"/> types, met otherwise.</summary>
    private sealed class MarkerFloor : INodeTypeDependencyFloor
    {
        public IObservable<string?> Unmet(string nodeTypePath) =>
            Observable.Return<string?>(nodeTypePath.Contains(BelowMarker, StringComparison.Ordinal)
                ? ABelowFloorReplicaRequestsNoSharedRecompileTest.Unmet
                : null);
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeTypeDependencyFloor>(new MarkerFloor()));

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    /// <summary>Loadable bytes in the store, under <paramref name="typePath"/>.</summary>
    private async Task<(string Collection, string ContentPath)> LoadableBytes(string typePath)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = await System.IO.File.ReadAllBytesAsync(
            typeof(ModuleVersionCompatibility).Assembly.Location, ct);
        var location = await Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>()
            .PutWithLocation(typePath, 1, bytes, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        return (location.Collection, location.ContentPath);
    }

    /// <summary>A type whose record names a build for <paramref name="framework"/>.</summary>
    private async Task<MeshNode> TypeWithBuild(string name, string framework, string? pinnedRelease = null)
    {
        var typePath = $"{TestPartition}/{name}";
        var (collection, contentPath) = await LoadableBytes(typePath);
        return await CreateAsSystem(new MeshNode(name, TestPartition)
        {
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LastCompiledVersion = 1,
                LatestAssemblyCollection = collection,
                LatestAssemblyPath = contentPath,
                CompiledFrameworkVersion = framework,
                RequestedReleasePath = pinnedRelease,
            },
        });
    }

    private Task<MeshNode> Activate(MeshNode typeNode, string instanceId, CancellationToken ct) =>
        NodeTypeEnrichmentHelpers
            .ApplyStreamResult(
                typeNode, new MeshNode(instanceId, TestPartition) { NodeType = typeNode.Path },
                typeNode.Path, new MeshConfiguration(Array.Empty<MeshNode>()), Compiler, Mesh,
                logger: null, recompileAttempts: 0)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the activation must reach a verdict",
                cancellationToken: ct);

    /// <summary>The verdict served the below-floor diagnosis, and the shared record was not touched.</summary>
    private async Task ShouldServeTheDiagnosis_AndLeaveTheRecord(MeshNode verdict, MeshNode typeNode)
    {
        verdict.HubConfiguration.Should().NotBeNull(
            "a replica below the floor serves a diagnosis — never the default configuration");
        var nack = verdict.HubConfiguration!(
                new MessageHubConfiguration(null, new Address("probe", typeNode.Id)))
            .Get<UnhandledMessageNack>();
        nack.Should().NotBeNull("the diagnosis overlay Nacks typed requests");
        nack!.Reason.Should().Contain("AI@^1.40.0").And.Contain("1.39.8",
            "the diagnosis names the unmet requirement and the version this replica runs");

        var record = await Mesh.GetMeshNodeStream(typeNode.Path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the type's record is readable", cancellationToken: TestContext.Current.CancellationToken);
        var def = record.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "🚨 a below-floor replica must not flip the shared record to Pending: no compile anywhere "
            + "can give THIS replica the dependency it lacks (#6195)");
        // Not the node Version: the type's own hub legitimately writes its source snapshot after it
        // activates. What a recompile request writes is the dispatch stamp and, on its refusal here,
        // a terminal that replaces the status — neither may appear.
        def.DispatchedBuildInputs.Should().BeNull("no compile was dispatched for the shared record");
        def.LatestAssemblyPath.Should().Be(
            typeNode.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.LatestAssemblyPath,
            "the recorded build is untouched");
    }

    /// <summary>
    /// 🚨 THE REGRESSION — the hot path: a usable recorded build whose hydration THIS replica
    /// refuses on the floor.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AUsableBuild_RefusedByTheFloor_ServesTheDiagnosis_AndRequestsNoRecompile()
    {
        var typeNode = await TypeWithBuild($"{BelowMarker}Usable", NodeTypeCompilationHelpers.FrameworkVersion);
        var verdict = await Activate(typeNode, "below-floor-usable", TestContext.Current.CancellationToken);
        await ShouldServeTheDiagnosis_AndLeaveTheRecord(verdict, typeNode);
    }

    /// <summary>
    /// The framework-stale branch: a recorded build for another framework is a recompile request
    /// everywhere else — but not from a replica below the floor.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStaleBuild_OnABelowFloorReplica_ServesTheDiagnosis_AndRequestsNoRecompile()
    {
        var typeNode = await TypeWithBuild($"{BelowMarker}Stale", "a-previous-framework");
        var verdict = await Activate(typeNode, "below-floor-stale", TestContext.Current.CancellationToken);
        await ShouldServeTheDiagnosis_AndLeaveTheRecord(verdict, typeNode);
    }

    /// <summary>The pinned-release extraction site — the second branch the refusal reached.</summary>
    [Fact(Timeout = 120_000)]
    public async Task APinnedRelease_RefusedByTheFloor_ServesTheDiagnosis_AndRequestsNoRecompile()
    {
        var name = $"{BelowMarker}Pinned";
        var typePath = $"{TestPartition}/{name}";
        var releasePath = $"{typePath}/{ReleaseNodeType.ReleaseSegment}/r1";
        var typeNode = await TypeWithBuild(name, NodeTypeCompilationHelpers.FrameworkVersion, releasePath);
        var def = typeNode.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
        await CreateAsSystem(new MeshNode("r1", $"{typePath}/{ReleaseNodeType.ReleaseSegment}")
        {
            NodeType = ReleaseNodeType.NodeType,
            Content = new NodeTypeRelease
            {
                Path = releasePath,
                NodeTypePath = typePath,
                Release = "r1",
                FrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                AssemblyStoreVersion = 1,
                AssemblyCollection = def.LatestAssemblyCollection,
                AssemblyPath = def.LatestAssemblyPath,
            },
        });

        var verdict = await Activate(typeNode, "below-floor-pinned", TestContext.Current.CancellationToken);
        await ShouldServeTheDiagnosis_AndLeaveTheRecord(verdict, typeNode);
    }

    /// <summary>NEGATIVE CONTROL — the identical bytes bind for a type whose floor is met.</summary>
    [Fact(Timeout = 120_000)]
    public async Task NegativeControl_TheSameBuild_WithTheFloorMet_Binds()
    {
        var typeNode = await TypeWithBuild("FloorMetUsable", NodeTypeCompilationHelpers.FrameworkVersion);
        var verdict = await Activate(typeNode, "floor-met-usable", TestContext.Current.CancellationToken);
        verdict.HubConfiguration.Should().BeNull(
            "these bytes load and carry no NodeType provider, so the build is bound as-is — only the "
            + "floor differs between the arms");
    }

    private Task<MeshNode> CreateAsSystem(MeshNode node)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
    }
}
