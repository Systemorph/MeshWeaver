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
}

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
    /// the pool is the candidate for what held it.
    /// </summary>
    /// <param name="reading">The stalled tick's reading.</param>
    /// <param name="stacks">The workers' stacks, or null when none could be captured.</param>
    /// <param name="failure">Why no stacks were captured, when <paramref name="stacks"/> is null.</param>
    /// <returns>The line.</returns>
    public static string Describe(ProcessLivenessReading reading, IReadOnlyList<string>? stacks, string? failure)
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

        var groups = stacks
            .Select(stack => string.Join(Environment.NewLine, Frames(stack).Take(FramesPerGroup)))
            .GroupBy(key => key, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        line.Append(CultureInfo.InvariantCulture, $". {stacks.Count} worker stack(s) in {groups.Length} distinct shape(s), ")
            .Append(CultureInfo.InvariantCulture, $"largest first (innermost {FramesPerGroup} frames each):");
        foreach (var group in groups.Take(MaxGroups))
        {
            line.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"  ×{group.Count()}:");
            foreach (var frame in Frames(group.Key))
                line.Append(Environment.NewLine).Append("     at ").Append(frame);
        }

        if (groups.Length > MaxGroups)
            line.Append(Environment.NewLine)
                .Append(CultureInfo.InvariantCulture, $"  … {groups.Length - MaxGroups} more shape(s) holding ")
                .Append(CultureInfo.InvariantCulture, $"{groups.Skip(MaxGroups).Sum(group => group.Count())} worker(s).");
        return line.ToString();
    }

    private static IEnumerable<string> Frames(string stack) =>
        stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(frame => frame.StartsWith("at ", StringComparison.Ordinal) ? frame[3..] : frame)
            .Where(frame => frame.Length > 0);

    private static string Seconds(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.00}s");
}
