---
NodeType: Markdown
Name: "Debugging Disposal, Storms and Leaks"
Abstract: "The playbook for disposal hangs, cross-mesh memory leaks, and writes that only reply in bulk: count distinct messages with MESHWEAVER_MSG_TRACE before assuming a runaway loop, recognise the debounced-flush reply gotcha (fix: write via stream.Update), and chase TimerQueue disposal leaks to their GC roots with ClrMD."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#c62828'/><circle cx='10' cy='10' r='5' fill='none' stroke='white' stroke-width='2'/><path d='M14 14l5 5' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='10' cy='8.5' r='1.2' fill='white'/><path d='M10 10.5v1.5' stroke='white' stroke-width='1.6' stroke-linecap='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Debugging"
  - "Disposal"
  - "Memory Leaks"
  - "Diagnostics"
---

# Debugging Disposal: Message Storms, Leaks, and "Who Holds the References"

When a test (or prod hub) **hangs on disposal**, **leaks memory across disposed
meshes**, or a write **never gets its reply in bulk** but passes in isolation, the
symptoms all look the same from the outside. This page is the playbook that
cracked the `TodoDataChangeWorkflowTest` bulk hang. It has three tools, in the
order you should reach for them.

> TL;DR of that investigation: the write succeeded fast, but its **response was
> gated on the debounced persistence flush** (`MeshNodeTypeSource.DebounceInterval`,
> 200 ms) — so the reply only arrived when `FlushOnDispose` forced the flush at
> teardown. The fix was to write via `stream.Update` (completes on the in-memory echo,
> not the flush). Two real `TimerQueue` disposal leaks were found along the way with ClrMD.
>
> ⚠️ **What was measured versus what was inferred.** The observed facts are the timings
> below: handler `ENTER → EXIT` in 10 ms, reply at `runLevel=Quiescing` ~12 s later, only
> in bulk. *Why* the 200 ms timer callback did not fire earlier was never pinned down —
> thread-pool starvation was the hypothesis, and it is no longer even reachable in that
> form, because the test assertions no longer block a thread (they `SubscribeOn` the pool
> and are `await`ed; see [Reactive Test Assertions](/Doc/Architecture/ReactiveTestAssertions)).
> Trust the **fingerprint** — "reply lands at Quiescing, consistently, only under load" —
> and the fix; do not carry the mechanism forward as established.

---

## 1. Is it actually an *endless* message loop? — `MESHWEAVER_MSG_TRACE`

Disposal posts a cascade of `ShutdownRequest`s. Before you assume a runaway loop,
**count distinct messages, not trace lines.**

```bash
MESHWEAVER_MSG_TRACE=1 dotnet test <project> --filter <Test> --no-build
# Path.GetTempPath() + meshweaver-msg-trace.log. NOTE: $TEMP is a Windows variable and expands
# to the empty string in a POSIX shell — on macOS the file is under $TMPDIR (a per-user
# /var/folders/… directory), on Linux under /tmp.
TRACE="$(ls "${TMPDIR:-/tmp}"/meshweaver-msg-trace.log)"

# Histogram by message type (counts LINES — ~7 phase-lines per message)
grep -aoE "msg=[A-Za-z0-9_]+" "$TRACE" | sort | uniq -c | sort -rn

# DISTINCT messages per hub (this is the real signal)
grep -a "msg=ShutdownRequest" "$TRACE" \
  | grep -aoE "hub=[^ ]+ msg=ShutdownRequest id=[A-Za-z0-9_-]+" \
  | sed -E 's/ id=.*//' | sort -u | sed -E 's/ id=.*//' \
  | sort | uniq -c | sort -rn | head
```

Interpretation:

- **~3 distinct `ShutdownRequest` per hub** (`Quiescing → DisposeHostedHubs → ShutDown`)
  = a **normal finite cascade**. A 96-hub mesh shutting down = ~300 distinct
  ShutdownRequests ≈ ~2000 trace *lines*. That is NOT a loop. (The Todo case looked
  like "2121 ShutdownRequest" but was 303 distinct messages × 7 phase-lines.)
- **Dozens or thousands per hub** = the **version-chase repost storm**. Read
  `MessageHub.ShutdownTurnsHandled`: a healthy disposal handles exactly three, and a value
  in the thousands is that storm's signature.

  🚨 **The gate that caused it has been REMOVED — do not go looking for "the other message".**
  `HandleShutdownCore` used to require `request.Version == Version - 1` and repost on mismatch.
  Because `++Version` runs for *every* message, any concurrent traffic between a repost and its
  re-handle pushed `Version` past the one-step window, so the gate never converged and
  self-sustained (2,820 reposts on a single `consumer/1` hub under the 2-core security tests;
  140k `ShutdownRequest` turns suite-wide). It also added nothing: duplicates are handled by the
  per-phase `RunLevel` idempotency guards, and the three phases are causally chained and
  FIFO-ordered. The regression test is
  `MessageHubTest.Dispose_UnderContinuousLoad_DoesNotStormShutdownRequests`. If you see a storm
  today, it is a *new* defect — do not re-derive the old diagnosis.

A disposal **watchdog** that force-completes after N seconds is masking a non-quiescing cascade,
not fixing it (see §3 for why the watchdog itself can leak).

---

## 2. The write succeeds but the reply never comes — trace the request/response pair

If a `*Request` times out, don't assume the handler is wedged. Trace both sides:

```bash
grep -a "<NodePath>" "$TRACE" | grep -a "<RequestType>"     # request side
grep -a "msg=<ResponseType>" "$TRACE"                       # response side
```

In the Todo case the owning hub showed `HandleMessageAsync ENTER → EXIT
state=Processed` in **10 ms**, but the `UpdateNodeResponse` reached the caller
**12 s later at `runLevel=Quiescing`** — i.e. the reply was posted *during the
caller's disposal*. That timing fingerprint ("reply arrives at Quiescing, ~12 s,
consistently") means the handler's async work was **gated on something that only
runs at teardown** — here the debounced persistence flush (`MeshNodeTypeSource`'s
200 ms `Timer`), which `FlushOnDispose` forces. Why the timer callback did not fire
on its own cadence under bulk load was never established; what the trace does prove
is the *dependency*, and that is enough to fix it.

**Fix the contract, not the timeout.** Writing via `stream.Update` completes on the
in-memory workspace **echo**, never the persistence flush, so it doesn't depend on a
`TimerQueue` callback getting a thread. (`stream.Update` is optimistic — if a
one-shot reader follows it, confirm the apply by polling the read until the new
state is visible.)

---

## 3. A disposed mesh isn't collected — ClrMD GC-root probe ("who holds the references")

Disposing a hub stops its timers/subscriptions but does **not** guarantee the object
graph is *unrooted*. A disposed-but-pinned mesh accumulates across tests and starves
the next one. To find the pin deterministically, see the probe at
`test/MeshWeaver.Hosting.Monolith.Test/MeshHubDisposalLeakTest.cs`:

1. In a `[MethodImpl(NoInlining)]` helper, build + exercise a mesh and return only a
   `WeakReference` to the mesh hub (the strong local dies with the frame).
2. `Mesh.Dispose()`, dispose + **null out** the `ServiceProvider` (an undisposed/
   still-referenced SP pins its singletons), force 12× blocking GCs.
3. If the hub survives, attach ClrMD to the live process
   (`DataTarget.CreateSnapshotAndAttach(Environment.ProcessId)`) and BFS from
   **non-stack** GC roots to the first `MessageHub`, printing the type chain.

Read the chain top-down — the root *kind* is the answer:

```
ROOT[StrongHandle] System.Object[] → System.Threading.TimerQueue → TimerQueueTimer
  → Task+DelayPromise → AsyncStateMachineBox<MessageHub.<Dispose>b__97_1> → MessageHub
```

That is a `Task.Delay` inside `Dispose` whose `TimerQueue`-rooted continuation
captured `this` (the 25 s watchdog — it pinned the whole graph for 25 s after *every*
disposal). Another run surfaced
`TimerQueue → TimerCallback → MeshNodeTypeSource → Workspace → MessageHub` — a
debounce `Timer` re-armed by a flush-echo `UpdateImpl` *during* Quiescing.

**Both are fixed in-tree** — the watchdog is an `Observable.Timer` on `DefaultScheduler`
now (`MessageHub.cs` carries the "🚨 Reactive, NOT a `Task.Delay`" note at the site), and
`MeshNodeTypeSource` gates its re-arm behind the `FlushOnDispose` flag. They are reproduced
here as the two *shapes* to recognise in a fresh chain, not as live bugs to hunt.

**Fail only on real leaks.** The probe distinguishes a **static / `TimerQueue` /
GC-handle** root (a true leak that accumulates) from a **stack** root — a disposal
continuation the snapshot froze mid-flight, which clears on resume. Assert on the
former; tolerate the latter.

🚨 **A PASS HERE PINS NOTHING.** This probe *samples* (a root live only for a bounded
window — 1 s, 100 ms — is caught only if the forced GC lands inside it), it *cannot
attribute* (it names whatever chain it happened to walk), and it *SKIPs on macOS*
(#674). It is for **discovery** — naming a root nobody knew about. Once you have found
one, pin the fix with a targeted, timing-free **ownership** test next to the code that
owns the subscription, and prove it with a **negative control** (revert only the
ownership line; watch that test fail). See
[Subscription Ownership](/Doc/Architecture/SubscriptionOwnership) for the convention
these leaks keep violating, and for the measured table of which primitives actually
root.

### Common disposal pins and their fixes

| Pin (ClrMD chain) | Cause | Fix |
|---|---|---|
| `TimerQueue → … → <Dispose> state machine → hub` | `await Task.Delay(t)` in `Dispose` with no cancellation; continuation captures `this` | Cancel the delay on disposal completion (or don't capture `this`) |
| `TimerQueue → TimerCallback → <Service> → … → hub` | A `System.Threading.Timer` not disposed, or **re-armed after** the dispose hook ran | Dispose the timer **synchronously** early + gate re-arm on a `_disposed` flag and `RunLevel > Started` |
| `… → MemoryCache → … → hub` | An `IMemoryCache`/`MemoryCache` whose scan timer pins the owner | Make the owner `IDisposable` and `Clear()` + `Dispose()` the cache on teardown |
| held by a `static` collection/SP | a process-wide cache/registry that outlives the mesh | Make it a mesh-scoped singleton (dies with the mesh); never `static` mutable state |

---

## Rules of thumb

- **Count distinct messages, not trace lines.** ~7 phase-lines per message.
- **A reply that lands at `runLevel=Quiescing`** was gated on teardown work — look
  for a debounced/flushed/timer-driven dependency, not a wedged handler.
- **Disposed ≠ unrooted.** Use the ClrMD probe; the *root kind* names the bug.
- **A watchdog that force-completes disposal is a smell** — it usually masks a
  non-quiescing cascade and can itself leak via its `Task.Delay` timer. Keep it
  *cancellable* so it never pins the hub it is guarding.
- **Never bump a timeout to "fix" a bulk-only hang.** It's a leak or a flush/
  thread-pool dependency; the timeout is the messenger.
