---
NodeType: Markdown
Name: "Event Subscriptions — the durable 'when THIS fires, run THAT' engine"
Abstract: "One reboot-surviving primitive behind every deferred / event-driven reaction: react to a CRUD event (an email invite that grants access the moment the invitee signs up), react to a timer (do something at a time), or react to a node reaching a resting status (a delegated sub-thread finishing). A subscription is a durable MeshNode in the Admin partition; a single background runner fires it live AND reconciles it against current state on startup, so a trigger that fired while the process was down still fires."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Events"
  - "Durability"
  - "Continuations"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) (everything here is reactive + `IIoPool`-bounded, never `async`/`await`) and [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) (the runner writes as system). This engine supersedes the former `ScheduledActionRunner`; it is the general form of the same reconcile-on-startup pattern.

## What it is

An **`EventSubscription`** is the durable record of *"when THIS trigger fires, run THAT continuation"* — and it **survives a reboot**. It is a MeshNode at `Admin/EventSubscription/{id}` (the always-present Admin partition), and a single background `EventSubscriptionRunner` drives every one of them by two complementary paths:

- **Live** — reacts to the event as it happens (the mesh change feed for CRUD; an `Observable.Timer` for a timer; a node-stream watch for a status change).
- **Reconcile-on-startup** — a live query over the outstanding subscriptions re-evaluates each against *current* state whenever the set changes (including at boot). So a trigger that fired **while the runner was down** — the invitee signed up during a deploy, a timer came due mid-restart, a sub-thread finished while the portal was rolling — **still fires**. The durable state is the subscription node itself (`Pending → Fired`), so nothing is lost and nothing double-fires (the terminal write gates re-entry; every continuation is idempotent).

Because the mesh node is the durable source of truth (Postgres), durability is **free** — we do not need Orleans reminders or durable streams (they aren't wired, and the monolith host has no Orleans at all; the reconcile query is host-uniform).

## The shape

`EventSubscription` (project `MeshWeaver.Mesh.Contract`) is flat + enum-discriminated — it serialises through the mesh content serializer like any node content (no polymorphic-content risk), and new trigger/continuation kinds are added as new enum values + nullable fields (additive):

| Group | Field | Meaning |
|---|---|---|
| **Trigger** | `TriggerType` | `NodeChange` \| `Timer` \| `NodeStatus` |
| NodeChange | `TriggerNodeType`, `TriggerKind`, `MatchField`, `MatchValue` | fire when a node of this type is created/updated/deleted and its field matches |
| Timer | `FireAt` | fire once at/after this instant (a past time fires on the next boot) |
| NodeStatus | `WatchPath`, `StatusField`, `RestingValues`, `RequireActiveFirst` | fire when the watched node's status enters a resting value |
| **Effect** | `ContinuationType` | `GrantSpaceAccess` \| `PostThreadMessage` |
| | `TargetPath`, `SubjectId`, `Role`, `Pin` | what the continuation does (and to whom) |
| **Lifecycle** | `Status`, `CreatedBy`, `CreatedAt`, `FiredAt`, `LastError` | runner-managed (`Pending → Fired \| Failed \| Cancelled`) |

## Trigger 1 — react to a CRUD event (the email invite)

The flagship case: invite someone to a Space by email. If they already have an account, grant now. If not, write an `EventSubscription` that grants (and pins) the moment a `User` with that email is **created** — so access lands automatically on sign-up, surviving any restart in between. This is exactly what `SpaceInviteService` writes:

```csharp
var subscription = new EventSubscription
{
    // Deterministic id per invitee+space → a re-invite upserts the SAME subscription (idempotent).
    Id = $"grant_{Slug(email)}_{Slug(spacePath)}",
    TriggerType = EventTriggerType.NodeChange,
    TriggerNodeType = "User",
    TriggerKind = MeshChangeKind.Created,
    MatchField = "email",
    MatchValue = email,
    ContinuationType = EventContinuationType.GrantSpaceAccess,
    TargetPath = spacePath,
    Role = "Editor",
    Pin = true,
};
EventSubscriptionOps.CreateSubscription(meshService, subscription).Subscribe();
```

When the invitee onboards, their `User` node is created → the change feed fires the subscription live; the reconcile path is the safety net if the sign-up happened during downtime. The continuation (`GrantSpaceAccess`) creates the `{space}/_Access/{user}_Access` assignment and pins the Space — both idempotent create-or-updates, so live + reconcile can never double-grant. The subject is the **triggering node's id** (a `User` node's path IS the userId).

## Trigger 2 — react to a timer

Fire a continuation at (or after) a time. The runner schedules one `Observable.Timer` per pending Timer subscription; a `FireAt` already in the past fires immediately on the next startup — **restart-safe at-least-once** without any external scheduler. A timer carries no triggering node, so the continuation subject is on the subscription (`SubjectId`):

```csharp
var subscription = new EventSubscription
{
    TriggerType = EventTriggerType.Timer,
    FireAt = DateTimeOffset.UtcNow.AddDays(7),        // e.g. grant a trial role in a week
    ContinuationType = EventContinuationType.GrantSpaceAccess,
    SubjectId = userId,                               // no trigger node → subject is explicit
    TargetPath = spacePath,
    Role = "Viewer",
};
```

## Trigger 3 — react to a node reaching a resting status

Fire when a watched node's status field leaves "running" and reaches a **resting** value — the exact shape of *"wait for a reply, then continue"*. The runner watches the node via the self-healing `SubscribeWithReEstablish` (re-establishes on a transient fault, and terminally **stops without a storm** when the watched node is gone), tracks whether it first saw a non-resting (active) state (`RequireActiveFirst`, so an initial replayed-resting of a node that never ran doesn't fire), and fires when the status enters `RestingValues`:

```csharp
var subscription = new EventSubscription
{
    TriggerType = EventTriggerType.NodeStatus,
    WatchPath = subThreadPath,                        // the delegated sub-thread
    StatusField = "Status",
    RestingValues = ["Idle", "Cancelled", "Done"],    // "not running any more"
    RequireActiveFirst = true,                        // saw it Executing first → this is a genuine finish
    ContinuationType = EventContinuationType.PostThreadMessage,
    TargetPath = parentThreadPath,                    // continue the parent
};
```

This is the durable backbone of **delegation**: a parent agent delegates to a sub-thread and continues when the sub-thread finishes. The in-memory wait (a `TaskCompletionSource`) is the fast path for the same-process happy case; the `EventSubscription` is the **reboot backstop** — if the portal restarts mid-delegation, the runner reconciles the subscription, sees the sub-thread already resting, and continues the parent. Nothing is lost. (See [Thread Operations](/Doc/Architecture/ThreadOperations).)

> **Asking a question vs. done.** A sub-thread that finished a task and one that asked the user a clarifying question both currently reach `Idle` — the thread status alone can't tell them apart. The `RequireActiveFirst` + summary-presence heuristic covers the common case; an explicit round disposition (`Completed` / `AwaitingInput` / `Failed`) is the clean fix and is the follow-up that makes the resting trigger fire only on genuine completion.

## Why one runner, reconciled on startup (not Orleans reminders)

- **Durability is already in the node.** The subscription is a Postgres-backed MeshNode; `Pending → Fired` is the durable checkpoint. The reconcile query gives exact replay for `Created` triggers and at-least-once for timers/status — for free.
- **Host-uniform.** Orleans reminders/durable streams are **not** wired (the silo uses memory streams; the monolith has no Orleans), so a reminder-based design would split behaviour between hosts and need net-new reminder storage — more surface, not less. The reconcile substrate runs identically in both.
- **No wedges.** Every read/write runs under `ImpersonateAsSystem` (the runner has no ambient identity); every timer/watch is idempotent per id, self-disposing on fire, and disposed with the runner; the node-status watch terminally stops (never resubscribes) when its node is gone — the anti-storm rule.

## Back-compat

`EventSubscription` generalises the former `ScheduledAction` (same field names for the NodeChange + GrantSpaceAccess case). On startup the runner **migrates** any legacy `Admin/ScheduledAction/{id}` nodes into `Admin/EventSubscription/{id}` (a lag-robust live query; a failed migration releases the id so a later emission retries) — so no in-flight invite is dropped. `ScheduledAction` is kept only for that deserialization + migration; nothing creates it any more.

## Testing

`test/MeshWeaver.Graph.Test/EventSubscriptionRunnerTest` covers the NodeChange grant (fires when the matching user is created), the legacy migration, the Timer (a past-due timer fires + grants), and the NodeStatus trigger (a watched node flipped `Running → Idle` fires). `SpaceInviteServiceTest` covers the invite → subscription write.
