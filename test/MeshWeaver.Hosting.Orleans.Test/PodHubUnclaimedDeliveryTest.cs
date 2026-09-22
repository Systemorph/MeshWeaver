using System;
using System.Linq;
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
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A delivery is not an ownership claim: refusing it must not tear down the activation serving
/// the other queued deliveries. Only an owner's Attach may relocate an unclaimed activation.
/// </summary>
/// <param name="fixture">The real two-silo cluster.</param>
/// <param name="output">The test's silo diagnostics.</param>
public class PodHubUnclaimedDeliveryTest(TwoSiloCacheUpdateFixture fixture, ITestOutputHelper output)
    : IClassFixture<TwoSiloCacheUpdateFixture>, IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Pins #2299/#5177: one refusal used to call DeactivateOnIdle, forwarding all queued calls
    /// into another activation which also deactivated on its first call. Orleans exhausted its
    /// forwarding budget and replaced the authoritative refusal with invalid-activation errors.
    /// </summary>
    [Fact]
    public async Task UnclaimedDeliveryBurst_EveryCallerReceivesTheExplicitRefusal()
    {
        var address = new Address("cache", $"unclaimed-burst-{Guid.NewGuid():N}");
        var grain = Grains(0).GetGrain<IPodHubGrain>(address.ToString());

        var failures = await Task.WhenAll(Enumerable.Range(0, 32).Select(index =>
            Record.ExceptionAsync(() => grain.Deliver(Delivery(address, index.ToString()))).AsTask()))
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        failures.Should().OnlyContain(ex => ex is PodHubNotHereException,
            "every refused delivery must retain the pod hub's verdict; tearing down on the first "
            + "refusal rejects the other queued calls as invalid activations (#2299/#5177)");
        failures.Cast<PodHubNotHereException>().Should().OnlyContain(ex =>
            ex.Address == address.ToString() && !ex.Released,
            "absence of a local route is not an owner's terminal release");
    }

    /// <summary>
    /// A refusal may leave an activation on a non-owner. An actual owner appearing on the other
    /// silo must still reclaim it through Attach and receive its next delivery.
    /// </summary>
    /// <param name="hasExistingHint">Whether the caller already carries a different placement hint.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclaimedActivation_AnOwnerOnAnotherSiloCanStillClaimIt(bool hasExistingHint)
    {
        Services(0).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
        Services(1).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = output;
        var address = new Address("cache", $"unclaimed-then-owned-{Guid.NewGuid():N}");
        var grain = Grains(0).GetGrain<IPodHubGrain>(address.ToString());
        var refusal = await Assert.ThrowsAsync<PodHubNotHereException>(() =>
            grain.Deliver(Delivery(address, "before-claim"))
                .WaitAsync(Bound, TestContext.Current.CancellationToken));
        var originalSilo = Services(0).GetRequiredService<ILocalSiloDetails>().SiloAddress;
        refusal.RespondingSilo.Should().Be(originalSilo.ToParsableString(),
            "the control must leave a live activation on the other silo before ownership is claimed");

        using var received = new AsyncSubject<IMessageDelivery>();
        var routes = (OrleansRoutingService)Services(1).GetRequiredService<IMessageHub>()
            .ServiceProvider.GetRequiredService<IRoutingService>();
        var previous = RequestContext.Get(IPlacementDirector.PlacementHintKey);
        var callerHint = hasExistingHint ? originalSilo : null;
        try
        {
            if (callerHint is null)
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            else
                RequestContext.Set(IPlacementDirector.PlacementHintKey, callerHint);

            using var registration = routes.RegisterStream(address, (delivery, _) =>
            {
                received.OnNext(delivery);
                received.OnCompleted();
                return Observable.Return(delivery);
            });
            Assert.Equal(callerHint, RequestContext.Get(IPlacementDirector.PlacementHintKey));
            var claimed = routes.PodHubClaimSettled(address);
            Assert.NotNull(claimed);
            await claimed.Timeout(Bound).Await(TestContext.Current.CancellationToken);

            var result = await grain.Deliver(Delivery(address, "after-claim"))
                .WaitAsync(Bound, TestContext.Current.CancellationToken);
            result.State.Should().Be(MessageDeliveryState.Forwarded);
            var landed = await received.Timeout(Bound).Await(TestContext.Current.CancellationToken);
            landed.Message.Should().Be("after-claim");
            (await Grains(1).GetGrain<IPodHubGrain>(address.ToString()).Attach()
                .WaitAsync(Bound, TestContext.Current.CancellationToken)).Should().BeTrue(
                "a subsequent claim must reach the relocated owner");
            Assert.Equal(callerHint, RequestContext.Get(IPlacementDirector.PlacementHintKey));
        }
        finally
        {
            if (previous is null)
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            else
                RequestContext.Set(IPlacementDirector.PlacementHintKey, previous);
        }
    }

    private IServiceProvider Services(int index) =>
        ((InProcessSiloHandle)fixture.Cluster.Silos[index]).SiloHost.Services;

    private IGrainFactory Grains(int index) => Services(index).GetRequiredService<IGrainFactory>();

    /// <inheritdoc />
    public void Dispose()
    {
        Services(0).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = null;
        Services(1).GetRequiredService<TestOutputHelperAccessor>().OutputHelper = null;
    }

    private static IMessageDelivery Delivery(Address address, string payload) =>
        new MessageDelivery<string>(payload, new PostOptions(address).WithTarget(address),
            JsonSerializerOptions.Default);
}
