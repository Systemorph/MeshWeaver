using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #6392: peers log a 30 s <c>GrainCallCancellationManager</c> timeout against one silo, and
/// the repo could not say whether that silo was stopping or starved, because its own stop logged
/// nothing between three holds. <see cref="SiloStopTimeline"/> timestamps every stop stage; these
/// tests pin that the timeline names every stage in Orleans' stop order and that a slow stage
/// shows up as a gap in front of the next one. Real <see cref="SiloLifecycleSubject"/>, no cluster,
/// no mocks.
///
/// <para>Why not a multi-silo cancellation test: the silos of a <c>TestCluster</c> share one
/// process and one memory store, so an in-process cluster cannot reproduce a defect that lives in
/// how OTHER processes see a departing one (the same limit OrleansServerRegistryExtensions records
/// for the pub-sub store). The cause is read from the production pod logs instead, with the lines
/// this participant writes; see the doc page <c>Doc/Architecture/ReadingASiloStop</c>.</para>
/// </summary>
public class SiloStopTimelineTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class CapturingLogger : ILogger<SiloStopTimeline>
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (messages) return messages.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }

    /// <summary>A stage whose stop stays pending until the test releases it - a slow stage.</summary>
    private sealed class HoldingObserver(IObservable<Unit> release) : ILifecycleObserver
    {
        private int entered;

        public bool Entered => Volatile.Read(ref entered) != 0;

        public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnStop(CancellationToken cancellationToken)
        {
            Volatile.Write(ref entered, 1);
            // ILifecycleObserver's Task boundary is Orleans', not ours: bridge through the one
            // sanctioned awaiter (continuations queued, never resumed inline on the signaller).
            return release.Await(cancellationToken);
        }
    }

    private static string[] StageNamesReported(CapturingLogger logger) =>
        logger.Messages
            .Select(m => Regex.Match(m, @"stage (\w+) reached"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToArray();

    /// <summary>
    /// The timeline names every stage, once, in the order Orleans stops them in; and a stage that
    /// takes time shows as the gap printed on the NEXT stage's line - the reading that tells a
    /// stop that spent its time in one stage from a stop that was starved throughout.
    /// </summary>
    [Fact]
    public async Task SiloStop_IsTimestampedStageByStage_AndASlowStageShowsAsAGapOnTheNextLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var timeline = new SiloStopTimeline(logger);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        timeline.Participate(lifecycle);

        var release = new AsyncSubject<Unit>();
        var slowStage = new HoldingObserver(release);
        lifecycle.Subscribe("SlowStage", ServiceLifecycleStage.GrainDeactivation, slowStage);
        await lifecycle.OnStart(ct);

        var stop = lifecycle.OnStop(ct);
        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => slowStage.Entered, Bound),
                "the slow stage must be reached: the stages above it do not hold the stop");

            stop.IsCompleted.Should().BeFalse("the slow stage is pending, so the stop cannot be complete");
            var whileHeld = StageNamesReported(logger);
            whileHeld.Should().Contain("Active", "the stage above the slow one ran before it");
            whileHeld.Should().Contain("BecomeActive", "the stage above the slow one ran before it");
            whileHeld.Should().NotContain("RuntimeServices", "a lower stage cannot start while a higher one is held");
            whileHeld.Should().NotContain("RuntimeInitialize", "a lower stage cannot start while a higher one is held");
            whileHeld.Should().NotContain("First", "a lower stage cannot start while a higher one is held");

            // Forces a measurable stage duration (about 300 ms; the bound below is deliberately
            // looser) - the sanctioned distinct-timestamps use, not a wait for propagation.
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }
        finally
        {
            // Released in a finally, so a failing assertion above cannot strand the lifecycle stop.
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }
        await stop.WaitAsync(Bound, ct);

        var expectedOrder = new (string Name, int Stage)[]
            {
                ("Active", ServiceLifecycleStage.Active),
                ("BecomeActive", ServiceLifecycleStage.BecomeActive),
                ("GrainDeactivation", ServiceLifecycleStage.GrainDeactivation),
                ("RuntimeServices", ServiceLifecycleStage.RuntimeServices),
                ("RuntimeInitialize", ServiceLifecycleStage.RuntimeInitialize),
                ("First", ServiceLifecycleStage.First),
            }
            .OrderByDescending(s => s.Stage)
            .Select(s => s.Name)
            .ToArray();
        StageNamesReported(logger).Should().Equal(expectedOrder,
            "every stage is named exactly once, in the order Orleans stops them in - a stop that "
            + "spent a minute somewhere must show where");

        var next = expectedOrder[Array.IndexOf(expectedOrder, "GrainDeactivation") + 1];
        var gapLine = logger.Messages
            .Select(m => Regex.Match(
                m, @"stage " + next + @" reached \d+ ms into the silo stop \((\d+) ms after the previous stage\)"))
            .First(m => m.Success);
        long.Parse(gapLine.Groups[1].Value).Should().BeGreaterThanOrEqualTo(200,
            "the line of the stage AFTER the slow one carries the time the slow one took - that gap is "
            + "what separates a stop that spent its time in one stage from one that was starved throughout");
    }

    /// <summary>The registration is idempotent: one participant however often the services are added.</summary>
    [Fact]
    public void AddSiloStopTimeline_RegistersOneLifecycleParticipant_EvenWhenAddedTwice()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<SiloStopTimeline>>(NullLogger<SiloStopTimeline>.Instance);
        services.AddSiloStopTimeline().AddSiloStopTimeline();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<ILifecycleParticipant<ISiloLifecycle>>()
            .OfType<SiloStopTimeline>()
            .Should().HaveCount(1, "a second registration would print every stage line twice");
    }
}

/// <summary>
/// The WIRING half, on a real silo: AddOrleansMeshServices must put the timeline on the silo's
/// lifecycle, or the lines <c>Doc/Architecture/ReadingASiloStop</c> tells an operator to read would never be written.
/// </summary>
public class SiloStopTimelineClusterTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(StallingResolverSiloConfigurator);

    [Fact]
    public void TheSilo_CarriesTheStopTimeline_AsALifecycleParticipant()
    {
        var silo = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;

        silo.GetServices<ILifecycleParticipant<ISiloLifecycle>>()
            .Should().Contain(p => p is SiloStopTimeline,
                "the silo lifecycle must carry the stop timeline, or a departing silo's log stays silent between its three holds");
    }
}
