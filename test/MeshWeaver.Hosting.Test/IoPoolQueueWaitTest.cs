using MeshWeaver.Messaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for <see cref="IIoPool.QueueWait"/> and <see cref="IIoPool.CurrentlyWaiting"/> — how long
/// granted work WAITED for a slot, and how much is queued right now.
///
/// <para>The instrument exists because MeshWeaver#1198 could not be decided without it: the
/// Postgres commit stage's writes serialize process-wide behind ONE cap-1 write pool, every
/// partition queued behind every other, and the cap has no measurement behind it.</para>
///
/// <para>🚨 <b>Every admission path gets a CONTENDED case, not just a counting one.</b> The pool
/// admits through four doors — <see cref="IIoPool.Invoke{T}(Func{CancellationToken, Task{T}})"/>,
/// <see cref="IIoPool.InvokeStream{T}"/>, <see cref="IIoPool.SubscribeThroughPool{T}"/> and
/// <see cref="IIoPool.InvokeBlocking{T}"/> — and a distribution that silently omits one still
/// reads as total. A test that only proves "a sample appeared" cannot tell a recorded wait from a
/// recorded zero, so each path here is made to WAIT and then asserted to have landed outside the
/// sub-millisecond bucket.</para>
///
/// <para>🚨 <b>Arrival at the gate is synchronised on, never assumed from a duration.</b> Holding
/// the slot for a fixed time and hoping the queued leaf got there first makes the contended case
/// pass on scheduling luck; <see cref="IIoPool.CurrentlyWaiting"/> is what makes it deterministic.
/// Concurrency shapes otherwise follow <see cref="IoPoolTest"/>: a signal a leaf produces is an
/// <see cref="AsyncSubject{T}"/> the test awaits through the assertion helpers; a release travelling
/// INTO a parked leaf is a volatile flag polled under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and written in a <c>finally</c>.</para>
/// </summary>
public class IoPoolQueueWaitTest
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the queued leaf is held AT the gate once its arrival has been observed. Not a
    /// budget — it is the quantity under test: the wait has to clear the sub-millisecond bucket
    /// for the contended assertion to mean anything. Arrival is synchronised on separately, so
    /// this duration is entirely wait and nothing here depends on scheduling luck.
    /// </summary>
    private const int MeasurableHoldMilliseconds = 25;

    [Fact]
    public async Task QueueWait_startsEmpty_andCountsAnAsyncAdmission()
    {
        using var pool = new IoPool(4);

        var before = pool.QueueWait;
        before.Samples.Should().Be(0, "nothing has been admitted yet");
        before.Mean.Should().Be(TimeSpan.Zero, "a mean over nothing is zero, never a divide by zero");
        pool.CurrentlyWaiting.Should().Be(0, "nothing is queued");

        (await pool.Invoke(_ => Task.FromResult(7)).Timeout(Timeout5).Await()).Should().Be(7);

        var after = pool.QueueWait;
        after.Samples.Should().Be(1, "one admission was granted");
        after.Samples.Should().Be(
            after.UnderMillisecond + after.UnderTenMilliseconds + after.UnderHundredMilliseconds
            + after.UnderSecond + after.UnderTenSeconds + after.TenSecondsOrMore,
            "Samples is DERIVED from the buckets, so a snapshot can never disagree with its own histogram");
        pool.CurrentlyWaiting.Should().Be(0, "the admission completed, so nothing is queued");
    }

    /// <summary>
    /// 🚨 THE shape, run once per admission path. One slot, held; a second admission is made to
    /// queue behind it — proven queued via <see cref="IIoPool.CurrentlyWaiting"/> rather than
    /// assumed — and its wait must land OUTSIDE the sub-millisecond bucket. An instrument that
    /// records nothing fails on <c>Samples</c>; one that records a zero fails on the bucket.
    /// </summary>
    [Theory]
    [InlineData(AdmissionPath.Invoke)]
    [InlineData(AdmissionPath.InvokeStream)]
    [InlineData(AdmissionPath.SubscribeThroughPool)]
    [InlineData(AdmissionPath.InvokeBlocking)]
    public async Task QueueWait_measuresARealWait_onEveryAdmissionPath(AdmissionPath path)
    {
        using var pool = new IoPool(1);
        var firstEntered = new AsyncSubject<Unit>();
        var releaseFirst = 0;

        // 🚨 The holder must occupy the SAME limiter the queued leaf will queue on. An IoPool has
        // TWO, sized alike but independent: the async gate (`_gate`) for Invoke / InvokeStream /
        // SubscribeThroughPool, and the limited-concurrency scheduler for InvokeBlocking. Holding
        // the async gate does not make blocking work wait at all — measured: the blocking case was
        // admitted immediately with a sub-millisecond wait while an `Invoke` held "the slot".
        using var first = path == AdmissionPath.InvokeBlocking
            ? pool.InvokeBlocking(_ =>
            {
                firstEntered.OnNext(Unit.Default);
                firstEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseFirst) == 1, Timeout5);
                return 1;
            }).Subscribe(_ => { }, _ => { })
            : pool.Invoke(_ =>
            {
                firstEntered.OnNext(Unit.Default);
                firstEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseFirst) == 1, Timeout5);
                return Task.FromResult(1);
            }).Subscribe(_ => { }, _ => { });

        await firstEntered.Should().Within(Timeout5)
            .Emit("the first leaf must be holding the limiter the queued one will wait on");
        pool.QueueWait.Samples.Should().Be(1, "only the first leaf has been admitted so far");

        var admitted = new AsyncSubject<Unit>();
        var feed = new Subject<int>();
        using var queued = Queue(pool, path, admitted, feed);

        try
        {
            // Deterministic: wait until the leaf has REACHED the gate. A fixed hold would let a
            // slow ThreadPool hand it the slot without it ever having waited, and the contended
            // assertion below would then pass on luck rather than on the instrument.
            SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 1, Timeout5)
                .Should().BeTrue($"the {path} admission must reach the gate and queue there");
            pool.QueueWait.Samples.Should().Be(1, "it is queued, so it has not been admitted");

            // Arrival makes it deterministic; the hold is what makes the wait MEASURABLE. Both are
            // needed: synchronising and releasing at once recorded a sub-millisecond wait on two of
            // the four paths and passed on the other two by accident of their slower setup.
            // Because the leaf is provably AT the gate, every millisecond here is its wait.
            var held = Stopwatch.StartNew();
            SpinWait.SpinUntil(() => held.ElapsedMilliseconds >= MeasurableHoldMilliseconds, Timeout5)
                .Should().BeTrue("the hold itself must complete well inside the test budget");
        }
        finally
        {
            Volatile.Write(ref releaseFirst, 1);
        }

        if (path == AdmissionPath.SubscribeThroughPool)
            SpinWait.SpinUntil(() => feed.HasObservers, Timeout5)
                .Should().BeTrue("the pooled subscribe must complete once the slot is granted");
        else
            await admitted.Should().Within(Timeout5)
                .Emit($"the queued {path} leaf must run once the slot is granted");

        SpinWait.SpinUntil(() => pool.QueueWait.Samples == 2, Timeout5)
            .Should().BeTrue($"the {path} admission must be counted");

        var stats = pool.QueueWait;
        (stats.Samples - stats.UnderMillisecond).Should().BeGreaterThan(0,
            $"the queued {path} leaf waited at the gate until the holder released, so it cannot have "
            + "landed in the sub-millisecond bucket — this is the assertion an instrument that "
            + "always recorded zero, or that skipped this path, would fail");
        stats.Max.TotalMilliseconds.Should().BeGreaterThan(1, "the longest wait is the queued leaf's");
    }

    /// <summary>
    /// A wait that is CANCELLED never became an admission, so it contributes nothing. Counting it
    /// would blend "how long work waited to run" with "how long a teardown took to unwind".
    /// </summary>
    [Fact]
    public async Task QueueWait_recordsNothingForAWaitThatWasCancelledBeforeItsSlot()
    {
        using var pool = new IoPool(1);
        var firstEntered = new AsyncSubject<Unit>();
        var firstFinished = new AsyncSubject<Unit>();
        var releaseFirst = 0;

        using var first = pool.Invoke(_ =>
        {
            firstEntered.OnNext(Unit.Default);
            firstEntered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseFirst) == 1, Timeout5);
            firstFinished.OnNext(Unit.Default);
            firstFinished.OnCompleted();
            return Task.FromResult(1);
        }).Subscribe(_ => { }, _ => { });

        await firstEntered.Should().Within(Timeout5).Emit("the first leaf must hold the only slot");

        using var queued = pool.Invoke(_ => Task.FromResult(2)).Subscribe(_ => { }, _ => { });
        try
        {
            // Cancelling it only tests a cancelled WAIT once it is provably waiting; subscribing
            // and disposing back to back admits it instead (measured: 2 admissions, not 1).
            SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 1, Timeout5)
                .Should().BeTrue("the second leaf must be queued on the gate before it is cancelled");
            queued.Dispose();
            SpinWait.SpinUntil(() => pool.CurrentlyWaiting == 0, Timeout5)
                .Should().BeTrue("cancelling must take it off the queue");
        }
        finally
        {
            // Strictly AFTER the cancellation: releasing first would race the permit to it.
            Volatile.Write(ref releaseFirst, 1);
        }

        await firstFinished.Should().Within(Timeout5).Emit("the holding leaf must have unwound");

        pool.QueueWait.Samples.Should().Be(1,
            "only the leaf that actually took a slot is an admission; the cancelled one is not");
    }

    /// <summary>The four doors into the pool, each of which must appear in the distribution.</summary>
    public enum AdmissionPath
    {
        Invoke,
        InvokeStream,
        SubscribeThroughPool,
        InvokeBlocking,
    }

    private static IDisposable Queue(
        IoPool pool, AdmissionPath path, AsyncSubject<Unit> admitted, Subject<int> feed) =>
        path switch
        {
            AdmissionPath.Invoke => pool.Invoke(_ =>
            {
                Signal(admitted);
                return Task.FromResult(2);
            }).Subscribe(_ => { }, _ => { }),

            AdmissionPath.InvokeStream => pool.InvokeStream(_ => Stream(admitted))
                .Subscribe(_ => { }, _ => { }),

            // The gate is taken to run the SUBSCRIBE itself, so arrival is observed on the
            // source gaining an observer rather than on a leaf signalling.
            AdmissionPath.SubscribeThroughPool => pool.SubscribeThroughPool(feed)
                .Subscribe(_ => { }, _ => { }),

            AdmissionPath.InvokeBlocking => pool.InvokeBlocking(_ =>
            {
                Signal(admitted);
                return 2;
            }).Subscribe(_ => { }, _ => { }),

            _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
        };

    private static void Signal(AsyncSubject<Unit> subject)
    {
        subject.OnNext(Unit.Default);
        subject.OnCompleted();
    }

    private static async IAsyncEnumerable<int> Stream(AsyncSubject<Unit> admitted)
    {
        Signal(admitted);
        yield return 2;
        await Task.CompletedTask;
    }
}
