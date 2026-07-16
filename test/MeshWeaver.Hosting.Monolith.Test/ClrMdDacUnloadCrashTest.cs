using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 Deterministic repro + pin for the endemic <c>Hosting.Monolith.Test exit=139</c>
/// teardown SIGSEGV (main flaking red since 2026-07-15; survived #467, #483 and #489).
///
/// <para><b>Root cause (named from the corruption-time core of CI run 29471199052 and
/// probe run 29482327524 — identical fault signature in both):</b> ClrMD's
/// <c>DataTarget.CreateSnapshotAndAttach</c> dlopens the DAC (<c>libmscordaccore.so</c>)
/// in-process; the DAC's embedded PAL registers a process-global pthread TLS destructor
/// (<c>pthread_key_create(&amp;key, InternalEndCurrentThreadWrapper)</c>) pointing into
/// the DAC's own code, and stores a <c>CorUnix::CPalThread*</c> in that key on every
/// thread that runs DAC code. Disposing the <c>DataTarget</c> dlcloses and UNMAPS the
/// DAC without deleting the key. Any poisoned thread that exits later (ThreadPool
/// retirement under GC pressure — which the ALC-unload GC probe's forced collections
/// amplify, hence the false "ALC unload" lead) has glibc's
/// <c>__nptl_deallocate_tsd</c> call the dangling destructor: instruction fetch into
/// unmapped/reused pages → native SIGSEGV in whatever unrelated test is running.
/// Fault context proof: <c>RIP == CR2</c> at DAC offset <c>0x2201b0</c>
/// (= <c>InternalEndCurrentThreadWrapper</c>), page-fault error 0x15 (user-mode
/// instruction fetch, protection violation), TLS payload vptr at DAC offset
/// <c>0x243a78</c> (= <c>vtable for CorUnix::CPalThread</c>), and one dangling key per
/// ClrMD-using test class in <c>__pthread_keys</c>.</para>
///
/// <para><b>This test IS the crash, made deterministic:</b> it runs one ClrMD snapshot
/// session on a dedicated thread and then EXITS that thread — the exact
/// destructor-at-thread-exit path that randomly killed CI hosts. Without
/// <see cref="ClrMdDacPin"/> the process dies with SIGSEGV right at
/// <c>Join()</c>; with the pin the DAC stays mapped, the destructor executes real code,
/// and the mapping assertion documents the invariant readably.</para>
/// </summary>
public class ClrMdDacUnloadCrashTest
{
    [Fact(Timeout = 120_000)]
    public void ThreadThatRanAClrMdSnapshot_MustExitSafely_AfterDataTargetDisposal()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "ClrMD snapshot attach (createdump) + glibc __nptl_deallocate_tsd are the Linux CI " +
            "crash path; /proc/self/maps is the Linux view of the mapping invariant.");

        // THE FIX under test: take the process-lifetime reference BEFORE ClrMD's first
        // DAC load, so its Dispose-time dlclose can never unmap the module.
        ClrMdDacPin.EnsurePinned();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Loads the DAC in-process; the heap read below executes DAC code ON THIS
                // THREAD, which plants a CPalThread* in the DAC-registered pthread TLS key.
                using var dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
                using var runtime = dataTarget.ClrVersions[0].CreateRuntime();
                if (runtime.Heap.Segments.Count() == 0)
                    throw new InvalidOperationException("snapshot heap walk saw no segments — the DAC was never exercised");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            // Disposals above dropped ClrMD's own DAC reference. This thread now EXITS with
            // a live value in the DAC's pthread key → glibc __nptl_deallocate_tsd invokes
            // the registered destructor. Unpinned, the DAC is unmapped by now and the call
            // is an instruction fetch into freed pages → native SIGSEGV (exit=139) — the
            // CI crash, deterministically. Pinned, the destructor is real code and frees
            // the CPalThread as designed.
        });
        thread.Start();
        thread.Join();

        Assert.Null(failure);

        // The invariant, in readable form: the DAC must still be mapped after ClrMD
        // released its handle — its pthread-key registration is process-global and
        // irrevocable, so the module's lifetime must be the process's.
        Assert.Contains("libmscordaccore", File.ReadAllText("/proc/self/maps"));
    }
}
