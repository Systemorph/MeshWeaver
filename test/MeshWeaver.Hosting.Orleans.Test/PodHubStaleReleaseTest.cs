using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The pod-hub claim is the third registry a hosted hub's teardown removes itself from, and it was
/// the last one where the removal was not value-matched (#5136, root R2 of
/// <c>Doc/Architecture/DisposedScopeAndDyingHubs</c>). The hosted-hub registry (#4741) and the local
/// route (#5159) both remove WHAT THEIR REGISTRATION REGISTERED; <c>PodHubGrain.Detach</c> stamped
/// the terminal <see cref="PodHubNotHereException.Released"/> tombstone on the address regardless of
/// who held it. Once a successor can be minted under an address while its predecessor is still
/// tearing down (<c>HostedHubsCollection.RetireCorpse</c>), the predecessor's release lands AFTER the
/// successor's claim, and a live hub was refused for ten minutes with the one verdict the owner-side
/// eviction acts on — strictly worse than the fault the retirement exists to cure.
///
/// <para><b>The discriminator is the silo's own route table.</b> <c>RegisterStream</c>'s disposal
/// removes its route, value-matched, BEFORE it releases the claim, so a live local route present when
/// the release is processed can only be a successor's. The grain is not reentrant, so that read is
/// ordered against the successor's <c>Attach</c> and every <c>Deliver</c>.</para>
///
/// <para><b>Control, measured, on the REAL grain in the real two-silo cluster.</b> With the route check
/// absent from <c>Detach</c>, <see cref="APredecessorsRelease_DoesNotTombstoneItsSuccessor"/> fails:
/// the delivery after the stale release throws <see cref="PodHubNotHereException"/> with
/// <c>Released=true</c> instead of landing on the successor. <see cref="AReleaseWithNoLiveRoute_StillTombstones"/>
/// is the other side: the #2426 tombstone must still stand when the release really is the owner's own
/// goodbye, so the fix cannot have been "never tombstone".</para>
/// </summary>
/// <param name="fixture">The real two-silo cluster.</param>
/// <param name="output">The test's silo diagnostics.</param>
public class PodHubStaleReleaseTest(TwoSiloCacheUpdateFixture fixture, ITestOutputHelper output)
    : IClassFixture<TwoSiloCacheUpdateFixture>, IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task APredecessorsRelease_DoesNotTombstoneItsSuccessor()
    {
        Services(0).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
        var address = new Address("cache", $"stale-release-{Guid.NewGuid():N}");
        var routes = (OrleansRoutingService)Services(0).GetRequiredService<IMessageHub>()
            .ServiceProvider.GetRequiredService<IRoutingService>();
        var ct = TestContext.Current.CancellationToken;

        // 1. The predecessor claims the address.
        var predecessor = routes.RegisterStream(address, (delivery, _) => Observable.Return(delivery));
        var predecessorClaim = routes.PodHubClaimSettled(address);
        Assert.NotNull(predecessorClaim);
        await predecessorClaim.Timeout(Bound).Await(ct);
        var grain = Grains(0).GetGrain<IPodHubGrain>(address.ToString());

        // 2. A successor registers under the SAME address while the predecessor still stands — the
        //    retire-and-replace shape. RegisterStream is last-writer-wins, so the live route is now
        //    the successor's; its claim re-asserts the pin.
        using var received = new AsyncSubject<IMessageDelivery>();
        using var successor = routes.RegisterStream(address, (delivery, _) =>
        {
            received.OnNext(delivery);
            received.OnCompleted();
            return Observable.Return(delivery);
        });
        var successorClaim = routes.PodHubClaimSettled(address);
        Assert.NotNull(successorClaim);
        await successorClaim.Timeout(Bound).Await(ct);

        var keepAlive = KeepAliveOf(grain, out var context);
        ((DateTime)keepAlive.GetValue(context)!).Should().Be(DateTime.MaxValue,
            "PRECONDITION: the successor's claim pinned the activation; the release below must not shorten it");

        // 3. The predecessor's registration is disposed: its route removal is value-matched (a no-op
        //    now) and it releases the claim. The disposal's own Detach is fire-and-forget, so the
        //    same call is also issued here, AWAITED, so the delivery below is ordered after it on the
        //    non-reentrant grain — a stale release that lands is what this test is about.
        predecessor.Dispose();
        await grain.Detach().WaitAsync(Bound, ct);

        // 4. The successor is live and must be served.
        var result = await grain.Deliver(Delivery(address, "to-the-successor")).WaitAsync(Bound, ct);
        result.State.Should().Be(MessageDeliveryState.Forwarded,
            "this silo holds a LIVE local route for the address — the successor's — so the release was the "
            + "predecessor's and must not have stamped the terminal Released tombstone on a live hub (#5136)");
        var landed = await received.Timeout(Bound).Await(ct);
        landed.Message.Should().Be("to-the-successor");
        ((DateTime)keepAlive.GetValue(context)!).Should().Be(DateTime.MaxValue,
            "the pin is the successor's; a stale release must not have cut it to the tombstone's lifetime");
    }

    /// <summary>
    /// The other side: with NO live route on this silo the release is the owner's own goodbye, and
    /// the #2426 tombstone must still stand — the fix is "release what you registered", never
    /// "never release".
    /// </summary>
    [Fact]
    public async Task AReleaseWithNoLiveRoute_StillTombstones()
    {
        Services(0).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
        var address = new Address("cache", $"honest-release-{Guid.NewGuid():N}");
        var routes = (OrleansRoutingService)Services(0).GetRequiredService<IMessageHub>()
            .ServiceProvider.GetRequiredService<IRoutingService>();
        var ct = TestContext.Current.CancellationToken;

        var registration = routes.RegisterStream(address, (delivery, _) => Observable.Return(delivery));
        var claim = routes.PodHubClaimSettled(address);
        Assert.NotNull(claim);
        await claim.Timeout(Bound).Await(ct);
        var grain = Grains(0).GetGrain<IPodHubGrain>(address.ToString());

        // The one registration goes: its route is removed and the claim released. Awaited copy of the
        // same Detach, for ordering — see the sibling test.
        registration.Dispose();
        await grain.Detach().WaitAsync(Bound, ct);

        var refusal = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            grain.Deliver(Delivery(address, "after-goodbye")).WaitAsync(Bound, ct));
        refusal.Released.Should().BeTrue(
            "no route on this silo means the release is the owner's own — the terminal tombstone that ends "
            + "the fan-out-to-a-corpse storm (#2426) must still be stamped");
    }

    /// <summary>
    /// The real activation's keep-alive, read the way <c>PodHubUnclaimedDeliveryTest</c> reads it:
    /// Orleans' registry is internal, and sleeping for a collection age would measure the clock.
    /// </summary>
    private PropertyInfo KeepAliveOf(IPodHubGrain grain, out IGrainContext context)
    {
        var directoryType = Assembly.Load("Orleans.Runtime").GetType("Orleans.Runtime.ActivationDirectory", true)!;
        var directory = (IEnumerable<KeyValuePair<GrainId, IGrainContext>>)Services(0).GetRequiredService(directoryType);
        context = directory.Single(entry => entry.Key.Equals(grain.GetGrainId())).Value;
        var keepAlive = context.GetType().GetProperty("KeepAliveUntil");
        Assert.NotNull(keepAlive);
        return keepAlive;
    }

    private IServiceProvider Services(int index) =>
        ((InProcessSiloHandle)fixture.Cluster.Silos[index]).SiloHost.Services;

    private IGrainFactory Grains(int index) => Services(index).GetRequiredService<IGrainFactory>();

    /// <inheritdoc />
    public void Dispose()
    {
        Services(0).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = null;
    }

    private static IMessageDelivery Delivery(Address address, string payload) =>
        new MessageDelivery<string>(payload, new PostOptions(address).WithTarget(address),
            JsonSerializerOptions.Default);
}
