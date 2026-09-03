using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A hosted hub's leaked callback must be visible on its OWNER after the child has gone.</b>
///
/// <para>Measured 2026-09-03 on a MeshWeaver.Plugins suite: 52 of 320 test classes disposed at
/// exactly the 2 s hosted-hub Quiescing budget — 104 s of wall clock — and the leak detector
/// reported ZERO. Two defects, both by construction. (1) A hosted hub removes itself from its
/// owner's <c>HostedHubsCollection</c> in its ShutDown phase, which runs after its Quiescing
/// verdict is set; the owner's <see cref="IMessageHub.AnyHubQuiescingTimedOut"/> walks the
/// collection only after its own disposal completed, i.e. after every child has departed — so a
/// child's timeout could never be seen. (2) <c>CreateMessageHub</c> starts every hosted hub from a
/// fresh configuration carrying the production 2 s budget, so a mesh whose root was configured
/// with a different budget had children draining under a different clock than their owner.</para>
///
/// <para>The shape: the CHILD is the requester. The abandoned <c>Observe</c> is registered on the
/// hosted hub's own response registry, so it is the hosted hub — not the owner — whose Quiescing
/// budget expires. That is what a leaked callback inside a per-node or per-circuit hub looks like
/// in production.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="ALeakInAHostedHub_IsReportedOnTheOwner_AfterTheChildDeparted"/>
/// reads <c>AnyHubQuiescingTimedOut() == false</c> on the owner, and
/// <see cref="AHostedHub_DrainsUnderItsOwnersQuiescingBudget"/> reads the 2 s default on the child.</para>
/// </summary>
public class HostedHubQuiesceLeakVisibilityTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record SwallowedRequest : IRequest<SwallowedResponse>;
    private record SwallowedResponse;

    private static readonly TimeSpan OwnerQuiesceTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly Address HostedAddress = new("hosted", "leaky-child");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithQuiesceTimeout(OwnerQuiesceTimeout);

    /// <summary>
    /// The child handles its OWN request and never answers it. Self-addressed on purpose: a request
    /// the child aimed at its owner is NACKed by the owner's teardown ("Post drops, incoming
    /// streams error"), which settles the callback and is exactly the case that does NOT leak. The
    /// leak that costs production its 2 s is a callback whose counterpart simply never replies.
    /// </summary>
    private static MessageHubConfiguration SwallowingChild(MessageHubConfiguration c)
        => c.WithHandler<SwallowedRequest>((_, delivery) => delivery.Processed());

    [Fact]
    public async Task ALeakInAHostedHub_IsReportedOnTheOwner_AfterTheChildDeparted()
    {
        var host = GetHost();
        var hosted = host.GetHostedHub(HostedAddress, SwallowingChild);
        var access = host.ServiceProvider.GetRequiredService<AccessService>();

        // The child asks ITSELF and is never answered: the pending callback lives in the CHILD, and
        // nothing on the teardown path will settle it for us. Posted AS the hub — the post pipeline
        // fails closed on a missing AccessContext, and a post that never left the pipeline would
        // register no callback and prove nothing.
        IDisposable abandoned;
        using (access.ImpersonateAsHub(hosted))
        {
            abandoned = hosted
                .Observe<SwallowedResponse>(new SwallowedRequest(), o => o.WithTarget(HostedAddress))
                .Subscribe(_ => { }, _ => { });
            // Let the child's action block actually process (and swallow) the request before teardown.
            await hosted.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(HostedAddress))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        }
        using var _ = abandoned;

        host.Dispose();
        await host.DisposalCompleted.FirstAsync().Await().WaitAsync(TestTimeouts.Convergence);

        host.AnyHubQuiescingTimedOut().Should().BeTrue(
            "the child's Quiescing budget expired on a callback nobody answered, and the child has "
            + "since removed itself from the owner's collection — a verdict that leaves with the child "
            + "is how 52 leaks in one suite reported as zero");
        var summary = host.GetQuiescingTimeoutSummary();
        Output.WriteLine(summary);
        summary.Should().Contain(HostedAddress.ToString(),
            "the report must NAME the hub that leaked, or the finding is unactionable");
        summary.Should().Contain(nameof(SwallowedRequest),
            "and the request whose reply never came — that is the line a developer greps for");
    }

    [Fact]
    public void AHostedHub_DrainsUnderItsOwnersQuiescingBudget()
    {
        var host = GetHost();
        var hosted = host.GetHostedHub(HostedAddress, c => c);

        hosted.Configuration.QuiesceTimeout.Should().Be(OwnerQuiesceTimeout,
            "a subtree drains under ONE policy: a child created from a fresh configuration would carry "
            + "the production default instead of the budget its owner was configured with, so the "
            + "owner's teardown would wait out a clock nobody chose");
    }

    [Fact]
    public void AHostedHubsOwnBudget_StillWins()
    {
        var host = GetHost();
        var own = TimeSpan.FromMilliseconds(700);
        var hosted = host.GetHostedHub(new Address("hosted", "opinionated"), c => c.WithQuiesceTimeout(own));

        hosted.Configuration.QuiesceTimeout.Should().Be(own,
            "the inherited budget is a seed, not a ceiling — a hub that reasons about its own drain keeps its number");
    }
}
