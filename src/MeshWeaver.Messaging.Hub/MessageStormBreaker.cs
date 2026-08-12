using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Reflection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// Universal message-storm circuit-breaker that sits at a hub's message-ingestion
/// point (<see cref="MessageService.ScheduleNotify"/>), BEFORE the single-threaded
/// turn loop does any work.
///
/// <para><b>Why it exists.</b> Every production wedge this codebase has hit is the
/// same class: an UNBOUNDED retry/resubscribe/repost loop floods one hub with the
/// SAME message — the single-threaded action block saturates and the hub (often the
/// whole portal) wedges. Examples: the ShutdownRequest repost livelock, the
/// DeliveryFailure⟷ShutdownRequest ping-pong (465k msgs), resubscribe storms, and an
/// AccessContext-denied <c>SubscribeRequest</c> retried unbounded into a Space hub.
/// We keep patching the individual loops; this is the universal backstop so no single
/// unbounded loop can saturate a hub again.</para>
///
/// <para><b>The discriminator is per-key RATE, not total volume.</b> A loop emits
/// thousands per second of ONE identical
/// <c>(sender, target, message-type, payload-identity)</c> key; real traffic is DIVERSE keys each
/// at a modest rate. The breaker keys every inbound message by that tuple and counts per-key
/// occurrences in a 1-second window. Only a single key sustaining a rate no legitimate caller can
/// reach trips — high-volume diverse traffic passes untouched because no single key crosses the
/// bar.</para>
///
/// <para><b>🚨 The payload-identity component is what makes that claim TRUE (#1200).</b> The
/// routing tuple alone is not a loop signature: the mesh funnels traffic through dispatchers where
/// ONE sender talks to ONE target with ONE message type ABOUT MANY DIFFERENT THINGS — every sync
/// stream an owner hub holds open to the shared mesh-node cache posts
/// <c>DataChangedEvent</c>/<c>RawJson</c> over that one tuple, and a bulk importer issues one
/// <c>CreateOrUpdateNodeRequest</c> per source node from one hub to the mesh hub. Keyed on the
/// tuple alone, a wide LEGITIMATE fan-out is arithmetically identical to one thing looping, so the
/// breaker crossed the bar and <see cref="ShouldDrop"/> DISCARDED real writes at ingestion — data
/// loss, not mitigation. The key therefore carries the payload's own identity
/// (<see cref="IDiagnosticKeyed.DiagnosticKey"/> — a stream id, a node path), read O(1) from the
/// already-typed message or, once a hub hop has erased the type to <c>RawJson</c>, from the
/// envelope property <c>Package</c> stamped there. A message that exposes no identity keys exactly
/// as before — the fallback is the OLD behaviour, never "always allow".</para>
///
/// <para><b>It is a LOUD diagnostic tripwire, never a silent swallow.</b> On a trip it
/// logs ONE <c>Error</c> naming the offending tuple and observed rate (so the root loop
/// still gets found and fixed), drops the looping message for a short cooldown, and
/// self-heals once the per-key rate falls back under threshold — no manual reset, no
/// <c>Clear()</c>. It does not paper over the loop; it stops the cascade from wedging
/// the hub and surfaces the culprit.</para>
///
/// <para><b>Lifetime.</b> This is an INSTANCE owned by its <see cref="MessageService"/>
/// (one per hub). It dies with the hub — no static state, nothing to clear for test
/// isolation.</para>
///
/// <para><b>Cost.</b> O(1) per message: one type check to read the payload identity (NEVER a
/// payload parse — the identity is either an already-computed property on the typed message or a
/// string already sitting on the envelope), one <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// lookup and a short per-key lock. No per-message LINQ or allocation beyond the (bounded,
/// self-evicting) per-key counter.</para>
///
/// <para><b>Visibility.</b> Public like its owner <see cref="MessageService"/> (a
/// framework type, not app API) — apps never construct or call it; <see cref="MessageService"/>
/// owns the one instance per hub. It is public only so the framework's own tests can
/// drive its rate logic with an injected clock and observe its trip signal WITHOUT an
/// assembly-wide <c>InternalsVisibleTo</c>, which would otherwise change overload
/// resolution of the internal <c>Observe(object, …)</c> for every test in the assembly.</para>
/// </summary>
public sealed class MessageStormBreaker
{
    /// <summary>
    /// Default sliding/tumbling window over which a single key's message count is measured.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default per-key trip threshold: messages of ONE identical
    /// <c>(sender, target, type, payload-identity)</c> key within a single window.
    ///
    /// <para><b>Justification.</b> A storm is a CPU-bound resubscribe/repost loop —
    /// the observed cadences were ~1ms/cycle for the DeliveryFailure⟷ShutdownRequest
    /// ping-pong (~1000/s on ONE key) and thousands of reposts on a single hub inside a
    /// dispose window. Legitimate single-key traffic is nothing like that: a
    /// request/response exchange is ONE message per key, and even an aggressive sync
    /// stream pushing change events is bounded by user-edit cadence (tens/s). 2000
    /// identical messages per second on ONE tuple is only reachable by a tight,
    /// uncontrolled loop — roughly two orders of magnitude above any real single-key
    /// burst. The bar is deliberately on per-key RATE so diverse high-volume traffic
    /// (many keys, each well under the bar) is never touched.</para>
    /// </summary>
    public const int DefaultThreshold = 2000;

    /// <summary>
    /// Default cooldown. Once a key trips, the breaker keeps dropping that key for this
    /// long before re-evaluating it. Short by design: the breaker self-heals the instant
    /// the per-key rate falls back under threshold, so the cooldown only governs how
    /// quickly a still-storming key is re-measured.
    /// </summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Default per-hub AGGREGATE inbound-depth watermark. The per-key breaker only trips when
    /// ONE (sender,target,type) tuple storms; every wedge we saw was MANY DISTINCT keys whose
    /// AGGREGATE saturated the single action block (per-key is the gap). When a hub's inbound
    /// queue depth crosses this, the block is being driven past its drain rate, so we SHED
    /// sheddable ([CanBeIgnored], non-lifecycle) traffic to keep it draining user-facing +
    /// lifecycle work. Deliberately high — no healthy hub backs its single inbox up this deep.
    /// Tests inject a low value via WithAggregateWatermark.
    /// </summary>
    public const int DefaultAggregateWatermark = 10_000;

    /// <summary>
    /// Soft cap on the number of per-key counters kept alive at once.
    ///
    /// <para><b>Why a cap exists at all.</b> Before the payload-identity component the key space
    /// was bounded by construction (peers × message types), so the opportunistic "evict on the
    /// next message for an idle key" was enough. With the payload identity in the key, a hub that
    /// sees many distinct paths/streams over its lifetime would accumulate one counter per thing
    /// it EVER saw, because a key that never receives a second message is never revisited and so
    /// never evicted. That is a leak on the hottest path in the hub.</para>
    ///
    /// <para>Crossing this cap arms a single inline sweep (at most one per window) that removes
    /// counters idle for longer than window+cooldown. The live set is then bounded by RECENT
    /// traffic — roughly rate × (window + cooldown + sweep interval) — instead of by all-time
    /// traffic, and it drains on its own with no timer, no background thread and no static state.
    /// Nothing about detection changes: a key idle for seconds is by definition not storming.</para>
    /// </summary>
    public const int DefaultMaxTrackedKeys = 10_000;

    private readonly int threshold;
    private readonly long windowTicks;
    private readonly long cooldownTicks;
    private readonly double windowSeconds;
    private readonly double cooldownSeconds;

    // Time source in "ticks" (Stopwatch ticks in production). Injectable so time-based
    // detection/self-heal logic is deterministically testable without wall-clock races.
    private readonly Func<long> nowTicks;

    private readonly ILogger logger;
    private readonly Address address;

    private readonly int aggregateWatermark;
    private long aggregateShedCount;
    private int aggregateShedding; // rising-edge guard: log once per overload episode
    private readonly Subject<AggregateShed> aggregateSheds = new();

    // Instance state — dies with the hub. ConcurrentDictionary is the sanctioned
    // exception for concurrent mutation (an instance field, never static).
    private readonly ConcurrentDictionary<StormKey, Counter> counters = new();

    // Live counter count + last-sweep stamp for the bounded key space (see DefaultMaxTrackedKeys).
    // counters.Count would take every bucket lock, so the live size is tracked alongside.
    private readonly int maxTrackedKeys;
    private int liveKeys;
    private long lastSweepTicks;

    // Surfaced for deterministic tests: emits once per trip transition (the same
    // moment the Error is logged). Hot observable; a test subscribes and asserts.
    private readonly Subject<StormTrip> trips = new();

    /// <summary>Production constructor — real Stopwatch clock and default tuning.</summary>
    public MessageStormBreaker(ILogger logger, Address address,
        int aggregateWatermark = DefaultAggregateWatermark)
        : this(logger, address, DefaultThreshold, DefaultWindow, DefaultCooldown,
            Stopwatch.GetTimestamp, Stopwatch.Frequency, aggregateWatermark)
    {
    }

    /// <summary>Number of per-key counters currently held (see <see cref="DefaultMaxTrackedKeys"/>).</summary>
    public int TrackedKeyCount => Volatile.Read(ref liveKeys);

    /// <summary>
    /// Test/tuning constructor — explicit threshold/window/cooldown and an injectable
    /// logical clock so the rate-based detection and self-heal transitions can be driven
    /// deterministically (no wall-clock sleeps). Production uses the parameterless-tuning
    /// overload above; this is how the type is made testable.
    /// </summary>
    /// <param name="logger">Logger used to emit the LOUD <c>Error</c> tripwire when a key storms or the hub overloads.</param>
    /// <param name="address">Address of the owning hub, named in the diagnostic log lines.</param>
    /// <param name="threshold">Per-key trip threshold: identical-tuple messages within one window above which the key trips.</param>
    /// <param name="window">Length of the per-key measurement window.</param>
    /// <param name="cooldown">How long a tripped key keeps being dropped before it is re-evaluated.</param>
    /// <param name="nowTicks">Injectable monotonic clock returning the current time in ticks; enables deterministic time control in tests.</param>
    /// <param name="ticksPerSecond">Ticks-per-second of the injected <paramref name="nowTicks"/> clock.</param>
    /// <param name="aggregateWatermark">Inbound-queue-depth watermark above which sheddable traffic is shed for aggregate overload.</param>
    /// <param name="maxTrackedKeys">Soft cap on live per-key counters before an inline stale sweep is armed; see <see cref="DefaultMaxTrackedKeys"/>.</param>
    public MessageStormBreaker(
        ILogger logger, Address address,
        int threshold, TimeSpan window, TimeSpan cooldown,
        Func<long> nowTicks, long ticksPerSecond,
        int aggregateWatermark = DefaultAggregateWatermark,
        int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        this.logger = logger;
        this.address = address;
        this.threshold = threshold;
        this.windowTicks = (long)(window.TotalSeconds * ticksPerSecond);
        this.cooldownTicks = (long)(cooldown.TotalSeconds * ticksPerSecond);
        this.windowSeconds = window.TotalSeconds;
        this.cooldownSeconds = cooldown.TotalSeconds;
        this.nowTicks = nowTicks;
        this.aggregateWatermark = aggregateWatermark;
        this.maxTrackedKeys = maxTrackedKeys;
    }

    /// <summary>
    /// Fires once each time a key crosses the threshold (the trip transition), carrying
    /// the offending tuple and the observed in-window count. Diagnostic only — the
    /// production signal is the <c>Error</c> log line.
    /// </summary>
    public IObservable<StormTrip> Trips => trips;

    /// <summary>
    /// Total number of trip transitions observed by this breaker instance.
    /// </summary>
    public long TripCount => Interlocked.Read(ref tripCount);
    private long tripCount;

    /// <summary>
    /// O(1) ingestion check. Returns <c>true</c> when <paramref name="delivery"/> belongs
    /// to a key that is currently storming and must be DROPPED before it reaches the turn
    /// loop. Returns <c>false</c> for all legitimate traffic.
    /// </summary>
    public bool ShouldDrop(IMessageDelivery delivery)
    {
        var message = delivery.Message;
        if (message is null)
            return false;

        // NEVER drop lifecycle / control traffic — dropping it could deadlock teardown
        // or init. These are exactly the messages NotifyAsync force-passes through every
        // initialization gate (ShutdownRequest, DisposeRequest, DeliveryFailure,
        // InitializeHubRequest, HeartBeatEvent), plus anything explicitly [CanBeIgnored]
        // (fire-and-forget control traffic with no awaiting requester). The storm-prone
        // path — SubscribeRequest, RawJson wire messages, patches, replies — is precisely
        // the NON-exempt path, which is what we want to be able to trip on.
        if (message is ShutdownRequest or DisposeRequest or DeliveryFailure
            or InitializeHubRequest or HeartBeatEvent)
            return false;

        var type = message.GetType();
        if (type.HasAttribute<CanBeIgnoredAttribute>())
            return false;

        var now = nowTicks();
        var key = new StormKey(delivery.Sender, delivery.Target, type.Name,
            ResolvePayloadKey(delivery, message));

        // Bounded key space: arm at most one inline stale sweep per window once the live set
        // crosses the cap. One volatile read on the live path when we are under it.
        if (Volatile.Read(ref liveKeys) > maxTrackedKeys)
            SweepStaleKeys(now);

        var counter = GetOrAddCounter(key, now);

        // When set inside the lock, the trip side effects (Error log + Trips.OnNext) run
        // AFTER the lock is released — never call out to a subscriber while holding the
        // per-key gate (a re-entrant or slow observer must not be able to stall the gate).
        var tripObservedCount = 0;

        lock (counter.Gate)
        {
            // Roll the window if the current one has elapsed.
            if (now - counter.WindowStartTicks >= windowTicks)
            {
                var previousCount = counter.Count;
                counter.WindowStartTicks = now;
                counter.Count = 0;

                // Self-heal: a tripped key whose just-elapsed window stayed under the
                // threshold has stopped storming — clear the trip.
                if (counter.Tripped && previousCount <= threshold)
                    counter.Tripped = false;

                // Opportunistic, O(1) eviction: an idle, non-tripped key that produced
                // nothing in the elapsed window is dropped from the dictionary so it can
                // never accumulate (a hub that sees diverse senders over its lifetime
                // would otherwise grow the map unboundedly). It re-adds itself on the
                // next message — at zero cost to the live path.
                if (previousCount == 0 && !counter.Tripped)
                {
                    RemoveCounter(key);
                    return false;
                }
            }

            counter.Count++;

            // Already tripped and still inside the cooldown → drop without re-logging.
            if (counter.Tripped)
            {
                if (now < counter.TrippedUntilTicks)
                    return true;
                // Cooldown elapsed but we're still inside a hot window: re-measure on the
                // next window roll. Keep dropping until then so a still-storming key can't
                // leak through between cooldown expiry and the roll.
                if (counter.Count > threshold)
                {
                    counter.TrippedUntilTicks = now + cooldownTicks;
                    return true;
                }
                counter.Tripped = false;
            }

            // Trip transition: this key just crossed the bar within the window.
            if (counter.Count > threshold)
            {
                counter.Tripped = true;
                counter.TrippedUntilTicks = now + cooldownTicks;
                tripObservedCount = counter.Count;
            }
        }

        if (tripObservedCount > 0)
        {
            OnTripped(key, tripObservedCount);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The payload's own identity, resolved WITHOUT deserializing anything. Two sources, in the
    /// order the type erasure happens:
    /// <list type="number">
    ///   <item>the typed message itself, when it opts in via <see cref="IDiagnosticKeyed"/> (an
    ///     already-computed stream id / node path — one interface check);</item>
    ///   <item>the envelope property that <c>MessageDelivery.Package</c> stamped, for a delivery
    ///     that crossed a hub boundary and now arrives as <see cref="RawJson"/> — the ONLY case
    ///     where the type is gone, so the property lookup is paid only there.</item>
    /// </list>
    /// <c>null</c> ⇒ this message exposes no identity, and the key degrades to exactly the
    /// pre-#1200 <c>(sender, target, type)</c> tuple. That is deliberate: the fallback is the old,
    /// stricter behaviour (fail CLOSED), never "let it through".
    /// </summary>
    private static string? ResolvePayloadKey(IMessageDelivery delivery, object message)
    {
        if (message is IDiagnosticKeyed keyed)
            return keyed.DiagnosticKey is { Length: > 0 } key ? key : null;
        if (message is not RawJson)
            return null;
        if (!delivery.Properties.TryGetValue(IDiagnosticKeyed.DeliveryProperty, out var value))
            return null;
        // In-process the value is the string we stamped; across a JSON wire hop it arrives as a
        // JsonElement, whose ToString() is the string content (same read PatchDataResponse's
        // RequestId correlation uses).
        return value switch
        {
            string s => s.Length > 0 ? s : null,
            null => null,
            _ => value.ToString() is { Length: > 0 } s ? s : null
        };
    }

    /// <summary>
    /// TryGetValue + TryAdd rather than <c>GetOrAdd</c>: the live-key count must only be
    /// incremented by the caller that actually INSERTED, and <c>GetOrAdd</c>'s factory can also run
    /// for a racing loser.
    ///
    /// <para>🚨 The retry loop is required, not defensive padding. Returning the locally-built
    /// counter when <c>TryAdd</c> loses would hand back an instance that is NOT in the dictionary
    /// whenever the winner's entry is evicted (window roll / sweep) in the gap before the re-read —
    /// this delivery would then be counted on an orphan nobody else can see, so a storming key's
    /// count and cooldown would silently reset. Loop until we either observe a published counter or
    /// publish ours; each pass strictly makes progress (find or insert).</para>
    /// </summary>
    private Counter GetOrAddCounter(StormKey key, long now)
    {
        while (true)
        {
            if (counters.TryGetValue(key, out var existing))
                return existing;
            var fresh = new Counter(now);
            if (counters.TryAdd(key, fresh))
            {
                Interlocked.Increment(ref liveKeys);
                return fresh;
            }
        }
    }

    private void RemoveCounter(StormKey key)
    {
        if (counters.TryRemove(key, out _))
            Interlocked.Decrement(ref liveKeys);
    }

    /// <summary>
    /// Inline, amortized eviction of counters that have been IDLE longer than one window plus the
    /// cooldown — by definition not storming, so removing them cannot affect detection. Armed only
    /// above <see cref="DefaultMaxTrackedKeys"/> and run at most once per window by a single
    /// thread, so the O(n) pass costs at most one traversal of a bounded map per second and the
    /// live set tracks RECENT traffic instead of growing with everything the hub ever saw.
    ///
    /// <para>🚨 Staleness is the ONLY criterion — a <c>Tripped</c> counter is swept like any other.
    /// Exempting tripped keys would break the bound this method exists to provide: a key that
    /// storms once and is never seen again would live for the hub's lifetime. It is also
    /// unnecessary, because "stale" means NO message for a full window+cooldown, which a storming
    /// key can never be (every message restamps its window) and which is strictly past the
    /// cooldown. Dropping such a counter is behaviourally identical to the self-heal its next
    /// message would perform anyway: a fresh counter starts at 1 and the message passes.</para>
    /// </summary>
    private void SweepStaleKeys(long now)
    {
        var last = Interlocked.Read(ref lastSweepTicks);
        if (now - last < windowTicks)
            return;
        // One sweeper: the loser of this exchange simply skips (the winner is already sweeping).
        if (Interlocked.CompareExchange(ref lastSweepTicks, now, last) != last)
            return;

        var staleBefore = now - (windowTicks + cooldownTicks);
        foreach (var pair in counters)
        {
            var counter = pair.Value;
            bool stale;
            lock (counter.Gate)
                stale = counter.WindowStartTicks < staleBefore;
            if (stale)
                RemoveCounter(pair.Key);
        }
    }

    private void OnTripped(StormKey key, int observedCount)
    {
        Interlocked.Increment(ref tripCount);

        // LOUD, once per trip: name the offending key — INCLUDING what the messages were about —
        // and the observed per-window rate, so the root loop is found and fixed. The payload
        // identity is the half that turns "something between these two hubs is looping" into a
        // pointer at the exact stream/path. This is a tripwire, not a silent swallow.
        logger.LogError(
            "MESSAGE STORM detected in hub {Address}: the key (sender={Sender}, target={Target}, type={Type}, "
            + "about={PayloadKey}) exceeded {Threshold} messages within {Window:0.##}s (observed {Observed} "
            + "in-window). Dropping this key at ingestion for {Cooldown:0.##}s to keep the hub responsive — "
            + "this indicates an UNBOUNDED retry/resubscribe/repost loop on that ONE thing; find and fix it.",
            address, key.Sender, key.Target, key.TypeName, key.PayloadKey ?? "(unidentified)",
            threshold, windowSeconds, observedCount, cooldownSeconds);

        try { trips.OnNext(new StormTrip(key.Sender, key.Target, key.TypeName, observedCount, key.PayloadKey)); }
        catch { /* a faulting test subscriber must never wedge the hot path */ }
    }

    /// <summary>
    /// Fires once per aggregate-shed decision (the same moment the overload Error is logged
    /// on the rising edge). Diagnostic only — the production signal is the <c>Error</c> log.
    /// </summary>
    public IObservable<AggregateShed> AggregateSheds => aggregateSheds;

    /// <summary>Total number of messages this breaker has shed for aggregate overload.</summary>
    public long AggregateShedCount => Interlocked.Read(ref aggregateShedCount);

    /// <summary>
    /// Aggregate (per-hub) overload check. The per-key <see cref="ShouldDrop"/> only fires
    /// when ONE tuple storms; this fires when the hub's TOTAL inbound queue depth crosses the
    /// watermark — the many-distinct-keys overload the per-key breaker can't see. When over
    /// the watermark we SHED sheddable ([CanBeIgnored], non-lifecycle) traffic so the single
    /// turn loop keeps draining user-facing and lifecycle work. User-facing and lifecycle
    /// messages are NEVER shed.
    /// </summary>
    public bool ShouldShedAggregate(IMessageDelivery delivery, int inboundDepth)
    {
        if (inboundDepth < aggregateWatermark)
        {
            Interlocked.Exchange(ref aggregateShedding, 0);
            return false;
        }
        if (!IsSheddable(delivery))
            return false;
        Interlocked.Increment(ref aggregateShedCount);
        if (Interlocked.Exchange(ref aggregateShedding, 1) == 0)
            logger.LogError(
                "ACTION-BLOCK OVERLOAD in hub {Address}: inbound queue depth {Depth} crossed the aggregate "
                + "watermark {Watermark}. Shedding sheddable [CanBeIgnored] traffic (e.g. {Type}) at ingestion to "
                + "keep the single-threaded turn loop draining — user-facing and lifecycle messages are NEVER shed. "
                + "An amplifying source (many distinct keys) is driving this block past its drain rate; find and fix it.",
                address, inboundDepth, aggregateWatermark, delivery.Message?.GetType().Name);
        try { aggregateSheds.OnNext(new AggregateShed(delivery.Sender, delivery.Target, delivery.Message!.GetType().Name, inboundDepth)); }
        catch { }
        return true;
    }

    private static bool IsSheddable(IMessageDelivery delivery)
    {
        var message = delivery.Message;
        if (message is null) return false;
        // The TRUE lifecycle/control set (teardown/init) must NEVER be shed — dropping it could
        // deadlock teardown or init. It is [CanBeIgnored], so it MUST be excluded FIRST, before the
        // attribute check.
        //
        // 🗑️ HeartBeatEvent is DELIBERATELY NOT in this set. A heartbeat is periodic grain keep-alive,
        // not lifecycle — and it is exactly the traffic that "piles up" when a hub is over the aggregate
        // watermark (the ONE sheddable class that used to be exempted, so it accumulated while everything
        // else shed). Shedding it under overload is SAFE: over the watermark the grain is BUSY draining a
        // deep queue (not idle), so a dropped keep-alive can't idle-deactivate it, and the next interval's
        // heartbeat resumes keep-alive once the backlog clears. "Heartbeats must not pile up; if they can't
        // be delivered under load, trash them." Below the watermark heartbeats are never shed (they pass
        // through and keep genuinely-idle grains alive).
        if (message is ShutdownRequest or DisposeRequest or DeliveryFailure
            or InitializeHubRequest)
            return false;
        return message.GetType().HasAttribute<CanBeIgnoredAttribute>();
    }

    /// <summary>
    /// Diagnostic record emitted once per aggregate-shed decision: the breaker dropped a
    /// sheddable message because the hub's total inbound queue depth crossed the watermark.
    /// Carries the shed message's routing tuple and the observed queue depth.
    /// </summary>
    /// <param name="Sender">The sender address of the shed message.</param>
    /// <param name="Target">The target address of the shed message, if any.</param>
    /// <param name="TypeName">The runtime type name of the shed message.</param>
    /// <param name="InboundDepth">The hub's inbound queue depth observed when the shed decision was made.</param>
    public readonly record struct AggregateShed(Address Sender, Address? Target, string TypeName, int InboundDepth);

    /// <summary>
    /// Completes and disposes the diagnostic subjects and clears the per-key counters.
    /// Called when the owning hub is disposed; the breaker's lifetime IS the hub's.
    /// </summary>
    public void Dispose()
    {
        try { trips.OnCompleted(); } catch { /* ignore */ }
        trips.Dispose();
        try { aggregateSheds.OnCompleted(); } catch { /* ignore */ }
        aggregateSheds.Dispose();
        counters.Clear();
        Interlocked.Exchange(ref liveKeys, 0);
    }

    /// <summary>
    /// The loop signature a storm is detected on: WHO → WHOM, WHAT KIND of message, and — the
    /// component #1200 was missing — WHICH THING it was about. Without the last one, N messages
    /// about N different things are indistinguishable from N messages about the SAME thing, and a
    /// legitimate fan-out gets dropped as a loop.
    /// </summary>
    /// <param name="Sender">Sending address.</param>
    /// <param name="Target">Target address, if any.</param>
    /// <param name="TypeName">Runtime type name of the message (or <c>RawJson</c> after a hub hop).</param>
    /// <param name="PayloadKey">The payload's own identity, or <c>null</c> when it exposes none.</param>
    private readonly record struct StormKey(
        Address Sender, Address? Target, string TypeName, string? PayloadKey);

    /// <summary>Emitted on each trip transition (carries the offending loop signature).</summary>
    /// <param name="Sender">Sending address of the storming key.</param>
    /// <param name="Target">Target address of the storming key.</param>
    /// <param name="TypeName">Message type name of the storming key.</param>
    /// <param name="ObservedCount">In-window count observed at the trip transition.</param>
    /// <param name="PayloadKey">What the storming messages were ABOUT (stream id / node path), or <c>null</c> when the type exposes no identity.</param>
    public readonly record struct StormTrip(
        Address Sender, Address? Target, string TypeName, int ObservedCount, string? PayloadKey = null);

    /// <summary>
    /// Per-key window counter. A small class (not a struct) so the
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> entry is updated in place under
    /// its own <see cref="Gate"/> lock — no read-modify-write race on the map itself.
    /// </summary>
    private sealed class Counter(long windowStartTicks)
    {
        public readonly Lock Gate = new();
        public long WindowStartTicks = windowStartTicks;
        public int Count;
        public bool Tripped;
        public long TrippedUntilTicks;
    }
}
