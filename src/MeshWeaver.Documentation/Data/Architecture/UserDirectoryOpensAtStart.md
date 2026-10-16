---
Name: The User Directory Opens at Start
Category: Architecture
Description: Why the first signed-in request on every new pod met an empty user directory, why the directory now opens when the application starts, and why a pod is not Ready until the directory has its first snapshot.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 4-6 8-6s8 2 8 6"/></svg>
---

The portal identifies a signed-in user through its **user directory**, `UserIdentityCache`. The
directory is an in-process index of every `User` node by email. A live `nodeType:User` query fills it.
`UserContextMiddleware`, `ResolveHttpCaller` and `CircuitAccessHandler` read it on every request.
Before its first snapshot arrives, the directory answers *"the mesh user index has not received its
first snapshot yet"*. That answer means Unavailable, not "no such user" (see #974).

## What went wrong

The directory is a DI singleton. A singleton is built when something first resolves it, and only
the request paths resolved it. So the **first signed-in request on a pod built the directory and
asked it in the same call**. The query subscribes on the query pool, so the answer is always
Unavailable at that moment. Every new pod therefore served one degraded identity, and logged:

> UserContextMiddleware: mesh user index UNAVAILABLE for … (the mesh user index has not received its
> first snapshot yet) — falling back to the email local-part for the partition key.

Measured on the control instance on 2026-10-16
([#6850](https://github.com/Systemorph/MeshWeaver/issues/6850)): 23 such lines in 24 hours across
8 pod generations, one per pod, each at that pod's first signed-in request, and none after it.
The line appeared on every image in that window, so it was not a regression of one build. The
"We could not check your account" page users reported that morning came from a different
instance and a different cause: per-node hub activation failing on a module-container fault
([#6847](https://github.com/Systemorph/MeshWeaver/issues/6847)).

## What it does now

`UserDirectoryReadiness` (`src/MeshWeaver.Hosting.AspNetCore/Portal`) is both a hosted service and
a health check. The portal's service defaults register it (`AddDefaultHealthChecks`).

- **It opens the directory when the application has started.** On
  `IHostApplicationLifetime.ApplicationStarted` it resolves the directory through the mesh hub's
  provider. That is the same instance every request path asks. Waiting for ApplicationStarted
  keeps the read after every other hosted service has started. An earlier read could see a
  partial first snapshot, and a partial snapshot answers a definitive "no such user".
- **The pod is not Ready until the first snapshot lands.** The `user_directory` check is
  Unhealthy until then. It is a **roll gate** (`ServiceDefaults.RollGateChecks`; see
  [The Bake Gate Only Stalls a Roll](../TheBakeGateOnlyStallsARoll)). It holds `/ready` only and
  never fails the startup probe. A pod whose directory never fills stays alive and out of the
  Service, and the roll stalls with the previous image serving. Restarting would not fill a
  shared directory read.
- **After the first snapshot it never takes the pod out of rotation again.** A rebuild or a
  terminated read reads Degraded: printed on `/health`, holding nothing. Lookups in that window
  already answer Unavailable, and the cache's next lookup re-opens a terminated read. Pulling every
  replica because the shared read stalled would turn degraded identity into an outage.
- **A probe is also a caller.** A pod held out of the Service receives no lookups, so the check
  calls `UserIdentityCache.EnsureOpen()` on every probe. A read that failed before its first
  snapshot is retried that way, with no timer and no retry loop. An **open that threw** at
  ApplicationStarted is retried the same way: the next probe after ApplicationStarted opens the
  directory again and clears the recorded failure once that succeeds, so one transient fault while
  resolving the directory cannot keep a replica Unhealthy for the life of the process. A recorded
  opening failure counts only while no directory is open: two probes can retry at once, and the
  loser may publish its failure after the winner opened the directory.
- **An incomplete snapshot is not a snapshot.** An `Initial` or `Reset` frame that names
  `SilentProviders` or carries `SnapshotIncomplete` is a floor: a provider did not answer, or
  dropped part of its read, so users may be missing and a miss would be a false "no such user".
  The directory read rejects such a frame in its pipeline, before it is applied. The rejection is
  recorded as the read's failure (Unhealthy before the first snapshot), the read is dropped, and the
  next probe or lookup opens a fresh one.
- A host that registers no directory (the migration job, a setup-only boot) reports Healthy with
  *"nothing to measure"*.

## The guard

`UserDirectoryOpensAtStartTest` (`test/Memex.Portal.Shared.Test`) runs a real mesh. Its directory
provider holds the snapshot until the test releases it:

- The host's lifetime is the real `ApplicationLifetime`. Before `NotifyStarted` the directory is
  not open, and a probe does not open it. After it, the directory read opens before any lookup.
- Readiness is Unhealthy while the snapshot is held, and Healthy once it lands.
- The **first** lookup the process makes is then Found, and it reads the same directory instance.
- An open that throws once is retried by the next probe, and readiness reaches Healthy.
- An incomplete first snapshot leaves the directory unhydrated with the failure recorded; the next
  probe opens a second read, whose complete snapshot hydrates it.
- The negative control is the old shape: with no readiness, the first lookup builds the directory
  and gets Unavailable.

With the open removed from ApplicationStarted, the first test fails: *"ApplicationStarted must open
the user directory read before any request asks it"*. Without the probe's retry, the open-failure
test fails on *"could not be opened"*. Without the rejection, the incomplete snapshot hydrates the
directory, no failure is recorded, and the incomplete-snapshot test times out waiting for one.

## Related

[The Bake Gate Only Stalls a Roll](../TheBakeGateOnlyStallsARoll) ·
[Query Fan-In Stall Terminal](../QueryFanInStallTerminal)
