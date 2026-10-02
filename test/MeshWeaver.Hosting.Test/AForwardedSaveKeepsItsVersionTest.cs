using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A <see cref="SaveMeshNodeRequest"/> from anyone but the owning hub carries a whole-node
/// SNAPSHOT, and a snapshot taken before the node's latest write must not put the node back.
///
/// <para>The save is forwarded to the checked upsert (<see cref="SaveMeshNodeRequestIsACheckedWriteTest"/>),
/// which takes content wholesale and never looked at the incoming <see cref="MeshNode.Version"/> —
/// so an older snapshot WON over a newer write and was acknowledged as a success. The raw path it
/// replaced was protected by the storage layer: a save at or below the flushed version was
/// dropped, a strictly lower one refused by the monotonic write guard. The forwarder now states
/// the snapshot's version as the upsert's precondition
/// (<see cref="CreateOrUpdateNodeRequest.SnapshotVersion"/>), and a stale one is refused and
/// ANSWERED.</para>
///
/// <para>Every wait here ends on a positive terminal — the refusal's answer, or the row the
/// control wrote — so no test spends a negative window.</para>
/// </summary>
public class AForwardedSaveKeepsItsVersionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ViewerId = "stale-save-viewer";
    private const string Newer = "written-by-the-owner";
    private const string Overwritten = "overwritten-by-save";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>The viewer reads the partition and holds no Update anywhere. Built from
    /// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — the default mesh grants Public → Admin,
    /// under which every identity may write.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode($"{ViewerId}_Viewer", $"{TestPartition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{ViewerId} — Viewer",
                MainNode = TestPartition,
                Content = new AccessAssignment
                {
                    AccessObject = ViewerId,
                    DisplayName = ViewerId,
                    Roles = [new RoleAssignment { Role = Role.Viewer.Id }],
                },
            });

    /// <summary>The lost update, and its control in the same mesh: the node is written by its
    /// owner, then a caller who MAY write saves the snapshot it took before that write — refused,
    /// with both versions named, and the owner's write still stored. The same caller then saves
    /// the snapshot it holds NOW, and that one is written: the refusal was about the version,
    /// not the caller, the message or the route.</summary>
    [Fact(Timeout = 60000)]
    public async Task ASaveCarryingAnOlderSnapshot_IsRefusedNamingBothVersions_AndTheCurrentSnapshotWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = NewPath("stale");
        var snapshot = await CreateNode(path);

        // The owner's write. Its acknowledgement chains off the durable write, so the stored row
        // carries it by the time this returns.
        await NodeFactory.UpdateNode(snapshot with { Name = Newer })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var current = await StoredAt(path);
        current.Name.Should().Be(Newer);
        current.Version.Should().BeGreaterThan(snapshot.Version, "the owner's write minted a new version");

        var client = GetClient();
        var refusal = await RefusalOf(client, snapshot with { Name = Overwritten }, TestUsers.Admin);

        refusal.Should().NotBeNull("the older snapshot must be refused — it was WRITTEN over the owner's newer write instead");
        if (refusal is null)
            return;
        refusal.ErrorType.Should().Be(ErrorType.Rejected, "a stale snapshot is a refusal by the write, not a fault");
        refusal.Message.Should().Contain($"version {snapshot.Version}", "the refusal names the version the save offered")
            .And.Contain($"version {current.Version}", "and the version the node is at");
        (await StoredAt(path)).Name.Should().Be(Newer, "the older snapshot must not put the node back");

        // The control: the CURRENT snapshot, same caller, same route — written.
        client.Post(new SaveMeshNodeRequest(current with { Name = Overwritten }),
            o => o.WithTarget(new Address(path)).WithAccessContext(TestUsers.Admin));
        await Overwrote(path).Should().Within(TestTimeouts.Convergence)
            .Emit("a save carrying the node's current version is forwarded to the checked upsert, which writes it",
                cancellationToken: ct);
    }

    /// <summary>The precondition is decided only for a caller established to hold write access.
    /// A viewer offering the same older snapshot is refused as a caller who may not write — the
    /// answer says nothing about the node's version, and the node is unchanged.</summary>
    [Fact(Timeout = 60000)]
    public async Task ACallerWithoutWrite_OfferingAnOlderSnapshot_IsRefusedWithoutBeingToldTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = NewPath("stale-viewer");
        var snapshot = await CreateNode(path);
        await NodeFactory.UpdateNode(snapshot with { Name = Newer })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var current = await StoredAt(path);
        current.Version.Should().BeGreaterThan(snapshot.Version, "the owner's write minted a new version");

        var refusal = await RefusalOf(GetClient(), snapshot with { Name = Overwritten },
            new AccessContext { ObjectId = ViewerId, Name = ViewerId });

        refusal.Should().NotBeNull($"'{ViewerId}' holds no Update on {path}; the save was WRITTEN instead of refused");
        if (refusal is null)
            return;
        refusal.ErrorType.Should().BeOneOf(
            [ErrorType.Forbidden, ErrorType.Unauthorized],
            $"'{ViewerId}' holds no Update on {path}");
        refusal.Message.Should().NotContain($"version {current.Version}",
            "a caller who may not write is not told where the node stands");
        (await StoredAt(path)).Name.Should().Be(Newer, "the refused save left the node as it was");
    }

    /// <summary>The rule refuses a snapshot that would put something BACK, not a low number. A
    /// cross-hub <c>stream.Update</c> hands its caller the node at the base version, so "write,
    /// then save what the write returned" always offers a version one behind the row it produced
    /// — carrying exactly what that row holds. It is acknowledged as unchanged, and writes
    /// nothing. Asked of the upsert directly, because a save that is not refused has no answer
    /// to wait for.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnOlderVersionCarryingWhatTheNodeAlreadyHolds_IsAcknowledgedUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = NewPath("same");
        var created = await CreateNode(path);
        await NodeFactory.UpdateNode(created with { Name = Newer })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var current = await StoredAt(path);
        current.Version.Should().BeGreaterThan(created.Version, "the owner's write minted a new version");

        var answer = await ObserveNodeOperation(
                new CreateOrUpdateNodeRequest(current with { Version = created.Version })
                {
                    RequestedBy = TestUsers.Admin.ObjectId,
                    SnapshotVersion = created.Version,
                })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the upsert answers", cancellationToken: ct);

        answer.Message.Success.Should().BeTrue(
            $"a snapshot that changes nothing is not stale, whatever its version: {answer.Message.Error}");
        answer.Message.WasCreated.Should().BeFalse();
        (await StoredAt(path)).Version.Should().Be(current.Version, "an unchanged node mints no version");
    }

    /// <summary>Posts the save and waits for what settles it: the failure a refused save is
    /// answered with, or — the defect — the save's content arriving in storage, reported as
    /// <c>null</c>. A written save is answered with nothing, so storage is the only place the
    /// wrong outcome shows; racing the two makes that outcome fail at once instead of on a bound.</summary>
    private async Task<DeliveryFailure?> RefusalOf(IMessageHub client, MeshNode node, AccessContext identity)
    {
        var answer = client.Observe(
                new SaveMeshNodeRequest(node),
                o => o.WithTarget(new Address(node.Path)).WithAccessContext(identity),
                Guid.NewGuid().ToString("N"))
            ?? Observable.Throw<IMessageDelivery>(
                new InvalidOperationException($"the save of {node.Path} could not be posted"));
        var settled = await answer
            .Select(_ => (Exception?)new InvalidOperationException("the save was answered with a response, not a failure"))
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Merge(Overwrote(node.Path).Select(_ => (Exception?)null))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the save is either refused with an answer or written",
                cancellationToken: TestContext.Current.CancellationToken);
        if (settled is null)
            return null;
        var failure = settled.Should().BeOfType<DeliveryFailureException>(
            "the sender is answered with a delivery failure").Subject.Failure;
        return failure.Should().BeOfType<DeliveryFailure>().Subject;
    }

    /// <summary>Emits once storage holds <paramref name="path"/> under the overwritten name — the
    /// sanctioned re-query shape for a source with no change signal of its own.</summary>
    private IObservable<bool> Overwrote(string path) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).DefaultIfEmpty(null).Take(1))
            .Where(node => node?.Name == Overwritten)
            .Select(_ => true)
            .Take(1);

    private Task<MeshNode> StoredAt(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .OfType<MeshNode>()
            .Should().Within(TestTimeouts.Convergence).Emit($"{path} is stored",
                cancellationToken: TestContext.Current.CancellationToken);

    private async Task<MeshNode> CreateNode(string path)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
}
