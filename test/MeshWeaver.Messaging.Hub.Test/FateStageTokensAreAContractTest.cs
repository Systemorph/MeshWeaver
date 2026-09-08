using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A <c>RequestFateLedger</c> stage token is a matched CONTRACT, not a log line.</b> Stages
/// render as <c>{stage}@{hub}</c>, and suites wait on that literal substring to know a delivery has
/// reached a hub's queue:
///
/// <code>
/// // DisposalRaceNackTest, SubscribeDuringRecycleTest
/// .Where(trail => trail.Contains($"ENQUEUED@{victimAddress}", StringComparison.Ordinal))
/// </code>
///
/// <para><b>What went wrong</b> (while instrumenting Plugins#1394). Adding queue-depth detail
/// <i>inside</i> the token — <c>fate?.Add($"ENQUEUED queue=main depth={n}")</c> — renders as
/// <c>ENQUEUED queue=main depth=1@hub</c>, which no longer contains <c>ENQUEUED@hub</c>. Both waits
/// above stopped firing and their tests timed out. **Measured: `DisposalRaceNackTest` 0/5 with the
/// token altered, 5/5 with it restored** — and the failures presented as routing stalls
/// (<c>ROUTED onTarget=False</c>, no handler entered) with nothing pointing at the stamp, which is
/// what makes this worth a guard rather than a comment.</para>
///
/// <para><b>The rule.</b> APPEND a new stage; never edit an existing token. Detail belongs in its
/// own stage (<c>QUEUED queue=main depth=N</c>), which is additive and cannot break a
/// <c>Contains</c> matcher. <c>MessageTrace</c> lines are free-form; a fate stage's LEADING TOKEN is
/// API.</para>
///
/// <para>This test pins the one token that is actually depended upon, by driving a real request
/// through a real hub and asserting the rendered trail — so a future edit to the stamp fails HERE,
/// naming the reason, instead of surfacing as an unrelated timeout in two other suites.</para>
/// </summary>
public class FateStageTokensAreAContractTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Probe : IRequest<ProbeAck>;

    private record ProbeAck;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Probe), typeof(ProbeAck))
            .WithHandler<Probe>((hub, delivery) =>
            {
                hub.Post(new ProbeAck(), o => o.ResponseFor(delivery));
                return delivery.Processed();
            });

    [Fact(Timeout = 30_000)]
    public async Task TheEnqueuedStageRendersAsTheBareTokenFollowedByTheAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        var requestId = Guid.NewGuid().ToString("N");

        // A real awaited request: the ledger only tracks ids something is waiting on, so an
        // unawaited Post would leave the trail empty and this guard would pass having checked
        // nothing.
        var response = host.Observe((object)new Probe(), o => o.WithTarget(host.Address), requestId)!
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .Await(ct);

        await response;

        var trail = host.DescribeRequestFate(requestId);
        Output.WriteLine($"fate trail: {trail}");

        // 🚨 The exact substring DisposalRaceNackTest and SubscribeDuringRecycleTest wait on.
        Assert.Contains($"ENQUEUED@{host.Address}", trail, StringComparison.Ordinal);

        // Denominator: a trail that rendered nothing at all would satisfy nothing above by
        // accident, but an empty/one-stage trail would mean the ledger stopped recording and this
        // guard would be pinning a string that no longer describes anything. Require the stages
        // that bracket it, so the assertion is about a REAL trail.
        Assert.Contains("RECEIVED", trail, StringComparison.Ordinal);
        Assert.Contains("HANDLER_ENTER", trail, StringComparison.Ordinal);
    }
}
