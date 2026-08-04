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
| `exit=124` | **not** a crash — the wall-clock cap (`timeout`) killed a hang or a too-slow run |

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

```bash
# find the shard artifact (the crashing shard's is the big one — the others are ~0MB)
gh api repos/Systemorph/MeshWeaver/actions/runs/<RUN_ID>/artifacts \
  --jq '.artifacts[] | select(.name|test("shard")) | "\(.id)  \(.name)  \(.size_in_bytes/1048576|floor)MB  expired=\(.expired)"'

mkdir -p "$HOME/segv-dump" && cd "$HOME/segv-dump"
gh api repos/Systemorph/MeshWeaver/actions/artifacts/<ARTIFACT_ID>/zip > shard.zip
unzip -q shard.zip -d shard && find shard -name '*.dmp'
```

## 🚨 You cannot open a Linux dump on macOS

`dotnet-dump` on macOS **cannot** read a Linux core dump. Analyse it in a **linux/amd64** container
(CI runners are x64; Apple-silicon Docker is arm64, so `--platform linux/amd64` and emulation are
required — it is slow but it works).

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
6. **`eeheap -gc`** — heap size, to rule in/out memory pressure. Cross-check against
   `_meshweaver-memory-delta.log` in the same artifact for the deltas around the crash.

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
