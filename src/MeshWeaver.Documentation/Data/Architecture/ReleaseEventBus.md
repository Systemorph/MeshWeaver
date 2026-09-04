---
Name: The Release Event Bus
Category: Architecture
Description: How a release in any repository becomes a fact every other repository can query — memex ingests release events as persistent mesh nodes, and dependents read that state instead of accumulating events
Icon: ArrowSync
---

# The release event bus

Artefacts ship from several repos — the platform, `MeshWeaver.Plugins`, and the node repos
(`Education`, `Reinsurance`, `SocialMedia`). Each can release independently, and each depends on
versions the others published. Coordinating that by hand does not scale, and coordinating it by CI
ordering couples pipelines that should stay independent.

**The bus is memex.** A release in any repo becomes a persistent fact in the mesh; a repo that
depends on it asks the mesh whether its dependencies are satisfied, and builds only when they are.

## Why the state is PERSISTENT, and not the events themselves

This is the load-bearing decision, so it goes first.

`FrameworkReleaseBroadcaster` is deliberately **reporter-class**: its own contract says *"a lost
dispatch costs at most one delayed rebake wave — never a hard failure"*, because the alternative is a
platform release path that holds on a hand-maintained subscriber list and a credential.

That property is only safe if **nobody accumulates state from the dispatches**. A consumer that built
its dependency set by adding up events would be permanently wrong after one lost dispatch, and
nothing would report it — the failure would look like "that repo just never rebuilt".

So:

> **The event is a WAKE-UP. The mesh is the TRUTH.**
> On wake, a consumer QUERIES the current released version of each dependency. A lost event costs
> latency, never correctness.

This is the same rule as `delivery-verdict` refusing to pass on an empty verdict, and as CD's
reconciler asking ACR whether a commit's image set is complete rather than trusting that a job ran.
An event asserts *something happened*; only a query establishes *what is true now*.

## The four pieces — three already exist

| piece | where | state |
|---|---|---|
| **Ingest** | `POST /webhooks/github` — `GitHubWebhookEndpoints`, HMAC-verified (`GitHub:Webhook:Secret`), dispatched by `GitHubWebhookProcessor.Process(eventType, payload)` | **exists** — already switches on `push`, `workflow_run`, `issues`, `issue_comment`; needs a `release` branch |
| **Storage** | a `Release` node per `(repository, packageId, version)` | to build |
| **Query** | "are all my dependencies satisfied?" | `ModulePublish` already gates a placement on the module's declared `MinMeshVersion` — the same predicate, widened from one floor to a declared set |
| **Fan-out** | `FrameworkReleaseBroadcaster` — memex holds the GitHub App; the subscriber set is the `Hosting/Deployment` records' registry sources | **exists, and dispatched nothing until 2026-09-03** (no caller) — see below |

The ingest surface is not new, which matters: a new public endpoint is a new thing to secure, and
this one is already HMAC-verified and already routes by event type.

🚨 **"Exists" is not "delivers", and the fan-out is the standing proof (#2235).** The broadcaster
has been built, unit-tested and DI-registered since 2026-08-23 and has dispatched **zero** events.
Three joints have to be closed for one release to reach one satellite, and each is silent when it
is open:

| joint | switched on by | when it is open |
|---|---|---|
| the inbox accepts the release event | `WebhookInbox__Targets__N` on the control instance | 404 — byte-identical to a wrong URL |
| the delivery verifies | `Hosting__PlatformWebhookSecret` | the watcher drops it as unverifiable; the POST already answered 2xx — the mismatched-secret hole, still open: [Webhook Inbox](/Doc/Architecture/WebhookInbox) → "What a 2xx does NOT prove" (#3312) |
| the verified release fans out | a caller of `Broadcast(...)` (since 2026-09-03: `PlatformBuildInboxWatcher`, MeshWeaver.Plugins) and at least one `Hosting/Deployment` record naming a registry source | `0 dispatched, 0 failed` — now a WARNING naming the records on the control instance |

Two lessons the rest of this design should inherit. **A key the code reads and no chart renders can
never be set**, so its feature is permanently off while reading as "off by choice" —
`FrameworkBroadcast:Subscribers` was in that state, in both repos, for the whole life of the
feature. And **prose naming a mechanism that does not exist stops the search**: `main-cd.yml` said
the subscriber set "lives in the Hosting fleet registry", a registry with no node type, no reader
and no writer anywhere, and that sentence survived two investigations into why nothing arrived.
`ReleaseDeliveryChainGuard` (`test/MeshWeaver.Documentation.Test`) fails RED on both shapes.

## The release fact

One release publishes **one fact per package**, not one per repo — a repo with several plugins
announces several, and a platform rebuild announces the platform version plus every plugin version
rebuilt against it.

```
Release
  repository    Systemorph/MeshWeaver.Plugins
  packageId     MeshWeaver.Blazor.EntityViews
  version       3.0.0-rc8.ci.5432
  platform      3.0.0-rc8.ci.5432     # the framework identity it was built against
  commit        <sha>
```

`platform` is not decoration. Bundles are adopted, not rebuilt, and a consumer can only adopt bytes
built against a framework identity it can resolve — the same equality `BakeEquivalenceTest` pins
between the bake and the gate.

## The dependency gate

A repo declares what it depends on. On wake it resolves each declared dependency against the mesh
and builds **only when every one is satisfied at or above its floor**.

Two properties this must have, both learned the hard way:

- **Unsatisfied is a WAIT, not a failure.** A dependency that has not published yet is a normal
  state, and rendering it red trains everyone to ignore the signal.
- **A gate that cannot fail is not a gate.** "All dependencies satisfied" over an EMPTY declared set
  is vacuously true — so a repo whose declaration failed to load must refuse, never proceed. The
  same refusal as a discovery that finds no hosts: finding nothing must not read as a pass.

## What this replaces

This is the **sole** release-coordination mechanism. Specifically, it replaces:

- ordering repos' pipelines against each other, and
- any consumer inferring a dependency's version from a dispatch payload rather than querying.

It does **not** replace, and must not be confused with:

- **Publication atomicity within one repo** — `staging-<sha>-<run_id>` tags plus an all-or-nothing
  `promote` (see [Continuous delivery contract](../ContinuousDeliveryContract)). That is about a
  single set never being half-published; this is about repos agreeing on what exists.
- **`main-cd` checking out no other repository.** It states *"there is not one `repository:` input in
  this file, so it could not compile them even by accident — and must never be given the chance."*
  The bus makes that constraint cheaper to keep, not something to relax: repos announce instead of
  compiling each other.

## Migration

Each repo, in this order — the platform first, since everything declares a floor on it:

1. **Emit.** On a real publication, POST the release facts. Emitted by the publisher on an OBSERVED
   publication, never from a step that runs regardless.
2. **Declare.** Record the dependency set the repo needs.
3. **Gate.** Query on wake; build when satisfied; wait otherwise.

A repo that has not migrated keeps working — it simply does not wait, which is exactly its behaviour
today.
