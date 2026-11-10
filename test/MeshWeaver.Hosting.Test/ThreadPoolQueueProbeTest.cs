using System;
using System.Collections.Generic;
using System.Threading;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The queue-delay probe behind the second <c>[POOLSTALL]</c> trigger (MeshWeaver#5388): one probe item
/// in the queue at a time, and its wait is what decides a capture.
/// </summary>
public class ThreadPoolQueueProbeTest
{
    [Fact]
    public void AnUnstartedProbe_ReportsItsWait_AndNoSecondIsQueued_UntilItStarts()
    {
        var queued = new List<Action>();
        var probe = new ThreadPoolQueueProbe(callback => { queued.Add(callback); return true; });

        probe.Poll(TimeSpan.FromSeconds(100)).Should().Be(TimeSpan.Zero, "the first poll only queues");
        queued.Should().HaveCount(1);

        probe.Poll(TimeSpan.FromSeconds(103)).Should().Be(TimeSpan.FromSeconds(3));
        probe.Poll(TimeSpan.FromSeconds(115.5)).Should().Be(TimeSpan.FromSeconds(15.5),
            "the wait keeps growing while the item sits in the queue");
        queued.Should().HaveCount(1, "a probe that has not started is never stacked behind by another");

        queued[0]();
        probe.Poll(TimeSpan.FromSeconds(116)).Should().Be(TimeSpan.Zero, "the item started");
        queued.Should().HaveCount(2, "a fresh probe is queued once the previous one started");
        probe.Poll(TimeSpan.FromSeconds(117)).Should().Be(TimeSpan.FromSeconds(1),
            "the wait is measured from the NEW probe's queueing, not the first one's");
    }

    /// <summary>An item the pool refused to queue can never start, so it must not be read as a growing wait.</summary>
    [Fact]
    public void AProbeThePoolDidNotQueue_IsNotReportedAsWaiting()
    {
        var attempts = 0;
        var probe = new ThreadPoolQueueProbe(_ => { attempts++; return false; });

        probe.Poll(TimeSpan.FromSeconds(100)).Should().Be(TimeSpan.Zero);
        probe.Poll(TimeSpan.FromSeconds(200)).Should().Be(TimeSpan.Zero,
            "nothing was queued, so nothing is waiting — a held probe would read 100 s here");
        attempts.Should().Be(2, "each poll tries to queue again");
    }

    /// <summary>
    /// Positive control on the production enqueue: on a pool that is not starved, the probe item really
    /// runs, so a non-zero wait in production is a real queue delay and not a probe that was never queued.
    /// </summary>
    [Fact]
    public void OnTheRealThreadPool_TheProbeItemRuns()
    {
        var probe = new ThreadPoolQueueProbe();
        probe.Poll(TimeSpan.Zero);

        SpinWait.SpinUntil(() => probe.Poll(TimeSpan.FromHours(1)) == TimeSpan.Zero, TimeSpan.FromSeconds(30))
            .Should().BeTrue("the probe item was queued on the ThreadPool and a worker started it");
    }
}
