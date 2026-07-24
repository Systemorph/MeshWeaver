using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using MeshWeaver.Kernel.Hub;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repro for the <see cref="MeshHubDisposalLeakTest"/> CI flake
/// (run 30068597014, shard 1): the ClrMD retention path was
/// <c>TimerQueueTimer → ExecutionContext → AsyncLocalValueMap →
/// MeshWeaver.Kernel.Hub.LoggerTextWriter → ActivityLogLogger → MessageHub
/// (kernelExec/…/_Activity/…, RunLevel=6)</c> — a DISPOSED kernel activity hub,
/// and through it the whole mesh, pinned forever.
///
/// <para>Mechanism: <see cref="CapturingTextWriter.Capture"/> stores the script's
/// stdout/stderr pipe in an <see cref="AsyncLocal{T}"/>. Any long-lived timer (or
/// pooled work item) whose creation falls INSIDE the capture window snapshots the
/// ExecutionContext — including that AsyncLocal map. Restoring the AsyncLocal on
/// scope disposal only affects the current flow; it can never reach the frozen
/// snapshots, so the writer → activity-hub → mesh graph stays rooted for the
/// timer's lifetime. Intermittent on CI because it needs an infrastructure
/// timer's first creation to land inside a script run.</para>
///
/// <para>The fix stores the target behind a mutable indirection cell; disposing
/// the capture scope nulls the cell's target, severing the graph even inside
/// already-captured ExecutionContexts. This test creates a timer inside a capture
/// scope, keeps the timer alive, and asserts the capture target is collectible
/// after the scope is disposed.</para>
/// </summary>
public class ConsoleCaptureExecutionContextLeakTest
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Target, Timer Timer) CreateTimerInsideCaptureScope()
    {
        var target = new StringWriter(); // stands in for LoggerTextWriter → ActivityLogLogger → hub
        Timer timer;
        using (CapturingTextWriter.Capture(target, target))
        {
            // A long-lived timer created inside the capture scope snapshots the
            // ExecutionContext — exactly what a lazily-initialized infrastructure
            // timer does when its first creation falls inside a kernel script run.
            timer = new Timer(static _ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        return (new WeakReference(target), timer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollect(WeakReference weak)
    {
        for (var i = 0; i < 12 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    [Fact]
    public void CaptureScopeDisposal_SeversTargetFromCapturedExecutionContexts()
    {
        var (weak, timer) = CreateTimerInsideCaptureScope();
        using (timer) // the timer stays alive across the collect — the CI TimerQueue pin
        {
            ForceCollect(weak);
            weak.IsAlive.Should().BeFalse(
                "disposing the console-capture scope must sever the capture target from " +
                "ExecutionContexts snapshotted by timers created inside the scope — otherwise " +
                "the LoggerTextWriter → ActivityLogLogger → kernel activity hub → mesh graph " +
                "stays pinned for the timer's lifetime (MeshHubDisposalLeakTest CI retention path)");
        }
    }
}
