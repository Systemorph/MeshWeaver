using System;
using System.Collections.Generic;
using Microsoft.Reactive.Testing;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// #5388: every hub used to run its own 5 s <c>Observable.Interval</c>, one Timer and one thread-pool
/// work item per hub per tick, phase-locked across a wave of activations. The registry replaces them
/// with one timer however many hubs register.
/// </summary>
public class StaleCallbackScanRegistryTest
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private sealed class Probe(Func<bool>? alive = null, Action? onScan = null) : IStaleCallbackScanTarget
    {
        public int Scans;
        public bool ScanStaleCallbacks()
        {
            Scans++;
            onScan?.Invoke();
            return alive?.Invoke() ?? true;
        }
    }

    [Fact]
    public void ManyHubsShareOneTimer()
    {
        var clock = new TestScheduler();
        var registry = new StaleCallbackScanRegistry(Interval, clock);
        var probes = new List<Probe>();
        for (var i = 0; i < 5000; i++)
        {
            var p = new Probe();
            probes.Add(p);
            registry.Register(p);
        }

        Assert.Equal(1, registry.TimersStarted);
        Assert.Equal(5000, registry.Count);

        clock.AdvanceBy(Interval.Ticks);
        Assert.All(probes, p => Assert.Equal(1, p.Scans));
        clock.AdvanceBy(Interval.Ticks * 2);
        Assert.All(probes, p => Assert.Equal(3, p.Scans));
    }

    [Fact]
    public void ADisposedRegistrationIsNotScannedAgain()
    {
        var clock = new TestScheduler();
        var registry = new StaleCallbackScanRegistry(Interval, clock);
        var kept = new Probe();
        var dropped = new Probe();
        registry.Register(kept);
        var handle = registry.Register(dropped);

        clock.AdvanceBy(Interval.Ticks);
        handle.Dispose();
        clock.AdvanceBy(Interval.Ticks);

        Assert.Equal(2, kept.Scans);
        Assert.Equal(1, dropped.Scans);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ATargetThatReportsItselfGoneIsRetired()
    {
        var clock = new TestScheduler();
        var registry = new StaleCallbackScanRegistry(Interval, clock);
        var gone = new Probe(alive: () => false);
        registry.Register(gone);

        clock.AdvanceBy(Interval.Ticks * 3);

        Assert.Equal(1, gone.Scans);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void AFailingScanDoesNotStopTheOthersOrTheTimer()
    {
        var clock = new TestScheduler();
        var registry = new StaleCallbackScanRegistry(Interval, clock);
        var bad = new Probe(onScan: () => throw new InvalidOperationException("scan failed"));
        var good = new Probe();
        registry.Register(bad);
        registry.Register(good);

        clock.AdvanceBy(Interval.Ticks * 2);

        Assert.Equal(2, good.Scans);
        Assert.Equal(2, bad.Scans);
        Assert.Equal(1, registry.TimersStarted);
    }

    [Fact]
    public void NothingTicksUntilSomethingRegisters()
    {
        var registry = new StaleCallbackScanRegistry(Interval, new TestScheduler());
        Assert.Equal(0, registry.TimersStarted);
    }
}
