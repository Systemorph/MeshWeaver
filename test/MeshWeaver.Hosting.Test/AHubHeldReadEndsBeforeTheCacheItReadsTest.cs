using System;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The pin for <see href="https://github.com/Systemorph/MeshWeaver/issues/6078">#6078</see>:
/// at mesh teardown a hub's held node read ends with the HUB, never with the cache it reads.</b>
///
/// <para><b>What production showed.</b> <c>PullRequestSweep</c>'s review-slot watcher and
/// <c>FleetCoordinatorRuntime</c>'s ledger wakes, both on <c>Hosting/PlatformBuilds</c>, hold
/// <c>GetMeshNodeStream("Hosting/Triage/Status")</c> for the hub's whole life and tie it to the hub
/// with <c>RegisterForDisposal</c>. Four times on four pods, each on the outgoing pod of a roll, the
/// read faulted with <c>ObjectDisposedException: The mesh-node cache was disposed; the read of
/// 'Hosting/Triage/Status' ended with it.</c> and both consumers logged it at Error.</para>
///
/// <para><b>The root.</b> The node-stream cache is one per mesh and lives on its own hub, which the
/// mesh hosts beside every other hub — the per-node hubs (grain-backed ones included) and the
/// readers among them. <c>HostedHubsCollection</c> disposed that whole set as ONE wave, so the cache
/// hub's ShutDown — where the cache ends every held read with the disposal terminal (#5011) — raced
/// the readers' own ShutDown, where their registered subscriptions are released. Whenever the cache
/// won, a reader whose hub was going down anyway received a fault for its own teardown.</para>
///
/// <para><b>The contract.</b> A hub hosted for others to depend on declares it
/// (<c>WithTeardownAfterSiblings</c>), and its owner tears it down only after every sibling has
/// finished. So a hub-owned reader releases its read before the cache ends anything.</para>
///
/// <para><b>Why the negative control.</b> A read that NO hub owns — held straight on the cache —
/// must still be told with the disposal terminal. Otherwise the first test could pass because the
/// cache had stopped ending its readers at all, which is the silent corpse #5011 removed.</para>
/// </summary>
public class AHubHeldReadEndsBeforeTheCacheItReadsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A turn that keeps the reader hub busy while the teardown begins.</summary>
    public record BusyTurn;

    private static readonly TimeSpan BusyTurnBound = TimeSpan.FromSeconds(5);

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private async Task<string> CreateNodeAsync(string prefix)
    {
        var path = $"{TestPartition}/{prefix}-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Ledger",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        await NodeFactory.CreateNode(node).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    // 240_000 ms: an attribute argument must be a constant, and it must dominate the inner waits
    // (Convergence at the CI factor) so a hang reports what it was waiting for.
    [Fact(Timeout = 240_000)]
    public async Task AHubOwnedRead_IsReleasedBeforeTheCacheIsDisposed_AndNeverFaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Cache;
        var path = await CreateNodeAsync("hub-held");

        var faults = new ConcurrentQueue<Exception>();
        var received = new AsyncSubject<Unit>();
        var parked = new AsyncSubject<Unit>();
        var released = new AsyncSubject<bool>();

        // The production shape: a hub hosted by the mesh, beside the cache hub, holding one
        // long-lived read for its whole life and tying it to its own disposal.
        var reader = Mesh.GetHostedHub(
            new Address("reader", Guid.NewGuid().ToString("N")),
            config => config
                .AddData()
                .WithType(typeof(BusyTurn), nameof(BusyTurn))
                // A reader that is still working when the teardown reaches it — the production
                // shape is a hub mid-turn or mid-quiesce on the outgoing pod. The turn ends when the
                // cache is disposed, or at a bound: without a teardown order the cache goes first
                // and releases the turn at once; with it, the cache waits for this hub and the
                // bound releases it. Either way the hub then proceeds to its ShutDown.
                .WithHandler<BusyTurn>((_, delivery) =>
                {
                    parked.OnNext(Unit.Default);
                    parked.OnCompleted();
                    System.Threading.SpinWait.SpinUntil(() => cache.IsDisposed, BusyTurnBound);
                    return delivery.Processed();
                })
                .WithInitialization(hub =>
                {
                    var read = hub.GetMeshNodeStream(path).Subscribe(
                        node =>
                        {
                            if (node is null) return;
                            received.OnNext(Unit.Default);
                            received.OnCompleted();
                        },
                        faults.Enqueue);
                    hub.RegisterForDisposal(_ =>
                    {
                        // Recorded at the moment the hub lets go of its read: was the cache
                        // still alive then? That is the ordering this test is about.
                        released.OnNext(cache.IsDisposed);
                        released.OnCompleted();
                        read.Dispose();
                    });
                }),
            HostedHubCreation.Always);
        reader.Should().NotBeNull("precondition: the reader hub is hosted by the mesh");

        await received.Should().Within(TestTimeouts.Convergence)
            .Emit("CONTROL: the hub-owned read is live before the teardown — a read that never "
                  + "connected could not fault and would pass vacuously", ct);

        reader!.Post(new BusyTurn(), o => o.WithTarget(reader.Address));
        await parked.Should().Within(TestTimeouts.Convergence)
            .Emit("the reader is mid-turn when the teardown begins", ct);

        Mesh.Dispose();
        await Mesh.DisposalCompleted.ObserveCompletion(
            ex => FileOutput.WriteLine($"late disposal fault: {ex}"),
            ct);

        var cacheWasDisposedAtRelease = await released.Should().Within(TestTimeouts.Convergence)
            .Emit("the reader hub is hosted by the mesh, so the mesh teardown releases its read", ct);
        cache.IsDisposed.Should().BeTrue("precondition: the mesh teardown disposed the cache");

        cacheWasDisposedAtRelease.Should().BeFalse(
            "the cache hub is a dependency of every other hub the mesh hosts, so it must be torn "
            + "down only after they have finished — the reader must let go of its read while the "
            + "cache is still alive (#6078)");
        faults.Should().BeEmpty(
            "a read owned by a hub ends with that hub; the outgoing pod of every roll logged "
            + "'The mesh-node cache was disposed; the read of … ended with it' from such a reader (#6078)");
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL. A read no hub owns is still ended by the cache's disposal, with the
    /// disposal terminal — the ordering above must not have been bought by the cache no longer
    /// telling its readers (#5011).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARead_NoHubOwns_IsStillToldWhenTheMeshTearsTheCacheDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Cache;
        var path = await CreateNodeAsync("unowned");

        var held = cache.GetStream(path, Mesh.JsonSerializerOptions).Materialize().Replay();
        using var holding = held.Connect();
        await held.Where(n => n.Kind == NotificationKind.OnNext && n.Value is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the unowned read receives the node before the teardown", ct);

        Mesh.Dispose();
        await Mesh.DisposalCompleted.ObserveCompletion(
            ex => FileOutput.WriteLine($"late disposal fault: {ex}"),
            ct);

        var terminal = await held.Where(n => n.Kind != NotificationKind.OnNext)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a read that no hub owns is ended by the cache's own disposal (#5011)", ct);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>();
    }
}
