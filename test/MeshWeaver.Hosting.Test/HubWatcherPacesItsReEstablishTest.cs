using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The hub watcher's transient re-establish is PACED and logs at the level an outage deserves.
///
/// <para><b>The defect.</b> <c>SubscribeWithReEstablish</c> / <c>SubscribeHubWatcher</c> re-opened a
/// faulted source after a fixed 1 s and logged EVERY re-open at Error, forever. During a real outage
/// of the source — a stalled query provider, a database that is down — that is 3600 Error lines an
/// hour per watcher (the Error channel files incidents) and 3600 fresh opens an hour against the
/// failing dependency.</para>
///
/// <para><b>The contract.</b> Re-opens back off 1 s, 2 s, 5 s, 15 s, 30 s, then every 60 s, counted
/// over consecutive faults with no emission between them; each is a Warning naming the watcher; a
/// run that reaches the cap is reported ONCE at Error, its recovery once at Information; an emission
/// restarts the cadence at 1 s; and the watcher never gives up. Virtual time throughout.</para>
///
/// <para>Negative control: with the pre-fix body (fixed 1 s, Error per re-open) restored, both tests
/// fail — the gaps read all 1 s and every re-open is an Error.</para>
/// </summary>
public class HubWatcherPacesItsReEstablishTest
{
    private static readonly Address Owner = new("mesh", "paced");

    /// <summary>A source whose first <c>failures</c> opens throw, then hands out a live subject.</summary>
    private sealed class FlakySource(TestScheduler clock, int failures)
    {
        public ImmutableList<long> OpenedAtTicks { get; private set; } = ImmutableList<long>.Empty;
        public Subject<int> Live { get; private set; } = new();

        public IObservable<int> Open()
        {
            OpenedAtTicks = OpenedAtTicks.Add(clock.Clock);
            if (OpenedAtTicks.Count <= failures)
                return Observable.Throw<int>(new InvalidOperationException($"outage, open {OpenedAtTicks.Count}"));
            Live = new Subject<int>();
            return Live;
        }

        public ImmutableList<double> GapsSeconds() =>
            OpenedAtTicks.Zip(OpenedAtTicks.Skip(1), (a, b) => TimeSpan.FromTicks(b - a).TotalSeconds).ToImmutableList();
    }

    /// <summary>Counts log lines by level; instance state only.</summary>
    private sealed class LevelLogger : ILogger
    {
        public int Warnings { get; private set; }
        public int Errors { get; private set; }
        public int Informations { get; private set; }
        public string? LastWarning { get; private set; }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            switch (logLevel)
            {
                case LogLevel.Warning: Warnings++; LastWarning = formatter(state, exception); break;
                case >= LogLevel.Error: Errors++; break;
                case LogLevel.Information: Informations++; break;
            }
        }
    }

    [Fact]
    public void ConsecutiveFaults_ReopenOnABackoff_WarnEach_ErrorOnce_AndAnEmissionRestartsTheCadence()
    {
        var clock = new TestScheduler();
        var source = new FlakySource(clock, failures: 8);
        var log = new LevelLogger();
        var received = ImmutableList<int>.Empty;

        using var watcher = ActivityControlPlaneExtensions.SubscribeWithReEstablishOn<int>(
            source.Open, x => received = received.Add(x), Owner, log, "paced test watcher", clock);

        clock.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

        source.GapsSeconds().Should().Equal(new[] { 1.0, 2, 5, 15, 30, 60, 60, 60 },
            "eight consecutive faults back off to the cap instead of re-opening every second");
        log.Warnings.Should().Be(7, "every re-open but the one that reaches the cap is a Warning");
        log.Errors.Should().Be(1, "the sustained outage is reported ONCE at Error, not once per re-open");
        log.LastWarning.Should().Contain("paced test watcher", "the Warning names the source");

        source.Live.OnNext(42);
        received.Should().Equal(42);
        log.Informations.Should().Be(1, "the recovery after an escalated outage is recorded once");

        // A new fault after the recovery starts a NEW run: back to 1 s, and a Warning again.
        var opensBefore = source.OpenedAtTicks.Count;
        var faultAt = clock.Clock;
        source.Live.OnError(new InvalidOperationException("one more hiccup"));
        clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        source.OpenedAtTicks.Count.Should().Be(opensBefore + 1);
        TimeSpan.FromTicks(source.OpenedAtTicks[^1] - faultAt).Should().Be(TimeSpan.FromSeconds(1),
            "an emission proved the source healthy, so the next fault re-opens at the first step again");
        log.Errors.Should().Be(1, "a single hiccup after recovery is a Warning, not another Error");
        log.Warnings.Should().Be(8);
    }

    [Fact]
    public void AnHourOfOutage_IsAFewDozenReopens_NotThreeThousand_AndOneErrorLine()
    {
        var clock = new TestScheduler();
        var source = new FlakySource(clock, failures: int.MaxValue);
        var log = new LevelLogger();

        using var watcher = ActivityControlPlaneExtensions.SubscribeWithReEstablishOn<int>(
            source.Open, _ => { }, Owner, log, "outage watcher", clock);

        clock.AdvanceBy(TimeSpan.FromHours(1).Ticks);

        // 1 + 2 + 5 + 15 + 30 = 53 s to reach the cap, then one open a minute: ~65 opens an hour.
        source.OpenedAtTicks.Count.Should().BeInRange(60, 70,
            "a persisting fault settles at one re-open a minute — the old 1 s timer made ~3600");
        log.Errors.Should().Be(1, "one outage, one Error line");
        log.Warnings.Should().Be(source.OpenedAtTicks.Count - 1,
            "every open faults once: one of those faults is the Error, every other one a Warning");
        source.GapsSeconds().Max().Should().Be(60, "the cadence is capped");
    }
}
