using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the Quiescing decision table (<see cref="QuiesceVerdict"/>). The defect it guards: the drain
/// is sampled on a poll and the deadline is a separate timer, so a reply landing after the last
/// sample reached the verdict as "deadline first" with NOTHING pending — and the hub set its sticky
/// <c>QuiescingTimedOut</c> flag over an answered callback. Every test base fails its class on that
/// flag (measured: <c>StalledReadDiagnosticsTest.AReadThatSucceeds_IsUntouched</c> in
/// MeshWeaver.Plugins, whose report read "drained DURING the 2.00s timeout hand-off … Nothing leaked
/// here"). Timing cannot open that window on demand; the decision can be driven into it, so the
/// decision is what is tested.
/// </summary>
public class QuiesceVerdictTest
{
    private const int MaxRearms = 20;

    /// <summary>The race shape: the deadline won, and nothing was pending when the verdict was taken.</summary>
    [Fact]
    public void TheDeadlineFiringFirst_WithNothingPendingAtTheVerdict_IsDrained() =>
        Assert.Equal(QuiesceVerdict.Outcome.Drained,
            QuiesceVerdict.Of(pollDrained: false, pendingAtVerdict: 0, owedByShuttingDownSiblings: 0,
                rearms: 0, maxRearms: MaxRearms));

    /// <summary>The ordinary drain.</summary>
    [Fact]
    public void ThePollDraining_IsDrained() =>
        Assert.Equal(QuiesceVerdict.Outcome.Drained,
            QuiesceVerdict.Of(pollDrained: true, pendingAtVerdict: 0, owedByShuttingDownSiblings: 0,
                rearms: 0, maxRearms: MaxRearms));

    /// <summary>Negative control: a callback still pending at the verdict is the leak the flag exists for.</summary>
    [Fact]
    public void ACallbackStillPending_NobodyOwesIt_IsATimeout() =>
        Assert.Equal(QuiesceVerdict.Outcome.TimedOut,
            QuiesceVerdict.Of(pollDrained: false, pendingAtVerdict: 1, owedByShuttingDownSiblings: 0,
                rearms: 0, maxRearms: MaxRearms));

    /// <summary>A reply a shutting-down sibling owes re-arms the budget, until the cycle breaker.</summary>
    [Fact]
    public void ACallbackOwedByAShuttingDownSibling_ReArms_UntilTheCycleBreaker()
    {
        Assert.Equal(QuiesceVerdict.Outcome.ReArm,
            QuiesceVerdict.Of(pollDrained: false, pendingAtVerdict: 1, owedByShuttingDownSiblings: 1,
                rearms: MaxRearms - 1, maxRearms: MaxRearms));
        Assert.Equal(QuiesceVerdict.Outcome.TimedOut,
            QuiesceVerdict.Of(pollDrained: false, pendingAtVerdict: 1, owedByShuttingDownSiblings: 1,
                rearms: MaxRearms, maxRearms: MaxRearms));
    }
}
