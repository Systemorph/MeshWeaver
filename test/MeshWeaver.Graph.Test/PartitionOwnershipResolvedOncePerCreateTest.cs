using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A create resolves "does this type own its partition?" ONCE, and every VALIDATOR of that
/// create shares the answer</b> (MeshWeaver#4449 item 3).
///
/// <para><b>What it cost before.</b> For a type declared in mesh content one resolution is TWO round
/// trips — an anchored listing that establishes the definition exists, then an authoritative stream
/// read of it as System. Three validators asked independently
/// (<see cref="RlsNodeValidator"/>, <see cref="PartitionWriteGuardValidator"/>,
/// <see cref="OwnsPartitionProvisioningValidator"/>), so a top-level create of such a type paid six
/// reads for one fact.</para>
///
/// <para><b>How it is measured.</b> Not by counting reads — by the memo the validators share. An
/// observer validator, registered LAST so it runs after the three, reads
/// <see cref="PartitionOwnershipMemo.Resolutions"/> on the way in, asks the same question itself,
/// and reads it again. <c>1</c> before its ask is the three validators having resolved ONCE between
/// them; <c>1</c> after is its own ask having been served from that one resolution. Reverting any
/// validator to the unshared resolver makes the first number wrong, which is the negative control.</para>
///
/// <para>The type is created at RUNTIME as a persisted <c>NodeType</c> node, never through
/// <c>AddMeshNodes</c> — a STATIC type answers from the in-process registry with no read at all, so
/// the sharing would be unobservable and this test would prove nothing. Security fixture:
/// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, so the create is judged by the real rules.</para>
/// </summary>
public class PartitionOwnershipResolvedOncePerCreateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeLibrary = "onceownertypes";
    private const string OwnerTypeId = "OncePartner";
    private const string OwnerType = TypeLibrary + "/" + OwnerTypeId;
    private const string InstanceId = "onceacmepartner";

    /// <summary>An authenticated identity with no grant anywhere — the ordinary-user shape.</summary>
    private static readonly AccessContext Probe = new() { ObjectId = "onceprobe", Name = "Once Probe" };

    /// <summary>What the observer saw for one create: the memo's count before and after its own ask.</summary>
    private sealed record Reading(string Path, int Before, int After, bool? Owns);

    private ImmutableList<Reading> readings = ImmutableList<Reading>.Empty;

    private void Observe(Reading reading) =>
        ImmutableInterlocked.Update(ref readings, (list, r) => list.Add(r), reading);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            // Registered last, so GetServices<INodeValidator> hands it back after the three under
            // test and it observes a memo they have already used.
            .ConfigureServices(services => services.AddScoped<INodeValidator>(
                sp => new MemoObserver(sp.GetRequiredService<IMessageHub>(), Observe)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The type library: a Space holding one NodeType definition, persisted at runtime, that owns
    /// its partition. Written as System — the platform installs types.
    /// </summary>
    private async Task SeedType()
    {
        await SeedTopLevel(new MeshNode(TypeLibrary)
        {
            Name = "Once Owner Types",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        });

        await Access.RunAsSystem(() => MeshService.CreateNode(new MeshNode(OwnerTypeId, TypeLibrary)
            {
                Name = OwnerTypeId,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { OwnsPartition = true, DefaultNamespace = "" },
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                $"the platform can persist the NodeType definition '{OwnerType}'",
                cancellationToken: TestContext.Current.CancellationToken);

        Mesh.ServiceProvider.FindStaticNode(OwnerType).Should().BeNull(
            "the premise: the owning type is NOT static. A static type answers with no read, so "
            + "sharing one resolution would save nothing and this test would measure nothing");
    }

    [Fact(Timeout = 180_000)]
    public async Task ATopLevelCreateOfAnInMeshOwningType_ResolvesItsOwnershipExactlyOnce()
    {
        await SeedType();

        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode(InstanceId)
            {
                Name = "Once Acme Partner",
                NodeType = OwnerType,
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the create must SUCCEED — sharing the resolution changes what it costs, never what "
                + "it decides",
                cancellationToken: TestContext.Current.CancellationToken);

        var seen = readings.Where(r => r.Path == InstanceId).ToList();

        seen.Should().NotBeEmpty(
            "the observer validator must have run for the node under test — with no reading the "
            + "assertions below would pass over an empty list and measure nothing");

        foreach (var reading in seen)
        {
            Assert.True(reading.Before == 1,
                "the three validators that judge this create resolved the type's ownership ONCE "
                + "between them. 0 means none of them went through the shared resolver (the "
                + $"negative control); more than 1 means they did not share it. Got {reading.Before}");
            Assert.True(reading.After == 1,
                "a fourth ask, made after those three, is served from the same resolution and costs "
                + $"nothing. Got {reading.After}");
            Assert.True(reading.Owns == true,
                "the shared answer is the RIGHT one: this type declares ownsPartition, and every "
                + $"check judging the create sees that. Got {reading.Owns?.ToString() ?? "(unestablished)"}");
        }
    }

    /// <summary>
    /// Reads the operation's memo on the way past, asks the same question itself, and reads it
    /// again — then accepts. It decides nothing; it is the instrument.
    /// </summary>
    private sealed class MemoObserver(IMessageHub hub, Action<Reading> observe) : INodeValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Create];

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context) =>
            // Defer: the count has to be read when this validator's turn comes, not when the chain
            // is composed — Concat builds the next element only after the previous one completed,
            // but a reading taken at composition time would be measuring the wrong moment.
            Observable.Defer(() =>
            {
                var before = context.PartitionOwnership.Resolutions;
                return PartitionOwningTypes.OwnsPartitionOnce(hub, context)
                    .Select(owns =>
                    {
                        observe(new Reading(
                            context.Node.Path, before, context.PartitionOwnership.Resolutions, owns));
                        return NodeValidationResult.Valid();
                    });
            });
    }
}
