using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The routing back-pressure report: one line when the in-flight route count crosses
/// <see cref="SaturationThreshold"/>, one line when it drains below half of it, and — only when a
/// leg has outlived every bound its own composition carries — one <see cref="LogLevel.Critical"/>
/// line per episode. Owned by <see cref="RoutingGrain"/>, one instance per activation.
///
/// <para>🚨 <b>This is a gauge, not a bound — and issues #1172/#1284 are what happens when a
/// diagnostic asserts more than it measures.</b> Nothing throttles, queues or refuses at the
/// threshold: the grain hands every route to the pool unconditionally and still answers
/// <c>Forwarded</c> immediately. The reason every report in prod said EXACTLY 64 is the latch below
/// — the report fires on the single increment that crosses the line, so 64 is the only value it
/// can print.</para>
///
/// <para><b>What the count measures.</b> A slot is claimed at DISPATCH (for the stream branch, at
/// ENQUEUE) and released when the leg terminates — and the leg's own bounds
/// (<see cref="RoutingGrain.ResolveTimeout"/>, <see cref="RoutingGrain.StreamPostTimeout"/>) are
/// operators INSIDE the cold observable, so they do not start until the routing pool actually gets a
/// ThreadPool thread. The count therefore mixes legs executing, legs queued behind another leg of the
/// same stream, and legs waiting for a thread. See
/// <c>Doc/Architecture/ReadingARoutingSaturationReport</c> for which number answers which question.</para>
///
/// <para>🚨 <b>THE LEVEL IS DECIDED BY THE OLDEST LEG'S AGE — nothing else on the line can say
/// "act now".</b> See <see cref="SaturationLevel"/> for the rule and the production evidence that
/// retired the previous rule (level by queue depth).</para>
/// </summary>
internal sealed class RoutingSaturationReport
{
    /// <summary>
    /// In-flight route count at which the crossing is reported. A diagnostic threshold, NOT a
    /// capacity: nothing is refused at it.
    /// </summary>
    internal const int SaturationThreshold = 64;

    /// <summary>
    /// 🚨 <b>The longest a STARTED leg can legitimately live: the sum of every bound its own
    /// composition carries.</b> The stream-routed leg is the longest — path resolution
    /// (<see cref="RoutingGrain.ResolveTimeout"/>), the subscriber probe
    /// (<see cref="RoutingGrain.SubscriberProbeTimeout"/>) and the post
    /// (<see cref="RoutingGrain.StreamPostTimeout"/>), 100 s. The grain leg is shorter: resolution,
    /// ONE Orleans response timeout (a response timeout is never re-sent — issue #1172) and at most
    /// six fast transient retries with backoff capped at 3 s.
    ///
    /// <para>So a leg OLDER than this cannot be explained by a slow destination — its own timeouts
    /// would have terminated it. It is one of exactly two things, both actionable: a leg that never
    /// terminates (a leaked slot, and the label names it), or a leg that never STARTED its timeouts
    /// because the silo had no thread to run it on (ThreadPool starvation, #5389). Every leg younger
    /// than this is a leg doing what its bounds allow.</para>
    ///
    /// <para>Derived from the leg's own constants rather than chosen, so it moves when they move. It is
    /// not a tuning knob, and raising it to make a report quieter is the band-aid the house rules
    /// forbid.</para>
    /// </summary>
    internal static readonly TimeSpan LegSelfBound =
        RoutingGrain.ResolveTimeout + RoutingGrain.SubscriberProbeTimeout + RoutingGrain.StreamPostTimeout;

    /// <summary>
    /// How often a LATCHED episode re-reads its oldest leg. Piggy-backed on dispatches the grain is
    /// making anyway — no timer, no poller — and O(in-flight legs) per read, so it is rate-limited
    /// rather than run per route. It only has to be finer than <see cref="LegSelfBound"/>: a leak is
    /// named at most this long after it becomes one.
    /// </summary>
    internal static readonly TimeSpan LatchedReviewInterval = TimeSpan.FromSeconds(10);

    private readonly string activationId;
    private readonly ILogger logger;
    private readonly Func<(int Channels, int Destinations, int Deepest, string? DeepestChannel)> queueSnapshot;
    private readonly Func<(string Label, TimeSpan Age)?>? oldestLeg;
    private readonly Func<(int Subscribing, int Waiting)> poolGauges;
    private readonly TimeProvider time;

    private int saturationReported;
    private int saturationEpisode;
    private long saturationSinceTicks;
    private long nextReviewTicks;
    private int escalatedEpisode;

    /// <summary>Creates the report for one routing activation.</summary>
    /// <param name="activationId">Short identity of the activation, stamped on every line (issue #1789).</param>
    /// <param name="logger">The routing grain's logger.</param>
    /// <param name="queueSnapshot">The ordered dispatcher's channel shape.</param>
    /// <param name="oldestLeg">The oldest in-flight leg, or <c>null</c> when the host tracks none.</param>
    /// <param name="poolGauges">The routing pool's subscribing / waiting gauges.</param>
    /// <param name="time">Clock for the episode's age; <see cref="TimeProvider.System"/> in production.</param>
    public RoutingSaturationReport(
        string activationId,
        ILogger logger,
        Func<(int Channels, int Destinations, int Deepest, string? DeepestChannel)> queueSnapshot,
        Func<(string Label, TimeSpan Age)?>? oldestLeg,
        Func<(int Subscribing, int Waiting)> poolGauges,
        TimeProvider? time = null)
    {
        this.activationId = activationId;
        this.logger = logger;
        this.queueSnapshot = queueSnapshot;
        this.oldestLeg = oldestLeg;
        this.poolGauges = poolGauges;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 🚨 <b>The level of a crossing is the OLDEST LEG's age against <see cref="LegSelfBound"/> —
    /// nothing else.</b> <see cref="LogLevel.Critical"/> is what the red-log ticketing path files an
    /// incident for, so it has to mean "act now", and only an age can say that from one sample.
    ///
    /// <para><b>Why not the queue depth (the previous rule, #5322).</b> Depth was chosen because a
    /// deepest per-channel queue of 1 or more means a leg is waiting on a leg. True — but it is sampled
    /// AT the crossing, where it is largely an artefact of the threshold, and it cannot tell a stream
    /// whose producer just posted 60 frames from a stream that is stuck. Production after #5322 shipped
    /// (every Critical quoted in #5595, #5616, #5617, #5618, #5622, #5631, #5632, #5638): deepest
    /// 63 with the oldest leg 11 ms; 62 at 343 ms; 57 at 9 ms; 54 at 81 ms; 43 at 14.8 s; 42 at
    /// 1.7 s; 1 at 7 ms and at 3.1 s — every one inside the leg's own bounds, so not one of them
    /// named a leg its own timeouts could not explain. A ticket for each.</para>
    ///
    /// <para>A leg older than <see cref="LegSelfBound"/> is the opposite reading: its own timeouts
    /// cannot explain it, so it is a leaked slot or a starved silo — both real defects, both still
    /// <see cref="LogLevel.Critical"/>. Everything else is <see cref="LogLevel.Warning"/>: still
    /// logged with every discriminator, now including WHICH channel is deepest, and still paired with
    /// the drained line — but it no longer files a ticket for a burst the silo absorbed.</para>
    ///
    /// <para>An unknown age (the host registered no <c>RoutingQuiescence</c>) is Warning: the report
    /// cannot claim what it cannot see, and the latched review below is what would name the leak.</para>
    ///
    /// <para>🚨 A permanent level decision with a cost/value argument, NOT a debugging tweak (AGENTS.md
    /// → log levels). Nothing is hidden: every crossing keeps its line.</para>
    /// </summary>
    /// <param name="oldestLegAge">Age of the oldest in-flight leg, or <c>null</c> when unknown.</param>
    /// <returns><see cref="LogLevel.Critical"/> only for a leg past its own bounds.</returns>
    internal static LogLevel SaturationLevel(TimeSpan? oldestLegAge) =>
        oldestLegAge is { } age && age >= LegSelfBound ? LogLevel.Critical : LogLevel.Warning;

    /// <summary>
    /// Called on every dispatch with the in-flight count AFTER the increment. O(1) unless it reports.
    /// </summary>
    /// <param name="inFlight">Routes in flight including this one.</param>
    /// <param name="addressPath">The dispatch target — named as the crossing leg, never as a diagnosis.</param>
    public void OnDispatched(int inFlight, string addressPath)
    {
        if (Volatile.Read(ref saturationReported) == 1)
        {
            ReviewLatchedEpisode();
            return;
        }
        if (inFlight < SaturationThreshold) return;
        if (Interlocked.Exchange(ref saturationReported, 1) == 1) return;

        var startedUtc = time.GetUtcNow().UtcDateTime;
        Volatile.Write(ref saturationSinceTicks, startedUtc.Ticks);
        Volatile.Write(ref nextReviewTicks, (startedUtc + LatchedReviewInterval).Ticks);
        var episode = Interlocked.Increment(ref saturationEpisode);
        var (channels, destinations, deepest, deepestChannel) = queueSnapshot();
        var (subscribing, waiting) = poolGauges();
        var oldest = oldestLeg?.Invoke();
        var level = SaturationLevel(oldest?.Age);
        if (level == LogLevel.Critical)
            Volatile.Write(ref escalatedEpisode, episode);

        logger.Log(level,
            "[ROUTE] Routing back-pressure [{ActivationId}#{Episode} started {StartedUtc:O}]: "
            + "{InFlight} route dispatches in flight (reporting threshold {Threshold}); "
            + "oldest leg in flight {OldestLeg} (a leg's own bounds end it within {LegSelfBoundMs} ms — "
            + "older is a leaked slot or a starved silo and is reported Critical; younger is load and is reported Warning); "
            + "ordered channels queued {Channels} over {Destinations} stream destination(s), "
            + "deepest per-channel queue {Deepest} on {DeepestChannel}, routing pool subscribing {PoolInFlight}, "
            + "waiting for a pool slot {PoolWaiting}. "
            + "Latest dispatch target {Address} — the address that happened to cross the threshold, NOT a diagnosis. "
            + "A CHANNEL is (destination, stream): a deep queue is ONE stream's frames stacking up, so the channel named "
            + "is the producer to look at when it recurs. 'waiting for a pool slot' high while 'subscribing' is below the "
            + "pool's cap is a THREAD shortage; both near zero means the legs are past their subscribe and the wait is "
            + "downstream I/O. A later line with a HIGHER episode on this activation means this episode drained; the "
            + "drained line says how long it took. See Doc/Architecture/ReadingARoutingSaturationReport.",
            activationId, episode, startedUtc, inFlight, SaturationThreshold,
            DescribeOldestLeg(oldest), (long)LegSelfBound.TotalMilliseconds,
            channels, destinations, deepest, deepestChannel ?? "no channel", subscribing, waiting, addressPath);
    }

    /// <summary>
    /// Called on every leg termination with the in-flight count AFTER the decrement. Clears the latch
    /// at half the threshold and says how long the episode lasted.
    /// </summary>
    /// <param name="inFlight">Routes still in flight.</param>
    public void OnTerminated(int inFlight)
    {
        if (inFlight > SaturationThreshold / 2) return;
        if (Interlocked.Exchange(ref saturationReported, 0) == 0) return;
        var since = Volatile.Read(ref saturationSinceTicks);
        // The episode's DURATION is the slow-vs-stuck discriminator across two lines: milliseconds is
        // a burst the silo absorbed, minutes is a leg that really was not completing.
        //
        // 🚨 WARNING, not Information and not Critical — a permanent level decision (AGENTS.md). At
        // Information the two halves of the pair ride independently-filterable channels and "did this
        // episode ever end?" becomes unanswerable (2026-08-17). At Critical the ticketing path would
        // file an incident for a RECOVERY.
        var lasted = since == 0 ? TimeSpan.Zero : time.GetUtcNow().UtcDateTime - new DateTime(since, DateTimeKind.Utc);
        logger.LogWarning(
            "[ROUTE] Routing back-pressure [{ActivationId}#{Episode}] cleared after {ElapsedMs} ms — {InFlight} route(s) in flight",
            activationId, Volatile.Read(ref saturationEpisode), (long)lasted.TotalMilliseconds, inFlight);
    }

    /// <summary>
    /// 🚨 <b>The one absence an event-driven report CAN state.</b> While an episode is latched the
    /// in-flight count has not fallen below half the threshold — and if a leg in it is past
    /// <see cref="LegSelfBound"/>, it is not coming back. Without this, an episode whose crossing
    /// happened to sample only young legs would say nothing, forever, about the leg that then leaked
    /// under it: the crossing line was the only line, and it said "load".
    ///
    /// <para>Rides the dispatches the grain is making anyway, rate-limited to
    /// <see cref="LatchedReviewInterval"/>, and escalates at most ONCE per episode — one Critical, one
    /// ticket, naming the leg. No timer, no watchdog.</para>
    /// </summary>
    private void ReviewLatchedEpisode()
    {
        if (oldestLeg is null) return;
        var now = time.GetUtcNow().UtcDateTime;
        var due = Volatile.Read(ref nextReviewTicks);
        if (now.Ticks < due) return;
        if (Interlocked.CompareExchange(ref nextReviewTicks, (now + LatchedReviewInterval).Ticks, due) != due) return;

        var episode = Volatile.Read(ref saturationEpisode);
        if (Volatile.Read(ref escalatedEpisode) == episode) return;
        var oldest = oldestLeg();
        if (SaturationLevel(oldest?.Age) != LogLevel.Critical) return;
        if (Interlocked.Exchange(ref escalatedEpisode, episode) == episode) return;

        var since = Volatile.Read(ref saturationSinceTicks);
        var (subscribing, waiting) = poolGauges();
        logger.LogCritical(
            "[ROUTE] Routing back-pressure [{ActivationId}#{Episode}] has not drained after {EpisodeMs} ms: "
            + "oldest leg in flight {OldestLeg}, past every bound its own composition carries ({LegSelfBoundMs} ms). "
            + "That leg is either not terminating (a leaked slot — the label names it) or never started its timeouts "
            + "because the silo had no thread for it (routing pool subscribing {PoolInFlight}, waiting for a pool slot {PoolWaiting}).",
            activationId, episode, (long)(now - new DateTime(since, DateTimeKind.Utc)).TotalMilliseconds,
            DescribeOldestLeg(oldest), (long)LegSelfBound.TotalMilliseconds, subscribing, waiting);
    }

    /// <summary>
    /// The oldest leg as ONE phrase. The "no reading" cases are words, never a sentinel number: an age
    /// of <c>0</c> reads as "brand new", which is the opposite of "not tracked".
    /// </summary>
    private string DescribeOldestLeg((string Label, TimeSpan Age)? oldest)
    {
        if (oldestLeg is null)
            return "not tracked on this host";
        return oldest is null
            ? "none in flight"
            : $"{(long)oldest.Value.Age.TotalMilliseconds} ms — {oldest.Value.Label}";
    }
}
