using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
}
