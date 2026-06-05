# Debugging Disposal: Message Storms, Leaks, and "Who Holds the References"

When a test (or prod hub) **hangs on disposal**, **leaks memory across disposed
meshes**, or a write **never gets its reply in bulk** but passes in isolation, the
symptoms all look the same from the outside. This page is the playbook that
cracked the `TodoDataChangeWorkflowTest` bulk hang. It has three tools, in the
order you should reach for them.

> TL;DR of that investigation: the write succeeded fast, but its **response was
> gated on the debounced persistence flush** (a 200 ms `Timer`), which gets
> thread-pool-starved during a synchronous test wait in bulk — so the reply only
> arrived when `FlushOnDispose` forced the flush at teardown. The fix was to write
> via `stream.Update` (completes on the in-memory echo, not the flush). Two real
> `TimerQueue` disposal leaks were found along the way with ClrMD.

---

## 1. Is it actually an *endless* message loop? — `MESHWEAVER_MSG_TRACE`

Disposal posts a cascade of `ShutdownRequest`s. Before you assume a runaway loop,
**count distinct messages, not trace lines.**

```bash
MESHWEAVER_MSG_TRACE=1 dotnet test <project> --filter <Test> --no-build
TRACE="$TEMP/meshweaver-msg-trace.log"     # %TEMP%/meshweaver-msg-trace.log

# Histogram by message type (counts LINES — ~7 phase-lines per message)
grep -aoE "msg=[A-Za-z0-9_]+" "$TRACE" | sort | uniq -c | sort -rn

# DISTINCT messages per hub (this is the real signal)
grep -a "msg=ShutdownRequest" "$TRACE" \
  | grep -aoE "hub=[^ ]+ msg=ShutdownRequest id=[A-Za-z0-9_-]+" \
  | sed -E 's/ id=.*//' | sort -u | sed -E 's/ id=.*//' \
  | sort | uniq -c | sort -rn | head
```

Interpretation:

- **~3 distinct `ShutdownRequest` per hub** (`Started → Quiescing → DisposeHostedHubs`)
  = a **normal finite cascade**. A 96-hub mesh shutting down = ~300 distinct
  ShutdownRequests ≈ ~2000 trace *lines*. That is NOT a loop. (The Todo case looked
  like "2121 ShutdownRequest" but was 303 distinct messages × 7 phase-lines.)
- **Dozens per hub** = a real **version-chase loop**: `MessageHub.HandleShutdownCore`
  re-posts the `ShutdownRequest` whenever `request.Version != Version - 1` (a message
  arrived after shutdown was posted). If new traffic keeps bumping `Version` during
  teardown (e.g. a stream resubscribing, a flush echo), it never settles. Find the
  *other* message that keeps arriving (filter the disposal window, histogram
  non-`ShutdownRequest` types) and stop it at the source — gate resubscription on
  `RunLevel > Started`.

A disposal **watchdog** that force-completes after N seconds is masking this, not
fixing it (see §3 for why the watchdog itself can leak).

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
200 ms `Timer`), which `FlushOnDispose` forces. In bulk, the synchronous test wait
pins a thread-pool thread and the timer callback can't fire, so the write parks
until disposal.

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

**Fail only on real leaks.** The probe distinguishes a **static / `TimerQueue` /
GC-handle** root (a true leak that accumulates) from a **stack** root — a disposal
continuation the snapshot froze mid-flight, which clears on resume. Assert on the
former; tolerate the latter.

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
