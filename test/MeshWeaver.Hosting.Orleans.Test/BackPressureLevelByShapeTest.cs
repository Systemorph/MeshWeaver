using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A gauge crossing is not an incident — the level of the routing back-pressure report is decided by
/// the OLDEST LEG's age against the leg's own bounds, and by nothing else.
///
/// <para>The red-log ticketing path files an incident per <see cref="LogLevel.Critical"/> fingerprint.
/// The first rule (#5322) levelled by queue depth: <c>deepest &gt;= 1</c> stayed Critical. Every
/// Critical quoted in the tickets filed after it shipped (#5595, #5616–#5618, #5622, #5631, #5632,
/// #5638) had a deep queue AND a young oldest leg — 63 deep at 11 ms, 54 at 81 ms, 43 at 14.8 s — and
/// every episode drained. Depth sampled at the crossing cannot tell a stream whose producer just posted
/// 60 frames from a stuck one; an age past the leg's own bounds can.</para>
/// </summary>
public class BackPressureLevelByShapeTest
{
    /// <summary>
    /// 🚨 The production crossings, verbatim (oldest-leg age in ms). Every one was filed as a Critical
    /// ticket under the depth rule; every one is a leg inside its own bounds, so none is "act now".
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(81)]
    [InlineData(343)]
    [InlineData(1715)]
    [InlineData(3068)]
    [InlineData(14785)]
    public void EveryLegInsideItsOwnBounds_IsLoad_AndFilesNoIncident(int oldestLegMs)
        => Assert.Equal(LogLevel.Warning,
            RoutingSaturationReport.SaturationLevel(TimeSpan.FromMilliseconds(oldestLegMs)));

    /// <summary>
    /// 🚨 The shape that must NOT be softened: a leg older than every bound its own composition carries
    /// is a leaked slot or a starved silo. The boundary is included deliberately — it is the edge that
    /// decides, and the negative control for the theory above.
    /// </summary>
    [Fact]
    public void ALegPastItsOwnBounds_IsCritical_AndOneTickBelowIsNot()
    {
        Assert.Equal(LogLevel.Critical, RoutingSaturationReport.SaturationLevel(RoutingSaturationReport.LegSelfBound));
        Assert.Equal(LogLevel.Critical, RoutingSaturationReport.SaturationLevel(TimeSpan.FromMinutes(20)));
        Assert.Equal(LogLevel.Warning,
            RoutingSaturationReport.SaturationLevel(RoutingSaturationReport.LegSelfBound - TimeSpan.FromTicks(1)));
    }

    /// <summary>
    /// The bound is DERIVED from the leg's own timeouts, never chosen — so it moves when they move and
    /// cannot be "raised to make it quieter" without changing what a leg is allowed to do.
    /// </summary>
    [Fact]
    public void TheBound_IsTheSumOfTheLegsOwnTimeouts()
        => Assert.Equal(
            RoutingGrain.ResolveTimeout + RoutingGrain.SubscriberProbeTimeout + RoutingGrain.StreamPostTimeout,
            RoutingSaturationReport.LegSelfBound);

    /// <summary>An unknown age cannot claim a leak it cannot see.</summary>
    [Fact]
    public void AnUnknownAge_IsNotTreatedAsALeak()
        => Assert.Equal(LogLevel.Warning, RoutingSaturationReport.SaturationLevel(null));

    /// <summary>
    /// The production burst end to end: a 63-deep channel with an 11 ms oldest leg crosses, drains,
    /// and leaves exactly two Warning lines — the crossing (naming the deep channel) and its clear.
    /// </summary>
    [Fact]
    public void AProductionBurst_CrossesAndDrains_AtWarning_AndNamesTheDeepChannel()
    {
        var rig = new Rig { Oldest = ("stream-routed → cache/x (delivery d1)", TimeSpan.FromMilliseconds(11)) };

        for (var i = 1; i <= RoutingSaturationReport.SaturationThreshold; i++)
            rig.Report.OnDispatched(i, "cache/x");
        rig.Report.OnTerminated(RoutingSaturationReport.SaturationThreshold / 2);

        Assert.Equal(2, rig.Logger.Records.Count);
        Assert.All(rig.Logger.Records, r => Assert.Equal(LogLevel.Warning, r.Level));
        Assert.Contains("deepest per-channel queue 63 on cache/x [sync/abc]", rig.Logger.Records[0].Message);
        Assert.Contains("cleared after", rig.Logger.Records[1].Message);
    }

    /// <summary>
    /// 🚨 The one absence an event-driven report CAN state: an episode that crossed on young legs and
    /// then kept a leg past its bounds. Before this, the crossing line ("load") was the only line the
    /// episode would ever produce. Exactly ONE Critical, on a dispatch after the review interval, and
    /// never a second one for the same episode.
    /// </summary>
    [Fact]
    public void ALatchedEpisodeWhoseLegOutlivesItsBounds_EscalatesExactlyOnce()
    {
        var rig = new Rig { Oldest = ("dispatch → Store (delivery d2)", TimeSpan.FromMilliseconds(20)) };
        rig.Report.OnDispatched(RoutingSaturationReport.SaturationThreshold, "Store");
        Assert.Equal(LogLevel.Warning, Assert.Single(rig.Logger.Records).Level);

        // The leg is now past its bounds, but the review is rate-limited: nothing is read before the interval.
        rig.Oldest = ("dispatch → Store (delivery d2)", RoutingSaturationReport.LegSelfBound + TimeSpan.FromSeconds(1));
        rig.Report.OnDispatched(40, "Store");
        Assert.Single(rig.Logger.Records);
        Assert.Equal(1, rig.OldestReads);

        rig.Clock.Advance(RoutingSaturationReport.LatchedReviewInterval);
        rig.Report.OnDispatched(41, "Store");
        rig.Clock.Advance(RoutingSaturationReport.LatchedReviewInterval);
        rig.Report.OnDispatched(42, "Store");

        var critical = Assert.Single(rig.Logger.Records, r => r.Level == LogLevel.Critical);
        Assert.Contains("has not drained", critical.Message);
        Assert.Contains("dispatch → Store (delivery d2)", critical.Message);
    }

    /// <summary>
    /// Negative control for the escalation: a latched episode whose legs stay young is load however long
    /// it lasts, and reviewing it files nothing.
    /// </summary>
    [Fact]
    public void ALatchedEpisodeOfYoungLegs_NeverEscalates()
    {
        var rig = new Rig { Oldest = ("dispatch → Store (delivery d3)", TimeSpan.FromMilliseconds(50)) };
        rig.Report.OnDispatched(RoutingSaturationReport.SaturationThreshold, "Store");
        for (var i = 0; i < 20; i++)
        {
            rig.Clock.Advance(RoutingSaturationReport.LatchedReviewInterval);
            rig.Report.OnDispatched(50, "Store");
        }
        Assert.DoesNotContain(rig.Logger.Records, r => r.Level == LogLevel.Critical);
    }

    /// <summary>
    /// A crossing that already sees a leg past its bounds IS the Critical, and the review does not file
    /// a second ticket for the same episode.
    /// </summary>
    [Fact]
    public void ACrossingThatSeesALeak_IsTheOneCritical()
    {
        var rig = new Rig { Oldest = ("dispatch → Store (delivery d4)", TimeSpan.FromMinutes(5)) };
        rig.Report.OnDispatched(RoutingSaturationReport.SaturationThreshold, "Store");
        rig.Clock.Advance(RoutingSaturationReport.LatchedReviewInterval);
        rig.Report.OnDispatched(60, "Store");

        Assert.Equal(LogLevel.Critical, Assert.Single(rig.Logger.Records).Level);
    }

    /// <summary>
    /// The deepest channel is sampled AFTER the crossing leg is enqueued — the snapshot can only see
    /// legs already queued, so sampling before the enqueue would miss the very leg that crossed. Pinned
    /// on the real dispatcher, and it now names the channel as well as its depth.
    /// </summary>
    [Fact]
    public void TheSnapshot_SeesTheCrossingLeg_AndNamesItsChannel()
    {
        using var pool = new IoPool(2);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);

        dispatcher.Enqueue("cache/x", "sync/abc", Observable.Never<Unit>(), () => { });
        var before = dispatcher.QueueSnapshot();
        dispatcher.Enqueue("cache/x", "sync/abc", Observable.Never<Unit>(), () => { });
        var after = dispatcher.QueueSnapshot();

        Assert.Equal(0, before.Deepest);
        Assert.Null(before.DeepestChannel);
        Assert.Equal(1, after.Deepest);
        Assert.Equal("cache/x [sync/abc]", after.DeepestChannel);
    }

    private sealed class Rig
    {
        public RecordingLogger Logger { get; } = new();
        public ManualClock Clock { get; } = new();
        public (string Label, TimeSpan Age) Oldest { get; set; }
        public int OldestReads { get; private set; }
        public RoutingSaturationReport Report { get; }

        public Rig()
        {
            Report = new RoutingSaturationReport(
                "testact1",
                Logger,
                () => (1, 1, RoutingSaturationReport.SaturationThreshold - 1, "cache/x [sync/abc]"),
                () =>
                {
                    OldestReads++;
                    return Oldest;
                },
                () => (0, 0),
                Clock);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => now += by;
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLogger : ILogger
    {
        public ImmutableList<(LogLevel Level, string Message)> Records { get; private set; } =
            ImmutableList<(LogLevel Level, string Message)>.Empty;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records = Records.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
