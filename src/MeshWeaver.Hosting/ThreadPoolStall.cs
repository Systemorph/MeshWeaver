using System.Globalization;
using System.Text;

namespace MeshWeaver.Hosting;

/// <summary>
/// Reads the managed stacks of this process's ThreadPool workers — the frames a stalled pool's
/// workers are standing in. Implemented where the stack reader lives (the Orleans connection's
/// <c>BlockedThreadStacks</c>, one <c>Normal</c> minidump of this process read back with ClrMD);
/// a host that registers none simply gets no <c>[POOLSTALL]</c> stacks, and the heartbeat says so.
/// </summary>
public interface IThreadPoolWorkerStacks
{
    /// <summary>
    /// One entry per ThreadPool worker thread: its frames, innermost first, one per line. Blocking
    /// (it writes and reads a dump of the process), so it is called only from the heartbeat's own
    /// dedicated thread. Throws when the dump cannot be written or read — the caller reports why.
    /// </summary>
    /// <returns>The workers' stacks.</returns>
    IReadOnlyList<string> CaptureWorkerStacks();

    /// <summary>
    /// The pool workers' stacks AND every other managed thread's, from ONE dump — the other threads are
    /// where a lock the workers wait on may be held (a timer thread, a scheduler thread), so the
    /// <c>[POOLSTALL]</c> line can name the candidate holder (MeshWeaver#5388). The default answers the
    /// workers alone, so a reader that cannot see the other threads keeps working unchanged.
    /// Blocking, and throws, exactly as <see cref="CaptureWorkerStacks"/>.
    /// </summary>
    /// <returns>The workers' stacks and the other threads' stacks.</returns>
    ThreadPoolStacks CaptureStacks() => new(CaptureWorkerStacks(), []);
}

/// <summary>
/// What one <c>[POOLSTALL]</c> capture read: every ThreadPool worker's stack, and every other managed
/// thread's. Each entry is one thread's frames, innermost first, one <c>at …</c> per line.
/// </summary>
/// <param name="Workers">The ThreadPool worker threads' stacks.</param>
/// <param name="OtherThreads">Every other live managed thread's stack — where a lock holder may stand.</param>
public sealed record ThreadPoolStacks(IReadOnlyList<string> Workers, IReadOnlyList<string> OtherThreads);

/// <summary>
/// The <c>[POOLSTALL]</c> reading (MeshWeaver#5388): when a whole heartbeat period passes in which
/// the ThreadPool finished NOTHING while work sat queued, the heartbeat captures every pool worker's
/// stack once and prints them grouped by their top frames, so the frame that held the pool is named
/// by the stall itself.
///
/// <para>🚨 <b>Why this exists.</b> The 2026-11-02 08:58Z stall on memex-cloud (21.95&#160;s and
/// 24.27&#160;s <c>LocalSiloHealthMonitor</c> readings) left a heartbeat that was ON TIME
/// (<c>gap=10.0x s</c>, so neither a machine pause nor a crash dump), GC at ~8&#160;% of wall time, and
/// 69 pool threads with <c>poolCompleted(+0)</c> and <c>poolPending</c> climbing past 20,000 for most
/// of 50&#160;s — genuine starvation. Nothing in the log could say what those 69 threads were doing:
/// the process exited cleanly after its drain, so no dump exists, and no governed action can take the
/// stacks of a live pod. A stall that cannot name its frame is re-filed, re-triaged and re-guessed
/// every time; this makes the next one name it.</para>
///
/// <para><b>Cost.</b> One <c>Normal</c> minidump (thread stacks and module data, no heap) suspends the
/// process for about a second; it runs at most once per <see cref="CaptureCooldown"/>, and only on a
/// tick whose pool already made no progress for a whole period, i.e. while the process is not serving
/// anyway.</para>
/// </summary>
public static class ThreadPoolStall
{
    /// <summary>The least time between two captures in one process. A stall that persists is one stall.</summary>
    public static readonly TimeSpan CaptureCooldown = TimeSpan.FromMinutes(30);

    /// <summary>How many of a worker's innermost frames decide which group it belongs to.</summary>
    public const int FramesPerGroup = 12;

    /// <summary>How many groups the line prints, largest first; the rest are counted.</summary>
    public const int MaxGroups = 8;

    /// <summary>
    /// How many distinct OUTER continuations of one group are printed in full. Workers sharing their
    /// innermost frames can have been called from different places — that caller is what the capture
    /// exists to name, so each distinct one is printed once, most frequent first; the rest are counted.
    /// </summary>
    public const int MaxCallersPerGroup = 4;

    /// <summary>How many candidate lock holders one lock site prints in full; the rest are counted.</summary>
    public const int MaxHoldersPerLockSite = 4;

    /// <summary>
    /// The line's budget in characters. Full stacks can be deep (an Rx chain runs to well over a
    /// hundred frames), so the line is bounded as a whole rather than per stack: groups are appended
    /// largest first until the next one would not fit, and the rest are counted. 96&#160;000 characters
    /// sits well under Loki's default 256&#160;KB line limit, and the line is written at most once per
    /// <see cref="CaptureCooldown"/> per process.
    /// </summary>
    public const int MaxLineChars = 96_000;

    /// <summary>
    /// True when the pool finished no work item in the whole period this reading spans while work was
    /// queued at BOTH ends of it — the starvation shape, as opposed to an idle pool (nothing queued) or
    /// a busy one (completions climbing). False on the first tick, which has no period to judge.
    ///
    /// <para>🚨 <b>Queued at the START too, not only now.</b> An idle pool also completes nothing in a
    /// period, and a work item queued milliseconds before the sample makes it read "pending &gt; 0" —
    /// a healthy hand-off, not a stall. Requiring work already pending at the previous tick means the
    /// queue went a whole period without the pool finishing anything, which an idle-to-queued
    /// transition cannot produce.</para>
    /// </summary>
    /// <param name="previous">The previous tick's sample, or null on the first tick.</param>
    /// <param name="reading">The tick's reading.</param>
    /// <returns>Whether the pool made no progress with work waiting for the whole period.</returns>
    public static bool MadeNoProgress(ProcessLivenessSample? previous, ProcessLivenessReading reading) =>
        previous is { ThreadPoolPending: > 0 }
        && reading.ThreadPoolCompletedDelta == 0
        && reading.Current.ThreadPoolPending > 0;

    /// <summary>
    /// Whether this tick captures: the pool made no progress, and the last capture (if any) is at
    /// least <see cref="CaptureCooldown"/> behind on the heartbeat's monotonic clock.
    /// </summary>
    /// <param name="previous">The previous tick's sample, or null on the first tick.</param>
    /// <param name="reading">The tick's reading.</param>
    /// <param name="lastCapture">The heartbeat clock at the last capture, or null if none was taken.</param>
    /// <returns>Whether to capture now.</returns>
    public static bool ShouldCapture(
        ProcessLivenessSample? previous, ProcessLivenessReading reading, TimeSpan? lastCapture) =>
        MadeNoProgress(previous, reading)
        && (lastCapture is not { } last || reading.Current.Elapsed - last >= CaptureCooldown);

    /// <summary>
    /// The <c>[POOLSTALL]</c> line: the tick's pool reading, then the workers' stacks grouped by their
    /// innermost <see cref="FramesPerGroup"/> frames, largest group first — the group holding most of
    /// the pool is the candidate for what held it. Each group prints its shared inner frames once and
    /// then every distinct OUTER continuation in full, so the caller that led the workers there is
    /// named: the 2026-11-07 capture cut every stack at 12 frames and so named the Rx gate the workers
    /// waited on but not who called into it.
    ///
    /// <para><b>Lock holders.</b> A group whose workers wait to enter a monitor names its LOCK SITE — the
    /// first frame outward of the <c>System.Threading.Monitor</c> enter frames — and then every captured
    /// thread (worker or, from <paramref name="otherThreads"/>, not) that is INSIDE that same method
    /// without waiting to enter it there. Those are the candidate holders: candidates, never proof. A
    /// <c>Normal</c> minidump carries no sync-block table (measured: ClrMD enumerates zero sync blocks
    /// from one, while a dump WITH heap names the owner but is hundreds of MB and suspends the process
    /// in proportion to the heap), and a thread inside the same method may hold another instance's gate.</para>
    /// </summary>
    /// <param name="reading">The stalled tick's reading.</param>
    /// <param name="stacks">The workers' stacks, or null when none could be captured.</param>
    /// <param name="failure">Why no stacks were captured, when <paramref name="stacks"/> is null.</param>
    /// <param name="otherThreads">The process's other managed threads' stacks, searched for lock holders.</param>
    /// <returns>The line.</returns>
    public static string Describe(
        ProcessLivenessReading reading, IReadOnlyList<string>? stacks, string? failure,
        IReadOnlyList<string>? otherThreads = null)
    {
        var current = reading.Current;
        var line = new StringBuilder(4096)
            .Append(CultureInfo.InvariantCulture, $"[POOLSTALL] tick={current.TickSeq}: the ThreadPool completed no work item in the last ")
            .Append(CultureInfo.InvariantCulture, $"{Seconds(reading.Gap ?? reading.Period)} with {current.ThreadPoolPending} queued over ")
            .Append(CultureInfo.InvariantCulture, $"{current.ThreadPoolThreads} thread(s)");

        if (stacks is null)
            return line
                .Append(" — the workers' stacks could NOT be read (")
                .Append(failure ?? "no stack reader is registered in this host")
                .Append("), so this line cannot name the frame that held the pool.").ToString();
        if (stacks.Count == 0)
            return line.Append(
                " — the capture found NO ThreadPool worker thread in the dump, so this line cannot name the frame "
                + "that held the pool.").ToString();

        var workers = stacks.Select(stack => Frames(stack).ToArray()).ToArray();
        var everyThread = workers.Concat((otherThreads ?? []).Select(stack => Frames(stack).ToArray())).ToArray();
        var groups = workers
            .GroupBy(frames => string.Join(Environment.NewLine, frames.Take(FramesPerGroup)), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        line.Append(CultureInfo.InvariantCulture, $". {stacks.Count} worker stack(s) in {groups.Length} distinct shape(s), ")
            .Append(CultureInfo.InvariantCulture, $"largest first (grouped by the innermost {FramesPerGroup} frames, each distinct caller in full; ")
            .Append(CultureInfo.InvariantCulture, $"{otherThreads?.Count ?? 0} other thread(s) searched for lock holders):");

        var printed = 0;
        foreach (var group in groups.Take(MaxGroups))
        {
            var rendered = RenderGroup(group.ToArray(), everyThread);
            if (line.Length + rendered.Length > MaxLineChars)
                break;
            line.Append(rendered);
            printed++;
        }

        if (groups.Length > printed)
            line.Append(Environment.NewLine)
                .Append(CultureInfo.InvariantCulture, $"  … {groups.Length - printed} more shape(s) holding ")
                .Append(CultureInfo.InvariantCulture, $"{groups.Skip(printed).Sum(group => group.Count())} worker(s)")
                .Append(printed < Math.Min(groups.Length, MaxGroups)
                    ? string.Create(CultureInfo.InvariantCulture, $" — not printed: the line's {MaxLineChars}-character budget is spent.")
                    : ".");
        return line.ToString();
    }

    /// <summary>One group: its shared inner frames, each distinct outer caller, and its lock site's candidate holders.</summary>
    private static string RenderGroup(IReadOnlyList<string[]> members, IReadOnlyList<string[]> everyThread)
    {
        var text = new StringBuilder();
        text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"  ×{members.Count}:");
        foreach (var frame in members[0].Take(FramesPerGroup))
            text.Append(Environment.NewLine).Append("     at ").Append(frame);

        var callers = members
            .GroupBy(frames => string.Join(Environment.NewLine, frames.Skip(FramesPerGroup)), StringComparer.Ordinal)
            .Where(caller => caller.Key.Length > 0)
            .OrderByDescending(caller => caller.Count())
            .ThenBy(caller => caller.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var caller in callers.Take(MaxCallersPerGroup))
        {
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"     -> ×{caller.Count()} called from:");
            foreach (var frame in caller.First().Skip(FramesPerGroup))
                text.Append(Environment.NewLine).Append("        at ").Append(frame);
        }
        if (callers.Length > MaxCallersPerGroup)
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture,
                $"     -> … {callers.Length - MaxCallersPerGroup} more caller(s) of {callers.Skip(MaxCallersPerGroup).Sum(c => c.Count())} worker(s).");

        if (LockSite(members[0]) is not { } site)
            return text.ToString();
        var holders = everyThread
            .Where(frames => IsInsideWithoutWaiting(frames, site))
            .GroupBy(frames => string.Join(Environment.NewLine, frames), StringComparer.Ordinal)
            .OrderByDescending(holder => holder.Count())
            .ThenBy(holder => holder.Key, StringComparer.Ordinal)
            .ToArray();
        text.Append(Environment.NewLine).Append("     LOCK waiting to enter a monitor at ").Append(site).Append(": ")
            .Append(holders.Length == 0
                ? "NO captured thread is inside that method without waiting there — the holder is outside the capture "
                  + "(in native code, or it left the method still holding the lock); a Normal dump records no monitor owner."
                : holders.Sum(h => h.Count()).ToString(CultureInfo.InvariantCulture)
                  + " thread(s) are inside it without waiting there — the candidate holder(s) "
                  + "(a Normal dump records no monitor owner, so a candidate may hold another instance's gate):");
        foreach (var holder in holders.Take(MaxHoldersPerLockSite))
        {
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"     LOCK ×{holder.Count()} candidate holder:");
            foreach (var frame in holder.First())
                text.Append(Environment.NewLine).Append("        at ").Append(frame);
        }
        if (holders.Length > MaxHoldersPerLockSite)
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture,
                $"     LOCK … {holders.Length - MaxHoldersPerLockSite} more candidate holder shape(s).");
        return text.ToString();
    }

    /// <summary>
    /// The method a waiter is trying to enter a monitor in: the first frame outward of its innermost
    /// <c>System.Threading.Monitor</c> enter frames that is not itself a <c>Monitor</c> frame. Null when
    /// the stack does not wait on a monitor within its innermost <see cref="FramesPerGroup"/> frames.
    /// </summary>
    /// <param name="frames">The stack, innermost first.</param>
    /// <returns>The lock site, or null.</returns>
    internal static string? LockSite(IReadOnlyList<string> frames)
    {
        var enter = -1;
        for (var i = 0; i < Math.Min(frames.Count, FramesPerGroup); i++)
            if (IsMonitorFrame(frames[i]) && frames[i].Contains("Enter", StringComparison.Ordinal))
                enter = i;
        if (enter < 0)
            return null;
        for (var i = enter + 1; i < frames.Count; i++)
            if (!IsMonitorFrame(frames[i]))
                return frames[i];
        return null;
    }

    /// <summary>Whether a thread is inside <paramref name="site"/> and NOT waiting to enter a monitor there.</summary>
    private static bool IsInsideWithoutWaiting(IReadOnlyList<string> frames, string site)
    {
        for (var i = 0; i < frames.Count; i++)
            if (string.Equals(frames[i], site, StringComparison.Ordinal))
                return i == 0 || !IsMonitorFrame(frames[i - 1]);
        return false;
    }

    private static bool IsMonitorFrame(string frame) =>
        frame.StartsWith("System.Threading.Monitor.", StringComparison.Ordinal);

    private static IEnumerable<string> Frames(string stack) =>
        stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(frame => frame.StartsWith("at ", StringComparison.Ordinal) ? frame[3..] : frame)
            .Where(frame => frame.Length > 0);

    private static string Seconds(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.00}s");
}
