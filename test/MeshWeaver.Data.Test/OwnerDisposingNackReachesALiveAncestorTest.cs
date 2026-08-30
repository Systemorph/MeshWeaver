using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>#2778 — the OwnerDisposing NACK was dropped exactly when it was needed.</b>
///
/// <para>Both NACK sites in <c>DataExtensions</c> tried ONE level up and stopped: post through the
/// parent, but only while <c>parent.RunLevel &lt; DisposeHostedHubs</c>. That condition is false in
/// precisely one situation — <b>the parent is disposing its hosted children</b> — which is the very
/// moment a batch of child streams goes down with patches in flight. The caller then got exactly
/// the silence the NACK exists to replace, and waited out its whole budget.</para>
///
/// <para>The guard's rationale, from the #1362 fix, was <i>"during a whole-mesh teardown the parent
/// is past that mark too, the post is skipped, and nobody is waiting."</i> <b>"Nobody is waiting" is
/// an assumption the code cannot verify.</b> A caller whose wait outlives the start of teardown is
/// still waiting — and the reported case is not a whole-mesh teardown at all: the parent is at
/// <c>DisposeHostedHubs</c> while ITS parent is still <c>Started</c>, so one more level up was all
/// that was ever needed.</para>
///
/// <para>Measured twice, in different repos and different subsystems, with the same outcome — a
/// caller burning its full 31 s budget on a write whose owner went away:
/// <c>StreamingCellWriteByteCountTest</c> (~13 distinct disposed streams over ~5 s, then a
/// <c>TimeoutException</c>) and <c>InboundMailTriageTest</c> (<c>ADVANCE_WITHOUT_HANDOFF …
/// bound=5000ms</c>, then <c>FAILED … elapsedMs=31045</c>).</para>
///
/// <para>This test drives the SELECTION — which hub carries the answer — over a real three-level
/// hub chain, because that is the property that changed. It is deliberately not a reproduction of
/// either flake: both are in other repos, and a test that had to race a disposal to observe the fix
/// would be the kind of timing-bounded test this codebase is currently removing.</para>
/// </summary>
public class OwnerDisposingNackReachesALiveAncestorTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Probe : IRequest<ProbeAnswer>;

    private record ProbeAnswer;

    private static readonly Address Middle = new("nack", "middle");
    private static readonly Address Leaf = new("nack", "leaf");

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(Probe), typeof(ProbeAnswer));

    /// <summary>
    /// The chain: host → middle → leaf. Disposing <c>middle</c> puts it past
    /// <c>DisposeHostedHubs</c> while the host is still <c>Started</c> — the exact shape the old
    /// parent-and-stop check answered with silence.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WhenTheParentIsDisposingItsChildren_TheAnswerGoesThroughTheGrandparent()
    {
        var host = GetHost();
        var middle = host.GetHostedHub(Middle, c => c.WithTypes(typeof(Probe), typeof(ProbeAnswer)));
        var leaf = middle.GetHostedHub(Leaf, c => c.WithTypes(typeof(Probe), typeof(ProbeAnswer)));

        leaf.Should().NotBeNull();
        var request = host.Post(new Probe(), o => o.WithTarget(Leaf))!;

        // Tear the MIDDLE hub down. Its own disposal drives it past DisposeHostedHubs; the host,
        // which is where the caller lives, stays Started.
        middle.Dispose();
        await middle.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"[middle disposal] {ex}"),
            TestContext.Current.CancellationToken);

        // The premise, asserted rather than assumed: the parent is past the mark the old check
        // tested, and an ancestor above it is still able to carry an answer.
        ((int)middle.RunLevel).Should().BeGreaterThanOrEqualTo((int)MessageHubRunLevel.DisposeHostedHubs);
        ((int)host.RunLevel).Should().BeLessThan((int)MessageHubRunLevel.DisposeHostedHubs);

        var carrier = DataExtensions.PostThroughFirstLiveAncestor(
            middle, new ProbeAnswer(), request);

        carrier.Should().NotBeNull(
            "the answer must reach the caller through the first ancestor that can still post. "
            + "Parent-and-stop returned nothing here, which is the silence #2778 reports — and the "
            + "caller is still waiting, whatever the old comment assumed");
        carrier!.Address.Should().Be(host.Address,
            "the grandparent is the level that can carry it; selecting anything else means the walk "
            + "stopped early or went too far");
    }

    /// <summary>
    /// The other half, and what keeps the walk honest: when NOTHING in the chain can post, the
    /// helper must SAY so (null) rather than silently claim delivery. That is the case the caller
    /// genuinely hangs in, and the callers log it — the silence used to be indistinguishable from a
    /// NACK that was never minted.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void WhenTheWholeChainIsDown_ItReportsThatNothingCarriedIt()
    {
        var host = GetHost();
        var request = host.Post(new Probe(), o => o.WithTarget(Leaf))!;

        DataExtensions.PostThroughFirstLiveAncestor(null, new ProbeAnswer(), request)
            .Should().BeNull("an empty chain carries nothing, and must not pretend otherwise");
    }
}
