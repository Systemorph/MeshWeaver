---
Name: The Initialization Budget Ladder
Category: Architecture
Description: Initialization is a nested wait, and every level of it used to take the same independently-written 120 s constant. Equal is not an ordering, so which level reported a hang was decided by scheduling — and when the enclosing one won, the level that knew which wait starved said nothing. A hosted hub now takes a strictly contracting rung.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 20h18"/><path d="M6 20v-5h4v5"/><path d="M10 20v-9h4v9"/><path d="M14 20V7h4v13"/></svg>
---

# The Initialization Budget Ladder

**A bound nested inside another bound has to be able to fire FIRST, because it is the only one that
knows WHICH wait starved.** The enclosing bound can say no more than "initialization ran out of
time". This repo already states that rule twice — `MeshOperationOptions` expresses it for writes
(see [The Query Fan-In's Initial Stall Is a Terminal](../QueryFanInStallTerminal) for its innermost
rung) and `ReadBudget` expresses it for reads — and this page is the same rule applied to hub
initialization.

## Initialization is a nested wait

```text
a hub's DataContext time-box                     ← waits on its data sources
  └─ IDataSource.Initialized                      ← waits on each stream's first frame
       └─ the sync/{clientId} sub-hub              ← a HUB, with an initialization of its own
            └─ its BuildupAction Concat            ← bounded by the sub-hub's own time-box
```

Every level of that nest used to take the same number, `120 s`, written independently in three
places: `MessageHub`'s buildup bound, `DataContext.InitializationTimeout`, and the sub-hub's own
copy of the first. Nothing in the code said the three were supposed to be ordered at all.

## Equal is not an ordering

The clocks are armed microseconds apart, on different action blocks.
`DataContext.InitializeDataSources` creates the sub-hubs a few microseconds before
`OpenInitializationGate` arms the enclosing time-box — but the sub-hub's own bound is armed on the
SUB-HUB's action block, after a scheduling hop, so which of the two expires first depends on how
loaded the runner is at that instant. In the event consolidated on
[Systemorph/MeshWeaver#1122](https://github.com/Systemorph/MeshWeaver/issues/1122) the two lines
were **5 ms apart**.

That gap is not a curiosity, because the two outcomes are not symmetric:

- **Inner first** — the sub-hub reports, naming the BuildupAction that never signalled, and the
  enclosing wait then completes normally because a FAILED hub still opens its gate and reaches
  `Started`.
- **Outer first** — the enclosing time-box expires, errors every stream the data source holds, and
  the sub-hub's initialization ends as a *recognised shutdown*: no error log, no
  `InitializationError`, nothing recorded. The level that knew the answer is torn down before it
  can give it, and what survives names only the enclosing wait.

## The ladder

Exactly ONE bound is configured per hub — `MessageHubConfiguration.InitializationBudget`, which is
either an explicit `WithStartupTimeout` or `HubInitializationBudget.Root` for a hub nobody hosts.
Everything nested inside it is DERIVED by `HubInitializationBudget.Nest`, which is strictly
contracting, so the ordering holds by construction and cannot drift apart again:

| rung | value | what it bounds |
|---|---|---|
| 1 | `InitializationBudget` | this hub's WHOLE initialization, as its host bounds it |
| 2 | `NestedInitializationBudget` = `Nest(rung 1)` | one wait inside it: the BuildupAction `Concat`, and the `DataContext` time-box |
| 3 | a hosted hub's rung 1 = `Nest(rung 2)` | that hub's whole initialization — this hub's rung-2 waits are what wait on it |

`Nest(t) = max(t − 5 s, t × 0.5)`, the same shape `MeshOperationOptions.Nest` uses. The reserve is
absolute because what it covers is absolute: a hub construction, a post, and the inner hub's first
turn reaching `HandleInitialize` on its own action block. The fraction floor only bites at the short
budgets tests configure, where subtracting the reserve would drive a rung to zero.

At the default this reads **120 s / 115 s** for a hub nobody hosts, **110 s / 105 s** for the hub it
hosts, and so on down.

### The two rung-2 bounds are SIBLINGS, and that is why they may be equal

The BuildupAction `Concat` and the `DataContext` time-box both take rung 2. They are not nested:
`DataExtensions.StartDataSourcesAndOpenGate` *arms* the time-box and then completes at once, so only
one of the two is ever the wait that is still open. Equal-by-coincidence is the defect; equal
between siblings over disjoint subjects is not, and the word NESTED is doing all the work in the
rule.

### Where the rung is stamped

`MessageHub.TryGetHostedHub` stamps the host's rung 2 onto the hosted hub's configuration as it is
created. It is **stamped, never resolved on read**: `MessageHubConfiguration.ParentHub` answers out
of the parent scope's `IMessageHub` registration, which for a hub built directly on a root provider
resolves to the hub ITSELF — deriving the ladder from it recursed until the stack died (`Stack
overflow`, exit 134, the whole test host down with zero tests run). The host is known at the one
moment that matters, so the value is carried.

## What did NOT change

- **The root value.** A hub nobody hosts still gets 120 s — including a per-node hub, which is
  created directly rather than as a hosted hub, so the production line still reads *"did not
  complete within 120s"*. Only the hubs nested inside it contract.
- **Nothing is widened, and nothing is narrowed to make a symptom go away.** The bound is a
  liveness guarantee, not a fix: when it fires, go and fix the hung dependency. What changed is
  only WHICH level gets to report it.
- **The log templates.** The seconds live inside the exception message, not in the structured
  template, so an existing `LogIncident` fingerprint keeps collecting its own history.

## Tests

- `NestedInitializationBudgetTest.EveryStepOfTheLadderIsStrictlySmallerThanTheOneItIsNestedIn` —
  the derivation contracts at both scales, and a real host/hosted-hub pair carries a strictly
  decreasing ladder.
- `NestedInitializationBudgetTest.AHungHostedHubReportsItself_AndTheHubWaitingOnItNeverGivesUp` —
  a host whose BuildupAction waits on a hosted hub whose own BuildupAction hangs. The hosted hub
  records the failure and names its action; the host answers requests normally and records nothing.
  With the derivation reverted to the flat constant, BOTH assertions go red and the surviving line
  is the host's — *"BuildupAction 1 of 1 (…WaitsForTheHostedChild) did not complete within 7s"* —
  which names the host's own action and says nothing about the hub that actually hung.

## See also

- [Hub Initialization Failure](../HubInitializationFailure) — what the FAILED state is, and how a
  hung BuildupAction reaches it.
- [What the DataContext Init Time-Box Bounds](../DataContextInitializationTimeout) — the enclosing
  wait, and what its own message names.
- [Initialization Gates](../InitializationGates) — why the gate is always opened, even on failure.
