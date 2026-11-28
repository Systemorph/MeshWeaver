using System;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// 🚨 Regression (core #8629's shard 6): createdump logged <c>ptrace(ATTACH, tid) ... No such process</c>
    /// for a thread that had just exited, aborted, and the runtime reported the dump as WRITTEN: no error,
    /// no file. That is the same race the thrown shape is retried for, so the first dump returns over no
    /// file and the second is real.
    /// </summary>
    [Fact]
    public void AWriterThatReturnsOverNoFileOnce_IsTakenAgain()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: path =>
        {
            if (Interlocked.Increment(ref dumps) == 1)
                return;
            BlockedThreadStacks.WriteDumpOfThisProcess(path);
        });

        var thread = new Thread(() => ParkedWhileAnUnrelatedThreadExits()) { IsBackground = true, Name = "parked" };
        try
        {
            thread.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref parked) == 1, Budget)
                .Should().BeTrue("precondition: the thread whose stack is wanted is parked before the capture");

            var captured = stacks.Capture([thread.ManagedThreadId]);

            captured[thread.ManagedThreadId].Should().Contain(nameof(ParkedWhileAnUnrelatedThreadExits));
            dumps.Should().BeInRange(2, BlockedThreadStacks.MaxDumpAttempts, "the file-less dump is taken again, bounded");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// 🚨 Regression (core #8409's shard 4): the runtime hands back at most 1023 characters of createdump's
    /// error, and createdump writes a tolerated <c>ptrace(ATTACH, tid) … No such process (3)</c> line for every
    /// thread already gone BEFORE the line that fails the dump. With a dozen threads stopping, the fatal line
    /// was past the cut, the error named nothing but exited threads, and the capture threw it from the first
    /// attempt instead of taking the dump again. The first dump fails with that error, shaped as on CI
    /// (eleven gone threads, the twelfth line cut, 1023 characters); every later dump is real.
    /// </summary>
    [Fact]
    public void AnErrorCutOffBeforeItsFatalLine_AfterOnlyThreadsGoneBeforeAttach_IsTakenAgain()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: path =>
        {
            if (Interlocked.Increment(ref dumps) == 1)
                throw new ServerErrorException(ThreadsGoneBeforeAttach_CutAtTheRuntimesErrorBuffer());
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
                "an error that names only threads which exited is the vanished-thread race, whose cure is a fresh dump");
            dumps.Should().BeInRange(2, BlockedThreadStacks.MaxDumpAttempts,
                "the dump the exiting threads voided is taken again, and the attempts stay bounded");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// Negative control for the cut-error rule: the same attach failures followed by a DIFFERENT fatal line
    /// are classified by that line, so the dump is not taken again.
    /// </summary>
    [Fact]
    public void ThreadsGoneBeforeAttach_FollowedByAnotherFatalLine_AreNotRetried()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: _ =>
        {
            Interlocked.Increment(ref dumps);
            throw new ServerErrorException(
                $"{AttachFailure(Environment.ProcessId + 1)}\n{AttachFailure(Environment.ProcessId + 2)}\n"
                + "[createdump] Could not write dump: No space left on device (28)");
        });

        var failure = Record.Exception(() => stacks.Capture([Environment.CurrentManagedThreadId]));

        failure.Should().BeOfType<ServerErrorException>("a visible fatal line that is not a vanished thread is the capture's answer");
        dumps.Should().Be(1);
    }

    /// <summary>Negative control: the process's OWN id among the attach failures is never a vanished thread.</summary>
    [Fact]
    public void AnErrorCutOffAfterTheProcessItselfWasGone_IsNotRetried()
    {
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: _ =>
        {
            Interlocked.Increment(ref dumps);
            throw new ServerErrorException(
                $"{AttachFailure(Environment.ProcessId)}\n{AttachFailure(Environment.ProcessId + 1)}\n[createdump] Problem susp");
        });

        var failure = Record.Exception(() => stacks.Capture([Environment.CurrentManagedThreadId]));

        failure.Should().BeOfType<ServerErrorException>();
        dumps.Should().Be(1);
    }

    /// <summary>
    /// Negative control (review of #8410): only a CUT last line is let through. A COMPLETE last line is held
    /// to the full "No such process (3)" form for a thread other than the process, so the process's own id or
    /// another errno in the last position is not retried; nor is a cut last line still showing the process's own id.
    /// </summary>
    /// <param name="shape">Which last line follows a gone-thread line.</param>
    [Theory]
    [InlineData("own-id-complete")]
    [InlineData("other-errno-complete")]
    [InlineData("own-id-cut")]
    public void ACompleteOrOwnIdLastLine_IsNotTreatedAsCut(string shape)
    {
        var pid = Environment.ProcessId;
        var last = shape switch
        {
            "own-id-complete" => AttachFailure(pid),
            "other-errno-complete" =>
                $"[createdump] Problem suspending thread: ptrace(ATTACH, {pid + 2}) FAILED Operation not permitted (1)",
            _ => $"[createdump] Problem suspending thread: ptrace(ATTACH, {pid}) FAILED No such pro",
        };
        var dumps = 0;
        var stacks = new BlockedThreadStacks(writeDump: _ =>
        {
            Interlocked.Increment(ref dumps);
            throw new ServerErrorException($"{AttachFailure(pid + 1)}\n{last}");
        });

        var failure = Record.Exception(() => stacks.Capture([Environment.CurrentManagedThreadId]));

        failure.Should().BeOfType<ServerErrorException>($"a last line of shape '{shape}' is not the vanished-thread race");
        dumps.Should().Be(1);
    }

    /// <summary>createdump's tolerated line for a thread that was gone before its attach.</summary>
    private static string AttachFailure(int tid) =>
        $"[createdump] Problem suspending thread: ptrace(ATTACH, {tid}) FAILED No such process (3)";

    /// <summary>
    /// The CI shard's error text, verbatim but the tids: attach failures for threads that are not this process,
    /// cut to the 1023 characters the runtime hands back — so the line that failed the dump is not in it.
    /// </summary>
    private static string ThreadsGoneBeforeAttach_CutAtTheRuntimesErrorBuffer()
    {
        var full = string.Join("\n", Enumerable.Range(1, 20).Select(i => AttachFailure(Environment.ProcessId + 40 + i)))
            + "\n[createdump] GetStatus fopen(/proc/1/status) FAILED No such file or directory (2)";
        var cut = full[..1023];
        cut.Should().NotContain("GetStatus", "precondition: the fatal line is past the cut, as on CI");
        cut.Should().NotEndWith("(3)", "precondition: the last line is cut part-way, as on CI");
        return cut;
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
