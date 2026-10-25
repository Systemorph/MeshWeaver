using System;
using System.IO;
using System.Threading;
using MeshWeaver.Kernel.Hub;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 The kernel's process-wide console hook (<see cref="CapturingTextWriter"/>) must never need
/// the lock of the writer it replaced. If it does, the hook and anything that captured the old
/// <c>Console.Out</c> take the same two locks in opposite order, and the process deadlocks on its
/// own stdout.
///
/// <para><b>The incident.</b> In core CI the <c>MeshWeaver.Hosting.Orleans.Test</c> host
/// sometimes stopped printing about a minute into its run. It printed no further test results and
/// no <c>[Long Running Test]</c> lines. It then hung until the 8-minute cap killed it (exit 137),
/// while its tests went on to completion in the trace log. A local loop reproduced it on the 9th
/// run, and the dump's <c>syncblk</c> named the cycle:</para>
/// <list type="bullet">
/// <item>xUnit's reporter thread captured <c>Console.Out</c> at startup, BEFORE any kernel cell
/// installed the hook. It held that original <c>SyncTextWriter</c> and, inside its flush, entered
/// <c>ConsolePal.WriteFromConsoleStream</c>. On Unix that method takes <c>lock (Console.Out)</c>,
/// which by then is the hook's own synchronized wrapper.</item>
/// <item>The Microsoft console-logger thread held that wrapper (it writes through the CURRENT
/// <c>Console.Out</c>). Its write fell through <see cref="CapturingTextWriter"/> to the fallback,
/// which was the ORIGINAL <c>SyncTextWriter</c>. So it waited for the reporter's lock.</item>
/// </list>
/// <para>After that, every console writer in the process queued behind those two threads: the
/// long-running reporter, the end-of-run summary, and <c>OrleansMeshPool</c>'s receipt line.
/// That queue is why the host could never exit. The same inversion is reachable in a portal,
/// where any component that kept the startup <c>Console.Out</c> plays the reporter's part.</para>
///
/// <para><b>The property tested.</b> A write through the hooked <c>Console.Out</c> by a thread
/// outside any capture scope completes while another thread HOLDS the replaced writer's lock,
/// which is exactly what xUnit's reporter does while it writes. The test asserts the property
/// directly and never builds the full cycle, so the negative control cannot wedge the test host.
/// Before the fix the write parks until the lock is released, and the bounded wait reports it.</para>
/// </summary>
public class ConsoleHookTakesNoLockOfTheWriterItReplacedTest
{
    [Fact]
    public void AWriteThroughTheHookedConsole_CompletesWhileTheReplacedWritersLockIsHeld()
    {
        AssertTheHookNeverWaitsOn(
            () => CapturingTextWriter.ReplacedOut,
            () => Console.Out.Flush(),
            "Console.Out");
    }

    [Fact]
    public void AWriteThroughTheHookedConsoleError_CompletesWhileTheReplacedWritersLockIsHeld()
    {
        AssertTheHookNeverWaitsOn(
            () => CapturingTextWriter.ReplacedError,
            () => Console.Error.Flush(),
            "Console.Error");
    }

    private static void AssertTheHookNeverWaitsOn(Func<TextWriter?> replaced, Action writeThroughHook, string stream)
    {
        // Install the hook exactly as a kernel cell does. The scope ends at once: the writes below
        // come from a thread outside any capture, which is the console logger's case.
        using (CapturingTextWriter.Capture(TextWriter.Null, TextWriter.Null)) { }
        var original = replaced();
        original.Should().NotBeNull("the hook records the writer it replaced when it installs");

        // A FLUSH, not a write: it reaches the fallback's lock on the same path a write does (the
        // synchronized wrapper locks before it delegates) but prints nothing into the test log.
        var done = 0;
        var faulted = (Exception?)null;
        var writer = new Thread(() =>
        {
            try { writeThroughHook(); }
            catch (Exception ex) { faulted = ex; }
            finally { Volatile.Write(ref done, 1); }
        }) { IsBackground = true, Name = $"hooked-{stream}-writer" };

        bool completedWhileHeld;
        // Hold the replaced writer's lock the way xUnit's reporter holds it while it writes.
        // TextWriter.Synchronized locks on the wrapper instance itself, so this IS that lock.
        lock (original!)
        {
            writer.Start();
            completedWhileHeld = SpinWait.SpinUntil(() => Volatile.Read(ref done) == 1, TimeSpan.FromSeconds(10));
        }
        // Released either way, so a red run never strands the writer. It then completes and joins.
        SpinWait.SpinUntil(() => Volatile.Read(ref done) == 1, TimeSpan.FromSeconds(10))
            .Should().BeTrue($"the {stream} write completes once nothing holds the replaced writer");

        faulted.Should().BeNull();
        completedWhileHeld.Should().BeTrue(
            $"a write through the hooked {stream} must never wait on the lock of the writer the hook replaced. "
            + "Whatever captured that writer before the hook (xUnit's reporter, a host's startup logger) holds "
            + "its lock and then enters lock(Console.Out) inside ConsolePal.WriteFromConsoleStream; a hook that "
            + "falls back THROUGH it takes the same two locks in the opposite order, and the process deadlocks "
            + "on its own stdout (the Hosting.Orleans.Test exit-137 hang)");
    }
}
