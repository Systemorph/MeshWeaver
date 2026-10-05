---
Name: Fault-Injection Harness
Category: Architecture
Description: Deterministic, releasable faults for isolated regression tests — start two (or three) silos and hand an address from one to the other, hold a storage flush, open a NotFound window, hold a change feed or a query's first frame, lose a webhook — and one regression per production incident, each with its negative control.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v4"/><path d="M12 18v4"/><circle cx="12" cy="12" r="5"/><path d="m9 12 2 2 4-4"/></svg>
---

# Fault-Injection Harness

Production incidents in a mesh are ORDERINGS: a write lands while its owner's pod lingers in a stop,
a move reads storage before a flush, a first query frame arrives after a grace, a route answers
NotFound for a node that was just created, a webhook meets a 500 mid-roll. Reproducing them by
timing — a sleep, a load generator, a lucky run — produces tests that pass alone and fail in the
suite, or the reverse. The harness makes each ordering a **switch**: the fault is in force from the
moment the switch is created until it is released, and nothing in between depends on a clock.

Two projects under `test/`:

| Project | What it holds |
|---|---|
| `MeshWeaver.Testing.FaultInjection` | the single-process injectors: `FaultSwitch`, `FaultInjectingStorageAdapter`, `HeldFirstFrameQueryProvider`, `CrossProcessChangeRelay`, `FaultInjectingHttpHandler`, `FaultInjectingInbox<TMessage,TResult>`, and `AddFaultInjectingStorage()` / `AddHeldFirstFrame()` for a monolith mesh. No Orleans dependency. |
| `MeshWeaver.Hosting.Orleans.TestBase` → `FaultInjectionCluster` | the multi-silo harness: N in-process silos over ONE store of record, each silo's store under its own injector, the LISTEN model between them, and `Kill` / `Drain` / `Linger`. |
| `MeshWeaver.FaultInjection.Test` | one isolated regression per incident, plus one worked example per injector (`InjectorExamplesTest`). |

A satellite reaches them the way it reaches `MeshWeaver.Fixture`: by `ProjectReference` through its
platform checkout (`$(MeshWeaverRoot)/test/…`). Nothing here is packed.

## The rules the harness keeps

- **A fault is a `FaultSwitch`.** Created closed, released exactly once, `Dispose` = release, so
  `using var hold = …` (or `try/finally`) releases it on every exit path — a failing assertion never
  strands the work it holds, and the fixture's teardown never waits on it.
- **Nothing parks a thread.** A held operation is an observable that has not emitted yet
  (`FaultSwitch.Gate` composes it behind an `AsyncSubject` the release completes). That is what makes
  a switch legal inside a hub's action block. No `SemaphoreSlim`, no `Task.Delay`, no sleep.
- **Every fault reports what it met.** `FaultSwitch.Arrivals` replays each operation the fault
  answered, noted when it ARRIVES. A case waits on that positive signal ("the write reached the
  hold") before it asserts, and asserts it again at the end — so a case whose fault never fired
  fails instead of passing having injected nothing.
- **The fixture proves its own wiring.** `FaultInjectionCluster` asserts, per silo, that the writable
  store is served through that silo's injector before any case runs.
- **Instance state only.** Every injector is a singleton of one mesh or one silo; the cluster owns
  the relay. No statics.

## The injectors, one example each

### Multi-silo: start two processes and transfer from one to the other

`FaultInjectionCluster` (derive a `Cluster` class per test class — a case that removes a silo leaves
the cluster changed, so it is never pooled; override `SiloCount` for three):

| Call | Production shape |
|---|---|
| `Kill(i)` | SIGKILL / SIGSEGV — no graceful stop, no deactivation |
| `Drain(i)` | a rolling restart — SIGTERM, then a graceful stop |
| `await using (Linger(i))` | the roll at its worst moment — told to stop, still in the cluster; every grain on it refuses as `ShuttingDown` and hands its address off, until the handle is disposed |
| `Relay.Hold()` / `Relay.Drop()` | a slow / reconnected-without-replay PostgreSQL LISTEN channel between silos |
| `HandOffTarget(path, leaving, active)` | which silo a lingering grain hands `path` to — so a case can CHOOSE a path whose hand-off lands where it needs it |

Silo indices are stable across kills. Death detection and the held-stream heartbeat run at test
cadence (`FaultInjectionCluster.Heartbeat`, 1 s). The cross-process relay is ON by default, so a
case measures the mesh production runs — before it, a multi-silo test had a shared store and no
cross-process invalidation at all, and published it by hand.

```csharp
public class AWriteDuringAPodRollIsReDrivenTest(AWriteDuringAPodRollIsReDrivenTest.Cluster mesh)
    : IClassFixture<AWriteDuringAPodRollIsReDrivenTest.Cluster>
{
    [Fact(Timeout = 180_000)]
    public async Task AWriteToAnOwnerWhoseSiloIsStopping_IsReDriven_AndTheHeldReadDeliversIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "roll-write", ct);
        await using (mesh.Linger(1))
        {
            await held.Write("v2").Should().Within(TestTimeouts.Convergence).Emit("…re-driven…", ct);
            await held.Delivers("v2").Should().Within(TestTimeouts.Convergence).Emit("…delivered…", ct);
        }
    }
    public class Cluster : FaultInjectionCluster;
}
```

### Storage flush hold

`FaultInjectingStorageAdapter.HoldWrites(path)`: every write of the path reaches the INNERMOST
adapter — under every guard the platform stacks on top — and waits. The owner has committed in
memory; storage still holds the previous state. `HeldWrites(path)` replays each held write, and
`EnumeratedWhileHeld(path)` reports a subtree enumeration (only a copy or a move does that) made
while the flush is held. This is the general form of MeshWeaver.Plugins' `SubmitFlushHoldingStorageAdapter`
(#5670, Plugins#2536).

```csharp
protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    => base.ConfigureMesh(builder.AddFaultInjectingStorage());   // BEFORE the base adds persistence

using (var flush = Storage.HoldWrites(path))
{
    await Mesh.GetMeshNodeStream(path).Update(n => n with { Name = "committed" }).Should().Emit(…);
    await Storage.HeldWrites(path).Where(n => n.Name == "committed").Should().Emit(…);
    // Storage.Inner still reads the previous state here.
}
```

🚨 Register it BEFORE the base configuration's `AddInMemoryPersistence` — persistence `TryAdd`s its
adapter, so registering first is what puts the injector under the guards. On a cluster the fixture
does this for every silo.

### Route / NotFound window

`HidePath(path)`: reads, existence probes, route resolution and the parent's child listing answer as
if the path did not exist yet; writes still land. The shape of a resolver that has not caught up
with a create another process acknowledged. Resolution stops at the parent, so the router answers
`No node found at '…'` exactly as production logs it.

### Change-feed hold

`HoldChangeFeed()`: the silo's change notifications — its own commits and relayed ones — queue in
order and are delivered on release. `Relay.Hold()` does the same for the cross-process leg only.

### First-frame delay

`AddHeldFirstFrame(partition)` adds a `HeldFirstFrameQueryProvider` to the query fan-in. `Hold()`
keeps it silent for queries into that partition, and the fan-in waits for every matching provider's
first frame (bounded by `QueryInitialBudget`), so the query's `Initial` is held until release.

```csharp
using (var hold = FirstFrame.Hold())
{
    var frames = MeshQuery.Query<MeshNode>(query).Replay();
    using var subscribed = frames.Connect();
    await hold.Arrivals.Should().Emit(…);
    await frames.Should().NotEmit(TimeSpan.FromMilliseconds(500), "no first frame while held");
    hold.Release();
    await frames.Should().Emit("released, the fan-in emits its first frame");
}
```

### Webhook loss / 500 during a roll

`FaultInjectingHttpHandler` sits in front of an HTTP inbox: `Refuse(status)` answers without
forwarding (the pod-roll 500 GitHub never retries), `FailAfterDelivery(status)` forwards and then
answers the error anyway. `FaultInjectingInbox<TMessage,TResult>` injects the same two faults into an
inbox that is a function. `Faulted` replays every delivery a fault met, so a case can assert that a
sweep re-fed exactly what the sender lost.

## The cases

| # | Incident | Test (`MeshWeaver.FaultInjection.Test`) | Injector | Negative control |
|---|---|---|---|---|
| 1 | Write during a pod roll (#5873) | `AWriteDuringAPodRollIsReDrivenTest` | `Linger` | #5873's two `ShuttingDown` arms reverted → red: `MeshNode Unknown … is shutting down … Rejecting now` |
| 4 | Steward's first write after its create (Plugins#2530, #6045, #6046) | `ARoutedWriteRightAfterItsCreateTest` (6 cases) | `HidePath`, `Relay.Hold`, a real pre-create probe | `MeshNodeStreamCache.ResetFailureState` made a no-op → the probe case red: `No node found at '…/Item'`; the read-window write fast-fail restored in `UpdateRaw` → the cross-replica case red with the same line; the cached-remainder route restored in `PathResolutionService` → the existing-parent case red: `… Closest ancestor is '…' (remainder='Item')` |
| 6 | Fleet watch freeze (#5011) | `AHeldReadSurvivesItsSourceSilo{BeingKilled,Draining}Test`, `AHeldReadOnAThirdSilo…`, `AHeldReadFollowsItsOwnersHandOffTest`, `AHeldReadFollowsItsOwnersHandOffWithoutTheChangeFeedTest` | `Kill`, `Drain`, `Linger`, `HoldChangeFeed`, `HandOffTarget` | held-stream heartbeat pushed beyond the budget → all four kill/drain cases red (the owner never re-activates); the owner's answer to a heartbeat for a stream it does not serve removed (`MeshExtensions.HandleHeartBeat`) → the withheld-feed hand-off case red: the held read emits nothing |

Cases 2, 3, 5, 7 and 8 exercise code that lives in MeshWeaver.Plugins, which reaches this harness by
`ProjectReference` through its platform checkout (`Requires-platform: MeshWeaver#5879`):

| # | Incident | Test (MeshWeaver.Plugins) | Injector | Negative control |
|---|---|---|---|---|
| 2 | Lost portal feedback (Plugins#2536, #5670) | `FeedbackSubmitRelocationTest` (Hosting.Monolith.Test) | `HoldWrites` | the hand-over fix reverted → red: the move enumerated the source while its flush was held |
| 3 | Index grace too early (Plugins#2511) | `ASlowFirstFrameResolvesTheRecordTest` (Fleet.Control.Test) | `HeldFirstFrameQueryProvider`, held `IndexGrace + 2 s` | grace started with the reads → red: `Unseen` after 10.0 s |
| 5 | Stale plan approve (Plugins#2542) | `AStalePlanApproveAcrossReplicasTest` (Fleet.Control.Test) | two silos, page and owner apart, `HoldChangeFeed` | page frozen on its first render → red; any digest accepted → red |
| 8 | Webhook 500 during a roll (Plugins#2530) | `ALostWebhookIsReFedByTheSweepTest` (Fleet.Control.Test) | `FaultInjectingInbox.Refuse` | the sweep not kicking an unrecorded head → red |
| 7 | Stuck Roll Verify (Plugins#2403) | not landed — see below | `Kill` | — |

Each test file names what it compiles from the in-mesh sources (the Hosting package's `Source/*.cs`
reach a test through `MeshWeaver.Fleet.Control`, which links them) and what, if anything, it supplies
because the test mesh lacks it (case 8: the one GitHub read, as the sweep's pure rule takes it).

## What the cases found

**#5011 / #6047 — a held read's liveness across a roll WAS only as good as its process's change
feed.** Held reads survive a killed, drained or lingering owner in every topology modelled, with
notifications flowing. But with the holder's change feed withheld and the owner handed off to a THIRD
silo, the held read froze: no value, no error, no completion — the #5011 symptom, "reads as holding
while it holds nothing" — and delivered the moment the one withheld notification was released. The
reason was structural: the sync stream's heartbeat was a bare keep-alive (`JsonSynchronizationStream`
called the change-feed resubscribe "the sole recycled-grain detector"), and a lingering owner's goodbye
rides the refused router. **Fixed (#6047):** while the owner has acknowledged a subscription, the
heartbeat names the stream (`HeartBeatEvent.StreamId`), and an owner activation that serves no such
stream for that subscriber (`Workspace.ServesClientSubscription`) answers `StreamEndedEvent` — the
announced-end re-ask the subscriber already has (bounded, teardown-gated, run as System). The OWNER is
now the detector, so `AHeldReadFollowsItsOwnersHandOffWithoutTheChangeFeedTest` delivers the write while
the notification is still withheld. A subscription still in flight is never named (the heartbeat is not
deferred behind an owner's initialisation; a `SubscribeRequest` is), and a tearing-down owner stays
silent. The case was first written with the hand-off left to chance and passed alone while freezing in
the suite: the hand-off target is a per-process ordinal string hash, which is why `HandOffTarget`
exists. **Not modelled:** an in-process kill still writes `Dead` to the membership table, so a SIGSEGV
whose row stays `Active` until the survivors vote it out is not exercised.

**Plugins#2530 / #6045 / #6046 — process-local negatives retracted only by a notification.** A point
read of a not-yet-existing path leaves two verdicts on the reading process: a storm-breaker window in
`MeshNodeStreamCache`, and — when the parent exists — an ancestor-plus-remainder route in the silo's
`PathResolutionService` cache, which the router turns into `No node found at '…/Item'. Closest ancestor
is '…'`. Both were retracted only by the create's change event reaching that process. When the probe,
the create and the write all run in one process — the steward's shape — the first write lands
(`APreCreateProbe_DoesNotPoisonTheIssuersFirstWrite_WhenNotificationsAreLate`). When ANOTHER replica
created and its notification was late, the prober's first write failed with the steward's exact line
until the notification arrived. **Fixed:** a write is never answered from a READ's window (only a
write-minted window fast-fails writes), and a route-shape lookup re-asks the store for a cached
remainder instead of serving it — see [MeshNode Stream Cache](../MeshNodeStreamCache), "A write is never
answered from a READ's miss". `ACrossReplicaCreate_WithALateNotification_IsWritableByTheProber_BeforeTheNotificationArrives`
and `ACrossReplicaCreate_UnderAnExistingParent_WithALateNotification_IsWritableByTheProber` hold the
relay for the whole case and require the write to land. Whether the steward's production NotFound met
the cross-replica shape is still **not established**; both halves are closed regardless. Separately, a
write routed into a transient NotFound window fails loudly naming the path, and the write-side breaker
then holds the path shut for one base cooldown (2 s, measured) because no change event follows to clear
it; the next natural write after that lands.

**Plugins#2403 — the resumer's hold, intermittently without a heartbeat.** The case-7 test (two
silos; the action's hub live on the old pod; `RunningActionResumer` started on the new pod; the old
pod killed) cannot be made to discriminate yet, which is why it is not landed:

- With the production read-stream idle release (10 min) it passes even when the resumer's hold is
  removed. A still-warm cached stream's heartbeat is what re-activates the action, so the control is
  invalid.
- With the idle release shortened to 500 ms (`MeshNodeStreamCacheOptions`), which is what isolates
  the hold, the same configuration was red in 2 of 5 runs. In a red run's message trace the new pod
  sends no heartbeat for the action at all after the resumer's first emission; in the green runs it
  sends one every second, and one of them re-activates the action after the kill.

That is the #5011 shape again — a held read attached to nothing. Why the heartbeat is absent is **not
established**; it is filed to triage with the traces. A core probe of the same hold that passed was
also green with the idle sweep ignoring live subscribers, so it did not discriminate and was not
kept.

## See also

- [Writing Tests](../WritingTests) · [Negative Controls](../NegativeControls) · [Reactive Test Assertions](../ReactiveTestAssertions)
- [Riding Out a ShuttingDown Address](../RidingOutAShuttingDownAddress) — the refusal `Linger` produces, and the riders that must survive it
- [Debugging Message Flow](../DebuggingMessageFlow) — `MESHWEAVER_MSG_TRACE=1` shows a routed delivery's resolution, which is how case 4's NotFound window was read
