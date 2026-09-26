using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #5011: a HELD read of a node must never become a silent corpse.
///
/// <para><b>The shape.</b> Every portal process holds <c>GetMeshNodeStream</c> of the fleet watch's
/// owner open for the life of the process (the Hosting package's <c>InboxHubAnchor</c>); the held
/// stream's sync-stream heartbeat is what keeps the owner activated. <see cref="MeshNodeStreamCache"/>
/// tears a read entry down on two paths that ignore its subscribers — <c>Invalidate</c> (after a
/// node delete commits) and its own <c>Dispose</c>. Both unsubscribed the entry's hydration and
/// disposed its upstream sync stream — which stops the heartbeat — and neither told the readers
/// still subscribed to the entry: its <c>Replay(1)</c> subject never completed and never faulted.
/// The reader kept "holding" a stream that no longer held anything, with no error, no completion
/// and no log line — the fleet watch's two silent hours have exactly that signature.</para>
///
/// <para><b>The contract.</b> A reader whose entry is torn down underneath it receives a
/// TERMINAL — so a holder can say, loudly, that it lost its hold.</para>
/// </summary>
public class AHeldReadIsToldWhenItsEntryIsTornDownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private IMeshService Nodes => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task<string> CreateNodeAsync(string prefix)
    {
        var path = $"{TestPartition}/{prefix}-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Held",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task AHeldReadOfADeletedNode_IsTerminated()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateNodeAsync("held-delete");

        // The anchor's shape: ONE long-lived subscription, materialized so both terminals show.
        var held = Cache.GetStream(path, Mesh.JsonSerializerOptions).Materialize().Replay();
        using var holding = held.Connect();
        await held.Where(n => n.Kind == NotificationKind.OnNext && n.Value is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the held read receives the node before anything is torn down", ct);

        await Nodes.DeleteNode(path).Take(1).DefaultIfEmpty()
            .Should().Within(TestTimeouts.Convergence).Emit("the delete commits", ct);

        await held.Where(n => n.Kind != NotificationKind.OnNext)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a held read whose cache entry was torn down by the delete must be TOLD — "
                  + "its upstream and heartbeat are gone, so staying subscribed in silence is a "
                  + "corpse that reads as a live hold (#5011)", ct);
    }

    [Fact]
    public async Task AHeldReadOfADisposedCache_IsTerminated()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateNodeAsync("held-dispose");

        var held = Cache.GetStream(path, Mesh.JsonSerializerOptions).Materialize().Replay();
        using var holding = held.Connect();
        await held.Where(n => n.Kind == NotificationKind.OnNext && n.Value is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the held read receives the node before anything is torn down", ct);

        Cache.Dispose();

        var terminal = await held.Where(n => n.Kind != NotificationKind.OnNext)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a held read whose cache was disposed must be TOLD, never left holding a "
                  + "subject nothing will ever write again (#5011)", ct);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();
    }
}
