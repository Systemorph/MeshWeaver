---
Name: Collectible Thread-Static Handle Reuse (CoreCLR)
Category: Architecture
Description: A CoreCLR defect that frees an unrelated GC-statics box of a LIVE collectible context when a thread exits — the mechanism behind the dangling static base of native-crash sighting 18, with a deterministic repro, the dump evidence, and what it means for the portal crashes.
Icon: Bug
---

# Collectible thread-static handle reuse (CoreCLR)

**A thread that exits can free an object it never touched, in a collectible context it never
touched.** When a thread has used a `[ThreadStatic]` of a type in collectible context **A**, context A
is unloaded while the thread lives on, and the thread-static *index* A's type held is handed to a type
in a newer collectible context **B**, then the thread's exit calls `FreeHandle` on **B**'s
`LoaderAllocator` with a handle that was minted by **A**. That releases whatever object happens to sit
at that slot of B's handle table — a GC-statics box, a `RuntimeType`, anything. The next GC collects
it, and every later read of that static goes through a pointer to memory that now holds something
else.

This is the mechanism behind [sighting #18](../DebuggingNativeCrashes) (the dangling static base of
`EqualityComparer<IScheduledObserver<IEnumerable<LineOfBusiness>>>`). It is a **runtime** defect, not
this repository's, and it reproduces deterministically in a 60-line program on .NET `10.0.11` and
`10.0.12`. The corrupted box is absent from `m_slots` while the rest of the handle table looks normal.
Investigated on Systemorph/MeshWeaver#4654.

## The defect, in the runtime source

All references are `dotnet/runtime` `release/10.0`. `main` has the same code as of 2026-09-26.

1. **A thread's first touch of a collectible thread static** allocates the thread's data block and
   pins it with a handle in the **type's** `LoaderAllocator`. The thread records that handle, an index
   into the allocator's managed `m_slots` array, in its own `pLoaderHandles[tlsIndex]`
   (`threadstatics.cpp`, `GetThreadLocalStaticBase`: `*pLoaderHandle = pMT->GetLoaderAllocator()->AllocateHandle(gc.tlsEntry)`).
2. **When that allocator is destroyed**, `FreeTLSIndicesForLoaderAllocator` marks its TLS indices
   *cleared* in the global map (`TLSIndexToMethodTableMap::Clear`). It **does not touch any thread's
   `pLoaderHandles`**, so the stale handle stays in every live thread that used the type.
   `pLoaderHandles` appears in exactly two files, `threads.h` and `threadstatics.cpp`, and no other
   code scrubs it.
3. **The next collectible type that needs a thread-static index reuses the cleared one**
   (`GetTLSIndexForThreadStatic` → `FindClearedIndex`), and that type lives in a different allocator.
4. **When the thread exits**, `FreeLoaderAllocatorHandlesForTLSData` walks the *current* map and, for
   every index where the thread's `pLoaderHandles` entry is non-null, calls
   `entry.pMT->GetLoaderAllocator()->FreeHandle(handle)`. That is the **new** type's allocator, called
   with the **old** allocator's handle. `FreeHandle` writes `null` into that slot of the new allocator's
   `m_slots` and pushes the index onto the free stack.
5. The victim is whatever lived at that slot. For a **GC-statics box** of a collectible type
   (`LoaderAllocator::AllocateGCHandlesBytesForStaticVariables`), the `m_slots` entry is the **only**
   strong root. `m_pGCStatics` is tracked by a *weak interior* handle, which is nulled when the box dies
   and leaves `m_pGCStatics` untouched. The box is collected, and `m_pGCStatics` keeps its old address.
6. The next `AllocateHandle` pops the freed index and fills the slot again, so **the handle table shows
   no gap afterwards**. Sighting #18 looked like that: `m_slots` had no null entries inside the used
   range and held no box for V.

A thread that **touches the new type first** overwrites its stale entry (the old handle is leaked,
which is harmless). The corruption needs the thread to **exit** without touching the new type.

## The repro — deterministic, with a control

Two projects. The library is loaded twice, into two collectible contexts:

```csharp
// lib.csproj — net10.0
namespace Lib;
public sealed class Marker { public long Tag = 0x5EED; }
public static class Tls { [System.ThreadStatic] static object t; public static void Touch() => t = new object[] { new Marker() }; }
public static class H0 { public static object V = new Marker(); }   // … H1 … H399, identical
public static class Probe
{
    public static void TouchAll() { _ = H0.V; /* … */ _ = H399.V; }
    public static int CountBad() { int bad = 0; if (!(H0.V is Marker m0 && m0.Tag == 0x5EED)) bad++; /* … */ return bad; }
}
```

The host:

1. Loads the library into collectible context **A**. It starts a background thread that calls
   `Tls.Touch()`, drops its reference to A, and parks.
2. Unloads A and runs `GC.Collect` until A's `WeakReference` is dead. The parked thread keeps its stale
   handle.
3. Loads the library into context **B**, calls `Probe.TouchAll()` (400 GC-statics boxes, one slot each
   in B's `m_slots`), then calls `Tls.Touch()` on the main thread. B's `Tls` reuses A's cleared index.
4. **Repro:** releases the parked thread and joins it (thread exit). **Control:** keeps it parked.
5. Runs five `GC.Collect`s, allocates 200,000 small arrays, collects again, and counts statics that no
   longer read a `Marker`.

| runtime | repro (thread exits) | control (thread kept alive) |
|---|---|---|
| `10.0.11`, macOS arm64 | **5 of 5 runs: 1 static corrupted** | 5 of 5 runs: 0 |
| `10.0.12`, linux-x64 (container, `DOTNET_EnableWriteXorExecute=0`, see below) | **3 of 3 runs: `H0.V` now reads `null`** | 3 of 3 runs: 0 |

The failure can take either form. In one arm64 run the dangling base pointed at a reused object, and
calling `GetType()` on it died with **`System.AccessViolationException` → `Fatal error.` → exit 134**.
That is sighting #18's exit shape. On x64 the reclaimed memory read as zero.

🚨 Under x64 **emulation** (colima, `--platform linux/amd64`), W^X on made even the **control**
segfault. A non-collectible GC baseline passed in the same container. With
`DOTNET_EnableWriteXorExecute=0`, the control passes every run and the repro corrupts every run. So
the emulated W^X crash is an artefact of the emulator, not a finding. Do not read it as evidence
either way.

## Evidence from the #18 dump that the preconditions hold in our process

Read with ClrMD over `MeshWeaver.Futu-1773.dmp` (the probe walks the three collectible allocators'
`m_slots`):

- The allocator whose box went missing (`LoaderAllocator 0x7fd74200c3c0`, the `v7-…` NodeType compile)
  holds a **collectible thread-static data block**. `m_slots[214]` is an `Object[1]` containing a
  `SharedArrayPoolThreadLocalArray[]`, which is the `[ThreadStatic] t_tlsBuckets` of
  **`SharedArrayPool<LineOfBusiness>`**. Next to it, `[213]` holds the pool's GC-statics box and `[215]`
  holds its lambda cache. Any `ArrayPool<T>.Shared` use over a NodeType-compiled `T` creates one,
  including the BCL's own pooled builders (LINQ `ToArray` and similar). Our code never has to write
  `[ThreadStatic]` itself.
- The process had **three** collectible NodeType contexts resident (`v7`, `v14` and a third). Every
  recompile retires one and mints another, so TLS indices are cleared and reused all the time.
- V's box is missing from `m_slots`, and `m_slots` has **no null entry inside its used range**. Step 6
  predicts exactly that. The earlier reading of that fact as *"never registered"* is not the only
  explanation, because a freed-then-reused slot looks the same.

**Not established from the dump:** which thread exited, which index was reused, and which handle was
freed. The per-thread `pLoaderHandles` arrays of threads that have already exited no longer exist, and
the dump cannot show history. The claim is that **this mechanism produces exactly #18's state, and all
its preconditions are present in #18's process**. It does not prove that this mechanism is what
happened there.

## Why this codebase meets it often: thread exits

The trigger is a **thread exit**. This process has two steady sources of them:

- **ThreadPool workers retire** after about 20 s idle. Those are the threads hub turns and
  deserialisation run on, so they touch NodeType-typed `ArrayPool<T>` constantly.
- **Every `IIoPool` blocking lane starts a fresh thread per burst, and that thread exits when its queue
  drains.** `LimitedConcurrencyLevelTaskScheduler.NotifyThreadPoolOfPendingWork` runs
  `new Thread(_ => DrainQueue())`, and `DrainQueue` returns when `_tasks` is empty. The CPU lane has
  worked this way since the dedicated-thread compile lane, and every IO lane since #5678 (blocking
  leaves off the ThreadPool, merged 2026-09-25 06:43Z; the images running on
  memex.systemorph.com / memex.meshweaver.cloud on 2026-09-26, core `4c8530d7dd`, contain it). Each
  blocking-leaf burst is therefore one thread exit, and one more chance to free a handle belonging to
  a context unloaded since that thread first ran.

Combined with the recompile cadence measured on `memex` (one new compiled assembly every ~8 s for
hours, #4654), the three conditions the defect needs (retire a context, reuse its index, exit a thread
that held the old handle) are routine, not rare.

## What this does and does not explain

| | |
|---|---|
| sighting #18 (CI, exit 134, dangling collectible static base) | **Explained by mechanism.** It reproduces the exact state. The dump cannot show which thread exited |
| sightings #1–#17 (CI, exit 139, a MethodTable word reading **exactly zero** inside `libcoreclr`) | **Consistent, not shown.** A static that dangles into reclaimed memory reads `null` (the x64 repro) or a wrong object. Once code copies that pointer into a live object, the GC marks through a pointer whose "MethodTable" is zeroed free space, which is that family's fingerprint. Nobody has traced a single #1–#17 dump back to a freed handle |
| #4654 and the ci.9218 portal deaths (exit 139 at `addr (nil)` and at `0x1880000005`; exit 134 ×3) | **Not read.** The production dumps are on the `memex-data` PVC behind break-glass access. Their fields are compatible with this mechanism and with others |

## Remedies

**The root fix is upstream.** It has two possible shapes. `FreeTLSIndicesForLoaderAllocator` could
null every live thread's `pLoaderHandles[index]` for the indices it clears, under the same
`g_TLSCrst` that `FreeLoaderAllocatorHandlesForTLSData` already takes. Or a thread's handle could
carry the identity of the allocator that minted it, with `FreeLoaderAllocatorHandlesForTLSData`
skipping on a mismatch. The repro above is the report. Filing it on `dotnet/runtime` is a public act
on the maintainer's behalf, and it has not been done.

**In this repository**, the defect fires only on a thread **exit**, so two changes remove it until a
runtime fix ships. Both are **stopgaps against a runtime defect**, and they are named as such:

1. `System.Threading.ThreadPool.ThreadsToKeepAlive = -1` (runtimeconfig, or
   `DOTNET_ThreadPool_ThreadsToKeepAlive=-1`). Pool workers stop retiring. The cost is that the
   pool's peak thread count stays allocated.
2. `IIoPool` lanes keep their dedicated threads alive when idle instead of exiting on every drain.
   That is a change to the sealed scheduler and needs its own review.

Neither has been applied. Each changes thread lifetime for the whole process, so each is an owner's
decision.

## Related

- [Debugging Native Crashes](../DebuggingNativeCrashes): sighting #18, and the fact-6 recipe that
  found the dangling base
- [Controlled IO Pooling](../ControlledIoPooling): the `IIoPool` lanes whose threads exit per drain
- [Node Type Compilation](../NodeTypeCompilation): where collectible contexts are minted and retired
