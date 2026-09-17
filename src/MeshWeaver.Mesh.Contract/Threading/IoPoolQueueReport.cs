using System.Globalization;
using System.Text;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// One pool's live state, as a reading taken from <see cref="IoPoolRegistry.Snapshot"/>.
///
/// <para>🚨 The <see cref="Name"/> is in here because a reading without one cannot be acted on, and
/// because the only way to get a pool's stats used to be <see cref="IoPoolRegistry.Get"/> — which
/// CREATES the pool when the name is wrong, and then honestly reports the pool it just minted as
/// idle. "There is no such pool" and "that pool is idle" came out as the same sentence, which is the
/// class of instrument MeshWeaver#1198 keeps being defeated by. A snapshot enumerates what EXISTS
/// and mints nothing, so a name that is absent is absent.</para>
/// </summary>
/// <param name="Name">The pool's registry name, e.g. <c>pg:Postgres</c>.</param>
/// <param name="MaxConcurrency">Its cap — how many operations it runs at once.</param>
/// <param name="InFlight">Operations executing right now.</param>
/// <param name="Waiting">Operations that have reached an admission point and are queued right now.</param>
/// <param name="QueueWait">How long granted work has waited for a slot, as a distribution.</param>
public readonly record struct IoPoolReading(
    string Name,
    int MaxConcurrency,
    int InFlight,
    int Waiting,
    IoPoolWaitStats QueueWait)
{
    /// <summary>
    /// A one-line reading: the cap, what is running, what is queued, and the wait distribution.
    /// </summary>
    public override string ToString() =>
        $"{Name}(cap {MaxConcurrency.ToString(CultureInfo.InvariantCulture)}): "
        + $"{InFlight.ToString(CultureInfo.InvariantCulture)} in flight, "
        + $"{Waiting.ToString(CultureInfo.InvariantCulture)} waiting — {QueueWait}";
}

/// <summary>
/// Turns an <see cref="IoPoolRegistry"/> into ONE sentence about whether anything was queued for a
/// pool slot at the moment a caller asked — the readout MeshWeaver#1198 has been open on.
///
/// <para><b>Why this exists as one place rather than at each call site.</b> The question has THREE
/// answers, not two, and collapsing any pair of them is how an unmeasured pool comes to look like
/// an idle one:</para>
/// <list type="bullet">
/// <item><description><b>Nothing was asked</b> — there is no pool registry on this hub, so no
/// reading was taken (<see cref="NotMeasured"/>).</description></item>
/// <item><description><b>It was asked, and nothing was queued</b> (<see cref="NothingQueued"/>).
/// </description></item>
/// <item><description><b>It was asked, and these pools had work queued</b> — named, with their caps
/// and depths.</description></item>
/// </list>
///
/// <para>🚨 <b>The two readings are not symmetric, and the report must not pretend they are.</b>
/// "Nothing was queued" is CONCLUSIVE about admission: work that is not waiting for a slot was not
/// starved by a cap, so a stage that made no progress was stuck somewhere the pool cannot explain.
/// "These pools had work queued" is a LEAD, never an attribution — a busy pool at the moment of a
/// failure is a coincidence until something ties the failing operation's own leaf to it. The
/// wording below says <i>had work queued</i> and never <i>caused</i>, deliberately.</para>
///
/// <para><b>Measured 2026-09-16</b> on memex.systemorph.com (the first reading ever taken of this
/// instrument): over 828 minutes of uptime the cap-1 <c>pg:Postgres</c> WRITE pool granted 2,786
/// admissions with a maximum wait of <b>205 ms</b> and both tail buckets at zero, against a 30 s
/// operation budget — while the cap-16 <c>pg-read:Postgres</c> READ pool granted 31,897,169 with a
/// MEAN of 342 ms and 48,122 admissions over a second. The pool the issue spent a month reasoning
/// about is not the contended one; see <c>Doc/Architecture/RecursiveDeleteDrain</c>.</para>
/// </summary>
public static class IoPoolQueueReport
{
    /// <summary>No registry was reachable — nothing was asked, which is not the same as a clean reading.</summary>
    public const string NotMeasured =
        "I/O pool queueing was not measured (no pool registry on this hub)";

    /// <summary>
    /// A reading WAS taken over the whole window: nothing queued at the end, and nothing admitted
    /// during it waited a second for a slot.
    /// </summary>
    public const string NothingQueued =
        "no I/O pool had work queued at that moment, and no admission during this stage waited a "
        + "second for a slot";

    /// <summary>
    /// 🚨 The WEAKER reading, used when no baseline was captured: an instant, not a window.
    ///
    /// <para>A queue DEPTH sampled once cannot exonerate a cap, and saying otherwise was this
    /// change's own first mistake: a leaf can wait most of the budget for a slot, be granted it, and
    /// only then stall — by which time the depth is zero and an instant-only reading would report
    /// the pools as innocent. That is why <see cref="Describe"/> takes a baseline and compares
    /// admissions over the window; without one it says only what it actually saw.</para>
    /// </summary>
    public const string NothingQueuedNow =
        "no I/O pool had work queued at that instant (no baseline was captured, so this says "
        + "nothing about waits earlier in the stage)";

    /// <summary>
    /// What a reading that DID find queued work begins with. Named because it is the only marker
    /// that separates the two outcomes by prefix: <see cref="NothingQueued"/> deliberately shares
    /// the phrase "work queued at that moment" with it (they answer the same question), so a caller
    /// that tests for a named pool by searching for that phrase matches BOTH and discriminates
    /// nothing — which is this issue's own failure mode reproduced in whoever reads the report.
    /// </summary>
    public const string QueuedPrefix = "I/O pools with work queued at that moment: ";

    /// <summary>
    /// How many pools the sentence names before it stops listing. A readout that pastes a hundred
    /// pool names into a failure message is unreadable, and the ones that matter are the deepest
    /// queues — so the list is ordered by depth and the remainder is COUNTED rather than dropped
    /// silently.
    /// </summary>
    private const int MaxNamed = 5;

    /// <summary>
    /// One sentence describing what, if anything, was queued for a pool slot.
    ///
    /// <para>It is called from failure paths — a timeout's own message — where a diagnostic that
    /// threw would REPLACE the failure it was describing. So it is built only from operations that
    /// cannot fail rather than wrapped in a guard that would swallow one: a null check, an
    /// enumeration of a <c>ConcurrentDictionary</c> (which never invalidates), lock-free counter
    /// reads, and string formatting. It mints nothing — see
    /// <see cref="IoPoolRegistry.Snapshot"/>.</para>
    /// </summary>
    /// <param name="registry">The mesh-scoped registry, or <c>null</c> when this hub has none.</param>
    /// <param name="baseline">
    /// A <see cref="IoPoolRegistry.Snapshot"/> taken when the window OPENED, so the reading can
    /// cover the window rather than an instant. 🚨 Pass it whenever the caller has one: without it
    /// the answer degrades to <see cref="NothingQueuedNow"/>, which cannot exonerate a cap.
    /// </param>
    public static string Describe(
        IoPoolRegistry? registry,
        IReadOnlyList<IoPoolReading>? baseline = null)
    {
        if (registry is null)
            return NotMeasured;

        var before = baseline?.ToDictionary(r => r.Name, StringComparer.Ordinal);

        // A pool is worth naming if it is queueing work NOW, or if anything it admitted DURING the
        // window had to wait a second or more for its slot. The second half is what makes the
        // negative answer mean something: a leaf that waited and was then granted has left the
        // depth at zero but has moved these buckets.
        var implicated = registry.Snapshot()
            .Select(r => (Reading: r, SlowAdmissions: SlowAdmissionsSince(r, before)))
            .Where(x => x.Reading.Waiting > 0 || x.SlowAdmissions > 0)
            .OrderByDescending(x => x.Reading.Waiting)
            .ThenByDescending(x => x.SlowAdmissions)
            .ThenBy(x => x.Reading.Name, StringComparer.Ordinal)
            .ToArray();

        if (implicated.Length == 0)
            return before is null ? NothingQueuedNow : NothingQueued;

        var text = new StringBuilder(QueuedPrefix);
        for (var i = 0; i < implicated.Length && i < MaxNamed; i++)
        {
            if (i > 0)
                text.Append("; ");
            var (reading, slow) = implicated[i];
            text.Append(reading.Name)
                .Append("(cap ")
                .Append(reading.MaxConcurrency.ToString(CultureInfo.InvariantCulture))
                .Append(") ")
                .Append(reading.Waiting.ToString(CultureInfo.InvariantCulture))
                .Append(" waiting, ")
                .Append(reading.InFlight.ToString(CultureInfo.InvariantCulture))
                .Append(" in flight");
            if (before is not null)
                text.Append(", ")
                    .Append(slow.ToString(CultureInfo.InvariantCulture))
                    .Append(" admission(s) waited >= 1 s during this stage");
            text.Append(", longest wait on this pool ever ")
                .Append(reading.QueueWait.Max.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" ms");
        }

        if (implicated.Length > MaxNamed)
            text.Append(" (+")
                .Append((implicated.Length - MaxNamed).ToString(CultureInfo.InvariantCulture))
                .Append(" more pool(s))");

        return text.ToString();
    }

    /// <summary>
    /// How many admissions on this pool waited a second or more SINCE the baseline — the two tail
    /// buckets, differenced.
    ///
    /// <para>Zero when there is no baseline for that pool, which covers both "no baseline at all"
    /// and "this pool did not exist when the window opened". The latter is right: a pool created
    /// mid-window has admitted nothing that predates the window, and its whole history is the
    /// window, so its own counters already are the difference — which is what subtracting an
    /// absent (all-zero) baseline computes.</para>
    /// </summary>
    private static long SlowAdmissionsSince(
        IoPoolReading now,
        IReadOnlyDictionary<string, IoPoolReading>? before)
    {
        if (before is null)
            return 0;
        var slowNow = now.QueueWait.UnderTenSeconds + now.QueueWait.TenSecondsOrMore;
        if (!before.TryGetValue(now.Name, out var was))
            return slowNow;
        var slowThen = was.QueueWait.UnderTenSeconds + was.QueueWait.TenSecondsOrMore;
        // Never negative: the counters only climb, but a lock-free read of one pool against a
        // lock-free read of the same pool a moment earlier is not a transaction.
        return Math.Max(0, slowNow - slowThen);
    }
}
