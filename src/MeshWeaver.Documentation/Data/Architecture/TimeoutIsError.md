# Timeout == error: a test may not pass through a product timeout

A test that passes only because the product recovered from a stall through **its own timeout** has
not tested the behaviour it names: it has measured that the stall exists and called the measurement
green. It also costs the suite the whole bound on every run. On one main run (43636407276) about
25 tests sat almost exactly 36 s — `TestTimeouts.Quick` on CI — and one sat 216.7 s
(`TestTimeouts.CrossSilo`), all passing; 167 tests of ≥ 5 s were 65 % of all test time.

The rule therefore has two halves:

1. **A product-side timeout terminal that fires during a test fails the test**, unless the test
   declares that the timeout IS its subject.
2. **A test whose subject is a timeout drives it on virtual time** (a `TimeProvider` / `IScheduler`
   seam the product accepts), and a test that asserts *absence* proves the processing that could
   have produced the event has completed before it asserts — a real-time `NotEmit` window is
   sanctioned only when no positive signal exists.

## The seam: `ITimeoutTerminalObserver`

`src/MeshWeaver.Messaging.Contract/TimeoutTerminal.cs` defines one product-side seam:

| piece | role |
|---|---|
| `TimeoutTerminalKinds` | open vocabulary of terminal kinds (string constants, policy `open-vocabulary-string-constants`) |
| `TimeoutTerminal(Kind, Origin, Message)` | one fired terminal |
| `ITimeoutTerminalObserver` | told about every fired terminal; **production registers none** |
| `serviceProvider.TimeoutTerminalReporter()` | resolves the observers ONCE, where the bound is ARMED (the container is alive), and returns the action invoked where it FIRES (possibly after the container is gone) |

The terminals that report today:

| kind | where it fires |
|---|---|
| `RequestTimeout` | `MessageHub.ApplyTimeout` — a hub request got no reply within `RequestTimeout` of silence |
| `QueryInitial` | `MeshQuery.InitialStallProbe` — the provider fan-in did not emit an Initial within `MeshOperationOptions.QueryInitialBudget` ([the stall terminal](../QueryFanInStallTerminal)) |
| `WriteVerdict` | `MeshNodeStreamHandle.UpdateRemote` `VERDICT_TIMEOUT` — no owner verdict within the write verdict bound |
| `WriteTeardownJoin` | `UpdateRemote` `TEARDOWN_JOIN_TIMEOUT` — a shutting-down owner's teardown did not settle within the caller's bound |
| `CreateVerdict` | the upsert's inner `CreateNodeRequest` got no verdict within `InnerCreateVerdictBound` (derived from `MeshOperationOptions.Timeout`) |
| `LifecycleStalled` | a create, move or copy did not reach a verdict within `MeshOperationOptions.Timeout` (`LifecycleVerdictStalledException`) |
| `CreatePostCreation` | a create's row was written but a post-creation step had not finished within `MeshOperationOptions.Timeout` — "outcome unknown" |
| `DeleteStage` | a delete stage (pre-flight, validators, children, cascade, commit …) ran out of its budget and its stage-tagged `TimeoutException` became the delete's verdict (`HandleDeleteNodeRequest`'s error sink) |
| `PermissionEstablishment` | a permission check reached no verdict within `MeshOperationOptions.PermissionEstablishmentBudget` and was answered Undetermined (`NodeTypeAccessRuleGate.CheckFor` / `ReconsiderPointRead`, `RlsNodeValidator`, the stream cache's standing-Read re-decision) |

A new timeout terminal reports through the same seam: resolve the reporter where the bound is
armed, invoke it on the timeout path, and add a constant (in its own module if it is not core's).
The observer must not throw; it is called from inside the product's timeout path.

Not yet reporting — and two of them measured as hiding real waits, so they are the next work, not
an oversight:

- **The write queue's backstop** (`MeshNodeStreamCache`, `QueueAdvanceBound`, 5 s,
  `ADVANCE_WITHOUT_HANDOFF`) and **the owner's flush-ack bound** (`ArmPatchAckWatcher`, 10 s,
  `FLUSH_OUTLIVED_BOUND`). A writer's acknowledgement waits for the DURABLE flush, and the next write
  on the same path waits for that acknowledgement. Reported, they fired under
  `AWriteAdmittedAtCommitIsWaitedForByTheDeleteTest` (Hosting: its negative control's late patch is
  queued behind a write whose flush the test parks, so it advances only on the 5 s backstop) and
  under `InjectorExamplesTest.HoldWrites_…` (FaultInjection: it awaited the write's ack while holding
  the flush and sat out the 10 s bound — fixed here with a positive signal, 10 s → under 2 s for the
  class). The first is not a test defect: making a queued write wait for its predecessor's durable
  flush rather than its commit is a product decision this work does not take, so these two report
  nothing yet and that test is listed, not declared.
- `DeclaredPointRead`'s own evaluation budget (it is composed inside
`ReconsiderPointRead`, which reports), `DeclaredReadSubscriptionFollow`'s re-check, the query
providers' own bounds (`StaticNodeQueryProvider`, `StorageAdapterMeshQueryProvider`), the base-state
read bound (`BaseStateSource`, which has no hub at hand), and the write's fast seam
`UpdateResponseWaitBound`, which hands over to the late registry rather than terminating.

### A reply that ENDS an unbounded computation carries no silence bound

`PostOptions.WithoutSilenceBound()` takes `RequestTimeout` off ONE awaited reply: the wait ends
with the reply, a delivery failure or the awaiting hub's disposal. It exists for a reply that is
the END of a computation of unbounded length — a user's script run — where a timeout was never a
verdict on anything. The kernel run is the case that needed it: the dispatcher (`CodeNodeType`)
and the kernel container both bounded their wait for the run's reply by `RequestTimeout` and
treated their own `TimeoutException` as "not yet". Every cell that outlived the budget therefore
fired two `RequestTimeout` terminals underneath a run that SUCCEEDED
(`LongCellOutlives…RequestBudgetTest`). Now:

- the kernel container ends its wait on the executor's reply or on the submission's settlement
  (`KernelExecutor.ExpectSettlement`), whichever comes first; the settlement carries the response
  the executor answered with, or `null` when the executor was torn down without answering, which
  the container answers as a failed run nobody settled — so the caller is always answered;
- the dispatcher waits without a bound, and what its deadline used to say — "no answer from the
  kernel yet" (#7362) — is a NOTICE on its own timer, `hub.RequestBudgetElapsed()` (the hub's
  `RequestTimeout` on its timeout clock), disarmed by the answer. A notice gives nothing up, so it
  is not a terminal. This hub's own disposal is no verdict on the kernel's run.

Never use it to make a slow request pass: every other request keeps its bound.

## The harness: `TimeoutTerminalGuard`

`test/MeshWeaver.Fixture/TimeoutTerminalGuard.cs`:

- `TestBase` registers one `TimeoutTerminalRecorder` per test container (an instance, never static).
- `AutoTestLoggingAttribute.Before` opens the test's window (a watermark, so a shared-mesh class
  does not carry an earlier test's terminals into the next); `After` closes it, writes every
  terminal that fired to the test output and the shard trace as `TIMEOUT_TERMINAL`, and — when the
  guard is ARMED — throws `UndeclaredTimeoutTerminalException`, which xUnit reports as the test's
  failure.
- **Armed** by `[TimeoutTerminalsAreErrors]` on a class or assembly, or for every test by
  `MW_TIMEOUT_TERMINALS_ARE_ERRORS=1`.
- **Declared** by `[ExpectsTimeoutTerminal(TimeoutTerminalKinds.X, "why the timeout is the subject")]`
  on the method or class. A declared kind that did not fire fails the armed test too — a stale
  exemption hides the next real stall of that kind.

Unarmed, nothing fails, but every terminal still lands in the output and the trace, so
`grep TIMEOUT_TERMINAL` over a shard's trace lists the offenders of that run.

🚨 **Never declare a terminal to make a slow test pass.** The declaration is for tests whose subject
is the timeout itself — and those drive it on virtual time. A stall is fixed at its root.

### Coverage

The recorder lives in the test's own container. Hubs in that container (every `HubTestBase` and
`MonolithMeshTestBase` mesh) report to it. An Orleans test cluster's silos and its client host
build their own containers, so `OrleansTestCluster.DeployAsync` registers a recorder in each
(`TimeoutTerminalSiloRecording`), and the cluster host and fixtures expose them as an
`ITimeoutTerminalSource`. `TestBase` folds the recorders of every `ITimeoutTerminalSource` held in
one of the test's instance fields into its window, each with its own watermark —
`TimeoutTerminalSiloCoverageTest` reports one terminal through a silo and one through the client
host and declares them, so it fails if either container is not wired in. A cluster built with a
hand-made `TestClusterBuilder` (not `DeployAsync`) is still not covered.

## Rollout

The guard is ARMED on every core test assembly that runs on `TestBase`
(`[assembly: TimeoutTerminalsAreErrors]` in each project's `TimeoutTerminalsAreErrors.cs`):
Compiler.Pipeline, ContentCollections, Data, Documentation, FaultInjection, Graph, Hosting,
Hosting.Orleans, Layout, Messaging.Hub, PluginTester, Testing.InMesh and Updates. Not armed:
Deployment.Contract and Testing.Xunit (no `TestBase`, nothing to record) and Security.MeshTest (a
decentralised suite with no test framework).

The enumeration run (`MW_TIMEOUT_TERMINALS_ARE_ERRORS=1` over every project) found these
offenders, each classified and settled before its assembly was armed:

| test | terminal | class | settled by |
|---|---|---|---|
| `LongCellOutlivesTheDispatchersRequestBudgetTest`, `…KernelContainersRequestBudgetTest` | `RequestTimeout` ×2 / ×1 | (b) product | the kernel run's reply waits carry no silence bound (above) |
| `KernelIdleClaimIsPerSubmissionTest` (2) | `RequestTimeout` | (b) product | same fix; the 1 ms request budget that forced the old hand-over is gone |
| `KernelReplyTimeoutNoticeTest` (2) | `RequestTimeout` | (b) product | same fix; the notice runs on `RequestBudgetElapsed`, which is not a terminal |
| `PostCreationVerdictIsSaidOnceTest`, `ACreateNeverWaitsOnAModuleContainerUnderConstructionTest` | `LifecycleStalled` beside `CreatePostCreation` | (b) product | the create's deadline bounded the whole SEQUENCE (absolute-time `Timeout`), so a post-creation `Matches` parked inside the chain's `OnNext` kept it open and a second, stale terminal ("waiting at stage 'write'") fired; it now bounds only the wait for the node. The remaining `CreatePostCreation` is (a) |
| `TimeoutMessageNamesTheCallersOwnStateTest` (5) | `RequestTimeout` | (a) | declared; already on the hub's virtual timeout clock |
| `StreamedResponseTest.A_stream_that_goes_silent…` | `RequestTimeout` | (a) | moved to the client's virtual timeout clock, declared |
| `StalledDeleteStillTimesItsCallerOutTest` | `RequestTimeout` | (a) | moved to the client's virtual timeout clock (fired once the store went silent), declared |
| `AnUploadTimeoutNamesTheStepThatTimedOutTest` (2) | `RequestTimeout` | (a) | moved to the client's virtual timeout clock (fired once the write was swallowed), declared |
| `CreateTimeoutOutcomeTest` | `CreateVerdict` | (a) | declared; on the virtual `CreateVerdictClock` (#8244) |
| `LifecycleRequestsAlwaysReachAVerdictTest` (4), `ACancelledMoveDeleteLeavesNoTombstoneTest` (3), `PostCreationVerdictIsSaidOnceTest`, `ACreateNeverWaits…` | `LifecycleStalled` / `CreatePostCreation` | (a) | declared (real time — see below) |
| `SnapshotFanOutEmitsOnlyAnswersTest.AStalledProvider_WithNothingToShow_FaultsNamingIt` (2) | `QueryInitial` | (a) | declared (0.5 s real time) |
| `DeleteCommitTimeoutNamesWhatIsStuckTest`, `DeleteCommitTimeoutSeparatesStarvedFromStuckTest`, `DeleteCommitQueuedBehindAMovingLaneTest`, `WideDeleteUnboundedFanOutTest`, `PartialDeleteIsReportedAsRetriableTest` (2) | `DeleteStage` (new) | (a) | declared — the commit stage's no-progress watchdog is what each asserts on (real time, 3–4 s) |
| `PictureUploadWithASilentPermissionFoldTest` | `PermissionEstablishment` (new) | (a) | declared — the budget answering a silent fold Undetermined is the subject |
| `InjectorExamplesTest.HoldWrites_…` | flush-ack + queue backstop (not reporting yet) | (c) | positive signal: the commit is read off the held flush, the ack only after release |

**Still on real time** (declared, and the next step): the lifecycle deadlines (create, move, copy,
post-creation — `MeshOperationOptions.Timeout`) and the query fan-in's `QueryInitialBudget` have no
clock seam yet, so their tests sit out a short budget (6 s or 0.5 s). Moving them needs the
lifecycle chains to take their deadlines from an injectable clock, the way
`MessageHubConfiguration.WithTimeoutClock`, `ReadBudgetClock` and `CreateVerdictClock` (the upsert's inner create bound) already do.
