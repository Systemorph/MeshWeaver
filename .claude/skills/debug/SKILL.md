---
name: debug
description: Debug a deadlock / hang / timeout in the mesh — a test that times out, a hub handler whose response never arrives, a GetRemoteStream / GetMeshNodeStream / GetQuery that never emits, an agent round or per-instance-hub activation that parks forever. Use whenever something times out and you need to find WHERE the message flow breaks down. The timeout is the symptom, never the bug — raising it just hangs longer. Grounded in DebuggingMessageFlow.md.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /debug — Find where the message flow breaks down

A timeout means **something never completed**. The timeout is the *symptom*; the bug is a
specific broken edge in the message flow — a message that was dropped, a handler that threw, an
observer that died, an initial `Full` that was never sent, a `Pending` flip that never reached
the owner. **Raising the timeout never fixes this — it just hangs longer.** Your job is to find
the exact edge where the flow stops, and fix *that*.

> Canonical reference: [DebuggingMessageFlow.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md)
> (served at `/Doc/Architecture/DebuggingMessageFlow`). Read it — this skill is the operating
> procedure; the doc is the full trace-tag reference and the catalogue of known broken edges.

## The one rule

**Run once. Grep the trace. Find the last thing that fired and ask what should have fired next.**
Do **not** rerun "to see if it still sticks" (it will), and do **not** bump the timeout. A flake
is a real race — pin the broken edge.

## 🚨 These hangs reproduce only IN BULK, not in isolation

The defining property of this class of bug: **the test passes on its own and hangs only when the
whole suite / project runs in one process.** Run `<TestName>` alone → green; run the full project
(or all four CI shards) → some test times out, and usually a *different* one each run. That is not
"flakiness to retry away" — it is the tell that the broken edge is a **cross-test interaction**:
shared scheduler/thread-pool pressure, a reused Orleans cluster, a per-process cache, an observer
that only loses its race when other tests' emissions interleave, a grain that only
deactivates→reactivates under concurrent load. A bug that vanishes in isolation is a bulk bug.

**So reproduce in bulk, not in isolation:**
```bash
# Reproduce the hang the way CI hits it — the whole project in ONE process:
dotnet test test/<Suite> --no-build > /tmp/trace.log 2>&1
# Narrow it: the class that ran JUST BEFORE the hung one is the prime suspect for the interaction.
grep -E "TEST CLASS INIT|TEST CLASS DISPOSE|\[FAIL\]|HandleMessageAsync ENTER" /tmp/trace.log | tail -60
```
The single-test run is only for *confirming the fix* once you've found the edge — never for
deciding whether the bug exists. If the suite hangs but the test passes alone, **the bug is real;
keep digging in the bulk run.**

## Procedure

1. **Crank logging to Trace** in the test's *runtime* appsettings — NOT in `src/` (log levels in
   source are a billed production contract; flipping them bleeds App Insights budget). Edit
   `test/<Suite>/bin/Debug/net10.0/appsettings.json` (`reloadOnChange: true` is wired); revert
   before committing:
   ```json
   { "Logging": { "LogLevel": {
     "Default": "Information",
     "MeshWeaver.Messaging": "Trace",
     "MeshWeaver.Data.Serialization": "Trace",
     "MeshWeaver.Mesh": "Debug"
   } } }
   ```

2. **Run the failing test exactly once**, capturing everything:
   ```bash
   dotnet test test/<Suite> --no-build --filter "FullyQualifiedName~<TestName>" > /tmp/trace.log 2>&1
   ```
   Per-test logs also land at `test/<Suite>/bin/Debug/net10.0/test-logs/<Class>_<Method>.log`.

3. **Grep the structured tags:**
   ```bash
   grep -E "MESSAGE_FLOW|SYNC_STREAM|exception occurred|No handler found|DeliveryFailure|deliveryId" /tmp/trace.log
   ```

4. **Find the LAST `MESSAGE_FLOW:` line that fired, then look at what should have happened next.**
   This is the whole technique — the gap between "last tag observed" and "next tag expected" is
   the broken edge:

   | Last tag observed | What broke | Look next at |
   |---|---|---|
   | No `Unpacking` at the target hub | Message lost in routing | `HIERARCHICAL_ROUTING_RESULT` `State` |
   | `Unpacking` but no `ROUTING_TO_LOCAL_EXECUTION` | Deferred / buffered (init gate) | `Deferring on-target` / `Buffering` lines |
   | `ROUTING_TO_LOCAL_EXECUTION` but no handler logs | Handler threw on entry | `An exception occurred during the processing of MessageDelivery` |
   | Handler logged success, caller still hangs | Response never routed back | the `o.ResponseFor(request)` post + its `Unpacking` at the sender |
   | `SubscribeAck` then silence until a heartbeat | Initial `Full` dropped | owner-side echo filter / `ChangedBy` (see doc §"never sent the initial Full") |
   | `type … is not registered in this hub's TypeRegistry` | Type-registry mismatch | `WithType(typeof(T), nameof(T))` on **every** hub the message transits |

## Is it a lock, or a missed observation? (they look identical from the outside)

Most "deadlocks" in this codebase are **not** locks — they are a reactive emission that nobody
observed. Tell them apart before you go hunting for a lock:

- **Real lock-deadlock** — one *large gap mid-work* where nothing runs, then timeout. The action
  block is wedged on a blocking continuation (`await` / `.Result` / `.Wait()` / `SemaphoreSlim`
  on a hub turn or grain turn).
- **Missed observation** — a *burst of work for seconds, then total silence* for the rest of the
  timeout. The work finished; the thing waiting on it never saw the terminal state.

Confirm with stacks mid-freeze — decisive in one shot:
```bash
pid=$(pgrep -f testhost | head -1)
dotnet-stack report -p $pid > /tmp/stacks.txt
grep -vE "System\.|Microsoft\.|xunit|testhost" /tmp/stacks.txt | sort -u   # any APP frame?
```
**No MeshWeaver frame on any thread + total trace silence ⇒ a dropped reactive emission, not a
lock.** (Idle cores + silence is never a hot loop.)

## The usual broken edges (root causes, not band-aids)

- **One-shot-with-give-up:** `stream.Take(1).Timeout(15s).Subscribe(...)` — the loaded-state
  emission arrives late or is dropped on the subscribe handshake, `Timeout` fires `onError`, the
  recovery **gives up and never retries** → the node stays non-terminal forever. Fix:
  re-establish, never give up (re-subscribe on fault); never `Timeout`-then-give-up on a
  lifecycle observer.
- **Lost-on-reactivation:** the observer lived only in the grain/agent-loop that set it up; on
  Orleans deactivate→reactivate (or hub re-init) it's gone and is never rebuilt. Fix: rebuild the
  observation on init; re-observe the existing child, don't re-run the loop and re-delegate.
- **Cross-hub write under the wrong identity:** a cold cross-hub `stream.Update` whose side effect
  runs on the emission thread where the inbound `AccessContext` was wiped → posts a NULL context,
  fails closed at the sender, the patch never reaches the owner, the wait never settles. Fix:
  `Observable.Using(() => AccessContextScope.AsSystem(...), _ => stream.Update(...))` so the
  identity survives the Subscribe.
- **Owner never sent the initial `Full`:** `SubscribeAck` routed, then ~one heartbeat of dead
  silence. The echo filter dropped the Full or `ChangedBy` collapsed to empty. Fix on the
  **owner** side, never the caller.

## When you've found it

Fix the broken edge — **never** the timeout (the user's standing rule: a bigger timeout "will
just hang longer"). Leave the `LogTrace`/`LogDebug` lines you relied on in place (they cost
nothing at higher levels and are how the next analogous hang gets debugged). If you genuinely need
a stopgap before the real fix lands, say so explicitly and then fix the root cause.
