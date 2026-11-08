using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MeshWeaver.Hosting;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// 🚨 Reads the managed stacks of named threads of THIS process — once, from one dump — so a pooled
/// leaf that outlived the silo join names the frame it is stuck in (#7277).
///
/// <para><b>Why a dump.</b> .NET has no in-process API that walks ANOTHER thread's stack. The one the
/// platform already relies on is the runtime's own dump writer (<c>createdump</c>, the facility behind
/// <c>DOTNET_DbgEnableMiniDump</c> and the <c>AnalyzeDump</c> action — Doc/Architecture/DebuggingNativeCrashes):
/// <see cref="DiagnosticsClient.WriteDump(DumpType, string, bool)"/> asks the runtime over its diagnostic
/// port to write a <see cref="DumpType.Normal"/> minidump (thread stacks and module data, no heap), and
/// ClrMD reads the named threads back out of it. The dump is written to the temp directory and deleted
/// once read: the stacks are the product, never the file.</para>
///
/// <para><b>Bounded and cheap.</b> It runs only on the expiry path of the silo join — after the silo has
/// already decided to release over live work — at most once per stop, and the caller bounds it by its
/// own budget. Measured on a developer machine: a 26 MB minidump in ~0.8 s (the process is suspended
/// for that long, as for any dump) and the stacks read in ~0.6 s.</para>
///
/// <para>🚨 <b>The DAC stays mapped for the life of the process.</b> Reading a dump loads the runtime's
/// Data Access Component in-process, and the DAC's PAL registers a process-global pthread-key destructor
/// that points into its own code. When ClrMD disposes its target it unloads the DAC, and any thread
/// that ran DAC code then crashes when it exits (SIGSEGV — measured in MeshWeaver.Plugins'
/// <c>ClrMdDacUnloadCrashTest</c>). One extra <see cref="NativeLibrary.Load(string)"/> taken BEFORE ClrMD
/// loads it keeps the mapping for the life of the process, so ClrMD's unload only drops the count to
/// ours. The handle is instance state held by this mesh-scoped singleton and deliberately never freed.</para>
///
/// <para>🚨 <b>A thread that exits during the dump voids that dump, never the capture.</b> On Linux the
/// runtime's dump writer (<c>createdump</c>) lists <c>/proc/&lt;pid&gt;/task</c>, attaches to each thread,
/// and then reads each listed thread's <c>/proc/&lt;tid&gt;/status</c> and registers. It tolerates a thread
/// gone before the attach (<c>ptrace(ATTACH) … No such process</c>), but a thread that was already EXITING
/// when it attached disappears before those reads, and createdump then fails the WHOLE dump
/// (<c>GetStatus fopen(/proc/&lt;tid&gt;/status) FAILED No such file or directory</c>) — losing the parked
/// thread's stack for an unrelated thread's exit (#7802, measured on a CI shard while the silo's threads
/// were stopping). The dump writer is outside this process and cannot be told to skip a thread, so a dump
/// that failed for exactly that reason — a thread other than this process vanished mid-dump — is taken
/// again: the next enumeration no longer lists the thread that exited. Any other failure is reported as
/// before, and when every attempt fails the error names each attempt's reason.</para>
/// </summary>
internal sealed class BlockedThreadStacks(string? dacPath = null, Action<string>? writeDump = null)
{
    /// <summary>
    /// How many dumps one capture may take when each earlier one failed because a thread exited during
    /// it. Each attempt costs one dump (about a second); the thread that voided a dump is gone from the next.
    /// </summary>
    internal const int MaxDumpAttempts = 3;

    /// <summary>
    /// The createdump failures that mean "a listed thread exited during the dump": its status file is
    /// gone (ENOENT, 2), or reading its registers finds no such thread (ESRCH, 3). Group <c>tid</c> is
    /// the thread — the process itself vanishing is a different failure and is never retried.
    /// </summary>
    private static readonly Regex VanishedThread = new(
        @"GetStatus fopen\(/proc/(?<tid>\d+)/status\) FAILED [^\r\n]*\(2\)"
        + @"|ptrace\(PTRACE_GETREGSET, (?<tid>\d+), NT_\w+\) FAILED [^\r\n]*\(3\)",
        RegexOptions.CultureInvariant);

    private readonly Action<string> dumpWriter = writeDump ?? WriteDumpOfThisProcess;

    /// <summary>Asks the runtime, over its diagnostic port, to write a Normal minidump of this process.</summary>
    /// <param name="path">Where the dump is written.</param>
    internal static void WriteDumpOfThisProcess(string path) =>
        new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Normal, path, logDumpGeneration: false);

    /// <summary>
    /// Whether <paramref name="failure"/> is the dump writer reporting that a thread OTHER than this
    /// process's main one exited while the dump was being taken — the one failure a fresh dump cures.
    /// </summary>
    /// <param name="failure">What writing the dump threw.</param>
    internal static bool IsVanishedThread(Exception failure) =>
        VanishedThread.Matches(failure.Message).Any(
            m => int.TryParse(m.Groups["tid"].Value, out var tid) && tid != Environment.ProcessId);

    /// <summary>How many frames of a thread's stack are reported — the blocking frame is at the top.</summary>
    internal const int MaxFrames = 40;

    /// <summary>The DAC this process's runtime ships beside itself — the ONE DAC a capture may use.</summary>
    private readonly string dac = dacPath ?? Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
        OperatingSystem.IsWindows() ? "mscordaccore.dll"
        : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib"
        : "libmscordaccore.so");

    private nint pinnedDac;

    /// <summary>
    /// The stacks of <paramref name="managedThreadIds"/>, keyed by managed thread id, read from ONE
    /// dump of this process. A thread no longer in the process (its leaf returned after all) is
    /// reported as such. Throws when the dump cannot be written or read — the caller reports why.
    /// Blocking: run it off any scheduler that matters.
    /// </summary>
    /// <param name="managedThreadIds">The managed thread ids whose stacks are wanted.</param>
    public IReadOnlyDictionary<int, string> Capture(IReadOnlyCollection<int> managedThreadIds)
        => Read(runtime => (IReadOnlyDictionary<int, string>)managedThreadIds.Distinct().ToDictionary(
            id => id,
            id => runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == id) is { } thread
                ? Format(thread)
                : "(no such thread in the dump — the leaf returned, or its thread ended, after the join expired)"));

    /// <summary>
    /// The stacks of every ThreadPool WORKER thread of this process, read from ONE dump — what a
    /// stalled pool's workers are standing in (MeshWeaver#5388, the <c>[POOLSTALL]</c> line). A worker
    /// is a thread the runtime marks <see cref="ClrThreadState.TS_TPWorkerThread"/>, or one whose
    /// outermost frames are the portable pool's worker loop — the second test covers a runtime that
    /// does not set the flag on its managed workers. Throws when the dump cannot be written or read.
    /// Blocking: run it off any scheduler that matters.
    /// </summary>
    /// <returns>One entry per worker: its frames, innermost first, one per line.</returns>
    public IReadOnlyList<string> CaptureThreadPoolWorkers() => CaptureThreadPool().Workers;

    /// <summary>
    /// Every live managed thread's stack from ONE dump, split into the ThreadPool workers (as
    /// <see cref="CaptureThreadPoolWorkers"/>) and every other thread — the other threads are where a
    /// monitor the workers wait on may be held. Each stack is captured to
    /// <see cref="MaxStallFrames"/> frames, so the OUTER caller that led a worker into its blocking frame
    /// is in the reading (the 2026-11-07 capture, cut at 12 frames, named the gate but not its caller).
    /// The capture thread itself is left out. Throws when the dump cannot be written or read.
    /// Blocking: run it off any scheduler that matters.
    /// </summary>
    /// <returns>The workers' and the other threads' stacks, frames innermost first, one per line.</returns>
    public ThreadPoolStacks CaptureThreadPool()
    {
        var self = Environment.CurrentManagedThreadId;
        return Read(runtime =>
        {
            var threads = runtime.Threads
                .Where(thread => thread.IsAlive && thread.ManagedThreadId != self)
                .Select(thread => (thread, frames: Frames(thread, int.MaxValue)))
                .Where(t => t.frames.Count > 0)
                .ToArray();
            bool IsWorker((ClrThread thread, IReadOnlyList<string> frames) t) =>
                t.thread.State.HasFlag(ClrThreadState.TS_TPWorkerThread)
                || t.frames.Any(frame => frame.Contains(PoolWorkerLoop, StringComparison.Ordinal));
            string Render((ClrThread thread, IReadOnlyList<string> frames) t) =>
                string.Join(Environment.NewLine, t.frames.Take(MaxStallFrames).Select(frame => "at " + frame));
            return new ThreadPoolStacks(
                threads.Where(IsWorker).Select(Render).ToArray(),
                threads.Where(t => !IsWorker(t)).Select(Render).ToArray());
        });
    }

    /// <summary>
    /// How many frames of each thread a <c>[POOLSTALL]</c> capture keeps — enough for a whole Rx chain
    /// down to the pool's worker loop. It guards against a corrupt, looping stack walk, not against size:
    /// the line bounds its own size (<c>ThreadPoolStall.MaxLineChars</c>).
    /// </summary>
    internal const int MaxStallFrames = 512;

    /// <summary>The portable ThreadPool's worker loop — on the outermost frames of every worker it runs.</summary>
    internal const string PoolWorkerLoop = "PortableThreadPool+WorkerThread";

    /// <summary>One dump of this process, read through the pinned DAC, and deleted once read.</summary>
    private T Read<T>(Func<ClrRuntime, T> read)
    {
        // Pinned FIRST, and nothing is dumped without it: see PinDac.
        PinDac();
        var path = Path.Combine(Path.GetTempPath(), $"mw-stacks-{Environment.ProcessId}-{Guid.NewGuid():N}.dmp");
        try
        {
            WriteDump(path);
            using var target = DataTarget.LoadDump(path);
            if (target.ClrVersions.Length == 0)
                throw new InvalidOperationException($"the dump of process {Environment.ProcessId} carries no CLR runtime");
            // The PINNED DAC, by path — never ClrMD's own resolution, which may find another copy
            // (a symbol server, a file locator) that the pin does not hold and its unload would then
            // pull out from under every thread that ran it.
            using var runtime = CreateRuntime(target.ClrVersions[0]);
            return read(runtime);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Writes the dump, taking it again when a thread exited during it (see the class remarks). Each
    /// failed attempt's reason is kept, so a capture that still fails says why every dump failed.
    /// </summary>
    private void WriteDump(string path)
    {
        var failures = ImmutableList<Exception>.Empty;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                dumpWriter(path);
                return;
            }
            catch (Exception ex) when (IsVanishedThread(ex))
            {
                failures = failures.Add(ex);
                if (File.Exists(path))
                    File.Delete(path);
                if (attempt >= MaxDumpAttempts)
                    throw new InvalidOperationException(
                        $"every one of {MaxDumpAttempts} dumps of process {Environment.ProcessId} failed because a thread "
                        + "exited while it was being taken: "
                        + string.Join(" | ", failures.Select((f, i) => $"attempt {i + 1}: {f.Message.Trim()}")),
                        new AggregateException(failures));
            }
        }
    }

    private static IReadOnlyList<string> Frames(ClrThread thread, int max) =>
        thread.EnumerateStackTrace()
            .Select(f => f.Method?.Signature ?? f.FrameName ?? f.Kind.ToString())
            .Take(max)
            .ToArray();

    /// <summary>
    /// The runtime of the dump, read through the pinned DAC. 🚨 On Linux a <see cref="DumpType.Normal"/>
    /// minidump does not carry the version resource of <c>libcoreclr.so</c> — ClrMD reads the dump's
    /// runtime version as <c>0.0</c> and its DAC check then refuses the matching DAC
    /// (<c>Mismatched dac. Dac version: 10.0.…, expected: 0.0.</c> — measured on the CI shard and in a
    /// <c>dotnet/sdk:10.0</c> container; macOS dumps carry the version). Where the version IS readable,
    /// ClrMD's own check stands. Where it is not, the identity is proven another way: the dump is of
    /// THIS process, so the runtime it names must be loaded from the very directory the pinned DAC
    /// sits in — the DAC that runtime ships with. Any other directory is refused, never guessed.
    /// </summary>
    private ClrRuntime CreateRuntime(ClrInfo clr)
    {
        if (clr.Version != default(Version) && clr.Version != new Version(0, 0))
            return clr.CreateRuntime(dac);
        var runtimeDirectory = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        var dacDirectory = Path.GetDirectoryName(dac);
        if (string.IsNullOrEmpty(runtimeDirectory)
            || !string.Equals(Path.GetFullPath(runtimeDirectory), Path.GetFullPath(dacDirectory!),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the dump does not carry its runtime's version, and its runtime '{clr.ModuleInfo.FileName}' is not "
                + $"loaded from the directory of the pinned DAC '{dac}' — the DAC cannot be proven to match, so the "
                + "stacks are not read");
        return clr.CreateRuntime(dac, ignoreMismatch: true);
    }

    private static string Format(ClrThread thread)
    {
        var frames = Frames(thread, MaxFrames + 1);
        if (frames.Count == 0)
            return "(no managed frame on the thread — it is in native code or between managed calls)";
        var shown = frames.Take(MaxFrames).Select(f => "   at " + f);
        return Environment.NewLine + string.Join(Environment.NewLine, shown)
               + (frames.Count > MaxFrames ? $"{Environment.NewLine}   … (deeper frames omitted)" : "");
    }

    /// <summary>
    /// Takes the process-lifetime reference on the runtime's own DAC, or REFUSES the capture. A host
    /// without a local DAC (trimmed / self-contained) must not let ClrMD resolve one elsewhere: that
    /// copy would be loaded without the pin, and its unload would crash every thread that ran it on
    /// exit (review of #7584). The caller reports the refusal per leaf.
    /// </summary>
    private void PinDac()
    {
        if (pinnedDac != 0)
            return;
        if (!File.Exists(dac))
            throw new InvalidOperationException(
                $"no DAC at '{dac}' to pin — the stacks are not read, because a DAC ClrMD resolved elsewhere "
                + "would be unloaded under the threads that ran it");
        pinnedDac = NativeLibrary.Load(dac);
    }
}
