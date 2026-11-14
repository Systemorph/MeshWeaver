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

A new timeout terminal reports through the same seam: resolve the reporter where the bound is
armed, invoke it on the timeout path, and add a constant (in its own module if it is not core's).
The observer must not throw; it is called from inside the product's timeout path.

Not yet reporting (the next candidates, by measured cost): the remaining `MeshOperationOptions`
budget consumers (the delete's `DeleteStageTimeout` stages, `DeclaredPointRead`, `NodeTypeAccessRuleGate`, `RlsNodeValidator`,
`MeshNodeStreamCache`), the base-state read bound (`BaseStateSource`, which has no hub at hand),
and the write's fast seam `UpdateResponseWaitBound`, which hands over to the late registry rather
than terminating.

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

### Coverage limits

The recorder lives in the test's own container. Hubs in that container (every `HubTestBase` and
`MonolithMeshTestBase` mesh) report to it. An Orleans test cluster's silos build their own
containers, so their terminals are not recorded yet — the guard under-reports there, it never
over-reports.

## Rollout

The guard ships ARMED but scoped. The enumeration run (`MW_TIMEOUT_TERMINALS_ARE_ERRORS=1` over the
five projects whose tests the slow-test profile named) found **26 offenders**, none in
`MeshWeaver.Data.Test` — so that assembly carries `[assembly: TimeoutTerminalsAreErrors]`, as does
the guard's own test class. The Messaging.Hub and
Graph runs predate the `LifecycleStalled` / `CreatePostCreation` terminals, so their rows are a
floor. The offenders:

| project | test class (tests) | terminal |
|---|---|---|
| Messaging.Hub.Test | `TimeoutMessageNamesTheCallersOwnStateTest` (5) | `RequestTimeout` (real-time; moving to a virtual hub clock) |
| Messaging.Hub.Test | `StreamedResponseTest` (1) | `RequestTimeout` |
| Graph.Test | `KernelIdleClaimIsPerSubmissionTest` (2) | `RequestTimeout` |
| Graph.Test | `LongCellOutlivesTheDispatchersRequestBudgetTest`, `LongCellOutlivesTheKernelContainersRequestBudgetTest` (1 each) | `RequestTimeout` |
| Graph.Test | `KernelReplyTimeoutNoticeTest` (2) | `RequestTimeout` |
| Graph.Test | `StalledDeleteStillTimesItsCallerOutTest` (1) | `RequestTimeout` |
| Graph.Test | `AnUploadTimeoutNamesTheStepThatTimedOutTest` (2) | `RequestTimeout` |
| Hosting.Test | `SnapshotFanOutEmitsOnlyAnswersTest` (2) | `QueryInitial` |
| Hosting.Test | `LifecycleRequestsAlwaysReachAVerdictTest` (4) | `LifecycleStalled`, `CreatePostCreation` |
| Hosting.Test | `PostCreationVerdictIsSaidOnceTest` (1) | `CreatePostCreation`, `LifecycleStalled` |
| Hosting.Test | `ACancelledMoveDeleteLeavesNoTombstoneTest` (3) | `LifecycleStalled` |
| Compiler.Pipeline.Test | `ACreateNeverWaitsOnAModuleContainerUnderConstructionTest` (1) | `CreatePostCreation`, `LifecycleStalled` |

Most of these name a timeout as their subject and run it on the real clock; each moves to virtual
time and declares its terminal before its assembly is armed. The two `LongCellOutlives…` tests are
the other kind — a healthy cell that SUCCEEDS while a `RequestTimeout` fires underneath it — and
need their stall found. The suite-wide arm is the end state.
