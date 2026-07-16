using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Pins the CLR Data Access Component (DAC, <c>libmscordaccore.so</c> /
/// <c>mscordaccore.dll</c>) for the lifetime of the process — the root-cause fix for the
/// endemic <c>MeshWeaver.Hosting.Monolith.Test exit=139</c> teardown SIGSEGV that kept
/// turning main red.
///
/// <para><b>The defect (proven from the corruption-time core dump of CI run 29471199052,
/// attempt 2, and reproduced identically by the ALC-unload probe run 29482327524):</b>
/// ClrMD's <c>DataTarget.CreateSnapshotAndAttach</c> dlopens the runtime's DAC in-process.
/// The DAC carries its own copy of the CoreCLR PAL, whose initialization calls
/// <c>pthread_key_create(&amp;key, InternalEndCurrentThreadWrapper)</c> — a PROCESS-GLOBAL
/// registration whose destructor pointer targets the DAC's own code
/// (<c>pal/src/thread/thread.cpp</c>). Every thread that executes DAC code (the ClrMD heap
/// walk) gets a <c>CorUnix::CPalThread*</c> stored in that key. When the
/// <c>DataTarget</c> is disposed, ClrMD dlcloses the DAC and glibc UNMAPS it — but the
/// pthread key is never deleted, and the per-thread values survive on every thread that
/// touched the DAC. Minutes later, when any such thread exits (ordinary ThreadPool
/// retirement — under CI GC pressure, or a dedicated compile thread ending), glibc's
/// <c>__nptl_deallocate_tsd</c> invokes the registered destructor: an instruction fetch
/// into unmapped (or, worse, reused) pages → native SIGSEGV, <c>exit=139</c>, in whatever
/// UNRELATED test happens to be running. The dump showed TWO dangling keys — one per
/// ClrMD-using test class (<c>KernelScriptMemoryLeakTest</c>,
/// <c>MeshHubDisposalLeakTest</c>) — each from a separate DAC load/unload cycle, with the
/// faulting pointer resolving to DAC offset <c>0x2201b0</c> =
/// <c>InternalEndCurrentThreadWrapper</c> and the TLS payload's vptr to
/// <c>vtable for CorUnix::CPalThread</c>.</para>
///
/// <para><b>The invariant this enforces:</b> a shared library that registers
/// process-global callbacks (pthread TLS destructors) must live as long as the process —
/// the same reason <c>libcoreclr.so</c> itself is never unloaded. Taking one extra
/// <see cref="NativeLibrary.Load(string)"/> reference before ClrMD ever loads the DAC
/// gives the module <c>RTLD_NODELETE</c>-equivalent lifetime: ClrMD's own dlclose then
/// merely drops the refcount to ours and the mapping — and with it every registered
/// destructor and vtable — stays valid for any thread that exits later. Subsequent
/// ClrMD sessions re-dlopen the SAME mapping (glibc refcounts by path), so no new keys
/// dangle either.</para>
///
/// <para>NoStaticState note: the pin is a single immutable native HANDLE whose lifetime
/// is deliberately the process (that is its entire purpose) — not a collection, not a
/// cache, and there is nothing to clear between tests: the pthread-key registration it
/// protects is itself process-global.</para>
/// </summary>
internal static class ClrMdDacPin
{
    private static IntPtr pinnedHandle;

    /// <summary>
    /// Idempotently takes the process-lifetime reference on the runtime's DAC. Call
    /// BEFORE the first <c>DataTarget.CreateSnapshotAndAttach</c> so ClrMD's
    /// load/unload cycle can never drop the module's refcount to zero.
    /// </summary>
    public static void EnsurePinned()
    {
        if (Volatile.Read(ref pinnedHandle) != IntPtr.Zero)
            return;

        var dacFileName =
            OperatingSystem.IsWindows() ? "mscordaccore.dll" :
            OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" :
            "libmscordaccore.so";
        var dacPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), dacFileName);
        if (!File.Exists(dacPath))
            return; // self-contained/trimmed host without a DAC — nothing ClrMD could load either

        var handle = NativeLibrary.Load(dacPath);
        // First pin wins; a concurrent second load just returns the same module with an
        // extra refcount — release ours, the winner's reference is the process pin.
        if (Interlocked.CompareExchange(ref pinnedHandle, handle, IntPtr.Zero) != IntPtr.Zero)
            NativeLibrary.Free(handle);
    }
}
