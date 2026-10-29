using System.Runtime.InteropServices;
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
/// </summary>
internal sealed class BlockedThreadStacks
{
    /// <summary>How many frames of a thread's stack are reported — the blocking frame is at the top.</summary>
    internal const int MaxFrames = 40;

    private nint pinnedDac;

    /// <summary>
    /// The stacks of <paramref name="managedThreadIds"/>, keyed by managed thread id, read from ONE
    /// dump of this process. A thread no longer in the process (its leaf returned after all) is
    /// reported as such. Throws when the dump cannot be written or read — the caller reports why.
    /// Blocking: run it off any scheduler that matters.
    /// </summary>
    /// <param name="managedThreadIds">The managed thread ids whose stacks are wanted.</param>
    public IReadOnlyDictionary<int, string> Capture(IReadOnlyCollection<int> managedThreadIds)
    {
        PinDac();
        var path = Path.Combine(Path.GetTempPath(), $"mw-blocked-leaf-{Environment.ProcessId}-{Guid.NewGuid():N}.dmp");
        try
        {
            new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Normal, path, logDumpGeneration: false);
            using var target = DataTarget.LoadDump(path);
            if (target.ClrVersions.Length == 0)
                throw new InvalidOperationException($"the dump of process {Environment.ProcessId} carries no CLR runtime");
            using var runtime = target.ClrVersions[0].CreateRuntime();
            return managedThreadIds.Distinct().ToDictionary(
                id => id,
                id => runtime.Threads.FirstOrDefault(t => t.ManagedThreadId == id) is { } thread
                    ? Format(thread)
                    : "(no such thread in the dump — the leaf returned, or its thread ended, after the join expired)");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string Format(ClrThread thread)
    {
        var frames = thread.EnumerateStackTrace()
            .Select(f => f.Method?.Signature ?? f.FrameName ?? f.Kind.ToString())
            .Take(MaxFrames + 1)
            .ToArray();
        if (frames.Length == 0)
            return "(no managed frame on the thread — it is in native code or between managed calls)";
        var shown = frames.Take(MaxFrames).Select(f => "   at " + f);
        return Environment.NewLine + string.Join(Environment.NewLine, shown)
               + (frames.Length > MaxFrames ? $"{Environment.NewLine}   … (deeper frames omitted)" : "");
    }

    private void PinDac()
    {
        if (pinnedDac != 0)
            return;
        var dac = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            OperatingSystem.IsWindows() ? "mscordaccore.dll"
            : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib"
            : "libmscordaccore.so");
        // A host without a DAC (trimmed / self-contained) cannot have it loaded by ClrMD either; the
        // read then fails and the caller reports that.
        if (File.Exists(dac))
            pinnedDac = NativeLibrary.Load(dac);
    }
}
