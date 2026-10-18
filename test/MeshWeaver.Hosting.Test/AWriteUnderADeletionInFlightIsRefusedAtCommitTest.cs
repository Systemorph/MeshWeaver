using System;
using System.Reactive;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A write to a node whose subtree is being deleted — or MOVED away, which deletes it from where
/// it is — is refused when the owner COMMITS it, not only when the owner stores it
/// (Systemorph/MeshWeaver#6953).
///
/// <para>Measured on a production portal: the feedback hand-over's move held the deletion scope on
/// its source while two writes reached the source's hub. The owner committed both to its in-memory
/// node, and the store refused both flushes ("the state is not durable. It stays pending"). The
/// writers were told their writes failed; the owner kept serving them; and once the move (which
/// failed) released the scope, the next save stored them after all. Had the move succeeded they would
/// have been lost to the tombstone instead. Either way the acknowledgement and the durable state
/// disagreed.</para>
///
/// <para>The test holds the same scope a move holds, writes through the ordinary cross-hub route
/// (a patch to the owner), releases the scope, and then makes a legitimate write. The refused change
/// must appear neither in what the owner serves nor in what that later write stores. The control is
/// the later write itself: it lands, so the scope is released and the node is writable again.</para>
/// </summary>
public class AWriteUnderADeletionInFlightIsRefusedAtCommitTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private RecentlyDeletedRegistry Scopes => Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();

    [Fact(Timeout = 120_000)]
    public async Task APatchUnderAMoveInFlight_IsRefusedWithNothingCommitted_AndNeverStoredLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/held-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "before",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var stream = Mesh.GetWorkspace().GetMeshNodeStream(path);
        // The owner is live and serving the node before the scope opens.
        await stream.Where(n => n?.Name == "before").Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Exception? refusal;
        using (Scopes.BeginSubtreeDeletion(path))
        {
            refusal = await stream.Update(n => n with { Name = "during" })
                .Take(1)
                .Select(_ => (Exception?)null)
                .Catch((Exception ex) => Observable.Return<Exception?>(ex))
                .DefaultIfEmpty(null)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        }
        refusal.Should().NotBeNull("a write under a move in flight cannot become durable, so it is refused");
        Output.WriteLine($"refused: {refusal!.Message}");

        // The control: with the scope released the node is writable again, and this write is durable
        // once it is acknowledged.
        var after = await stream.Update(n => n with { Description = "after" })
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        after.Description.Should().Be("after");
        after.Name.Should().Be("before",
            "the refused write was never committed, so the owner does not serve it");

        var stored = await Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        stored.Should().NotBeNull();
        stored!.Description.Should().Be("after", "the later write is durable");
        stored.Name.Should().Be("before",
            "a write its writer was told failed must never be stored behind its back by a later save");
    }
    /// <summary>
    /// The OWNER's own write under the same scope is DEFERRED, never acknowledged — the same contract
    /// the version-guarded delete's seal gives it (#6856): it re-runs when the scope lifts. Here the
    /// operation aborted (nothing was tombstoned), so the deferred write then applies.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOwnWriteUnderAMoveInFlight_IsDeferred_AndAppliedWhenTheMoveAborts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await SeedOwned("own-abort", ct);

        IConnectableObservable<Notification<MeshNode>> write;
        IDisposable connection;
        using (Scopes.BeginSubtreeDeletion(path))
        {
            write = owner.GetWorkspace().GetMeshNodeStream()
                .Update(n => n with { Name = "during" })
                .Materialize()
                .Where(n => n.Kind != NotificationKind.OnCompleted)
                .Replay();
            connection = write.Connect();
            await write.Should().NotEmit(TimeSpan.FromSeconds(1),
                "a write the move may remove is neither acknowledged nor refused while its scope is open", ct);
        }

        using (connection)
        {
            var answer = await write.Should().Within(TestTimeouts.Convergence)
                .Emit("the scope lifted with nothing deleted, so the deferred write runs", ct);
            answer.Kind.Should().Be(NotificationKind.OnNext, answer.Exception?.Message ?? "the scope lifted");
            answer.Value!.Name.Should().Be("during");
        }
    }

    /// <summary>The same deferred own write, when the operation COMPLETES (the path is tombstoned):
    /// it is refused, never acknowledged for a node that is gone.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOwnWriteUnderAMoveInFlight_IsRefusedWhenTheMoveCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (path, owner) = await SeedOwned("own-commit", ct);

        IConnectableObservable<Notification<MeshNode>> write;
        IDisposable connection;
        using (Scopes.BeginSubtreeDeletion(path))
        {
            write = owner.GetWorkspace().GetMeshNodeStream()
                .Update(n => n with { Name = "during" })
                .Materialize()
                .Where(n => n.Kind != NotificationKind.OnCompleted)
                .Replay();
            connection = write.Connect();
            await write.Should().NotEmit(TimeSpan.FromSeconds(1), "deferred while the scope is open", ct);
            Scopes.MarkDeleted(path);
        }

        using (connection)
        {
            var answer = await write.Should().Within(TestTimeouts.Convergence)
                .Emit("the scope lifted on a removed node, so the deferred write is answered", ct);
            answer.Kind.Should().Be(NotificationKind.OnError, "a write to a node that went away is never acknowledged");
        }
    }

    private async Task<(string Path, IMessageHub Owner)> SeedOwned(string prefix, System.Threading.CancellationToken ct)
    {
        var path = $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "before",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await Mesh.GetMeshNodeStream(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable", cancellationToken: ct);
        var owner = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is { IsDisposing: false } && h.Started.IsCompletedSuccessfully)
            .Select(h => h!)
            .Should().Within(TestTimeouts.Convergence).Emit("the node's own hub is hosted", cancellationToken: ct);
        return (path, owner);
    }
}
