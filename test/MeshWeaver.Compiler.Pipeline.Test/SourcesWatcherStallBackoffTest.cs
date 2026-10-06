using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// #5344: after a query-provider stall the sources watcher re-establishes with a bounded, growing,
/// jittered delay instead of the re-establish primitive's fixed 1 s timer. Virtual time throughout.
/// </summary>
public class SourcesWatcherStallBackoffTest
{
    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Delay_is_zero_until_a_stall_and_resets_when_an_element_is_delivered()
    {
        var backoff = new SourcesWatcherStallBackoff(Seconds(5), Seconds(120), () => 1.0);
        Assert.Equal(TimeSpan.Zero, backoff.NextDelay());

        backoff.OnStall();
        Assert.Equal(Seconds(5), backoff.NextDelay());

        backoff.OnDelivered();
        Assert.Equal(TimeSpan.Zero, backoff.NextDelay());
        Assert.Equal(0, backoff.ConsecutiveStalls);
    }

    [Fact]
    public void Delay_doubles_per_stall_and_is_capped()
    {
        var backoff = new SourcesWatcherStallBackoff(Seconds(5), Seconds(120), () => 1.0);
        var seen = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            backoff.OnStall();
            seen.Add(backoff.NextDelay());
        }

        Assert.Equal(
            new[] { 5.0, 10, 20, 40, 80, 120, 120, 120 }.Select(Seconds).ToList(),
            seen);
    }

    [Theory]
    [InlineData(0.0, 2.5)]
    [InlineData(0.5, 3.75)]
    [InlineData(1.0, 5.0)]
    public void Jitter_spreads_the_delay_between_half_and_the_full_ceiling(double random, double expectedSeconds)
    {
        var backoff = new SourcesWatcherStallBackoff(Seconds(5), Seconds(120), () => random);
        backoff.OnStall();
        Assert.Equal(Seconds(expectedSeconds), backoff.NextDelay());
    }

    [Fact]
    public void Without_the_backoff_a_stalled_watcher_re_establishes_every_second_forever()
    {
        // Negative control: this is main's behaviour (the fixed 1 s re-establish timer, no growth).
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

        var gaps = SubscribeGapsSeconds(source => source, () => new InvalidOperationException(), scheduler);

        Assert.True(gaps.Count > 100);
        Assert.All(gaps, gap => Assert.Equal(1.0, gap));
    }

    [Fact]
    public void With_the_backoff_a_stalled_watcher_re_establishes_at_growing_capped_intervals()
    {
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(
            Seconds(5), Seconds(120), () => 1.0, ex => ex is InvalidOperationException, scheduler);

        var gaps = SubscribeGapsSeconds(source => backoff.Apply(source), () => new InvalidOperationException(), scheduler);

        // The primitive's fixed 1 s timer plus the extra delay: 1+5, 1+10, 1+20, ... capped at 1+120.
        Assert.Equal(new[] { 6.0, 11, 21, 41, 81, 121, 121, 121 }, gaps);
    }

    [Fact]
    public void A_fault_that_is_not_a_stall_adds_no_delay()
    {
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(
            Seconds(5), Seconds(120), () => 1.0, ex => ex is InvalidOperationException, scheduler);

        var gaps = SubscribeGapsSeconds(source => backoff.Apply(source), () => new ArgumentException(), scheduler);

        Assert.True(gaps.Count > 100);
        Assert.All(gaps, gap => Assert.Equal(1.0, gap));
    }

    [Fact]
    public void A_delivered_element_resets_the_backoff()
    {
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(
            Seconds(5), Seconds(120), () => 1.0, ex => ex is InvalidOperationException, scheduler);
        backoff.OnStall();
        backoff.OnStall();
        Assert.Equal(Seconds(10), backoff.NextDelay());

        var values = new List<int>();
        backoff.Apply(Observable.Return(1)).Subscribe(values.Add);
        scheduler.AdvanceBy(Seconds(10));

        Assert.Equal(new[] { 1 }, values);
        Assert.Equal(TimeSpan.Zero, backoff.NextDelay());
    }

    private static QueryProviderStalledException Stall() =>
        new("pg", TimeSpan.FromSeconds(16), "nodeType:NodeType", "system");

    public static TheoryData<string, Exception, bool> DefaultClassifierCases() => new()
    {
        { "the stall itself", Stall(), true },
        { "a stall wrapped as InnerException", new InvalidOperationException("outer", Stall()), true },
        { "a stall as an aggregate's FIRST member", new AggregateException(Stall()), true },
        // AggregateException.InnerException is only the first member: a stall behind another fault
        // is invisible to an InnerException-only walk (review of #6182).
        { "a stall as an aggregate's SECOND member",
            new AggregateException(new TimeoutException(), Stall()), true },
        { "a stall nested in an aggregate inside a wrapper",
            new InvalidOperationException("outer", new AggregateException(new ArgumentException(), Stall())), true },
        { "an unrelated fault", new InvalidOperationException("boom"), false },
        { "an aggregate of unrelated faults",
            new AggregateException(new TimeoutException(), new ArgumentException()), false },
    };

    [Theory]
    [MemberData(nameof(DefaultClassifierCases))]
    public void The_production_classifier_recognises_a_stall_through_every_wrapping(
        string because, Exception fault, bool expected)
        => Assert.True(SourcesWatcherStallBackoff.IsQueryStall(fault) == expected, because);

    [Fact]
    public void The_default_classifier_is_the_one_Apply_uses()
    {
        // No isStall injected: the production predicate decides, and a stall behind another fault counts.
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(Seconds(5), Seconds(120), () => 1.0, scheduler: scheduler);

        backoff.Apply(Observable.Throw<int>(new AggregateException(new TimeoutException(), Stall())))
            .Subscribe(_ => { }, _ => { });
        scheduler.AdvanceBy(Seconds(1));

        Assert.Equal(1, backoff.ConsecutiveStalls);
    }

    [Fact]
    public void A_fault_that_is_not_a_stall_breaks_the_run_and_pays_no_earlier_stall_delay()
    {
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(
            Seconds(5), Seconds(120), () => 1.0, ex => ex is InvalidOperationException, scheduler);
        backoff.OnStall();
        backoff.OnStall();

        // The attempt waits its 10 s (the run so far), then faults with something that is NOT a stall.
        backoff.Apply(Observable.Throw<int>(new ArgumentException())).Subscribe(_ => { }, _ => { });
        scheduler.AdvanceBy(Seconds(10));

        Assert.Equal(0, backoff.ConsecutiveStalls);
        Assert.Equal(TimeSpan.Zero, backoff.NextDelay());
    }

    [Fact]
    public void A_completion_without_an_element_breaks_the_run()
    {
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(
            Seconds(5), Seconds(120), () => 1.0, ex => ex is InvalidOperationException, scheduler);
        backoff.OnStall();

        backoff.Apply(Observable.Empty<int>()).Subscribe(_ => { }, _ => { });
        scheduler.AdvanceBy(Seconds(5));

        Assert.Equal(0, backoff.ConsecutiveStalls);
    }

    [Fact]
    public void The_real_re_establish_primitive_re_subscribes_the_factory_and_pays_the_growing_delay()
    {
        // Review of #6182: the backoff only works if the consuming operator RE-SUBSCRIBES the factory
        // on fault (Observable.Defer recomputes the delay per subscription). This drives the real
        // primitive — the same core SubscribeHubWatcher runs InstallSourcesWatcher on — with its
        // 1 s re-establish moved onto the virtual clock through the public scheduling seam, and a
        // REAL QueryProviderStalledException through the production classifier.
        var scheduler = new HistoricalScheduler(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var backoff = new SourcesWatcherStallBackoff(Seconds(5), Seconds(120), () => 1.0, scheduler: scheduler);
        var subscribedAt = new List<DateTimeOffset>();
        var source = Observable.Defer(() =>
        {
            subscribedAt.Add(scheduler.Now);
            return Observable.Throw<int>(Stall());
        });

        using var watcher = ActivityControlPlaneExtensions.SubscribeWithReEstablish(
            () => source.WithStallBackoff(backoff),
            _ => { },
            new MeshWeaver.Messaging.Address("stall-backoff", "probe"),
            logger: null,
            faultLogContext: "SourcesWatcherStallBackoffTest",
            scheduleReEstablish: reEstablish =>
                Observable.Timer(TimeSpan.FromSeconds(1), scheduler).Subscribe(_ => reEstablish()));
        scheduler.AdvanceBy(TimeSpan.FromMinutes(10));

        var gaps = subscribedAt.Zip(subscribedAt.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
        // The primitive's 1 s plus the backoff's 5, 10, 20, ... capped at 120.
        Assert.Equal(new[] { 6.0, 11, 21, 41, 81, 121, 121, 121 }, gaps);
    }

    // Models SubscribeWithReEstablish's schedule on the virtual clock: on every fault, a FIXED 1 s timer
    // and then a fresh subscription. Returns the gaps between consecutive subscriptions of the source.
    private static List<double> SubscribeGapsSeconds(
        Func<IObservable<int>, IObservable<int>> shape,
        Func<Exception> fault,
        HistoricalScheduler scheduler)
    {
        var subscribedAt = new List<DateTimeOffset>();
        var source = Observable.Defer(() =>
        {
            subscribedAt.Add(scheduler.Now);
            return Observable.Throw<int>(fault());
        });

        void Establish() =>
            shape(source).Subscribe(
                _ => { },
                _ => Observable.Timer(TimeSpan.FromSeconds(1), scheduler).Subscribe(__ => Establish()));

        Establish();
        scheduler.AdvanceBy(TimeSpan.FromMinutes(10));
        return subscribedAt.Zip(subscribedAt.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
    }
}
