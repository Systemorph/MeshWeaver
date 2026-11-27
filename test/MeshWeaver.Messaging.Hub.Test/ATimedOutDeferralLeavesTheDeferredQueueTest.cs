using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// MeshWeaver#8433 — a delivery whose deferral deadline has FIRED must stop occupying the deferred
/// queue, because the queue's cap (<c>MessageService.MaxDeferredMessages</c>) is a bound on LIVE
/// deferrals.
///
/// <para>The timeout failed the delivery back to its sender but left the parked turn in the queue,
/// where its only possible outcome was the <c>DEFERRAL_TIMEOUT_ALREADY_FIRED_DROPPED</c> drop. A hub
/// whose gate never opens therefore filled to the cap with such corpses at the rate of its own
/// inbound traffic — measured on a live instance: a client sync hub receiving two
/// <c>StreamEndedEvent</c>s every 45 s reached 512 after about three hours and logged
/// <c>GATE-STUCK</c>, after which every new delivery was dropped although nothing live was
/// parked.</para>
/// </summary>
public class ATimedOutDeferralLeavesTheDeferredQueueTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Filler(int N);

    // This line has no virtual timeout clock, so the budget is real and short.
    private static TimeSpan Budget => TimeSpan.FromMilliseconds(300);
    private static TimeSpan Immediate => TestTimeouts.Quick;

    private static readonly Address GatedAddress = new("never-opens", "1");

    private static int DeferredDepth(IMessageHub host)
        => Regex.Matches(host.GetDisposalDiagnostics(), @"deferred=(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();

    private static async Task WaitForDepth(IMessageHub host, Func<int, bool> predicate)
        => await Observable.Interval(TestTimeouts.Quick / 100)
            .StartWith(0L)
            .Select(_ => DeferredDepth(host))
            .Where(predicate)
            .Take(1)
            .Timeout(Immediate)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 THE PIN. Five deliveries park behind a gate that never opens; their budget expires; the
    /// queue must be EMPTY afterwards.
    ///
    /// <para><b>Fail-without:</b> the depth stays at five for ever and the wait times out.</para>
    /// </summary>
    [Fact]
    public async Task ADeliveryWhoseDeferralTimedOut_NoLongerCountsAgainstTheDeferredQueue()
    {
        var host = GetHost();
        var gated = host.GetHostedHub(
            GatedAddress,
            c => c.WithTypes(typeof(Filler))
                .WithDeferralTimeout(Budget)
                .WithInitializationGate("test-never-opens", _ => false));
        gated.Should().NotBeNull();

        const int posts = 5;
        for (var i = 0; i < posts; i++)
            host.Post(new Filler(i), o => o.WithTarget(GatedAddress));

        await WaitForDepth(host, d => d >= posts);

        await WaitForDepth(host, d => d == 0);
    }
}
