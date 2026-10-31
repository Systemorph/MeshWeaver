#nullable enable
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// When ONE layout-area open delivered its first full frame, split into the owner's hops: the
/// host was constructed, its base frame was set (<c>"Building layout…"</c>), the generator produced
/// the first control for the requested area (compose), and the stream applied the store update that
/// carries it (the owner's update queue).
///
/// <para>One latch and three timestamps per host. Nothing here schedules, subscribes or waits: each
/// mark is taken where <see cref="LayoutAreaHost"/> already passes, and the line is written by the
/// update that carries the area, exactly once. It is <c>Debug</c> by default, so a healthy open
/// writes nothing to Loki; it is <c>Information</c> only when the first full frame took longer than
/// <see cref="ThresholdKey"/> (default <see cref="DefaultThreshold"/>), which is the anomaly an
/// operator needs to see (Systemorph/MeshWeaver.Plugins#4399: an area that arrived about a second
/// after its base frame on production, and in no test rig). See <c>Doc/Architecture/LayoutAreas</c>.</para>
/// </summary>
public sealed class FirstFrameTiming
{
    /// <summary>The configuration key of the slow-first-frame bound (a <see cref="TimeSpan"/>, e.g. <c>00:00:01</c>).</summary>
    public const string ThresholdKey = "Layout:SlowFirstFrameThreshold";

    /// <summary>The bound when <see cref="ThresholdKey"/> is not configured.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(1);

    private readonly long constructedAt = Stopwatch.GetTimestamp();
    private long baseAt;
    private long composedAt;
    private int reported;

    /// <summary>The threshold read from <paramref name="configuration"/>, or <see cref="DefaultThreshold"/>
    /// when the key is absent or not a positive duration. Pure.</summary>
    /// <param name="configuration">The hub's configuration, if any.</param>
    public static TimeSpan ThresholdFrom(IConfiguration? configuration) =>
        configuration?[ThresholdKey] is { Length: > 0 } raw
        && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
        && parsed > TimeSpan.Zero
            ? parsed
            : DefaultThreshold;

    /// <summary>The base frame was set. Only the first call counts.</summary>
    public void MarkBase() => Interlocked.CompareExchange(ref baseAt, Stopwatch.GetTimestamp(), 0);

    /// <summary>The generator produced the first control for the requested area. Only the first call counts.</summary>
    public void MarkComposed() => Interlocked.CompareExchange(ref composedAt, Stopwatch.GetTimestamp(), 0);

    /// <summary>Whether the first full frame has already been reported.</summary>
    public bool Reported => Volatile.Read(ref reported) != 0;

    /// <summary>
    /// The store update carrying the requested area was applied: write the one line, once.
    /// Returns <c>false</c> when it was already written.
    /// </summary>
    /// <param name="logger">Where the line goes.</param>
    /// <param name="threshold">Above this, the line is <c>Information</c>; otherwise <c>Debug</c>.</param>
    /// <param name="hub">The owner hub's address.</param>
    /// <param name="area">The requested area.</param>
    public bool ReportDelivered(ILogger logger, TimeSpan threshold, object hub, string area)
    {
        if (Interlocked.Exchange(ref reported, 1) != 0)
            return false;
        var deliveredAt = Stopwatch.GetTimestamp();
        var total = Stopwatch.GetElapsedTime(constructedAt, deliveredAt);
        var level = total > threshold ? LogLevel.Information : LogLevel.Debug;
        if (!logger.IsEnabled(level))
            return true;
        logger.Log(level,
            "FIRST FULL FRAME of layout area '{Area}' on {Hub} after {DeliveredMs} ms: base frame at {BaseMs} ms, "
            + "first control composed at {ComposeMs} ms, then the owner's update queue applied it "
            + "(slow-first-frame threshold {ThresholdMs} ms; -1 = hop not passed)",
            area, hub,
            (long)total.TotalMilliseconds,
            Since(Volatile.Read(ref baseAt)),
            Since(Volatile.Read(ref composedAt)),
            (long)threshold.TotalMilliseconds);
        return true;
    }

    // -1 when that hop was never marked (e.g. the area arrived through a path that composes inside the update).
    private long Since(long at) => at == 0 ? -1 : (long)Stopwatch.GetElapsedTime(constructedAt, at).TotalMilliseconds;
}
