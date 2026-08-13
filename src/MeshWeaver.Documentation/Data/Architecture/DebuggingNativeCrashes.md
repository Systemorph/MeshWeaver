---
Name: Debugging Native Crashes (core dumps)
Category: Architecture
Description: How to get and read a CI core dump when a test host dies on a signal (exit=139 SIGSEGV, exit=134 abort) instead of failing a test.
Icon: Bug
---

# Debugging native crashes (core dumps)

A test host that dies on a **signal** does not fail a test — it fails the *shard*. There is no
assertion, no stack in the trx, often no trx at all. CI reds it via the exit-marker gate:

```
[CI] MeshWeaver.FutuRe.Test exit=139
##[error]Shard 3: a test host exited non-zero (failure, crash, or timeout kill):
```

| marker | meaning |
|---|---|
| `exit=139` | SIGSEGV (128+11) — segmentation fault |
| `exit=134` | SIGABRT (128+6) — runtime abort / failfast |
| `exit=124` | **not** a crash — CI's per-project wall-clock cap (`timeout --signal=TERM --kill-after=30s 8m` in `dotnet-test.yml`) killed a hang or a too-slow run. GNU `timeout` is a Linux/CI thing; macOS ships no `timeout` binary, so this marker never appears locally |

**The crashing project name is meaningful; the crashing TEST name usually is not.** The signal lands
wherever the process happened to be, which is frequently not where the defect is.

## The dump is already being collected

`dotnet-test.yml` sets, for every shard:

```yaml
DOTNET_DbgEnableMiniDump: 1
DOTNET_DbgMiniDumpType: 2          # heap dump — required for `verifyheap` / object inspection
DOTNET_DbgMiniDumpName: /tmp/coredumps/%e-%p.dmp
```

and the collect + upload steps are `if: always()`, so a dump survives even when the shard is killed.
It arrives inside the **`testResults-shard<N>`** artifact under `collected-logs/dotnet-<pid>.dmp`,
alongside `_meshweaver-test-trace.log` and `_meshweaver-memory-delta.log`. **Retention is 15 days** —
pull it before it expires.

### 🚨 Ask the trace log whether it is complete before you reason from it

`_meshweaver-test-trace.log` carries `[FAULT]` records — exception type, message and stack for
everything logged at `Warning` or above with an exception. Its fault records are **rate-bounded**
(100 per 10 s per process), so a storming process has records missing, and reasoning from an
absence in a truncated log is how you conclude the wrong thing. The log always says when that
happened:

```bash
grep FAULT-BUDGET collected-logs/_meshweaver-test-trace.log
```

Nothing back ⇒ every fault this process logged is in the file. Otherwise each hit names the
running count of suppressed records, and a `resuming fault records after suppressing N` line
states exactly how many were lost in that gap. A **budget never silences a later fault** — the
window refills, so the fault next to a wedge is written even if an earlier storm saturated the
allowance (issue #982).

```bash
# find the shard artifact (the crashing shard's is the big one — the others are ~0MB)
gh api repos/Systemorph/MeshWeaver/actions/runs/<RUN_ID>/artifacts \
  --jq '.artifacts[] | select(.name|test("shard")) | "\(.id)  \(.name)  \(.size_in_bytes/1048576|floor)MB  expired=\(.expired)"'

mkdir -p "$HOME/segv-dump" && cd "$HOME/segv-dump"
gh api repos/Systemorph/MeshWeaver/actions/artifacts/<ARTIFACT_ID>/zip > shard.zip
unzip -q shard.zip -d shard && find shard -name '*.dmp'
```

## 🚀 Start here on macOS: name the faulting frame with no container at all

**Before reaching for Docker, answer "where did it fault and on what address" in about ten minutes,
entirely on the Mac.** A `createdump` core is a plain ELF64 file; every fact needed to name the
faulting function is inside it plus one 138 MB download from Microsoft's public symbol server. This
is the route that produced the 2026-08-09 confirmation below, and it needs no DAC, no emulation and
no `dotnet-dump` — all of which are what make the container route slow and failure-prone.

Four steps, each a few lines of pure Python over the core (no debugger, no elfutils):

1. **`NT_SIGINFO` → the kernel's verdict.** Walk the `PT_NOTE` program headers; note type
   `0x53494749` carries `si_signo` / `si_code` / `si_addr`. `si_code == 1` is `SEGV_MAPERR`, and
   `si_addr` is the dereferenced address — `0x0` versus a plausible-but-unmapped pointer is already
   the difference between a null read and a use-after-unload.
2. **`NT_FILE` → the module load bases.** Note type `0x46494c45` maps every file-backed range;
   the minimum start for `libcoreclr.so` is the load base you subtract to get an RVA.
3. **The faulting `ucontext`** — *not* `NT_PRSTATUS`, which `createdump` records from inside its own
   signal handler (its `rip` is `waitpid` in libc). Scan the `PT_LOAD` segments on 8-byte alignment
   for a `gregs[23]` block whose `TRAPNO` (index 20) is `14` (page fault) and whose `RIP` (index 16)
   lands inside `libcoreclr`; `ERR` (19) and `CR2` (22) then decode the access — `ERR == 0x4` is a
   user-mode **read** of a non-present page. Read the bytes at `RIP` straight out of the core through
   the same `PT_LOAD` table: that is the faulting instruction, and with the register values it names
   the exact dereference.
4. **RVA → function name, via the public symbol server.** The shipped `libcoreclr.so` is stripped to
   nine exported `STT_FUNC` symbols, so resolving against it fails — and that failure looks like the
   technique not working rather than the file being stripped. Fetch the separate debug file, keyed by
   build-id:

   ```bash
   curl -sL -o rt.tar.gz \
     "https://builds.dotnet.microsoft.com/dotnet/Runtime/<VER>/dotnet-runtime-<VER>-linux-x64.tar.gz"
   tar xzf rt.tar.gz shared/Microsoft.NETCore.App/<VER>/libcoreclr.so
   # <BUILD_ID> comes from that libcoreclr.so — see below
   curl -sfL -o coreclr.debug \
     "https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-<BUILD_ID>/_.debug"
   ```

   **Reading `<BUILD_ID>` is the same Python you already have** — walk the section headers
   (`e_shoff`/`e_shentsize`/`e_shnum` at `0x28`/`0x3A`, names via the `e_shstrndx` section), find
   `.note.gnu.build-id`, and read its note: `namesz`/`descsz`/`type` as three `<I`, then skip
   `12 + ((namesz+3) & ~3)` bytes and take `descsz` bytes — that hex string is the id.

   Then walk `coreclr.debug`'s `.symtab` for the `STT_FUNC` entry (`st_info & 0xf == 2`) whose
   `[st_value, st_value + st_size)` contains the RVA; symbol names come from the section its `sh_link`
   points at. It carries ~31 800 symbols, so the hit is exact — e.g. RVA `0x5cb1e1` →
   `_ZN3WKS7gc_heap16background_sweepEv` (`WKS::gc_heap::background_sweep()`, `+0xa61`).

Take the runtime version from `collected-logs/symbols-<Project>/_runtimes.txt` in the same artifact —
CI's patch is regularly not the one you have locally, and a mismatched `libcoreclr.so` yields a
different build-id and therefore no symbols at all.

## You do need a container for the managed side

The **managed** commands — `clrthreads` / `clrstack` / `verifyheap`, i.e. anything that goes through
the DAC — need the target's own `libmscordaccore.so`, a Linux ELF library. `dotnet-dump analyze` on
macOS cannot load it, so those commands require a **linux/amd64** container
(CI runners are x64; Apple-silicon Docker is arm64, so `--platform linux/amd64` and emulation are
required — it is slow but it works).

🚨 **This restriction is about the DAC, not about the file.** A `createdump` core is a plain ELF64
that any tool can read; the native analysis in the previous section runs entirely on the Mac. Do not
read "you need a container" as "a Linux core cannot be opened on macOS" — believing that has cost an
investigation a slow multi-hundred-MB download and an emulated container for facts that were ten
minutes of local Python away.

**Stage the dump under `$HOME`, not `/private/tmp`** — Colima does not mount `/private/tmp`, so a
dump left in the scratch directory is invisible inside the container.

```bash
docker run --rm --platform linux/amd64 -v "$HOME/segv-dump/shard/collected-logs:/dumps" \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    curl -sL https://aka.ms/dotnet-dump/linux-x64 -o /tmp/dotnet-dump && chmod +x /tmp/dotnet-dump
    /tmp/dotnet-dump analyze /dumps/<name>.dmp -c "<command>" -c "exit"
  '
```

Use the **`curl` single-file download above, not `dotnet tool install -g dotnet-dump`** — the tool
installer fails under qemu emulation with `There was an error reflecting type '…DotNetCliTool'`,
and the resulting `dotnet-dump: command not found` looks like a PATH problem rather than what it is.

## Free triage before you start a container

`strings` on the raw core answers two questions in seconds, with no DAC and no emulation:

```bash
strings -a dump.dmp | grep -oE "AccessViolation|FailFast|SIGSEGV" | sort | uniq -c
strings -a dump.dmp | grep -oE "/usr/share/dotnet/shared/Microsoft.NETCore.App/[0-9.]+/lib[a-z]+\.so" | sort -u
```

- `AccessViolation` + `FailFast` ⇒ the runtime tripped over bad memory; this is not a managed
  exception that someone forgot to catch.
- The second line reveals **which runtime patch CI actually ran**. It is regularly *not* the one you
  have locally (2026-08-03: CI on `10.0.10`, local on `10.0.9`) — on its own a candidate explanation
  for "only fails on CI", and worth eliminating before blaming load or shard composition.

## The command sequence that actually answers the question

Run these in order; each one kills off a class of hypothesis.

1. **`clrmodules`** — *confirm you have the right process first.* Grep for the test assembly
   (`MeshWeaver.<X>.Test.dll`). Some dumps are produced deliberately (a DAC/unload probe test), so
   check you are not analysing an intentional crash. Also count copies of a given assembly: **more
   than one copy means duplicate statics across AssemblyLoadContexts**, which silently defeats any
   process-wide lock.
2. **`clrthreads`** — find the faulting thread. It is usually the one in **`Cooperative`** GC mode
   and/or flagged **`(GC)`**; the `Exception` column is typically empty for a signal death.
3. **`clrstack -all`** — every managed stack in one pass. Grep it for the suspect frames, and to test
   *concurrency* hypotheses: if only one thread is inside the library you suspect of being torn by
   concurrent access, a locking fix is not the answer.
4. **`setthread <DBG-id>` + `clrstack -f`** — the faulting thread interleaved with native frames and
   module names. This is the definitive stack.
5. **`verifyheap`** — clean output means **no GC heap corruption**, which distinguishes "this code is
   the culprit" from "this code is an innocent victim of corruption elsewhere". Requires
   `DbgMiniDumpType: 2` (already set).

   ⚠️ **It may not run at all.** On a large dump `dotnet-dump` can die inside its own scan:

   ```
   Scanning heap: 6 MB / 266 MB (2%)...
   Unhandled exception: System.NullReferenceException
      at Microsoft.Diagnostics.DebugServices.Implementation.Utilities.Invoke(…)
   ```

   That is the TOOL failing, not a verdict — it says nothing about the heap either way. Do not read
   it as "clean" and do not retry it hoping for a different answer (2026-08-06, a 1.2 GB FutuRe
   dump). When it happens, step 5 is simply unavailable: say so rather than implying culprit-vs-victim
   was established, and get the confidence elsewhere — a **deterministic repro** is worth more than
   `verifyheap` anyway, because it pins the cause instead of describing the wreckage.
6. **`eeheap -gc`** — heap size, to rule in/out memory pressure. Cross-check against
   `_meshweaver-memory-delta.log` in the same artifact for the deltas around the crash.

## When `clrstack` on the faulting thread prints NOTHING

An empty managed stack on the crashing thread is not a dead end — it is the answer to a different
question: **the thread is runtime-internal** (a GC / finalizer / EE worker), so there are no managed
frames to show and SOS has nothing more to give. Switch to native symbols.

Two traps make this look impossible when it is not:

- **lldb cannot map modules out of a `createdump` minidump.** `image list` shows only the executable,
  so `bt` prints bare addresses and every frame looks unsymbolisable. You symbolise by hand instead —
  the addresses are perfectly good.
- **A backtrace address is a RETURN address.** The frame you want is the target of the `call`
  immediately *before* it, not the function the address lands in. Disassembling the few bytes ahead of
  the return address is what names the real callee.

The recipe, end to end (each step is seconds). **This is the elfutils spelling of the same four
facts the macOS-native section above extracts with plain Python** — `eu-readelf` and `eu-addr2line`
are elfutils binaries that a stock macOS does **not** ship (and Homebrew does not carry a working
`eu-readelf` either), so run the whole block inside the container, not on the Mac. If you only need
the faulting frame, prefer the pure-Python route above and skip the container entirely.

```bash
# 1. Which thread, and what did the kernel say? si_code 1 = SEGV_MAPERR; si_addr is the deref'd pointer.
eu-readelf -n dump.dmp | grep -E "SIGINFO|si_signo:|fault address:|  pid: "

# 2. Load base of libcoreclr in the crashed process (NT_FILE mappings inside the core).
eu-readelf -n dump.dmp | grep libcoreclr.so | head -1     # e.g. 7f17d1000000-...

# 3. Native backtrace (bare addresses are expected).
lldb --core dump.dmp -o "thread list" -o "bt all" -o quit

# 4. RVA = frame address − load base. Fetch the MATCHING symbols by build-id and resolve.
BID=$(eu-readelf -n /usr/share/dotnet/shared/Microsoft.NETCore.App/<ver>/libcoreclr.so \
      | awk '/Build ID/{print $3}')
curl -sfL -o coreclr.dbg \
  "https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-$BID/_.debug"
eu-addr2line -f -C -e coreclr.dbg 0x<RVA>
```

Run the block in a **linux/amd64** container whose runtime patch matches `_runtimes.txt` from the
artifact (the shard already records it) — then `libcoreclr.so` is byte-identical and the build-id
lookup succeeds. The `.debug` carries a symbol table but no DWARF lines, so you get function names,
not line numbers; that is enough to name the phase.

### Recovering the FAULTING registers (PRSTATUS is not them)

`createdump` records the crashing thread's `PRSTATUS` from *inside its own signal handler*, so the
`rip` you read there is `waitpid` in libc — not the fault. The real faulting context is the
`ucontext_t` the kernel pushed onto the **alternate signal stack**; find it by scanning that stack for
the `mcontext` signature (no debugger needed — plain Python over the ELF core):

```python
# gregs[] at ucontext+40; indices: RIP=16, ERR=19, TRAPNO=20, CR2=22
# match TRAPNO==14 (page fault) and a RIP inside libcoreclr's mapping
```

`CR2` is the dereferenced address and `ERR` decodes the access (`0x4` = user-mode **read** of a
non-present page). Then disassemble a window around the faulting RVA — that names the exact
dereference, which is what turns "it segfaulted in the GC" into a specific claim.

### 2026-08-06: `MeshWeaver.FutuRe.Test exit=139` (run 31083356138)

Resolved this way in minutes after the 2026-08-04 attempt stalled for want of symbols. Crash was
**mid-run** (75 s in, 44 ms into a fresh fixture's `PreWarmNodeTypeHubs`), not at process exit. The
faulting thread carried **no managed frames** and symbolised to:

```
CorUnix::CPalThread::ThreadEntry → CreateSuspendableThread
  → WKS::gc_heap::bgc_thread_function() → WKS::gc_heap::gc1()
    → WKS::gc_heap::background_sweep()      ← faulting frame
```

and the recovered `ucontext` pinned the instruction (`TRAPNO=14`, `ERR=0x4`, `CR2=0x0`):

```asm
mov rax, QWORD PTR [r15]     ; rax = object->MethodTable      (r15 = the object being swept)
and rax, 0xfffffffffffffff8  ; strip the GC low bits
mov ecx, DWORD PTR [rax]     ; ← FAULT: read MT->m_dwFlags, rax == 0
```

**The swept object's MethodTable pointer is exactly NULL.** That single fact is worth more than the
whole stack, because of what it *rules out*:

- **It is not the collectible-ALC use-after-unload shape.** A dangling pointer into a freed
  `LoaderAllocator` is a non-null address that happens to be unmapped. Zero is not that. Several
  earlier fixes (and the `alc-unload-probe` workflow) were aimed at that hypothesis; this dump does
  not support it. Do not keep paying it forward.
- **It is not MeshWeaver corrupting the heap.** There is no `unsafe`, no `GCHandle`, no pinning, no
  `stackalloc` of object memory and no custom GC configuration anywhere in `src/` — pure managed code
  has no way to zero a MethodTable.
  ⚠️ **2026-08-12: that argument is weaker than it reads.** It is sometimes restated as *"the crashing
  process ships no native libraries"*, and that restatement is false. `NT_FILE` in `dotnet-3592.dmp`
  lists **19 file-backed `.so` mappings** — `libcoreclr`, `libclrjit`, `libSystem.Native`,
  `libSystem.Security.Cryptography.Native.OpenSsl`, `libcrypto`/`libssl`, `libicu*`, `libstdc++`,
  `libhostpolicy`, `libhostfxr` — beside the 132 managed `.dll`s. What holds is the narrow claim (the
  *app's own* code is pure managed); what does not hold is "there is no native code in the process,
  therefore nothing could have written the zero".

A zero MT inside the swept range means the sweep walked memory the GC believed held an object but that
is in fact zeroed — a gap that was never filled with a free object, or a walk that ran past the true
allocated end of the region. That is runtime-internal bookkeeping, so treat it as a **CoreCLR
background-GC issue** and report it upstream with this dump rather than "fixing" it here. What
MeshWeaver contributes is the *workload* that provokes it: per-`[Fact]` mesh build + teardown with
collectible NodeType assemblies loading and unloading, and 48 gen2 GCs in 75 s.

🚨 **Do not "fix" this by turning off concurrent GC** in the test host. That hides the fault without
changing anything about why it happens, and the same workload runs in prod. **This was tried anyway**
(#1274, `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` on `MeshWeaver.FutuRe.Test`)
and it did not even hide it — see the 2026-08-12 section, which measures why and removes it again.

### 2026-08-09: reproduced twice more — the verdict is not a one-off, and `+0x5cb1e1` is its fingerprint

Both `exit=139`s of 2026-08-09 resolve to the **identical** fault, days and many merges after the
original analysis. On runtime `10.0.10` the whole comparison collapses to one number: **RVA
`0x5cb1e1`**, `WKS::gc_heap::background_sweep()+0xa61`. Compare that first on any recurrence.

| | 2026-08-06 (`31083356138`) | 2026-08-09 06:59Z (`31299547995`) | 2026-08-09 11:05Z (`31309378803`) |
|---|---|---|---|
| `si_signo`/`si_code`/`si_addr` | 11 / 1 / — | 11 / 1 (`SEGV_MAPERR`) / **`0x0`** | 11 / 1 / **`0x0`** |
| faulting RVA | `background_sweep()` | **`0x5cb1e1` (+`0xa61`)** | **`0x5cb1e1` (+`0xa61`)** |
| instruction | `mov ecx,[rax]`, `rax == 0` | **`8b 08` = `mov ecx,[rax]`, `RAX=0x0`** | **`8b 08`, `RAX=0x0`** |
| `TRAPNO`/`ERR`/`CR2` | 14 / `0x4` / `0x0` | **14 / `0x4` / `0x0`** | **14 / `0x4` / `0x0`** |
| mutator phase | 44 ms into a fresh fixture | **96 ms into a `Mesh.Dispose()`** | **476 ms into a fresh fixture** |
| dump | — | `dotnet-3050.dmp` | `dotnet-3032.dmp` |

**The mutator phase varies while the faulting instruction does not — and that is the point.** One
lands mid-teardown, two land inside a fresh test. A background-GC sweep runs *concurrently* with
whatever the mutator is doing, so an identical fault at three different phases is what this
diagnosis predicts, and it is the opposite of what a teardown-ordering defect would produce (those
reproduce at one phase, by construction). Do not read the phase of any single sighting as evidence
about the cause; read the RVA.

> ⚠️ **"Read the RVA" is now known to be half a rule — see the 2026-08-12 section.** An RVA is only
> meaningful against the runtime build it was taken on, and the runtime moves under you. Two dumps on
> `10.0.11` fault at `0x5cf24c`, which is **not** `background_sweep`. The portable fingerprint is the
> **instruction plus register state** (`8b 08` = `mov ecx,[rax]` with `RAX=0x0`, `si_addr=0x0`), which
> is identical across all five sightings; the RVA and the function name are not.

Two further things these sightings settle, both re-litigated at length in #613:

- **The teardown work neither caused nor cured it.** The 11:05Z tree contains #967 (node ALCs unload
  at the *end* of teardown) and #996 (pending Rx timers no longer root disposed hubs) — it crashed
  87 minutes after the latter merged. Across both processes' `_meshweaver-test-trace.log`, **all 22
  and all 44** `DISPOSE_DONE` records read `teardown clean — all pooled I/O joined, async dispose
  queue drained`, with **zero** `DISPOSE_QUIESCE_LEAK` and **zero** `DISPOSE_DIRTY_TEARDOWN`.
- **It is not the quiescing-leak family (#981).** Over 18 failing shard-job logs across those two
  days, the signal death and the `left Observe subscriptions pending past the Quiescing budget`
  failure each occur twice and **never co-occur** — not in a run, a shard, or a job. In one job
  `FutuRe.Test` ran 69 s to `exit=0` while the quiescing leak fired in `Hosting.Monolith.Test`.

**Check the trace log's completeness before reasoning from it** (see the `FAULT-BUDGET` note above):
in the 11:05Z file all four suppression windows belonged to a *later* project's pid, so the crashing
process's record was whole — which is what makes "zero quiesce leaks" evidence rather than an absence.

### 2026-08-12: on runtime `10.0.11` it is **`plan_phase`**, not `background_sweep` — the frame moved, the fault did not

Three dumps from the post-#1274 window, resolved against `10.0.11`. All fault at the **same
instruction with the same registers** as the three `10.0.10` sightings — and in a **different GC
function**.

| | `dotnet-3592.dmp` (`31590331741`, 11:05Z) | `dotnet-3500.dmp` (`31597122789`, 12:34Z) | `dotnet-3433.dmp` (`31603530862`, 14:06Z) |
|---|---|---|---|
| runtime (from `NT_FILE`) | `10.0.11` | `10.0.11` | `10.0.11` |
| `si_signo`/`si_code`/`si_addr` | 11 / 1 (`SEGV_MAPERR`) / **`0x0`** | 11 / 1 / **`0x0`** | 11 / 1 / **`0x0`** |
| `TRAPNO`/`ERR`/`CR2` | 14 / `0x4` / `0x0` | 14 / `0x4` / `0x0` | 14 / `0x4` / `0x0` |
| instruction | **`8b 08` = `mov ecx,[rax]`, `RAX=0x0`** | **`8b 08`, `RAX=0x0`** | **`8b 08`, `RAX=0x0`** |
| faulting RVA | **`0x5cf24c`** | **`0x5cf24c`** | **`0x5cf24c`** |
| resolves to | **`WKS::gc_heap::plan_phase(int)+0x24fc`** | **same** | **same** |

**This is not RVA drift.** Resolved against the same `10.0.11` `coreclr.debug`, the old fingerprint
`0x5cb1e1` still lands in `background_sweep` (`+0xad1`, versus `+0xa61` on `10.0.10` — the function
moved 0x70 bytes between builds). The two functions are disjoint (`background_sweep` at
`0x5ca710`+`0x140b`; `plan_phase` at `0x5ccd50`+`0x4b56`), and the crash is 0x4000 bytes away from
the old one. The frame genuinely changed.

The faulting instruction is `MethodTable::GetComponentSize` inlined into the heap walk — `mov ecx,
[rax]; test ecx,ecx; js …` reads the MT flags dword and branches on `enum_flag_HasComponentSize`.
`RAX` **is** the MethodTable pointer, and it is zero. Same dereference as before.

#### What this settles, and what it does not

**It settles the question #613 was reopened on.** The reopen offered two possibilities and could not
choose: *(1) the `System.GC.Concurrent:false` setting is not reaching the crashing process*, or
*(2) the fault is not in `background_sweep`*. It is **(2)**, measured directly. `plan_phase` is the
stop-the-world plan/relocate step of a **blocking** collection; it is on the path of every blocking
GC, including one that runs with concurrent GC disabled. So the premise #1274 was built on — *"only
background GC can reach `background_sweep`, therefore disabling it removes the fault"* — does not
cover the crash that is actually happening, and the unchanged CI rate (4.2 % → 3.8 %) needs no
appeal to hypothesis (1) to explain it.

Supporting, though weaker than the frame: `System.GC.Concurrent` is present as a host
config-property key in the crashing process's memory (3 hits in `dotnet-3592.dmp`, including the
UTF-16 `AppContext` copy), which is consistent with the runtimeconfig having carried it.

**It does not settle what zeroes the MethodTable.** `plan_phase` also runs as the foreground
collection *during* a background GC, so this frame alone does not prove concurrent GC was off at the
moment of the fault. What five dumps do establish is the invariant: **the GC walks the heap and finds
an object header holding exactly zero.** Which phase discovers it is incidental — and "background-GC
bug" named the incidental part. Anything built on that name (a mitigation, an upstream report, a
closure as external) needs re-deriving from the invariant instead.

#### Two hypotheses these dumps falsify — check them off, do not re-open them

Both were proposed again while this was being analysed, and both are decidable from the dump alone:

- **Use-after-unload of a collectible ALC** (the `MessageHubGrain.OnDeactivateAsync` "KNOWN GAP":
  `loadContext.Unload()` orders only on the hub's `DisposalCompleted`, and pooled I/O leaves are
  mesh-shared). **Falsified twice over.** `RIP` is inside a **file-backed** mapping —
  `/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.11/libcoreclr.so`, resolving to a named
  runtime symbol — not in unloaded or collected JIT code. And a freed `LoaderAllocator` yields a
  **non-null unmapped** pointer; `si_addr` here is exactly `0x0`. This is the same distinction the
  `exit=134` analysis already drew, in the other direction. The gap may be real and worth closing on
  its own merits; it does not produce *this* fault.
- **"Bounded disposal gives up and unloads on top of live work."** **Falsified for all three.**
  `dotnet-3433`'s trace has **29** `DISPOSE_DONE … teardown clean`, **zero**
  `DISPOSE_QUIESCE_LEAK`, **zero** `DISPOSE_DIRTY_TEARDOWN`, and no disposal-timeout record of any
  kind — the only timeout-shaped lines are `MEM_WATCHDOG` memory samples. Its last teardown finished
  cleanly in 2009 ms and the **next fixture had already started** (`CTOR` / `INIT_START` /
  `INIT_BASE_DONE` at `14:06:27.943`) when the fault landed ~1 s later. `dotnet-3592` is the same
  shape: 40 clean `DISPOSE_DONE`, zero leaks.

#### Consequences recorded here so they are not re-litigated

- **`<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` was removed** from
  `MeshWeaver.FutuRe.Test.csproj`. It is a mitigation whose mechanism is not the one firing, it
  measurably changed nothing, and left in place it reads to the next reader as "GC was already ruled
  out" — the most expensive kind of comment. The instruction above ("do not fix this by turning off
  concurrent GC") and the tree now agree again.
- **Scope it as *ALC-heavy assemblies*, not `FutuRe.Test`.** `FutuRe.Test` is where it recurs, but a
  `MeshWeaver.Hosting.Orleans.Test exit=139` has been observed once. Both build and tear down a mesh
  with collectible NodeType assemblies per `[Fact]`; that workload, not the project, is the sample.
- **The crashing process's phase, again, is not evidence.** `dotnet-3592`'s last trace record is
  `FutuReAnalysisTest DISPOSE_INVOKED` at `11:21:52.614`; **40** `DISPOSE_DONE` records in that
  process read `teardown clean`, with **zero** `DISPOSE_QUIESCE_LEAK` and **zero**
  `DISPOSE_DIRTY_TEARDOWN`.

#### The dump IS uploaded — and here is how to fetch 200 MB in ten minutes

Two beliefs have repeatedly stalled this investigation. Both are wrong:

- *"The `.dmp` is never uploaded; CI ships symbols with nothing to symbolicate."* It is uploaded.
  `.github/workflows/dotnet-test.yml` copies `/tmp/coredumps/*.dmp` into `collected-logs/` before the
  `testResults-shard<N>` upload, and both dumps above came straight out of that artifact.
- *"The download is infeasible."* Through `gh api` it is — that measures ~11 KB/s, which is where two
  investigations gave up. The artifact's blob URL supports HTTP **range** requests, so fetch it in
  parallel chunks instead. 208 MB lands in about ten minutes:

```bash
# 1. mint the redirect (do NOT let gh follow it)
TOK=$(gh auth token)
URL=$(curl -s -o /dev/null -D - -H "Authorization: Bearer $TOK" \
  "https://api.github.com/repos/Systemorph/MeshWeaver/actions/artifacts/<ARTIFACT_ID>/zip" \
  | grep -i '^location' | sed 's/^location: //I' | tr -d '\r')
# 2. fetch chunk i of N with a plain range request
curl -sf -r "$START-$END" -o "part-$i" "$URL"
```

🚨 **Two traps, both of which silently corrupt the result:**

- **The SAS expires in ~10 minutes.** Re-mint `URL` before each wave and fetch only the chunks that
  are still missing — a resumable loop, not one long run.
- **Use `curl -sf`, never a bare `curl` with `--retry`.** Without `-f`, an expired-SAS `403` writes
  its 544-byte `AuthenticationFailed` XML body *into the part file* and curl reports success; with
  `--retry` it will also do that on top of a chunk that had already completed. The first attempt here
  assembled an 8 MB "208 MB" file that way. Verify every chunk's exact byte length before
  concatenating.

## Reading the result honestly

The trap in this class of bug is confirmation: the stack shows *a* plausible culprit and it is
tempting to fix that and declare victory. Discipline:

- A stack tells you **where** the process died, not **why**. Use steps 5–6 to establish culprit vs
  victim before proposing a fix.
- **Check which phase you are in.** Read the bottom of the stack, not the top. `MeshBuilder.BuildHub`
  means construction/fixture-init; a dispose cascade means teardown. Getting this wrong sends fixes
  at the wrong guard — the FutuRe SIGSEGV was framed as a teardown race for weeks while the dump
  showed it crashing during hub *construction*.
- A method's **name** is not evidence of the phase. `SubscribeToOwnDeletion` is a
  `.WithInitialization(...)` hook: it names what the subscription later watches for, not when it runs.
- **A frame appearing TWICE in one stack is re-entrancy, and re-entrancy is the finding.** That is
  what the 2026-08-03 crash turned out to be — `CreateHub → Build → … → CreateHub → Build` nested an
  Autofac `ComponentRegistryBuilder.Build` inside the in-progress one, and that builder is not
  re-entrant. Scan the stack for repeats before theorising about anything else (fixed in #774: the
  own-node subscription moved from the synchronous `WithInitialization` overload, which runs *inside*
  `Build`, to the observable one, which runs on `InitializeHubRequest` after `Build` returns).
- If a hypothesis survives, write down what would disprove it, then go and check that.

## Why the dump may have no precursor in the logs

If a bare `catch {}` wraps the failing region, nothing is logged before the crash. That is not an
accident of the dump — it is a defect in the code. Error paths must log (see
[ErrorPropagationAndWedges](../ErrorPropagationAndWedges)), and the logging itself must
be unable to escalate: resolving a logger can throw `ObjectDisposedException` on a disposing
container, so a diagnostic emitted from inside a `catch` or an Rx `onError` can convert a handled
fault into an unhandled one. Route such emissions through a guarded helper whose single empty catch
swallows only the *logging* failure.

## Related

- [DebuggingMessageFlow](../DebuggingMessageFlow) — for hangs and lost messages (a
  timeout, not a signal).
- [DebuggingDisposalAndLeaks](../DebuggingDisposalAndLeaks) — teardown stragglers and
  retention.
- [WritingTests](../WritingTests) — why a flake is a real race and re-running hides it.
