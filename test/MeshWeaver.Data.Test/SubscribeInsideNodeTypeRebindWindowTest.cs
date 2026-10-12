using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A subscriber that attaches between a hub's self-recycle DECISION and its DisposeRequest, and a
/// subscriber the hub had already acknowledged, must BOTH end on the REPLACEMENT activation's
/// snapshot, including when the replacement's initialization gate is still closed when their
/// re-asks arrive (Systemorph/MeshWeaver#6548).
///
/// <para><b>The CI shape.</b> MeshWeaver.Plugins run 38205254016: the <c>Roland</c> hub decided a
/// NodeType rebind recycle at 05:12:41.947, a render subscribed inside the window, the
/// DisposeRequest ran at 05:12:42.017, the subscriber logged its re-ask at 05:12:42.036, and
/// nothing followed for 30 s. Information logging cannot tell a re-ask that was answered from one
/// parked behind the replacement's gate.</para>
///
/// <para><b>What this drives.</b> The owner's turn loop is parked; the self-recycle is queued
/// exactly as <c>NodeTypeRebindWatcher</c> queues it (<c>RecycleSelfAfterAcceptedWork</c>); a
/// second subscriber attaches and is OBSERVED enqueued; on release the replacement activation holds
/// its <c>DataContextInit</c> gate until both re-asks are observed deferred behind it, and only
/// then opens. Heartbeats are off for the whole test, so no heartbeat can be what recovers either
/// stream. Every SubscribeRequest the mesh routes to the owner is printed with its request-fate
/// trail whichever way the test ends, so a red run says where a re-ask stopped.</para>
/// </summary>
public class SubscribeInsideNodeTypeRebindWindowTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OwnerType = "rebind-owner";

    private static readonly Address OwnerAddress = new(OwnerType, "1");

    /// <summary>Longer than every wait below, so no heartbeat fires during the test.</summary>
    private static readonly TimeSpan NoHeartbeat = TimeSpan.FromMinutes(5);

    /// <summary>One record per owner activation; <see cref="Activation"/> names the activation that served it.</summary>
    public record Generation([property: Key] string Id, int Activation);

    /// <summary>Occupies the owner's turn loop so the test decides what runs when.</summary>
    private record Blocker;

    private sealed record RoutedSubscribe(string Id, DateTimeOffset At);

    // Instance state, never static: nothing bleeds between tests.
    private readonly ConcurrentQueue<RoutedSubscribe> routedSubscribes = new();
    private readonly AsyncSubject<Unit> blockerEntered = new();
    private readonly AsyncSubject<Unit> replacementInitialized = new();
    private int releaseBlocker;
    private int initialLoads;

    private MessageHubConfiguration ConfigureOwner(MessageHubConfiguration configuration)
        => configuration
            .WithTypes(typeof(Blocker))
            .WithHandler<Blocker>((hub, delivery) =>
            {
                blockerEntered.OnNext(Unit.Default);
                blockerEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseBlocker) == 1
                    || hub.RunLevel >= MessageHubRunLevel.ShutDown, TimeSpan.FromSeconds(60));
                return delivery.Processed();
            })
            .WithPostingIdentity(PostingIdentity.System)
            .AddData(data => data.AddSource(src => src.WithType<Generation>(t => t
                .WithKey(g => g.Id)
                // Activation 1 loads at once. Every later activation holds its DataContextInit gate
                // until the test opens it, so a re-ask that reaches it is DEFERRED behind the gate.
                .WithInitialData(() => Observable.Defer<IEnumerable<Generation>>(() =>
                {
                    var activation = Interlocked.Increment(ref initialLoads);
                    IEnumerable<Generation> loaded = new[] { new Generation("owner", activation) };
                    return activation == 1
                        ? Observable.Return(loaded)
                        : replacementInitialized.Select(_ => loaded);
                })))));

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .WithTypes(typeof(SubscribeRequest), typeof(SubscribeAck), typeof(Blocker))
            .WithRoutes(forward => forward
                // Passive: records every SubscribeRequest routed to the owner and returns the
                // delivery untouched, so the hosted-hub route below handles it as it always does.
                .RouteAddress(OwnerType, (address, delivery) =>
                {
                    if (OwnerAddress.Equals(address) && delivery.Message is SubscribeRequest)
                        routedSubscribes.Enqueue(new RoutedSubscribe($"{delivery.Id}", DateTimeOffset.UtcNow));
                    return delivery;
                })
                .RouteAddressToHostedHub(OwnerType, ConfigureOwner));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(Blocker))
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = NoHeartbeat;
                o.FirstHeartbeat = NoHeartbeat;
            }))
            .AddData(data => data.AddHubSource(OwnerAddress, ds => ds.WithType<Generation>()));

    [HubFact]
    public async Task ASubscribeInsideTheRebindWindow_AndAnAcknowledgedStream_BothReachTheReplacement()
    {
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // 1. Activation 1 serves stream A, and A is acknowledged.
            var owner = Mesh.GetHostedHub(OwnerAddress, ConfigureOwner, HostedHubCreation.Always);
            owner.Should().NotBeNull("the owner must exist before it can recycle itself");
            var clientA = GetClient();
            var workspaceA = clientA.ServiceProvider.GetRequiredService<IWorkspace>();
            await workspaceA.GetObservable<Generation>()
                .Should().Within(TestTimeouts.Convergence)
                .Match(x => x.Any(g => g.Activation == 1),
                    "stream A must be served by activation 1 before the recycle");

            // 2. Park the owner's turn loop.
            clientA.Post(new Blocker(), o => o.WithTarget(OwnerAddress));
            await blockerEntered.Should().Within(TestTimeouts.Convergence).Emit(
                "the blocker must hold the owner's turn loop");

            // 3. The recycle decision, queued exactly as NodeTypeRebindWatcher queues it.
            var teardownFinished = owner!.DisposalCompleted.FirstAsync()
                .Timeout(TestTimeouts.CrossSilo).Await(ct);
            owner.RecycleSelfAfterAcceptedWork("SubscribeInsideNodeTypeRebindWindowTest: rebind", Logger);

            // 4. Stream B attaches inside the window. "Accepted" is OBSERVED, never assumed.
            var routedBefore = routedSubscribes.Count;
            var clientB = GetClient();
            var workspaceB = clientB.ServiceProvider.GetRequiredService<IWorkspace>();
            var subscribeB = await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => routedSubscribes.Skip(routedBefore).FirstOrDefault())
                .Where(s => s is not null)
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => Mesh.DescribeRequestFate(subscribeB!.Id))
                .Where(trail => trail.Contains($"ENQUEUED@{OwnerAddress}", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);

            // 5. Release: the recycle turn runs, then the dispose, and the owner announces the end.
            Volatile.Write(ref releaseBlocker, 1);
            await teardownFinished;

            // 6. Both re-asks reach the replacement activation and wait behind its closed gate.
            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Select(_ => DeferredOnReplacement(owner))
                .Should().Within(TestTimeouts.Convergence)
                .Match(n => n >= 2,
                    "the re-asks of A and B must reach the replacement activation and wait behind its "
                    + "closed DataContextInit gate; fewer means a re-ask was lost before it reached the "
                    + "replacement, and the trails printed below say where");

            // 7. The replacement finishes initializing.
            replacementInitialized.OnNext(Unit.Default);
            replacementInitialized.OnCompleted();

            // 8. Both streams must end on the replacement's snapshot.
            await workspaceA.GetObservable<Generation>()
                .Should().Within(TestTimeouts.Convergence)
                .Match(x => x.Any(g => g.Activation >= 2),
                    "stream A's re-ask was parked behind the replacement's gate; once the gate opens the "
                    + "deferred re-ask must be answered with the replacement's snapshot");
            await workspaceB.GetObservable<Generation>()
                .Should().Within(TestTimeouts.Convergence)
                .Match(x => x.Any(g => g.Activation >= 2),
                    "stream B attached inside the rebind window; it must end on the replacement's "
                    + "snapshot exactly as an acknowledged stream does");
        }
        finally
        {
            Volatile.Write(ref releaseBlocker, 1);
            replacementInitialized.OnNext(Unit.Default);
            replacementInitialized.OnCompleted();
            Output.WriteLine($"DIAG owner initial loads: {Volatile.Read(ref initialLoads)}");
            foreach (var routed in routedSubscribes)
                Output.WriteLine(
                    $"DIAG SubscribeRequest {routed.Id} routed at {routed.At:HH:mm:ss.fff}: {Mesh.DescribeRequestFate(routed.Id)}");
        }
    }

    /// <summary>
    /// How many deliveries the REPLACEMENT activation holds behind its gates; 0 while the address
    /// still resolves to the first activation or to nothing. Queue depth is public diagnostic surface
    /// (<see cref="IMessageHub.GetPendingRequestDiagnostics"/>) with no observable behind it.
    /// </summary>
    private int DeferredOnReplacement(IMessageHub firstActivation)
    {
        var current = Mesh.GetHostedHub(OwnerAddress, HostedHubCreation.Never);
        if (current is null || ReferenceEquals(current, firstActivation))
            return 0;
        var match = Regex.Match(current.GetPendingRequestDiagnostics(), @"deferred=(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }
}
