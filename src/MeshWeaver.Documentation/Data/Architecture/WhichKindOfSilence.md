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

## Where this sits

[Error Propagation & Wedges](../ErrorPropagationAndWedges) — what must always happen once you know it is
a wedge. [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — why a single thread saturates.
[Reading a Disposal Stall Verdict](../DisposalStallVerdicts) — the same "what does this field actually
measure" discipline applied to the disposal snapshot, including three fields that were read as
evidence while measuring nothing. [Debugging Message Flow](../DebuggingMessageFlow) — how to trace a live
hang once the process is known to be alive.
