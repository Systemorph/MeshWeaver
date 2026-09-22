---
Name: The Initialization Budget Ladder
Category: Architecture
Description: Initialization is a nested wait, and every level of it used to take the same independently-written 120 s constant. Equal is not an ordering, so which level reported a hang was decided by scheduling — and when the enclosing one won, the level that knew which wait starved said nothing. A hub born inside another hub's initialization now takes a strictly contracting rung; a hub nothing is waiting on does not.
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
either an explicit `WithStartupTimeout` or `HubInitializationBudget.Root` for a hub nothing is
waiting on.
Everything nested inside it is DERIVED by `HubInitializationBudget.Nest`, which is strictly
contracting, so the ordering holds by construction and cannot drift apart again:

| rung | value | what it bounds |
|---|---|---|
| 1 | `InitializationBudget` | this hub's WHOLE initialization, as whatever waits on it bounds it |
| 2 | `NestedInitializationBudget` = `Nest(rung 1)` | one wait inside it: the BuildupAction `Concat`, and the `DataContext` time-box |
| 3 | rung 1 of a hub created DURING this one's initialization = `Nest(rung 2)` | that hub's whole initialization — this hub's rung-2 waits are what wait on it |

`Nest(t) = max(t − 5 s, t × 0.5)`, the same shape `MeshOperationOptions.Nest` uses. The reserve is
absolute because what it covers is absolute: a hub construction, a post, and the inner hub's first
turn reaching `HandleInitialize` on its own action block. The fraction floor only bites at the short
budgets tests configure, where subtracting the reserve would drive a rung to zero.

It carries `MeshOperationOptions`' **1 ms domain** as well as its formula, and for the same stated
reason: below that, halving a tick truncates to zero, two rungs land on the same instant, and a
timer at `TimeSpan.Zero` fires at once — the equal-bounds collision in its worst form. The domain is
not academic, because the fraction floor halves per level: a configured 2 s budget reaches it around
the twelfth level of nesting. `Nest` refuses a smaller enclosing bound rather than collapsing the
ladder, and `NestedInitializationBudgetTest` pins both sides of that boundary.

At the default this reads **120 s / 115 s** for a hub nothing is waiting on, **110 s / 105 s** for a
hub created inside that one's initialization, and so on down.

### The two rung-2 bounds are SIBLINGS, and that is why they may be equal

The BuildupAction `Concat` and the `DataContext` time-box both take rung 2. They are not nested:
`DataExtensions.StartDataSourcesAndOpenGate` *arms* the time-box and then completes at once, so only
one of the two is ever the wait that is still open. Equal-by-coincidence is the defect; equal
between siblings over disjoint subjects is not, and the word NESTED is doing all the work in the
rule.

### Where the rung is stamped

`MessageHub.TryGetHostedHub` stamps the host's rung 2 onto the new hub's configuration as it is
created.

🚨 **HOSTED is not ENCLOSED, and reading them as the same narrows a production bound for
nothing.** A per-node hub IS a hosted hub of the mesh root — both `MessageHubGrain` and
`MeshExtensions` create it that way — but routing activates it on demand long after the mesh hub
reached `Started`, so nothing in the mesh hub's initialization is waiting on it. The stamp is
therefore applied only while the host's `RunLevel` is below `Started`, which is exactly the window
in which one of its rung-2 waits can be waiting on the new hub (a data source's stream is served by
a `sync/{clientId}` sub-hub built inside `StartDataSourcesAndOpenGate`). A per-node hub consequently
takes the root budget, and only the sub-hubs born inside its initialization contract.

It is **stamped, never resolved on read**: `MessageHubConfiguration.ParentHub` answers out of the
parent scope's `IMessageHub` registration, which for a hub built directly on a root provider
resolves to the hub ITSELF — deriving the ladder from it recursed until the stack died (`Stack
overflow`, exit 134, the whole test host down with zero tests run). The host is known at the one
moment that matters, so the value is carried.

## What changes, and what does not

- **The root value, and what a per-node hub gets.** Rung 1 is still 120 s for a per-node hub, because
  nothing is waiting on it (above). What DOES move is its rung 2: its BuildupAction `Concat` and its
  `DataContext` time-box read **115 s** rather than 120 s, so the production line changes from *"did
  not complete within 120s"* to *"… within 115s"*. That is the price of having a rung above them at
  all, and the rung above them is what a configured `WithStartupTimeout` arms
  (`MessageService`'s startup timer covers every gate and is armed in the constructor, i.e. strictly
  earlier — equal values there meant the anonymous *"gates still closed: […]"* answer always beat
  the one naming the pending action).
- **Nothing is widened, and nothing is narrowed to make a symptom go away.** The bound is a
  liveness guarantee, not a fix: when it fires, go and fix the hung dependency. What changed is
  only WHICH level gets to report it.
- **The log templates**, which is what a `LogIncident` fingerprint is built from at the
  `DataContext` and `MessageHub` sites: the seconds live inside the exception message, not in the
  template. That fingerprint demonstrably survived an exception-message rewrite already. 🚨 Where a
  fingerprint IS built from the exception message — `MessageHubGrain`'s `ActivationFaultReason`
  excludes the reporter's prose by design and keeps the exception text — a changed number re-keys
  it, exactly as the query-layer change did.

## Tests

- `NestedInitializationBudgetTest.EveryStepOfTheLadderIsStrictlySmallerThanTheOneItIsNestedIn` —
  the derivation contracts at both scales, both sides of the 1 ms domain boundary are pinned, a real
  host/child pair carries a strictly decreasing ladder, and — the control on the other side of the
  discriminator — a hub created AFTER the host started takes the root budget unchanged.
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
