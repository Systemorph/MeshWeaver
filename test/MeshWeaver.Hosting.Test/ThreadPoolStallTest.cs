using System;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The <c>[POOLSTALL]</c> decision and line (MeshWeaver#5388), over the readings of the 2026-11-02
/// memex-cloud stall: 69 pool threads, <c>poolCompleted(+0)</c> with 20,335 queued, between ticks that
/// were otherwise ordinary.
/// </summary>
public class ThreadPoolStallTest
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    private static ProcessLivenessSample Sample(long tick, double elapsedSeconds, long pending, long completed) =>
        new(tick, TimeSpan.FromSeconds(elapsedSeconds), 937, 382, 129, TimeSpan.FromSeconds(34.87),
            3_540_000_000L, true, 69, pending, completed);

    /// <summary>Tick 1098 of the incident: 20,335 already queued.</summary>
    private static ProcessLivenessSample Before(double atSeconds = 20) =>
        Sample(1098, atSeconds - 10, 20_335, 43_739_919);

    /// <summary>Tick 1099 of the incident: nothing completed, 20,357 queued.</summary>
    private static ProcessLivenessReading Stalled(double atSeconds = 20) =>
        ProcessLiveness.Read(Before(atSeconds), Sample(1099, atSeconds, 20_357, 43_739_919), Period);

    [Fact]
    public void NoCompletionWithWorkQueuedAtBothEnds_IsAStall()
        => ThreadPoolStall.MadeNoProgress(Before(), Stalled()).Should().BeTrue();

    /// <summary>
    /// An idle pool that is handed a work item just before the sample also completed nothing and now
    /// has work pending — a healthy hand-off, never a stall, and it must not spend the capture window.
    /// </summary>
    [Fact]
    public void AnIdlePoolHandedWorkJustBeforeTheSample_IsNotAStall()
    {
        var idle = Sample(1, 0, 0, 100);
        ThreadPoolStall.MadeNoProgress(idle, ProcessLiveness.Read(idle, Sample(2, 10, 1, 100), Period))
            .Should().BeFalse("nothing was queued when the period began");
    }

    /// <summary>Controls: an IDLE pool (nothing queued) and a BUSY one (completions climbing) are not stalls.</summary>
    [Fact]
    public void AnIdlePoolAndABusyPool_AreNotStalls()
    {
        var idle = Sample(1, 0, 0, 100);
        ThreadPoolStall.MadeNoProgress(idle, ProcessLiveness.Read(idle, Sample(2, 10, 0, 100), Period))
            .Should().BeFalse("nothing was queued, so nothing was waiting");
        var busy = Sample(1, 0, 1_236, 43_739_919);
        ThreadPoolStall.MadeNoProgress(busy, ProcessLiveness.Read(busy, Sample(2, 10, 1_260, 43_789_010), Period))
            .Should().BeFalse("the pool completed work in the period");
        ThreadPoolStall.MadeNoProgress(null, ProcessLiveness.Read(null, Sample(1, 0, 5_000, 100), Period))
            .Should().BeFalse("the first tick has no period to judge");
    }

    [Fact]
    public void TheCaptureIsTakenOnce_ThenNotAgainInsideTheCooldown()
    {
        ThreadPoolStall.ShouldCapture(Before(), Stalled(), lastCapture: null).Should().BeTrue();
        ThreadPoolStall.ShouldCapture(Before(40), Stalled(atSeconds: 40), lastCapture: TimeSpan.FromSeconds(20))
            .Should().BeFalse("a stall that persists is one stall");
        var later = 20 + ThreadPoolStall.CaptureCooldown.TotalSeconds;
        ThreadPoolStall.ShouldCapture(Before(later), Stalled(atSeconds: later), TimeSpan.FromSeconds(20))
            .Should().BeTrue("a stall after the cooldown is a new reading");
    }

    [Fact]
    public void TheLine_GroupsTheWorkers_LargestFirst_AndNamesTheFrame()
    {
        string Stack(string top) =>
            string.Join(Environment.NewLine, $"at {top}", "at Some.Caller()", "at System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()");
        var stacks = new[]
        {
            Stack("Held.Here()"), Stack("Held.Here()"), Stack("Held.Here()"), Stack("Elsewhere.Running()"),
        };

        var line = ThreadPoolStall.Describe(Stalled(), stacks, null);

        line.Should().StartWith("[POOLSTALL] tick=1099");
        line.Should().Contain("20357 queued over 69 thread(s)");
        line.Should().Contain("4 worker stack(s) in 2 distinct shape(s)");
        line.IndexOf("×3:", StringComparison.Ordinal).Should()
            .BeLessThan(line.IndexOf("×1:", StringComparison.Ordinal), "the largest group is printed first");
        line.Should().Contain("at Held.Here()");
    }

    /// <summary>
    /// 🚨 The largest shape of the 2026-11-11 14:40:57Z capture (×20 of 44) was the pool's own idle wait,
    /// and it was read as twenty busy handlers. The line now counts idle workers by name. Control: a busy
    /// worker and a worker whose semaphore wait sits under a work item are not idle.
    /// </summary>
    [Fact]
    public void IdleWorkers_AreCountedByName_SoAnIdleShapeIsNotReadAsWhatHeldThePool()
    {
        string[] idle =
        [
            "System.Threading.LowLevelLifoSemaphore.<WaitNative>g____PInvoke|3_0(IntPtr, Int32)",
            "System.Threading.LowLevelLifoSemaphore.WaitNative(Microsoft.Win32.SafeHandles.SafeWaitHandle, Int32)",
            "System.Threading.LowLevelLifoSemaphore.WaitForSignal(Int32)",
            "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()",
            "System.Threading.Thread.StartCallback()",
        ];
        string[] busy =
        [
            "MeshWeaver.Messaging.MessageService.DrainOne()",
            "System.Threading.ThreadPoolWorkQueue.Dispatch()",
            "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()",
        ];
        string[] semaphoreUnderAWorkItem =
        [
            "System.Threading.LowLevelLifoSemaphore.WaitForSignal(Int32)",
            "Some.Handler.Blocks()",
            "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()",
        ];

        ThreadPoolStall.IsIdleWorker(idle).Should().BeTrue();
        ThreadPoolStall.IsIdleWorker(busy).Should().BeFalse("a worker running a work item is busy");
        ThreadPoolStall.IsIdleWorker(semaphoreUnderAWorkItem).Should().BeFalse(
            "a wait inside a work item holds the worker; only the pool's own loop directly under it is idle");

        var line = ThreadPoolStall.Describe(Stalled(), [Stack(idle), Stack(idle), Stack(busy)], null);
        line.Should().Contain("3 worker stack(s) in 2 distinct shape(s), 2 of them IDLE");
        line.Should().Contain("so only 1 worker(s) were running while the queue waited");

        ThreadPoolStall.Describe(Stalled(), [Stack(busy)], null).Should().Contain("0 of them IDLE");
        ThreadPoolStall.Describe(Stalled(), [Stack(busy)], null).Should().Contain("every worker was busy");
    }

    /// <summary>The census, when one was taken, is the line's last section.</summary>
    [Fact]
    public void TheQueueCensus_IsAppendedAsTheLastSection()
    {
        var census = ThreadPoolQueueCensus.Of([new System.Threading.Tasks.Task(() => { })], 10);
        var line = ThreadPoolStall.Describe(Stalled(), [Stack("Held.Here()", "Worker.Loop()")], null, census: census);

        line.Should().Contain("QUEUED WORK: 1 item(s) read");
        line.IndexOf("QUEUED WORK", StringComparison.Ordinal).Should()
            .BeGreaterThan(line.IndexOf("at Held.Here()", StringComparison.Ordinal), "the census follows the stacks");
    }

    /// <summary>The innermost 12 frames of the 2026-11-07 shape: a worker waiting on an Rx Zip gate.</summary>
    private static string[] ZipWaiter() =>
    [
        "System.Threading.Monitor.Enter_Slowpath(System.Object)",
        "System.Threading.Monitor.ReliableEnter(System.Object, Boolean ByRef)",
        "System.Reactive.Linq.ObservableImpl.Zip+_.Observer.OnNext(System.Object)",
        .. Enumerable.Range(1, ThreadPoolStall.FramesPerGroup - 3).Select(i => $"Rx.Inner{i}()"),
    ];

    private static string Stack(params string[] frames) =>
        string.Join(Environment.NewLine, frames.Select(frame => "at " + frame));

    /// <summary>
    /// 🚨 Workers that share their innermost 12 frames but were called from DIFFERENT places must have
    /// each caller named — the 2026-11-07 line keyed and printed 12 frames, so the caller was lost. The
    /// control is the frame at index 12: present in the stack, printed by the new line.
    /// </summary>
    [Fact]
    public void WorkersSharingTheirInnerFrames_EachDistinctOuterCallerIsPrintedOnce()
    {
        var stacks = new[]
        {
            Stack([.. ZipWaiter(), "EaGraphAuth+CredentialRead.Mailboxes()", "Caller.A()", "PortableThreadPool+WorkerThread.WorkerThreadStart()"]),
            Stack([.. ZipWaiter(), "EaGraphAuth+CredentialRead.Mailboxes()", "Caller.A()", "PortableThreadPool+WorkerThread.WorkerThreadStart()"]),
            Stack([.. ZipWaiter(), "EaGraphAuth+CredentialRead.Mailboxes()", "Caller.B()", "PortableThreadPool+WorkerThread.WorkerThreadStart()"]),
        };

        var line = ThreadPoolStall.Describe(Stalled(), stacks, null);

        line.Should().Contain("3 worker stack(s) in 1 distinct shape(s)", "the group key is still the innermost 12 frames");
        line.Should().Contain("-> ×2 called from:").And.Contain("-> ×1 called from:");
        line.Should().Contain("at Caller.A()").And.Contain("at Caller.B()");
        line.Should().Contain("at EaGraphAuth+CredentialRead.Mailboxes()",
            "frame 13 — the one a 12-frame line could never show — is in the line");
        line.IndexOf("Caller.A()", StringComparison.Ordinal).Should()
            .BeLessThan(line.IndexOf("Caller.B()", StringComparison.Ordinal), "the most frequent caller first");
    }

    /// <summary>
    /// A group waiting on a monitor names its lock site and every thread INSIDE that method without
    /// waiting there — worker or not. A thread waiting at the same site is not a candidate (control),
    /// and neither is one that never entered it (control).
    /// </summary>
    [Fact]
    public void AGroupWaitingOnAMonitor_NamesTheLockSiteAndTheCandidateHolders()
    {
        var waiters = new[] { Stack([.. ZipWaiter(), "Worker.Loop()"]), Stack([.. ZipWaiter(), "Worker.Loop()"]) };
        var others = new[]
        {
            Stack("System.Net.Http.HttpConnectionWaiter.Wait()", "Downstream.Blocking()",
                "System.Reactive.Linq.ObservableImpl.Zip+_.Observer.OnNext(System.Object)", "Timer.Callback()"),
            Stack("Unrelated.Idle()", "Timer.Other()"),
        };

        var line = ThreadPoolStall.Describe(Stalled(), waiters, null, others);

        line.Should().Contain("LOCK waiting to enter a monitor at System.Reactive.Linq.ObservableImpl.Zip+_.Observer.OnNext(System.Object)");
        line.Should().Contain("1 thread(s) are inside it without waiting there");
        line.Should().Contain("at Downstream.Blocking()", "the holder's stack is printed in full");
        line.Should().NotContain("Unrelated.Idle()", "a thread that never entered the lock site is not a candidate");
        line.Should().Contain("2 other thread(s) searched for lock holders");
    }

    [Fact]
    public void AMonitorGroupWithNoThreadInsideTheLockSite_SaysTheHolderIsOutsideTheCapture()
        => ThreadPoolStall.Describe(Stalled(), [Stack([.. ZipWaiter(), "Worker.Loop()"])], null, [])
            .Should().Contain("NO captured thread is inside that method without waiting there — every managed thread was read");

    /// <summary>
    /// When the other threads were NOT read (null), an empty holder list must not be blamed on native
    /// code — the line says the non-pool threads were not searched. Control: the read-and-empty case above.
    /// </summary>
    [Fact]
    public void AMonitorGroupWhoseOtherThreadsWereNotRead_SaysSo_InsteadOfBlamingNativeCode()
    {
        var line = ThreadPoolStall.Describe(Stalled(), [Stack([.. ZipWaiter(), "Worker.Loop()"])], null, otherThreads: null);

        line.Should().Contain("the other threads were NOT read");
        line.Should().Contain("a holder on a non-pool thread (a timer, a scheduler) cannot be named by this capture");
        line.Should().NotContain("every managed thread was read");
    }

    /// <summary>A group that is not waiting on a monitor prints no lock section.</summary>
    [Fact]
    public void AGroupNotWaitingOnAMonitor_PrintsNoLockSection()
        => ThreadPoolStall.Describe(Stalled(), [Stack("Held.Here()", "Worker.Loop()")], null, [])
            .Should().NotContain("LOCK");

    /// <summary>
    /// Full stacks are bounded by the line as a whole: groups past <see cref="ThreadPoolStall.MaxLineChars"/>
    /// are counted, never cut mid-stack, and the line says the budget was spent.
    /// </summary>
    [Fact]
    public void TheLineStaysWithinItsBudget_AndCountsTheShapesItCouldNotPrint()
    {
        var deep = Enumerable.Range(0, 8)
            .Select(g => Stack([.. Enumerable.Range(0, 400).Select(f => $"Shape{g}.Frame{f}(System.String, System.Collections.Generic.IReadOnlyList`1<System.Object>)")]))
            .ToArray();

        var line = ThreadPoolStall.Describe(Stalled(), deep, null);

        line.Length.Should().BeLessThanOrEqualTo(ThreadPoolStall.MaxLineChars + 200);
        line.Should().Contain("budget is spent");
        line.Should().Contain("Shape0.Frame399", "the first shape fits and is printed whole");
    }

    [Fact]
    public void WithoutStacks_TheLineStillNamesTheStall_AndSaysWhyItCannotNameTheFrame()
    {
        ThreadPoolStall.Describe(Stalled(), null, null)
            .Should().Contain("no stack reader is registered in this host");
        ThreadPoolStall.Describe(Stalled(), null, "InvalidOperationException: no DAC")
            .Should().Contain("could NOT be read (InvalidOperationException: no DAC)");
        ThreadPoolStall.Describe(Stalled(), [], null)
            .Should().Contain("found NO ThreadPool worker thread");
    }

    /// <summary>
    /// The 2026-11-09 22:55Z memex-cloud stall (pod <c>…5cf7b87497-4l9xx</c>, Orleans: 15.56&#160;s): two
    /// consecutive heartbeat ticks, verbatim. The pool went from idle to 11,196 queued and still completed
    /// 9,310 items in the period, so the no-progress rule does not fire — the reason no frames were
    /// captured for it. A probe item queued behind that burst waits as long as Orleans' timer did.
    /// </summary>
    [Fact]
    public void ABacklogThePoolIsStillDraining_IsMissedByNoProgress_AndCaughtByQueueDelay()
    {
        var tick201 = new ProcessLivenessSample(201, TimeSpan.FromSeconds(2010.0), 1160, 506, 189,
            TimeSpan.FromSeconds(28.12), 3_600_000_000L, true, 32, 0, 14_003_138);
        var tick202 = new ProcessLivenessSample(202, TimeSpan.FromSeconds(2020.1), 1173, 512, 189,
            TimeSpan.FromSeconds(28.97), 4_290_000_000L, true, 32, 11_196, 14_012_448);
        var reading = ProcessLiveness.Read(tick201, tick202, Period);

        ThreadPoolStall.ShouldCapture(tick201, reading, lastCapture: null)
            .Should().BeFalse("the pool completed 9,310 items in the period — the shape the counters cannot call a stall");
        ThreadPoolStall.ShouldCaptureQueueDelay(TimeSpan.FromSeconds(15.56), tick202.Elapsed, lastCapture: null)
            .Should().BeTrue("a work item queued behind the burst waited as long as the Orleans timer did");
    }

    /// <summary>Controls: an ordinary hand-off, a wait under the threshold, and a capture inside the cooldown do not capture.</summary>
    [Fact]
    public void AShortQueueDelay_OrOneInsideTheCooldown_DoesNotCapture()
    {
        var now = TimeSpan.FromMinutes(40);
        ThreadPoolStall.ShouldCaptureQueueDelay(TimeSpan.Zero, now, null).Should().BeFalse("the probe started");
        ThreadPoolStall.ShouldCaptureQueueDelay(
                ThreadPoolStall.QueueDelayCaptureThreshold - TimeSpan.FromMilliseconds(1), now, null)
            .Should().BeFalse("under the threshold");
        ThreadPoolStall.ShouldCaptureQueueDelay(ThreadPoolStall.QueueDelayCaptureThreshold, now, null)
            .Should().BeTrue("at the threshold");
        ThreadPoolStall.ShouldCaptureQueueDelay(TimeSpan.FromSeconds(20), now, now - TimeSpan.FromMinutes(1))
            .Should().BeFalse("one capture per cooldown, whichever rule took it");
        ThreadPoolStall.ShouldCaptureQueueDelay(TimeSpan.FromSeconds(20), now, now - ThreadPoolStall.CaptureCooldown)
            .Should().BeTrue("the cooldown has passed");
    }

    [Fact]
    public void TheQueueDelayLine_NamesTheWait_ThePoolState_AndTheGroups()
    {
        var sample = new ProcessLivenessSample(202, TimeSpan.FromSeconds(2020.1), 1173, 512, 189,
            TimeSpan.FromSeconds(28.97), 4_290_000_000L, true, 32, 11_196, 14_012_448);
        string Stack(string top) =>
            string.Join(Environment.NewLine, $"at {top}", "at System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart()");

        var line = ThreadPoolStall.DescribeQueueDelay(sample, TimeSpan.FromSeconds(15.56),
            [Stack("Slow.Item()"), Stack("Slow.Item()"), Stack("Other.Item()")], null, []);

        line.Should().StartWith("[POOLSTALL] tick=202: a probe work item queued 15.56s ago had not started");
        line.Should().Contain("11196 queued over 32 thread(s)");
        line.Should().Contain("×2:").And.Contain("Slow.Item()");
        line.IndexOf("Slow.Item()", StringComparison.Ordinal).Should()
            .BeLessThan(line.IndexOf("Other.Item()", StringComparison.Ordinal), "largest group first");
        ThreadPoolStall.DescribeQueueDelay(sample, TimeSpan.FromSeconds(6), null, null)
            .Should().Contain("no stack reader is registered in this host");
    }
}
