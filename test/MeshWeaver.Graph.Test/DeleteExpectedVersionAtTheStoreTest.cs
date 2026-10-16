using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6856: a delete guarded by <see cref="DeleteNodeRequest.ExpectedVersion"/> is a compare-and-delete
/// AT THE STORE, so its guarantee no longer rests on the seal the deleting process holds in memory.
///
/// <para><b>The isolated fault.</b> The delete is paused at the store after the owner confirmed the
/// version and while this process's seal is in place (<see cref="HeldWriteStorageAdapter.HoldDeletesOf"/>,
/// a controlled switch). A write then lands DIRECTLY on the store of record — beneath every decorator
/// and every seal of this process — which is exactly what a writer in another process sharing the
/// database does. Before #6856 the commit was an unconditional row delete and removed that write while
/// reporting success. Negative control: the same paused delete with no foreign write deletes.</para>
///
/// <para>And the acknowledged-write half: the node a cross-hub <c>GetMeshNodeStream(path).Update(...)</c>
/// emits carries the version the OWNER committed, so naming it as the expected version deletes —
/// before, it carried the writer's base version and was refused as a conflict that never happened.</para>
/// </summary>
public class DeleteExpectedVersionAtTheStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ONE database, two processes: this mesh's adapter, and a second adapter over the same rows with
    // its OWN change feed — the shape of a writer in another process whose notification has not
    // reached this one. Nothing it writes is announced to this mesh.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MeshNode> _rows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<object>> _objects =
        new(StringComparer.OrdinalIgnoreCase);
    private InMemoryStorageAdapter? _thisProcess;
    private InMemoryStorageAdapter? _otherProcess;
    private InMemoryStorageAdapter StoreOfRecord => _thisProcess ??= new InMemoryStorageAdapter(_rows, _objects);
    private InMemoryStorageAdapter OtherProcess => _otherProcess ??= new InMemoryStorageAdapter(_rows, _objects);
    private HeldWriteStorageAdapter? _storage;
    private HeldWriteStorageAdapter Storage => _storage ??= new HeldWriteStorageAdapter(StoreOfRecord);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so the production decorators wrap it.
            services.AddSingleton<IStorageAdapter>(Storage);
            return services;
        }));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<MeshNode> Seed(string id) =>
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: Ct);

    private async Task<MeshNode?> Stored(string path) =>
        await StoreOfRecord.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: Ct);

    /// <summary>
    /// Issues a guarded delete naming <paramref name="expected"/>, waits until it has passed the
    /// owner's verdict and is paused at the store, runs <paramref name="whilePaused"/>, releases it and
    /// returns its answer.
    /// </summary>
    private async Task<DeleteNodeResponse> PausedGuardedDelete(string path, long expected, Func<Task> whilePaused)
    {
        Storage.HoldDeletesOf(path);
        var delete = ObserveNodeOperation(new DeleteNodeRequest(path) { ExpectedVersion = expected }).Replay(1);
        using var connection = delete.Connect();
        try
        {
            await Storage.DeleteHeld.Should().Within(TestTimeouts.Convergence)
                .Emit("the guarded delete passed the owner's verdict and reached the store", cancellationToken: Ct);
            await whilePaused();
        }
        finally
        {
            Storage.ReleaseDelete();
        }
        var response = (await delete.Should().Within(TestTimeouts.Convergence)
            .Emit("a delete always answers", Ct)).Message;
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");
        return response;
    }

    /// <summary>
    /// THE FALSIFIER: a newer row written straight into the store of record after the owner confirmed
    /// the version — past this process's seal, as another process's writer would — is refused by the
    /// store's compare-and-delete and survives. The delete answers Conflict.
    /// </summary>
    [Fact]
    public async Task ANewerRowWrittenPastTheSeal_IsRefusedAtTheStore_AndSurvives()
    {
        var node = await Seed("store-guard-foreign");
        var read = (await Stored(node.Path))!.Version;

        var response = await PausedGuardedDelete(node.Path, read, async () =>
        {
            var current = (await Stored(node.Path))!;
            await OtherProcess
                .Write(current with { Name = "written by another process", Version = read + 1 }, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit("the foreign write lands", cancellationToken: Ct);
        });

        response.Success.Should().BeFalse(
            $"version {read + 1} was stored after the owner confirmed {read}; deleting it removes a write "
            + "the deleter never saw");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Conflict);
        var survivor = await Stored(node.Path);
        survivor.Should().NotBeNull("a refused compare-and-delete removes nothing");
        survivor!.Name.Should().Be("written by another process");
        survivor.Version.Should().Be(read + 1);

        // The delete pass tombstoned the root before its commit. The foreign write was announced to
        // NOBODY here, so no notification can lift that tombstone: the refused delete itself must, or
        // the surviving node reads as "gone for good" and every save to it is refused for the TTL.
        Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>()
            .IsRecentlyDeleted(node.Path)
            .Should().BeFalse("the node survived the refused delete and must not stay tombstoned");
    }

    /// <summary>
    /// NEGATIVE CONTROL: the identical paused delete with no foreign write deletes — so the refusal
    /// above is the comparison, not a refusal of every delete that was paused.
    /// </summary>
    [Fact]
    public async Task ThePausedGuardedDelete_WithNoForeignWrite_Deletes()
    {
        var node = await Seed("store-guard-control");
        var read = (await Stored(node.Path))!.Version;

        var response = await PausedGuardedDelete(node.Path, read, () => Task.CompletedTask);

        response.Success.Should().BeTrue(response.Error ?? "nothing changed after the owner's check");
        response.AlreadyAbsent.Should().BeFalse();
        (await Stored(node.Path)).Should().BeNull("the node is gone");
    }

    /// <summary>
    /// The acknowledged-write path (#6856 item 3): a cross-hub update's emitted node carries the version
    /// the owner COMMITTED, and naming it as the expected version deletes. Before, the emitted node
    /// carried the writer's base version, one behind the owner, and the delete was refused.
    /// </summary>
    [Fact]
    public async Task ACrossHubWritesAcknowledgedVersion_IsTheOwnersVersion_AndDeletes()
    {
        var node = await Seed("store-guard-acked");
        var before = (await Stored(node.Path))!.Version;

        var acked = await Mesh.GetMeshNodeStream(node.Path)
            .Update(n => n with { Name = "changed across hubs" })
            .Should().Within(TestTimeouts.Convergence).Emit("the owner acknowledges", cancellationToken: Ct);
        Output.WriteLine($"[write] base={before} acked={acked.Version}");

        acked.Version.Should().BeGreaterThan(before,
            "the owner minted a new version for the applied patch, and the acknowledgement names it");
        var stored = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => StoreOfRecord.Read(node.Path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "changed across hubs")
            .Should().Within(TestTimeouts.Convergence).Emit("the write is stored", cancellationToken: Ct);
        acked.Version.Should().Be(stored!.Version, "the acknowledged version is the version the owner committed");

        var response = (await ObserveNodeOperation(new DeleteNodeRequest(node.Path) { ExpectedVersion = acked.Version })
            .Should().Within(TestTimeouts.Convergence).Emit("a delete always answers", Ct)).Message;
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");
        response.Success.Should().BeTrue(response.Error ?? "the deleter named the version the owner holds");
    }
}
