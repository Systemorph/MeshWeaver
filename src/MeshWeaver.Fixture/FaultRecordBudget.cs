using System.Globalization;

namespace MeshWeaver.Fixture;

/// <summary>
/// What <see cref="FaultRecordBudget.Claim"/> decided about one fault record.
/// </summary>
/// <param name="WriteRecord">
/// <c>true</c> when the record itself may be written; <c>false</c> when it is suppressed.
/// </param>
/// <param name="Notice">
/// A sink-level line to write when non-null — <em>before</em> the record when
/// <paramref name="WriteRecord"/> is <c>true</c>, and <em>instead of</em> it when it is
/// <c>false</c>. This is the only thing that keeps a suppressed stretch visible, so a caller
/// must never drop it.
/// </param>
public readonly record struct FaultRecordVerdict(bool WriteRecord, string? Notice);

/// <summary>
/// The write budget for
/// <see cref="TestTraceLog.AppendFault(string, Microsoft.Extensions.Logging.LogLevel, string, Exception)"/>
/// — a refilling per-window
/// allowance, not a lifetime cap.
///
/// <para><b>Why a rate and not a total (issue #982).</b> The hazard this bounds is a
/// <em>storm</em>: a resubscribe loop logging a fault per iteration writes until the runner's
/// disk fills (the 2026-07 colima ENOSPC failure mode). A storm is a property of the write
/// RATE, so a rate is what has to be bounded. The previous design bounded the lifetime TOTAL
/// (1000 records per process) instead, which is a different quantity and had a fatal
/// consequence for the sink's actual job: once a busy process passed the total, EVERY later
/// fault was dropped for the rest of the process — including the one that fires next to the
/// wedge, which is the only record anyone ever wants. Two shard-3 processes hit that ceiling in
/// every sampled run of #982's 17-run sample, so the sink was at its least useful exactly in
/// the long, busy processes where a wedge is most likely.</para>
///
/// <para>A window budget refills, so an earlier storm can never permanently silence the sink:
/// a fault arriving after a quiet stretch is always written. What it costs is fidelity
/// <em>during</em> a storm — the 101st fault in a 10-second burst is dropped — and that is the
/// right trade, because a storm's records are the same record repeated while the late,
/// isolated fault is unique.</para>
///
/// <para><b>Why not keep first-N + last-N.</b> Retaining the tail means buffering it in memory
/// and flushing at the end — but this sink exists precisely for the case where there IS no end:
/// a shard killed by its wall-clock cap (<c>exit=124</c>) or a host dying on a signal never runs
/// a flush. Anything not already on disk when the process dies is gone, so every record must be
/// written eagerly, line by line. A ring buffer would trade the one property that makes this
/// file the only diagnostic CI keeps.</para>
///
/// <para><b>Truncation is never silent.</b> Whenever suppression starts in a window the budget
/// hands back a notice line carrying the running suppressed count, and the first record written
/// after a suppressed stretch carries a second notice naming exactly how many were lost in the
/// gap. Both are tagged <c>[FAULT-BUDGET]</c>, so <c>grep FAULT-BUDGET</c> over a collected log
/// answers "is this file complete?" — and a process killed mid-storm leaves the entering notice
/// as the last thing in the file rather than an unremarkable silence.</para>
/// </summary>
public sealed class FaultRecordBudget
{
    private readonly int recordsPerWindow;
    private readonly TimeSpan window;
    private readonly object gate = new();

    private DateTime windowStart;
    private int writtenInWindow;
    private bool noticedInWindow;
    private long suppressedSinceLastRecord;
    private long suppressedTotal;

    /// <summary>
    /// Creates a budget allowing <paramref name="recordsPerWindow"/> fault records per
    /// <paramref name="window"/>.
    /// </summary>
    /// <param name="recordsPerWindow">Records allowed per window; must be positive.</param>
    /// <param name="window">The window length; must be positive.</param>
    public FaultRecordBudget(int recordsPerWindow, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordsPerWindow);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        this.recordsPerWindow = recordsPerWindow;
        this.window = window;
    }

    /// <summary>
    /// Total fault records this budget has suppressed. Monotonic for the life of the instance.
    /// </summary>
    public long SuppressedTotal
    {
        get { lock (gate) return suppressedTotal; }
    }

    /// <summary>
    /// Claims the right to write one fault record at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">
    /// The record's timestamp. Passed in rather than read from the clock so the window
    /// behaviour is deterministically testable without sleeping.
    /// </param>
    /// <returns>The verdict; see <see cref="FaultRecordVerdict"/>.</returns>
    public FaultRecordVerdict Claim(DateTime now)
    {
        // A plain lock, not an async gate: this is a synchronous file-sink leaf with no await
        // inside it, called from ILogger.Log. It is on nobody's hub scheduler.
        lock (gate)
        {
            var elapsed = now - windowStart;
            // elapsed < 0 rolls too, so a clock stepping backwards cannot freeze the window
            // open and starve the budget for the rest of the process.
            if (elapsed >= window || elapsed < TimeSpan.Zero)
            {
                windowStart = now;
                writtenInWindow = 0;
                noticedInWindow = false;
            }

            if (writtenInWindow < recordsPerWindow)
            {
                writtenInWindow++;
                if (suppressedSinceLastRecord == 0)
                    return new FaultRecordVerdict(true, null);

                var dropped = suppressedSinceLastRecord;
                suppressedSinceLastRecord = 0;
                return new FaultRecordVerdict(
                    true,
                    $"resuming fault records after suppressing {Plural(dropped)} "
                    + $"({suppressedTotal} suppressed in this process so far) — "
                    + "this log is NOT a complete record of faults");
            }

            suppressedSinceLastRecord++;
            suppressedTotal++;
            if (noticedInWindow)
                return new FaultRecordVerdict(false, null);

            // One notice per window, not one per dropped record: enough to keep the file
            // honest while it is storming, cheap enough that the notice can never become the
            // storm itself.
            noticedInWindow = true;
            return new FaultRecordVerdict(
                false,
                // Invariant so the line reads identically whatever locale the runner is in —
                // a diagnostic that changes shape by machine is a diagnostic you cannot grep.
                $"budget of {Plural(recordsPerWindow)} per "
                + $"{window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s "
                + $"exhausted — suppressing further fault records until the window rolls "
                + $"({suppressedTotal} suppressed in this process so far); "
                + "this log is NOT a complete record of faults");
        }
    }

    private static string Plural(long count) =>
        count == 1 ? "1 fault record" : $"{count} fault records";
}
