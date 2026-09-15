using System;
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
/// Tests for <see cref="IIoPool.QueueWait"/> — how long granted work WAITED for a slot.
///
/// <para>The instrument exists because MeshWeaver#1198 could not be decided without it: the
/// Postgres commit stage's writes serialize process-wide behind ONE cap-1 write pool, every
/// partition queued behind every other, and the cap has no measurement behind it. Raising it is
/// the band-aid; a wait distribution is the reading that would justify a number instead.</para>
///
/// <para>🚨 The load-bearing test here is the CONTENDED one. A counter that is never incremented,
/// or that always records a zero wait, satisfies every "it starts empty / it counts an admission"
/// assertion perfectly — so this file's job is to contain at least one case those two failure
/// modes cannot pass. That is
/// <see cref="QueueWait_measuresARealWait_whenAnAdmissionQueuesBehindAnother"/>.</para>
///
/// <para>Concurrency shapes follow <see cref="IoPoolTest"/>: a signal a pool leaf produces is an
/// <see cref="AsyncSubject{T}"/> the leaf completes and the test awaits through the assertion
/// helpers; a release travelling the other way — INTO a leaf the test deliberately parks — is a
/// volatile flag polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and
/// written in a <c>finally</c>, so a failing assertion cannot strand a pool thread.</para>
/// </summary>
public class IoPoolQueueWaitTest
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the contended test holds the only slot. 🚨 This is NOT a budget — it is the
    /// quantity under test. It has to dominate the ThreadPool hop between `Subscribe` and the
    /// queued leaf reaching the gate, or the leaf could be granted its slot without ever having
    /// waited, and the test would be measuring scheduling luck instead of the instrument.
    /// </summary>
    private const int ContendedHoldMilliseconds = 200;

    [Fact]
    public async Task QueueWait_startsEmpty_andCountsAnAsyncAdmission()
    {
        using var pool = new IoPool(4);

        var before = pool.QueueWait;
        before.Samples.Should().Be(0, "nothing has been admitted yet");
        before.Mean.Should().Be(TimeSpan.Zero, "a mean over nothing is zero, never a divide by zero");

        (await pool.Invoke(_ => Task.FromResult(7)).Timeout(Timeout5)).Should().Be(7);

        var after = pool.QueueWait;
        after.Samples.Should().Be(1, "one admission was granted");
        after.Samples.Should().Be(
            after.UnderMillisecond + after.UnderTenMilliseconds + after.UnderHundredMilliseconds
            + after.UnderSecond + after.UnderTenSeconds + after.TenSecondsOrMore,
            "Samples is DERIVED from the buckets, so a snapshot can never disagree with its own histogram");
    }

    /// <summary>
    /// Blocking work does not pass through the async gate — it queues on the pool's
    /// limited-concurrency scheduler — so it is timed at that scheduler's grant point instead.
    /// Omitting it would leave a whole admission path out of a reading that looks total, which is
    /// the failure mode this repository calls an answer that reads like a pass.
    /// </summary>
    [Fact]
    public async Task QueueWait_countsBlockingWorkToo_theOtherAdmissionPath()
    {
        using var pool = new IoPool(4);

        (await pool.InvokeBlocking(_ => 7).Timeout(Timeout5)).Should().Be(7);

        pool.QueueWait.Samples.Should().Be(1,
            "blocking work queues on the limited-concurrency scheduler rather than the async gate; "
            + "a distribution that silently excluded it would under-report every blocking pool");
    }

    /// <summary>
    /// 🚨 THE test. One slot, held; a second admission must queue behind it and its wait must show
    /// up OUTSIDE the sub-millisecond bucket. An instrument that records nothing fails on
    /// <c>Samples</c>; one that records a zero wait fails on the bucket. Both are the ways this
    /// could be wrong while looking right.
    /// </summary>
    [Fact]
    public async Task QueueWait_measuresARealWait_whenAnAdmissionQueuesBehindAnother()
    {
        using var pool = new IoPool(1);
        var firstEntered = new AsyncSubject<Unit>();
        var firstFinished = new AsyncSubject<Unit>();
        var secondEntered = new AsyncSubject<Unit>();
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

        await firstEntered.Should().Within(Timeout5)
            .Emit("the first leaf must be holding the pool's only slot");
        pool.QueueWait.Samples.Should().Be(1, "only the first leaf has been admitted so far");

        using var second = pool.Invoke(_ =>
        {
            secondEntered.OnNext(Unit.Default);
            secondEntered.OnCompleted();
            return Task.FromResult(2);
        }).Subscribe(_ => { }, _ => { });

        try
        {
            var held = Stopwatch.StartNew();
            SpinWait.SpinUntil(
                    () => held.ElapsedMilliseconds >= ContendedHoldMilliseconds, Timeout5)
                .Should().BeTrue("the hold itself must complete well inside the test budget");
        }
        finally
        {
            Volatile.Write(ref releaseFirst, 1);
        }

        await secondEntered.Should().Within(Timeout5)
            .Emit("the queued leaf must be granted the slot once the first releases it");
        await firstFinished.Should().Within(Timeout5).Emit("the first leaf must have unwound");

        var stats = pool.QueueWait;
        stats.Samples.Should().Be(2, "both leaves were admitted");
        (stats.Samples - stats.UnderMillisecond).Should().BeGreaterThan(0,
            $"the queued leaf waited at least {ContendedHoldMilliseconds} ms, so it cannot have "
            + "landed in the sub-millisecond bucket — an instrument that always recorded zero "
            + "would fail exactly here");
        stats.Max.TotalMilliseconds.Should().BeGreaterThan(1,
            "the longest wait is the queued leaf's, which is the hold");
        stats.Mean.Should().BeGreaterThan(TimeSpan.Zero, "a real wait moves the mean off zero");
    }

    /// <summary>
    /// A wait that is CANCELLED never became an admission, so it contributes nothing. Counting it
    /// would blend "how long work waited to run" with "how long a teardown took to unwind" in one
    /// number, and the second is not what a cap is chosen against.
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
            // 🚨 The hold is load-bearing here, not decoration. Subscribing and disposing back to
            // back does NOT test a cancelled wait: the holder releases in the same breath, and the
            // queued leaf is admitted before its cancellation lands — measured, it recorded 2
            // admissions. Holding first puts the leaf provably ON the gate, so disposing it
            // cancels the wait itself, which is the case under test.
            var held = Stopwatch.StartNew();
            SpinWait.SpinUntil(
                    () => held.ElapsedMilliseconds >= ContendedHoldMilliseconds, Timeout5)
                .Should().BeTrue("the hold itself must complete well inside the test budget");
            queued.Dispose();
        }
        finally
        {
            // Strictly AFTER the cancellation: releasing first would race the permit to the leaf
            // we just cancelled.
            Volatile.Write(ref releaseFirst, 1);
        }

        await firstFinished.Should().Within(Timeout5).Emit("the holding leaf must have unwound");

        pool.QueueWait.Samples.Should().Be(1,
            "only the leaf that actually took a slot is an admission; the cancelled one is not");
    }
}
