using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using MeshWeaver.Connection.Orleans;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 #7802 regression: an UNRELATED thread that exits while the dump is being taken must not cost the
/// parked thread its stack. On a CI shard the runtime's dump writer (createdump) attached to a thread
/// that was already exiting, could not then read <c>/proc/&lt;tid&gt;/status</c>, and failed the whole
/// dump — so the silo-stop report said the parked canceller's stack "could not be read".
///
/// <para>The race inside createdump is a few microseconds wide and cannot be hit on demand, so the
/// reproduction makes it deterministic at the one seam this process owns: the first dump really has an
/// unrelated thread start and EXIT during it, and fails exactly as createdump fails for that thread;
/// every later dump is a real dump of this process. The parked thread is real, and its stack is read by
/// ClrMD from the real dump. Before the fix the first failure was the capture's answer.</para>
/// </summary>
public class BlockedThreadStacksThreadExitTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private int parked;
    private int release;

    [Fact]
    public void AThreadThatExitsDuringTheDump_DoesNotCostTheParkedThreadItsStack()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: path =>
        {
            if (Interlocked.Increment(ref dumps) == 1)
                throw AnUnrelatedThreadExitsDuringTheDump();
            BlockedThreadStacks.WriteDumpOfThisProcess(path);
        });

        var thread = new Thread(() => ParkedWhileAnUnrelatedThreadExits()) { IsBackground = true, Name = "parked" };
        try
        {
            thread.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref parked) == 1, Budget)
                .Should().BeTrue("precondition: the thread whose stack is wanted is parked before the capture");

            var captured = stacks.Capture([thread.ManagedThreadId]);

            captured[thread.ManagedThreadId].Should().Contain(nameof(ParkedWhileAnUnrelatedThreadExits),
                "the parked thread is still there, and an unrelated thread's exit must not lose its stack");
            // The replacement dump is a REAL dump, so it can meet the same race on its own; any count
            // from 2 up to the bound is correct. Exact counts are asserted only where the writer is fully faked.
            dumps.Should().BeInRange(2, BlockedThreadStacks.MaxDumpAttempts,
                "the dump the exit voided is taken again, and the attempts stay bounded");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>Negative control: a dump failure that is NOT a vanished thread is not retried.</summary>
    [Fact]
    public void ADumpFailureThatIsNotAVanishedThread_IsReportedFromTheFirstAttempt()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: _ =>
        {
            Interlocked.Increment(ref dumps);
            throw new ServerErrorException(
                $"[createdump] Problem suspending thread: ptrace(ATTACH, {Environment.ProcessId}) FAILED Operation not permitted (1)");
        });

        var failure = Record.Exception(() => stacks.Capture([Environment.CurrentManagedThreadId]));

        failure.Should().BeOfType<ServerErrorException>("a failure a fresh dump cannot cure is the capture's answer");
        dumps.Should().Be(1);
    }

    /// <summary>When every dump is voided by an exiting thread, the error names each attempt's reason.</summary>
    [Fact]
    public void WhenEveryDumpIsVoidedByAnExitingThread_TheErrorNamesEachAttempt()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: _ =>
        {
            Interlocked.Increment(ref dumps);
            throw AnUnrelatedThreadExitsDuringTheDump();
        });

        var failure = Record.Exception(() => stacks.Capture([Environment.CurrentManagedThreadId]));

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain($"attempt {BlockedThreadStacks.MaxDumpAttempts}:")
            .And.Contain("GetStatus fopen(/proc/");
        dumps.Should().Be(BlockedThreadStacks.MaxDumpAttempts, "the attempts are bounded");
    }

    /// <summary>
    /// Starts an unrelated thread, lets it EXIT, and returns the error createdump writes for a listed
    /// thread whose status file vanished before it was read (the CI shard's text, verbatim but the tid).
    /// </summary>
    private static ServerErrorException AnUnrelatedThreadExitsDuringTheDump()
    {
        var tid = 0;
        var unrelated = new Thread(() => Volatile.Write(ref tid, NativeThreadId())) { IsBackground = true };
        unrelated.Start();
        SpinWait.SpinUntil(() => !unrelated.IsAlive, Budget)
            .Should().BeTrue("the unrelated thread exits while the dump is being taken");
        return new ServerErrorException(
            $"[createdump] Gathering state for process {Environment.ProcessId} dotnet{Environment.NewLine}"
            + $"[createdump] Target process is alive{Environment.NewLine}"
            + $"[createdump] GetStatus fopen(/proc/{Volatile.Read(ref tid)}/status) FAILED No such file or directory (2){Environment.NewLine}"
            + "[createdump] Failure took 31ms");
    }

    /// <summary>The OS id of the calling thread where <c>/proc</c> names it; otherwise an id that is not the process's.</summary>
    private static int NativeThreadId()
    {
        var target = File.Exists("/proc/thread-self/status")
            ? new DirectoryInfo("/proc/thread-self").LinkTarget
            : null;
        return target is not null && int.TryParse(Path.GetFileName(target), out var tid)
            ? tid
            : Environment.ProcessId + 1;
    }

    /// <summary>The frame the capture must name. Not inlined, so it is a frame of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ParkedWhileAnUnrelatedThreadExits()
    {
        Volatile.Write(ref parked, 1);
        SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Budget);
    }
}
