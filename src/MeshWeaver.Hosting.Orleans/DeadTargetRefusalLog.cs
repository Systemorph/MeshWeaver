using System.Collections.Concurrent;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Per-address WINDOW over the router's dead-target refusal logging — the log-cost half of the
/// no-live-subscriber delivery storms (issues #2426 / #2546).
///
/// <para><b>What it bounds, and what it deliberately does NOT.</b> A refused delivery is still
/// refused, still traced, and its sender is still NACKed — every requester gets its terminal
/// answer, every time. The only thing this window changes is which refusals earn a FULL
/// error-level line: the FIRST refusal of an address (and the first after each window elapses)
/// reports at Error with complete detail; repeats inside the window report at Debug and are
/// counted, and the count is folded into the next full line. Measured shape this exists for:
/// 64,627 error lines in 30 minutes from THREE addresses (#2546) — after the window, the same
/// storm is ~3 error lines per window, each carrying the suppressed count, so the evidence
/// SHRINKS without disappearing. This is a permanent log-cost decision (Information+ ships to
/// Loki), not a debugging tweak.</para>
///
/// <para><b>Why this is not a negative cache on the DELIVERY path.</b> The subscriber probe is
/// authoritative and measured cheap (0.010&#160;ms warm — <c>RoutingGrain.HasLiveSubscriber</c>),
/// and skipping it during a window would fast-refuse a subscriber that JUST re-attached: the
/// owner-side eviction (the root-cause fix for #2426) answers a refusal by disposing the
/// server-side stream, the subscriber's latch then resubscribes, and a fast-refuse window would
/// NACK the fresh stream's first frames and re-evict — an evict/resubscribe loop manufactured by
/// the "optimisation". So every delivery keeps its probe and its NACK; only the LOG LINE is
/// windowed.</para>
///
/// <para><b>Lifetime and bounds.</b> Instance state on the routing grain — its lifetime is the
/// activation's, so a recycle at worst re-earns one full line per address. Entries are evicted
/// when the address delivers again (<see cref="Clear"/>), and inserting a new key past
/// <see cref="SweepThreshold"/> lazily sweeps entries idle for ten windows — no timer, no
/// watchdog, and the sweep only removes entries whose NEXT refusal would earn a full line
/// anyway, so it can never lose a suppressed count that is still inside its window.</para>
/// </summary>
/// <param name="window">How long one full error-level line covers an address.</param>
/// <param name="clock">Test seam; defaults to <see cref="DateTime.UtcNow"/>.</param>
internal sealed class DeadTargetRefusalLog(TimeSpan window, Func<DateTime>? clock = null)
{
    /// <summary>The default window: one full line per dead address per minute.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    internal const int SweepThreshold = 512;

    private sealed class Entry
    {
        /// <summary>False until the first full line — an explicit flag, never a sentinel tick
        /// value: arithmetic against <c>long.MinValue</c> overflows, and a test clock may
        /// legitimately start at 0.</summary>
        public bool Opened;
        public long WindowStartTicks;
        public int Suppressed;
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> now = clock ?? (static () => DateTime.UtcNow);
    private readonly long windowTicks = window.Ticks;

    /// <summary>
    /// Records one refusal of <paramref name="key"/> and answers whether the caller should emit
    /// its FULL (error-level) line — true for the first refusal of the key and for the first
    /// after each elapsed window; false inside a window, where the caller logs at Debug.
    /// </summary>
    /// <param name="key">The refused address (or the unreachable NACK sender).</param>
    /// <param name="suppressedSincePriorReport">How many refusals were suppressed to Debug since
    /// the last full line — fold it into the full line so the storm's true volume stays on the
    /// record. Zero on the very first report.</param>
    /// <returns>True when the full error-level line is owed.</returns>
    public bool ShouldReport(string key, out int suppressedSincePriorReport)
    {
        var nowTicks = now().Ticks;
        var entry = entries.GetOrAdd(key, _ =>
        {
            SweepIfCrowded(nowTicks);
            return new Entry();
        });
        lock (entry)
        {
            if (!entry.Opened || nowTicks - entry.WindowStartTicks >= windowTicks)
            {
                suppressedSincePriorReport = entry.Suppressed;
                entry.Opened = true;
                entry.Suppressed = 0;
                entry.WindowStartTicks = nowTicks;
                return true;
            }
            entry.Suppressed++;
            suppressedSincePriorReport = 0;
            return false;
        }
    }

    /// <summary>
    /// The address delivered (a live subscriber answered the probe) — drop its window so a LATER
    /// death earns a fresh full line immediately, and the dictionary never accumulates entries
    /// for healthy addresses.
    /// </summary>
    /// <param name="key">The address that just delivered.</param>
    public void Clear(string key) => entries.TryRemove(key, out _);

    /// <summary>
    /// Lazy bound on the dictionary: invoked only when a NEW key is inserted while more than
    /// <see cref="SweepThreshold"/> entries exist, it removes entries idle for ten windows.
    /// Removing an idle entry loses nothing — its window has long elapsed, so its next refusal
    /// earns a full line with or without the entry, and its suppressed count was already folded
    /// into (or owed to) a line inside its own window.
    /// </summary>
    private void SweepIfCrowded(long nowTicks)
    {
        if (entries.Count <= SweepThreshold)
            return;
        foreach (var kv in entries)
        {
            bool idle;
            lock (kv.Value)
                idle = kv.Value.Opened && nowTicks - kv.Value.WindowStartTicks >= windowTicks * 10;
            if (idle)
                entries.TryRemove(kv.Key, out _);
        }
    }
}
