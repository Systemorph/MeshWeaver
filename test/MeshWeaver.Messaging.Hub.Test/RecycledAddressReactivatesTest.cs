using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A per-node hub that has finished disposing must not be handed out again — the address has to
/// REACTIVATE (#2025).
///
/// <para><b>The failure this pins.</b> <c>ThreadAgentIntegrationTest</c> flaked on shard 4 with</para>
///
/// <code>
/// AddressRecyclingException: GetMeshNode('ACME/ProductLaunch'): the owning hub was still
///   recycling (ShuttingDown) after 110 probes
///   ← DeliveryFailureException: Hub ACME/ProductLaunch is shutting down (RunLevel=Dead)
/// </code>
///
/// <para>110 probes at the 500 ms re-probe pace is the reader burning its ENTIRE 60 s budget, and
/// <c>RunLevel=Dead</c> says why it could never win: a Dead hub is not "still shutting down", it
/// is finished. The reader's paced re-probe loop is correct — <c>ErrorType.ShuttingDown</c> means
/// "ask again", and it asked 110 times — but every probe resolved to the SAME corpse, because a
/// hub that completed disposal was never removed from its parent's hosted-hub registry.</para>
///
/// <para><c>HostedHubsCollection.Add</c> wires exactly that removal
/// (<c>hub.RegisterForDisposal(h =&gt; messageHubs.TryRemove(h.Address, out _))</c>) — and has no
/// callers anywhere in <c>src/</c>. Every hub in practice is registered by the creation path,
/// which only does <c>messageHubs[a] = newHub</c>. So the eviction was written, and never ran.</para>
///
/// <para><b>Only DEAD is evicted.</b> A hub in <c>Quiescing</c>/<c>ShutDown</c> is still tearing
/// down and must keep being handed out: its NACK is what makes the caller re-probe, and standing
/// up a second hub on a live address would be a duplicate activation. Dead is the point at which
/// waiting can no longer help anyone.</para>
/// </summary>
public class RecycledAddressReactivatesTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact(Timeout = 30_000)]
    public async Task ADeadHostedHub_IsNeverHandedOutAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var address = new Address("recycled", "one");

        var first = host.GetHostedHub(address, HostedHubCreation.Always);
        first.Should().NotBeNull();

        first!.Dispose();
        await first.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await(ct);
        first.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the precondition for this test is a hub that has FINISHED disposing");

        var second = host.GetHostedHub(address, HostedHubCreation.Always);

        second.Should().NotBeNull(
            "a recycled address must reactivate — returning nothing would make every read on it "
            + "look like a routing NotFound, i.e. 'deleted'");
        second.Should().NotBeSameAs(first,
            "handing back the Dead hub is what made ThreadAgentIntegrationTest burn 110 probes "
            + "over its whole 60 s budget: every re-probe resolved to the same corpse, which NACKs "
            + "ShuttingDown forever, so the paced retry loop could never win (#2025)");
        second!.RunLevel.Should().NotBe(MessageHubRunLevel.Dead,
            "the successor must be a live activation, not another corpse");
    }

    /// <summary>
    /// A never-create lookup — the hot routing probe — must not resolve to a corpse either. It is
    /// the one that turns into the <c>ShuttingDown</c> NACK the reader re-probes against.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADeadHostedHub_IsNotResolvedByANeverCreateProbe()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var address = new Address("recycled", "two");

        var first = host.GetHostedHub(address, HostedHubCreation.Always);
        first!.Dispose();
        await first.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await(ct);

        var probed = host.GetHostedHub(address, HostedHubCreation.Never);

        probed.Should().NotBeSameAs(first,
            "routing must not deliver into a Dead hub — that delivery becomes the ShuttingDown "
            + "NACK the caller re-probes against, and re-probing a corpse never terminates");
    }
}
