using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
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
