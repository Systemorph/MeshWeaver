---
Name: Which Kind of Silence
Category: Architecture
Description: "A log that goes quiet has four causes with four owners — a wedged hub, a starved pool, a suspended process, a dead instrument — and until the liveness heartbeat existed they were the same bytes. What the heartbeat reads, how to read it, and the two instruments that agreed with a wrong answer."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></svg>
---

# Which Kind of Silence

A portal's log stops. The page in front of the user says *"Subscribing to {path}…"*. Ninety seconds
later a test gives up, or a person reloads.

[Error Propagation & Wedges](../ErrorPropagationAndWedges) says what to do about a wedge, and the whole
of that page assumes the thing you are looking at **is** one. This page is about the step before: a
quiet log has **four** causes, they have **four different owners**, and for a long time they produced
the same bytes — none.

| what was happening | who owns it | what the log said |
|---|---|---|
| one hub or stream never answered | the mesh — read `target pump now:` on the pending-callback report | nothing |
| the thread pool was starved | a blocking bridge, somewhere off the hot path | nothing |
| every managed thread was suspended | the runtime — a GC pause, a heap that outgrew its container | nothing |
| the instrument itself had died | whoever wrote the instrument | nothing |

## The heartbeat

`ProcessLiveness` publishes one line, unconditionally, every
`Diagnostics:LivenessHeartbeatSeconds` (default 10 s; `0` switches it off and says so):

```
[LIVENESS] tick=7 gap=10.01s/10.00s gen0=453(+3) gen1=422(+1) gen2=21(+0)
           gcPause=20.00s(+0.00s) heap=5.81GiB serverGC=False
           poolThreads=55 poolPending=0 poolCompleted=1004321(+4321)
```

Read a quiet window against it:

| the heartbeat, during the silence | the verdict |
|---|---|
| keeps printing, `gcPause` delta ≈ 0, `poolCompleted` climbing | the process is **alive and working** — this is a genuine wedge. Go to [Error Propagation & Wedges](../ErrorPropagationAndWedges) and read `target pump now:` for the hub that is stuck |
| keeps printing, `poolPending` climbing while `poolCompleted` is flat | **thread-pool starvation** — a blocking bridge, not a stream defect |
| **stops with everything else, and never resumes** | every managed thread was **suspended**. A GC pause that outlived the process, or a runtime-level freeze. Not a wedge, and not the mesh's to fix |
| resumes carrying `OVERRAN by N`, with a matching `gcPause` delta | a GC pause of exactly that length, **self-reported in band** |

Two design choices carry the whole thing, and both are the opposite of what a first draft does.

**The line is unconditional.** Every other stall instrument in the platform — Orleans' watchdog, the
[disposal stall verdict](../DisposalStallVerdicts), the pending-callback report — fires when something
is *noticed*, and every one of them reports **on resume**. A stop that outlives the process therefore
prints nothing, and that is precisely the window a wedge investigation is reading. An instrument that
prints only when it is unhappy makes *"nothing happened"* and *"I was not running"* the same bytes —
the ambiguity the `/health` **census tag** removes one layer up, restated at the log. The cost is one
~200-byte line per process per period (≈1.7 MB per pod per day at the default), stated rather than
minimised.

**The tick runs on a thread of its own.** A tick scheduled on the ThreadPool — a `Timer`,
`Observable.Interval`, a `BackgroundService` loop — is silenced *by* the pool starvation it exists to
name, so rows 2 and 3 of that table would collapse into one. The precedent is
`IoPool.StartCanceller`, which refuses the pool for the same reason: the work you are
trying to observe may be holding every slot. And a tick that throws **reports and continues** — a
heartbeat that dies on one bad read goes silent forever, and its silence then reads as row 3. An
instrument that can manufacture its own verdict is not an instrument.

## The case this was built from (#4234)

MeshWeaver.Education's install e2e opened a freshly-installed exercise page and waited 90 s for its
workbench. One page never rendered. Its accessibility snapshot resolved every breadcrumb and held the
wedge string this platform documents by name:

```yaml
- text: Subscribing to …/ThinkInStreams/06-DataBinding/Exercise/MergePatch…
- progressbar
- text: Reconnecting… The server was updated. Reloading the page…
```

The portal's **last log line of the entire run** was 08:29:13.917Z. The container never restarted,
no core dump was written, and `docker stats` sampled the process at **1–7 % CPU** for the whole
window. It was filed as a layout subscription that wedged while the portal sat idle.

**Both readings that supported that were wrong, and the run's own artifacts said so.**

🚨 **The CPU samples exclude nothing — the run carries its own positive control.** Five minutes
earlier the same process took a stall Orleans *did* report on resume: `Platform stalled for
00:00:13.70. Total GC Pause duration during that period: 00:00:13.64`. The `docker stats` sample that
lands inside that confirmed stop-the-world pause reads **2.14 %**. So on this host a measured 13.64 s
GC pause and an idle process are indistinguishable by CPU. *"Single-digit CPU, therefore not GC"* is
falsified by the same artifact that proposed it.

🚨 **The failing unit was not a subscription.** The run's Playwright trace records every request the
browser made. At 08:29:14.09 — 0.17 s after the portal's last log line — **six ordinary static-file
`GET`s** for `_content/…/*.razor.js` were issued and never completed (`status = -1`), and at
08:29:46.17 the Blazor reconnect's `POST /_blazor/negotiate` did the same. Static files are served by
Kestrel's static-file middleware and touch no hub, no stream and no layout area. **The whole HTTP
server stopped answering**; the *"Subscribing to…"* text was simply what the page happened to be
showing when the process froze underneath it.

The remaining question — a suspended process versus a merely-idle one — is the row-1-versus-row-3
question above, and nothing inside the process could answer it. That is what the heartbeat is for.

### Two lessons that generalise past this incident

**A stall instrument that reports on resume cannot see the stop that kills the process.** Orleans'
watchdog reported the 13.7 s pause and reported nothing about the 90 s one, and both behaviours are
correct for what it is. Any instrument of that shape has the same blind spot; the fix is never to
lower its threshold.

**An external CPU sample is not a liveness signal.** It was read as one here, and it is read as one
often. Measure liveness from inside the process, where "I am still executing" is a thing that can be
said.

## A heap step names its allocator: the `[HEAPSTEP]` line

The heartbeat's `heap=` field (`GC.GetTotalMemory(false)`) is what showed MeshWeaver#5555's shape.
The public instance's replicas took **multi-GiB live-heap steps**: +2 to +10 GiB inside one 100 s sample, on
several replicas within seconds of each other (01:00:02Z and 01:00:11Z; 03:31:48Z and 03:31:56Z on
2026-09-24). The heap stayed up through gen-2 collections, and one replica died of
`OutOfMemoryException`. The field says **how much** was added. It cannot say **what** was added,
and no reading could be taken at the moment of a step:

- a `--type Heap` dump of a replica this size freezes it for about 106 s, which is past the
  liveness budget, so it restarts the container ([The Portal Heap Is Hubs](../PortalHeapIsHubs));
- the steps are unpredictable, so nobody is at the pod with a tool when one lands.

So the heartbeat now carries the reading itself. `AllocationByTypeSampler` is an in-process
`EventListener` on the runtime's `GC` keyword. The runtime already raises one `GCAllocationTick`
event per ~100 KB allocated, and each event names the allocated type. The sampler sums those events
per type between two ticks. When a tick's heap has grown by `ProcessLiveness.HeapStepThresholdBytes`
(512 MiB) or more since the previous tick, a `Warning` follows the `[LIVENESS]` line:

```
[HEAPSTEP] tick=41 heap=2.00GiB→8.00GiB (+6.00GiB) in 10.00s; sampled allocation in the window
6.00GiB, heaviest types: System.Byte[]=5.00GiB (83%) System.String=1.00GiB (17%). Allocated, not
retained: the leading type is the candidate for what the step holds.
```

How to read it:

- **It names what was allocated, not what is retained.** A step is memory that was allocated AND is
  still reachable. A type that dominates a window in which the heap grew by gigabytes is the leading
  candidate. A dump of a *quiet* replica confirms what roots it.
- **It is a sample.** Types of a few hundred KB are noise. A type of several GiB is not.
- **The line is conditional, and the `[LIVENESS]` line is not.** An absent `[LIVENESS]` line is a
  reading on its own. An absent `[HEAPSTEP]` line only means the heap did not step in that tick, and
  `heap=` already says so.
- **An empty type list is stated, never implied, and the two reasons for one are told apart.** If
  the sampler could not start, a step still prints and says *"the allocation sampler is NOT running in
  this process"*. If it is running and sampled nothing in that tick, the line says *"the sampler is
  running but sampled NO allocation in this window"*. It never prints an empty list that would read as
  "nothing was allocated".
- **A sample is never lost to a drain.** The window is one immutable map, and both sides replace it
  by compare-and-swap: a record folds its sample in with `ImmutableInterlocked.AddOrUpdate`, and a
  drain takes the map with `Interlocked.Exchange`. A record that races a drain therefore retries
  against the fresh map instead of adding to one nobody reads again.
- **Find it** with the same `Logs` action as the heartbeat: `query: "HEAPSTEP\\] tick="`, per pod.

### What the first readings named

The first live readings came from image `3.0.0-ci.9408` on both portals, 2026-09-26 between 10:00Z
and 18:15Z. `Logs` actions `Ops/Actions/logs-memexcloud-20260926-heapstep-5555` and
`…/logs-memex-20260926-heapstep-5555` read 43 and 88 lines, none cut.

- **Every step past warm-up has the same allocation mix.** `System.String` is 53–56 % of the sampled
  bytes, `System.Byte[]` 25–31 %, and `System.Text.Json.JsonDocument` 2–4 %. The mix is the same on
  the control instance and on the public instance, and across pods. A window allocated 4–7 GiB in 10 s. Only the first ticks
  after boot differ, when assembly loading (`CoffHeader`) and serializer set-up are in the mix.
- **The steps are churn, not retention.** With server GC, a gen-0 budget is gigabytes, so
  `GC.GetTotalMemory(false)` includes garbage until the next collection. On
  `memex-portal-deployment-6c7669df84-nbrdx`, tick 314 read `heap=4.44GiB`. Tick 315 ran two gen-0,
  one gen-1 and one gen-2 collection and read `3.03GiB`. At the 512 MiB threshold, a `[HEAPSTEP]` line
  here marks an allocation burst. It does not by itself mark a leak.
- **String ≈ 2 × Byte[] plus a `JsonDocument` suggests a JSON round trip**, but does not identify
  its caller or prove duplication: UTF-8 input and a correctly materialized UTF-16 leaf can also
  contribute that mix. `ObjectPolymorphicConverter.ReadObject` did duplicate the whole value for
  every `object`-typed value. It copied the value into a `JsonDocument`, took `GetRawText()` as a
  string, and deserialized that string again. Each nested `object` member re-enters the converter, so
  this happened once per nesting level. `PolymorphicReadAllocationTest` measures one read of a value
  nested 8 levels deep, with a 400,000-byte leaf string: **4,006,624 bytes** allocated before the fix,
  **402,024** after. The leaf itself is the floor.
- **The fix.** When `$type` is the first property, names a registered type, and there is no
  reference metadata, the converter now deserializes straight from the reader, with no document and
  no string. Every other shape takes the general path as before, and that path now deserializes from
  UTF-8 instead of a string.
- **Not yet established:** whether this converter is the dominant caller in production. The sampler
  names types, not call sites. A high `String` share after the repair keeps the allocation investigation
  open, but cannot distinguish another whole-JSON copy from strings that content needs to materialize.

### A query read still copied the whole value

On 2026-09-27, `Ops/Actions/verify-5555-memexcloud-20260927-heapstep` read and landed 50 lines from
the three `3.0.0-ci.9443` replicas, with no failures or truncation. Nine windows still attributed more
than 50 percent of sampled allocation to strings. That image contains the converter repair. These
are allocated bytes, not retained roots, and they still do not attribute a caller.

An independent code-path control found another full-value copy: `MeshNodeStreamCache.DeserializeContent`,
which re-types every emitted query node, called `JsonElement.GetRawText()` or `JsonObject.ToJsonString()`
before deserialization. `QueryContentAllocationTest` drives that production method with real hub
serializer options and a registered content type. After warm-up, a 512,000-byte leaf costs
**1,024,616 allocated bytes** through either input shape before the repair. Reading the JSON
representation directly reduces the `JsonElement` case to **512,504** and the `JsonObject` case to
**512,696**, while preserving the typed leaf. Raw diagnostic text is now formed only when re-typing
fails; registry recovery and the degradation warning's exception still run as before.

Truncating after `GetRawText()` or `ToJsonString()` also allocated the whole failed payload.
`BoundedJsonDiagnostic` (since removed — see below) read a bounded UTF-8 span from a `JsonElement`, or visited a `JsonNode`
until its character budget was reached. String escaping received only the remaining prefix, and
custom value converters were not invoked for diagnostics. The same formatter supplied stream and
query warnings. A 256,000-character escaped leaf used **1,026,168 allocated bytes** with the old
serialize-then-truncate path; the three JSON shapes now stay below the 64,000-byte test bound.
Small JSON, UTF-8 cuts, and disposed-document diagnostics have separate controls.

That formatter has since been replaced, for a reason that has nothing to do with memory: the
degradation warnings are Warning-level and ship to the central log store, which row-level security
does not reach, so a bounded *prefix of the stored value* still copied a node's content to every
reader of the logs. The warnings now carry the content's **shape** — the `$type` (or `no $type`),
the JSON kind, the top-level property names (at most 32, each cut and JSON-escaped) and the byte
length — and no value. `ContentShapes.Describe` (`MeshWeaver.Data.Contract`) is the one
implementation every site uses, and it keeps the allocation property above: a `JsonElement` is
measured from its raw UTF-8 span; a `JsonNode` is sized over its top level only, never serialized
and never descended into (descending would materialise a parsed tree's lazy containers), so no
converter runs and no payload is materialised — the size is printed `~` when complete and `≥` when
a nested container or the entry budget left it a lower bound. Only the displayed names are decoded,
each cut before decoding; the discriminator is matched on raw bytes. The marker's `MeshNodeContentDegradedException.Shape` carries the same
text; its old `RawJson` property survives only as an `[Obsolete]` forwarder that the platform's
own sites leave `null`. `DegradedContentLogsShapeNotValuesTest` drives the production seams with a
value-bearing payload and asserts the value is absent from the formatted line, the marker's
`Message` and its `ToString()`, while the `$type` and keys are present.

This establishes and removes a per-query whole-JSON string copy. It does not establish its share
of the production allocation windows or explain retained memory. After the supported roll, exercise
query content re-typing on the portal and repeat the heap readings before deciding whether #5555
can be closed.

`HeapStepNamesItsAllocatorTest` pins the rule with pure tests. It also has a live test: it allocates
~160 MiB of a marker type and requires the sampler to attribute at least 64 MiB of it to that type.
That live test is the positive control. A sampler that received nothing would make every real line
say "cannot name".

## A stalled pool names its frame: the `[POOLSTALL]` line

The `[LIVENESS]` line can say *that* the pool starved — `poolCompleted(+0)` with `poolPending` above
zero, on a tick whose `gap` is on time. It cannot say *what* held the workers. A stall like that
leaves nothing behind: the process does not crash, so no dump is written, and no governed action
can read the stacks of a live pod. Every recurrence was therefore re-triaged from the same
symptom (#5388).

So the heartbeat now acts on that reading. On the first tick in which the pool finished **no** work
item while work was queued at both the previous tick and this one, it captures every ThreadPool worker's stack, once, and logs them grouped by
their innermost 12 frames, the largest group first:

```
[POOLSTALL] tick=1099: the ThreadPool completed no work item in the last 10.12s with 20357 queued over 69 thread(s). 69 worker stack(s) in 3 distinct shape(s), largest first (grouped by the innermost 12 frames, each distinct caller in full; N other thread(s) searched for lock holders):
  ×61:
     at <the frame the pool is held in>
     …
```

- **How the stacks are read.** The heartbeat has its own thread, which the stall cannot hold, and
  it hands the capture to a second dedicated thread so a slow or hung capture can never silence the
  `[LIVENESS]` line; one capture is in flight at a time. The capture writes one `Normal` minidump of its own process (thread stacks and module data, no heap) and reads
  it back with ClrMD, then deletes the file. This is the same reader, with the same DAC pin, that the
  silo stop uses for leaves that outlive its join (#7277): `BlockedThreadStacks.CaptureThreadPoolWorkers`,
  registered as `IThreadPoolWorkerStacks` by `AddThreadPoolWorkerStacks()` in every Orleans silo.
- **Outer callers and lock holders (#5388, after the 2026-11-07 capture).** That capture named the
  frame — ≥13 workers in `Monitor.Enter_Slowpath` inside an Rx `Zip` gate under
  `EaGraphAuth+CredentialRead` — but cut every stack at 12 frames, so it named neither the caller
  that led the workers into the gate nor the thread holding it. The capture now reads every live
  managed thread to 512 frames, and the line prints:
  - each group's shared innermost 12 frames once, then every distinct **outer continuation** in
    full (`-> ×N called from:`), most frequent first, up to `MaxCallersPerGroup` (4);
  - for a group waiting to enter a monitor, its **lock site** (the first frame outward of the
    `System.Threading.Monitor` frames) and every captured thread — pool worker or not — that is
    inside that method WITHOUT waiting to enter it there (`LOCK ×N candidate holder:`, full stack).

  🚨 **A candidate is not a proven owner.** A `Normal` minidump carries no sync-block table:
  measured on macOS, ClrMD enumerates **0** sync blocks from a 28 MB `Normal` dump of a process
  whose monitor had three waiters, while a `WithHeap` dump of the same process (410 MB, ~1 s) names
  the owner thread. A heap dump of a multi-GB portal would suspend it in proportion to its heap, so
  the capture stays `Normal` and the owner is inferred: a thread inside the same method may hold a
  different instance's gate. `NO captured thread is inside that method` means the holder is in native
  code or left the method still holding the lock.

  **Size.** The line is bounded as a whole: `ThreadPoolStall.MaxLineChars` (96,000 characters, under
  Loki's default 256 KB line limit). Groups are appended largest first until the next would not
  fit, and the rest are counted with "budget is spent". The old line was at most 8 × 12 frames,
  about 10–15 KB; the new one is at most ~94 KB once per 30 minutes per pod.
- **The second trigger: queue delay (#5388, after the 2026-11-09 stalls).** The no-progress rule
  missed five of the six stalls logged in the three days after the capture shipped; only one
  (2026-11-08 13:29Z, `…697fc49dc8-fvqn2`) printed a `[POOLSTALL]` line. On `…5cf7b87497-4l9xx`
  (2026-11-09 22:55Z) Orleans logged 15.6 s and 10.1 s stalls while the heartbeat read
  `poolPending=11196 poolCompleted(+9310)` and `poolPending=11813 poolCompleted(+1284)`: a burst of
  ~11,000 items that 32 threads were still draining. Every period completed *something*, so the
  rule never fired, yet every item behind the burst — Orleans' health-monitor timer included —
  waited 15 s. `LocalSiloHealthMonitor` measures that wait, not the completion count. So the
  heartbeat thread now also keeps ONE probe work item in the ThreadPool's global queue
  (`ThreadPoolQueueProbe`), polls it every 250 ms slice, and captures when the probe has waited
  `ThreadPoolStall.QueueDelayCaptureThreshold` (5 s, half the 10 s at which the Orleans reading is
  filed) without starting. The line then begins `a probe work item queued N s ago had not started`,
  and its groups name what the workers were busy with while the queue backed up. The two triggers
  share one cooldown and one in-flight capture.
- **Idle workers and the queue census (#5388, #5799, after the 2026-11-11 captures).** The first
  production queue-delay capture (memex-cloud `…69f587c4c8-fxvlm`, 14:40:57Z: probe waited 5.19 s,
  69,012 queued over 42 threads) printed its largest shape, `×20` of 44 workers, as
  `LowLevelLifoSemaphore.WaitForSignal` directly under `PortableThreadPool+WorkerThread.WorkerThreadStart`
  — the pool's own wait for work. It was read as twenty workers busy in a hub handler, because the
  `×20:` header and the frames under it were read from separate log rows. Twenty idle workers beside
  a five-second queue is the opposite finding: the pool was not short of threads and nothing held
  it; it was draining a backlog at fewer workers than it had, so the item that names the producer
  was in the QUEUE, on no stack. The line now says both:
  - after the shape count, `N of them IDLE (parked in the pool's own semaphore …)` and how many were
    running — so an idle shape is never again taken for what held the pool
    (`ThreadPoolStall.IsIdleWorker`);
  - a last section, `QUEUED WORK: N item(s) read, in M producer(s)`, grouping every queued item by
    the code it will run — a `Task`'s delegate, an async method's state machine, or the first
    delegate a work-item wrapper holds — largest first (`ThreadPoolQueueCensus`). A hub drain reads
    `MeshWeaver.Messaging.MessageService.DrainOne`. The census is read through the runtime's debugger
    enumeration `ThreadPool.GetQueuedWorkItems()` (internal, reached by reflection, the same walk SOS's
    `!ThreadPoolQueue` does over a full dump), BEFORE the dump suspends the process, bounded at
    500,000 items. A runtime without that method gets `the queue could NOT be read (…)`, never an
    empty census.

  When reading a capture through a `Logs` action, include the header rows in the query
  (`|~ "^\\s+(at |×|->|LOCK|…|QUEUED|    ×)|POOLSTALL"`): a filter on `at ` alone drops every `×N:` and
  `-> ×N called from:` row, and the groups can then no longer be told apart.
- **Cost.** The dump suspends the process for about a second. It is taken at most once every
  30 minutes (`ThreadPoolStall.CaptureCooldown`), and only after a whole period in which the pool
  served nothing, or once a queued item has waited 5 s. The probe itself is one queued no-op at a
  time.
- **When it cannot read stacks.** The line still prints and says why: no reader registered in this
  host, the capture failed (with the exception), or the dump held no pool worker.
- **A missing `[POOLSTALL]` line does NOT rule out starvation.** Three cases print none for a real
  stall: a capture already taken in the last 30 minutes (the cooldown), a capture still in flight,
  and a capture that hangs and never logs. Decide whether the pool made progress from the
  `[LIVENESS]` counters (`poolCompleted(+0)` with `poolPending` above zero, on consecutive ticks),
  never from the absence of this line.
- **Tests.** `ThreadPoolWorkerStacksTest` parks real pool workers in a known method, captures a real
  dump, and requires exactly those workers in that method, rendered as one group naming it. Its
  negative control takes the
  same capture with nothing parked and must not find the method. `ThreadPoolStallTest` pins the
  decision (stalled, idle, busy, first tick, cooldown) and the line's grouping and ordering over
  controlled inputs; `ABacklogThePoolIsStillDraining_IsMissedByNoProgress_AndCaughtByQueueDelay`
  replays the two 4l9xx ticks verbatim, and `ThreadPoolQueueProbeTest` pins the probe (one item in
  flight, wait measured from its own queueing, and a real-pool positive control). `WorkersWaitingOnAMonitor_TheLineNamesTheirOuterCallerAndTheCandidateHolder`
  parks pool workers 20 frames below a known outer method, waiting on a monitor a dedicated
  non-pool thread holds, and requires the line from a real dump to name the outer method (asserted
  to sit deeper than frame 12, so a 12-frame line cannot pass) and the holder's frame.
  `ThreadPoolStallTest.IdleWorkers_AreCountedByName_SoAnIdleShapeIsNotReadAsWhatHeldThePool` replays
  the fxvlm idle frames (with a busy worker and a semaphore wait under a work item as controls), and
  `ThreadPoolQueueCensusTest.OnTheRealThreadPool_TheCensusNamesTheItemsActuallyQueued` parks every pool
  worker, queues 1,000 marker items from the test thread and requires the census of the real queue to
  count all 1,000 under the marker's method — and none before they were queued.

### The stall that made this necessary

memex-cloud, pod `6997ff97bc-5q8t2`, image `3.1.12401`, 2026-11-02. Read through governed `Logs`
actions on the control instance (`Ops/Actions/t1102-5388-*`):

| tick (UTC) | gap | gcPause in tick | poolThreads | poolPending | poolCompleted |
|---|---|---|---|---|---|
| 08:57:57 | 10.06 s | +0.89 s | 69 | 37 | +33,049 |
| 08:58:27 | 10.20 s | +0.65 s | 69 | 97 | **+0** (and +0 in the two ticks before) |
| 08:58:37 | 10.01 s | +0.88 s | 69 | 20,335 | +2,314 |
| 08:58:48 | 10.12 s | +0.80 s | 69 | 20,357 | **+0** |
| 08:58:58 | 10.11 s | +0.58 s | 69 | 1,236 | +49,091 |
| 08:59:08 | 10.17 s | +0.78 s | 69 | 1,260 | +1 |
| 08:59:18 | 10.29 s | +0.80 s | 69 | 1,279 | **+0** |
| 08:59:28 | 10.22 s | +1.12 s | 69 | 1 | +96,728 |

Orleans logged `Thread Pool execution stalled for 21.95s` (08:58:56) and `24.27s` (08:59:21), and a
`SiloHealthMonitor` timer that fired 35.7 s late. The pod then drained and exited cleanly
(`exit 0`, no dump on the volume).

What these readings establish:

- **It was not a machine pause or a crash dump.** Every heartbeat tick was on time. The 10-23 and
  10-30 reopens of #5388 were the opposite case: `createdump` freezing a dying process.
- **It was not GC.** GC took 6–11 % of each tick.
- **It was real starvation.** 69 workers finished nothing for whole periods. The runtime added no
  thread in about 90 s.
- **It happened during a NodeType compile and release wave.** At 08:59:21 the compile lane was full,
  with six of six slots running and four queued, and its longest wait so far was 235 s. Sampled
  allocation was dominated by Roslyn symbol types.

What they do not establish is the frame the 69 workers were in. That is what the `[POOLSTALL]`
line exists to capture at the next occurrence.

## Where this sits

[Error Propagation & Wedges](../ErrorPropagationAndWedges) — what must always happen once you know it is
a wedge. [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — why a single thread saturates.
[Reading a Disposal Stall Verdict](../DisposalStallVerdicts) — the same "what does this field actually
measure" discipline applied to the disposal snapshot, including three fields that were read as
evidence while measuring nothing. [Debugging Message Flow](../DebuggingMessageFlow) — how to trace a live
hang once the process is known to be alive.
