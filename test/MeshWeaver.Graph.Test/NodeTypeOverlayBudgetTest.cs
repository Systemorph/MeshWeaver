using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ORDERING INVARIANT between the NodeType slow-path budget and the hub request ceiling
/// it is nested inside.
///
/// <para><b>The bug this pins.</b> <c>NodeTypeEnrichmentHelpers.SlowPathTimeout</c> was 60 s and
/// <see cref="MessageHubConfiguration.DefaultRequestTimeout"/> is 60 s. Two equal bounds means the
/// inner one can never fire first — so the compilation-error overlay the slow path exists to
/// produce could never be delivered inside the caller's own window. The overlay's message is
/// correct and specific (<c>"NodeType 'X' is not registered (referenced by instance '…')"</c>) and
/// it had never been seen: every occurrence surfaced instead as the ceiling's generic
/// <c>"No response received in hub …"</c>, which names neither the culprit nor the remedy.</para>
///
/// <para><b>Why a test and not a comment.</b> The ceiling is in a different assembly and was, until
/// this change, invisible outside it — so the relationship existed only as a hand-copied number in
/// prose, which is precisely how it drifted into collision. A comment cannot fail. This can.</para>
///
/// <para>The same collision is recorded independently from the test side in
/// <c>OrleansDynamicCompilationTest</c>, where an equal Fact timeout preempted the framework's own
/// graceful sink and left, in its author's words, "five sessions theorised from that silence".</para>
/// </summary>
public class NodeTypeOverlayBudgetTest
{
    /// <summary>
    /// The inner bound must fire STRICTLY before the outer one. Equality is the defect.
    /// </summary>
    [Fact]
    public void SlowPathBudget_FiresStrictlyInside_TheHubRequestCeiling()
        => NodeTypeEnrichmentHelpers.SlowPathTimeout
            .Should().BeLessThan(MessageHubConfiguration.DefaultRequestTimeout,
                "the slow-path overlay is the only party that knows WHICH NodeType starved, so it "
                + "must be able to fire before the hub ceiling reports its generic timeout");

    /// <summary>
    /// Strictly-less is necessary but not sufficient: the overlay still has to be BUILT and
    /// DELIVERED after the budget expires. A one-second margin would satisfy "less than" and still
    /// lose the diagnostic in transit, so the headroom is pinned too.
    /// </summary>
    [Fact]
    public void SlowPathBudget_LeavesRealHeadroom_ForTheOverlayToBeDelivered()
    {
        var ceiling = MessageHubConfiguration.DefaultRequestTimeout;
        var headroom = ceiling - NodeTypeEnrichmentHelpers.SlowPathTimeout;

        headroom.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(ceiling.TotalSeconds / 4),
            "expiring a hair under the ceiling still loses the overlay to the caller's timeout — "
            + "the margin has to cover building the overlay hub and delivering its first frame");
    }

    /// <summary>
    /// The in-flight visibility grace must surface its live progress overlay well before the
    /// no-progress budget gives up, or a slow-but-healthy compile would be reported as a failure.
    /// </summary>
    [Fact]
    public void InFlightGrace_SurfacesProgress_BeforeTheNoProgressBudgetExpires()
        => NodeTypeEnrichmentHelpers.InFlightOverlayGrace
            .Should().BeLessThan(NodeTypeEnrichmentHelpers.SlowPathTimeout,
                "a compile that is genuinely running must show progress rather than fall out of "
                + "the no-progress budget as an error");
}
