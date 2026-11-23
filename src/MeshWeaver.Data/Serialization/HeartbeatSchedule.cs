using System;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Spreads a mirror's <c>HeartBeatEvent</c> schedule over time, so a WAVE of mirrors does not
/// beat in lock-step (Systemorph/MeshWeaver#5532).
///
/// <para><b>The shape this removes.</b> A heartbeat is <c>Observable.Timer(first, interval)</c> per
/// stream, started when the stream is opened. A burst of opens (a page that reads the attachments of
/// thousands of mail copies, a cold start, a module landing re-subscribing every mirror) therefore
/// starts thousands of timers within the same second, and because every one of them has the SAME first
/// tick and the SAME period they stay phase-locked forever: every 45 s the whole wave posts its
/// heartbeats in one instant. Each heartbeat crosses the pod's mesh hub (the router's single-threaded
/// queue), and an owner that does not serve the named stream answers each with a
/// <c>StreamEndedEvent</c> through the same queue (#6047), so one wave is 2N messages in one turn
/// window. memex-cloud, 2026-11-21T09:29:20Z: <c>mesh/…</c> crossed the aggregate watermark of 10000
/// with 27 of the 30 shed lines naming <c>HeartBeatEvent</c> and 3 <c>StreamEndedEvent</c>, on a pod
/// whose per-node hubs were being disposed by the thousand.</para>
///
/// <para><b>The fix is a spread, never a delay.</b> The first tick and the period are each pulled EARLIER
/// by a per-stream fraction taken from a stable hash of the stream id, so the cadence is never longer
/// than configured (the heartbeat exists to keep the owner's grain alive; a later one could let it
/// idle out) and the early poke still arrives inside its budget. Different periods per stream make the
/// phases drift apart on top of the spread of the first tick, so the wave decorrelates within a few
/// cycles instead of staying in step. Nothing is dropped, paced or refused.</para>
/// </summary>
internal static class HeartbeatSchedule
{
    /// <summary>How much earlier than <see cref="SyncStreamOptions.FirstHeartbeat"/> a stream's first
    /// beat may land: up to 80 %, i.e. 1-5 s for the default 5 s.</summary>
    internal const double FirstTickSpread = 0.8;

    /// <summary>How much shorter than <see cref="SyncStreamOptions.HeartbeatInterval"/> a stream's
    /// period may be: up to 25 %, i.e. 33.75-45 s for the default 45 s. Kept small because every
    /// percent of it is extra steady-state heartbeat traffic.</summary>
    internal const double PeriodSpread = 0.25;

    /// <summary>The tick for <paramref name="streamId"/>: <paramref name="tick"/> pulled earlier
    /// by up to <paramref name="spread"/> of itself. Never later than <paramref name="tick"/>.</summary>
    internal static TimeSpan Spread(string streamId, TimeSpan tick, double spread)
        => spread <= 0 ? tick : TimeSpan.FromTicks((long)(tick.Ticks * (1.0 - spread * Fraction(streamId))));

    /// <summary>A stable value in [0, 1) derived from the stream id (FNV-1a plus a final mix, so it is
    /// the same on every process and every run).</summary>
    internal static double Fraction(string streamId)
    {
        var hash = 2166136261u;
        foreach (var ch in streamId)
        {
            hash = (hash ^ ch) * 16777619u;
        }
        // Final avalanche: FNV leaves the high bits of similar ids close together.
        hash ^= hash >> 15; hash *= 2246822519u; hash ^= hash >> 13; hash *= 3266489917u; hash ^= hash >> 16;
        return hash / 4294967296.0;
    }
}
