using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the two properties issue #982 is about, on the policy that decides whether a fault
/// record reaches <c>/tmp/meshweaver-test-trace.log</c> — the only diagnostic that survives a
/// CI wedge.
///
/// <list type="number">
/// <item>A late fault is never lost to an earlier storm. The old lifetime cap dropped every
/// record after the 1000th for the rest of the process, so in exactly the long, busy
/// processes where a wedge is likely, the fault next to the wedge was already discarded.</item>
/// <item>Truncation is never silent. Whatever is dropped, the file says so, in a greppable
/// line that carries the count.</item>
/// </list>
///
/// <para>Every test uses its OWN budget instance, so there is no shared state to reset and no
/// ordering dependency between tests.</para>
/// </summary>
public class FaultRecordBudgetTest
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Fact]
    public void WithinTheWindowBudget_EveryRecordIsWrittenAndNothingIsAnnounced()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 3, Window);

        for (var i = 0; i < 3; i++)
        {
            var verdict = budget.Claim(T0);
            Assert.True(verdict.WriteRecord);
            // A healthy run must stay free of sink chatter, or the notice loses its meaning.
            Assert.Null(verdict.Notice);
        }

        Assert.Equal(0, budget.SuppressedTotal);
    }

    [Fact]
    public void OverflowingTheWindow_SuppressesButAnnouncesItWithACount()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 2, Window);
        budget.Claim(T0);
        budget.Claim(T0);

        var first = budget.Claim(T0);
        Assert.False(first.WriteRecord);
        Assert.NotNull(first.Notice);
        // The words a reader needs to see to know the file is partial.
        Assert.Contains("suppressing", first.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT a complete record", first.Notice, StringComparison.Ordinal);
        Assert.Contains("1 suppressed in this process so far", first.Notice, StringComparison.Ordinal);
        // And the bound it hit, so the reader can tell a storm from a bug in the sink.
        Assert.Contains("2 fault records per 10s", first.Notice, StringComparison.Ordinal);

        Assert.Equal(1, budget.SuppressedTotal);
    }

    [Fact]
    public void AStormEmitsOneNoticePerWindow_NotOnePerDroppedRecord()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, Window);
        budget.Claim(T0);

        var announced = 0;
        for (var i = 0; i < 500; i++)
        {
            var verdict = budget.Claim(T0);
            Assert.False(verdict.WriteRecord);
            if (verdict.Notice is not null)
                announced++;
        }

        // Otherwise the notice becomes the storm it is reporting.
        Assert.Equal(1, announced);
        Assert.Equal(500, budget.SuppressedTotal);
    }

    /// <summary>
    /// THE regression for #982: a storm early in a long process must not silence the sink for
    /// the rest of it. Under the old lifetime cap this record was dropped; under a refilling
    /// window it is written.
    /// </summary>
    [Fact]
    public void ALateFaultAfterAnEarlierStorm_IsStillWritten()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, Window);
        budget.Claim(T0);
        for (var i = 0; i < 10_000; i++)
            budget.Claim(T0);

        // Ten minutes later — the wedge — a single fault fires.
        var late = budget.Claim(T0 + TimeSpan.FromMinutes(10));

        Assert.True(late.WriteRecord);
        Assert.Equal(10_000, budget.SuppressedTotal);
    }

    [Fact]
    public void ResumingAfterSuppression_StatesExactlyHowManyRecordsWereLostInTheGap()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, Window);
        budget.Claim(T0);
        budget.Claim(T0);
        budget.Claim(T0);
        budget.Claim(T0);

        var resumed = budget.Claim(T0 + Window);

        Assert.True(resumed.WriteRecord);
        Assert.NotNull(resumed.Notice);
        Assert.Contains("resuming fault records after suppressing 3 fault records",
            resumed.Notice, StringComparison.Ordinal);
        Assert.Contains("NOT a complete record", resumed.Notice, StringComparison.Ordinal);

        // The gap counter resets, so the next resume reports ITS gap, not a running total.
        budget.Claim(T0 + Window + Window);
        var afterQuiet = budget.Claim(T0 + Window + Window + Window);
        Assert.True(afterQuiet.WriteRecord);
        Assert.Null(afterQuiet.Notice);
    }

    [Fact]
    public void ASustainedStorm_KeepsAnnouncingSoTheTailIsNeverSilent()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, Window);
        var suppressionNotices = new List<string>();
        var resumeNotices = new List<string>();

        // Three back-to-back saturated windows, as a wedged process would produce.
        for (var w = 0; w < 3; w++)
        {
            var at = T0 + w * Window;
            for (var i = 0; i < 50; i++)
            {
                var verdict = budget.Claim(at);
                if (verdict.Notice is null)
                    continue;
                (verdict.WriteRecord ? resumeNotices : suppressionNotices).Add(verdict.Notice);
            }
        }

        // One suppression notice per window — a process killed at any point during the storm
        // leaves one within a window's worth of records of the tail, never a bare silence.
        Assert.Equal(3, suppressionNotices.Count);
        // Plus one per window the storm carried into, naming that gap exactly.
        Assert.Equal(2, resumeNotices.Count);
        Assert.All(resumeNotices, n =>
            Assert.Contains("after suppressing 49 fault records", n, StringComparison.Ordinal));

        // Each notice carries the running total, so the LAST one is a live lower bound on the
        // loss without the reader having to scroll back to where the storm began.
        Assert.Contains("1 suppressed in this process so far",
            suppressionNotices[0], StringComparison.Ordinal);
        Assert.Contains("99 suppressed in this process so far",
            suppressionNotices[^1], StringComparison.Ordinal);
        Assert.Equal(147, budget.SuppressedTotal);
    }

    /// <summary>
    /// A backwards clock step (NTP) must roll the window rather than freeze it open, which
    /// would starve the budget for the rest of the process — the very failure being fixed.
    /// </summary>
    [Fact]
    public void AClockStepBackwards_RollsTheWindowInsteadOfStarvingIt()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, Window);
        budget.Claim(T0);
        Assert.False(budget.Claim(T0).WriteRecord);

        Assert.True(budget.Claim(T0 - TimeSpan.FromMinutes(1)).WriteRecord);
    }
}
