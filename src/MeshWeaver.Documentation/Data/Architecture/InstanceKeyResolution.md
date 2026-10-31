---
Name: Instance-Key Resolution
Category: Architecture
Description: How the registry turns an mwi_ instance key into a grant — three legs read as LIVE process-wide streams, existence decided by a listing and content by the owner's mirror — and why the per-request point read with a ten-second budget it replaced was the cause of the fleet-wide 503s (#3119).
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/></svg>
---

`InstanceRegistryAuthenticator` (`src/MeshWeaver.PluginCatalog/`) is the one resolver behind every
registry surface an installation talks to: `/api/plugins`, `/api/plugins/bundles/…`, the NuGet v3
feed and the OCI `/v2` pull surface all hand it an `Authorization` header and act on the
`InstanceAuthResult` it returns. Three reads stand between a presented `mwi_` key and its grant:

| Leg | Path | Normal state |
|---|---|---|
| index | `MeshWeaverInstance/{sha256-prefix}` | **absent** for an unknown key — the definitive 401 |
| instance | `{owner}/MeshWeaverInstance/{id}` (the index says where) | present; the hash is re-checked here, the index is only a routing hint |
| grant | `Admin/_PluginGrant/{id}` | **absent** for a freshly registered instance — authenticates, entitled to nothing |

Each read has three outcomes — `Present`, `Absent`, `Unavailable` — and the difference between the
last two is the whole design: an `Absent` is a verdict the endpoint renders as 401/404, an
`Unavailable` is "I could not find out", rendered as `503` + `Retry-After` and never remembered
(#2695). Nothing on this page changes that contract; it changes how the three reads are performed.

## What was wrong (#3119)

Until 2026-09-02 each leg was a one-shot `GetDataRequest` to the owning per-node hub with a ten-second
budget, and the verdict was memoised for one minute per key. Measured on the public instance, 14:05–15:25Z:
the portal pods were running the unanchored cross-schema fan-outs inventoried on #2194 (`[CrossSchema]
SLOW` 810 / 552 / 783 per ten minutes on three pods, one pod at 11.7 GB with 104 GC stalls in half an
hour), and under that load the owning hub of the CI instance's node did not answer inside ten seconds.
The read reported `Unavailable`, the endpoint answered
`503 Instance-key resolution is temporarily unavailable`, and because an unavailable outcome is
(correctly) not cached, the very next request paid the same ten seconds again — 177 app-side 503s in
eighty minutes, 149 of them on `bundles/prebuilt` from the CI runner. MeshWeaver.Reinsurance's
publish-bake failed on it three times that day; every satellite gate fetches through this endpoint.

Neither the budget nor a retry is the fix. Widening ten seconds to thirty moves the cliff; a retry
inside the registry multiplies the load that caused the stall. The defect is that a **hot request
path performed a fresh owner round-trip at all**.

## What it does now

Every leg reads through the process-wide `IMeshNodeStreamCache` — the same per-path
`Replay(1)` mirror every Blazor page and every routing decision uses — composed the way
[CQRS — Queries vs. Content Access](../CqrsAndContentAccess) prescribes for a node that may not exist:

```csharp
// EXISTENCE — a live children listing of the parent (Replay(1).AutoConnect(1), one per namespace).
hub.GetQuery($"instance-key-listing:{parent}", $"path:{parent} scope:children select:path")
    .Take(1)                                                    // the CURRENT snapshot
    .SelectMany(nodes => nodes.Any(n => string.Equals(n.Path, path, StringComparison.Ordinal))
        ? hub.GetMeshNodeStream(path).Where(n => n is not null).Take(1)   // CONTENT — the owner's mirror
              .Select(NodeReadOutcome.Present).DefaultIfEmpty(NodeReadOutcome.Absent)
        : Observable.Return(NodeReadOutcome.Absent))
    .Timeout(budget)                                            // the SAME ten seconds, first frame only
    .Catch((TimeoutException _) => Unavailable(…))              // no frame yet ≠ not found
    .Catch((Exception ex) => Unavailable(ex));
```

Three properties follow, and each is pinned by `InstanceKeyLiveResolutionTest`
(`test/Memex.Portal.Shared.Test`):

1. **Only the first request for an instance waits on an owner.** The listing and the point streams
   are cache entries: hydrated once per process, kept current by the change feed, read from memory by
   every later request. The test proves it against the cache's own bookkeeping — the mirror is live
   after the first resolution and no eviction occurs across the second.
2. **A change is seen by the next request, on every replica.** A disable written through
   `GetMeshNodeStream(path).Update(...)` lands in the same mirror the authenticator reads. There is no
   verdict cache any more, so there is nothing to invalidate: `Invalidate(keyHash)` is retained as a
   no-op for callers compiled outside this repository, and `CacheDuration` / `NegativeCacheDuration`
   are gone.

   🚨 **"Lands in the mirror" is not the same moment as "the write completed" (#6818).** A write's
   terminal is the OWNER's ack; the mirror advances on the owner's commit echo — a different message,
   to a different hub, with nothing ordering the two. So a writer whose completion promises "the
   next request decides with this" must wait for the mirror to carry the write, not just for the
   ack. `InstancePlanService.SetPlan` (a plan promotion) does exactly that: it completes only once
   `GetMeshNodeStream(path)` shows a version past the one the write was diffed against, or the new
   plan itself. On every OTHER replica the change is seen when that replica's change feed delivers
   it — eventually, not at the writer's completion. `PluginBundlePlanTest
   .PromotingTheInstance_WidensTheNextRequest` pins it by holding the owner's echo back at its
   outbound pipeline: the promotion must not report done while the mirror still says `free`.

   🚨 Waiting at the WRITER only works for the writer's own replica, so it is the right tool for a
   WIDENING change (a promotion seen a moment late costs a retry) and the wrong one for a
   REVOCATION, which must hold on every replica from its commit. The build-principal leg therefore
   reads its node from the OWNER, not from this mirror — see
   [Plugin Build Contract](../PluginBuildContract).
3. **An unknown key never opens a point read on an absent path.** The listing is empty-on-absent, so
   the definitive 401 is served from memory and the storm breaker is never armed — the failure mode
   that page's table warns about cannot start here.

The ten-second budget still exists and is deliberately unchanged, but it now bounds a **cold
hydration** only: the subscriber that gives up does not tear the entry down, the hydration completes
behind it, and the retry the 503 asks for reads the frame from memory. The `Unavailable` reason
names the path and says so, so the log line beside a 503 can no longer be mistaken for "the owning
per-node hub never answered".

## One budget for the whole resolution (#7484)

The three legs run one after another, and each used to be bounded by the ten-second budget on its
own. A resolution whose legs were each slow but each inside its bound therefore answered after up to
thirty seconds. The registry's authentication hook gives the exchange ten seconds. So when the
registry instance's node-stream cache hub wedged, the exchange's correct retryable 503 arrived after
the hook had already given up and reported a bare 500 for every key whose mirrors were cold.

The legs now share ONE budget (`InstanceRegistryAuthenticator.WithinBudget`). Whatever has not
answered when it is spent is `Unavailable`: a retryable 503, never "unknown key". Each leg's
hydration keeps running in the process-wide cache, so the retry finds it in memory. The plan ladder
stays outside the deadline: it has its own query budget and snapshot, and cutting it would cancel the
one read that warms that snapshot.

The caller's own limit must exceed this budget for the 503 to reach it. Making the hook's
"no answer in time" distinguishable from a 5xx is the hook's half, in the deployment repository
(MeshWeaver.Deploy#106: separate exit codes for a stall, a 503, another 5xx and an unreachable
exchange).

Pinned by `InstanceKeyResolutionAnswersWithinOneBudgetTest` (`MeshWeaver.Hosting.Test`, virtual time):
three legs of four seconds each answer `Unavailable` at ten seconds, and three legs of three seconds
each still resolve the instance.

## The exchange's line on `/health` (#7721)

During #7484 the validating instance's `/health` read Healthy on every line except an overdue fleet
watch, a different subsystem. Nothing on it described the exchange.

The authenticator now counts what every instance-key resolution answered
(`InstanceRegistryAuthenticator.Outcomes`, an `InstanceKeyResolutionTally`): resolved, refused and
unavailable, with the time of the newest of each. It counts once per answer, after the fault handler,
so a faulted read is counted as the `Unavailable` the caller is told. A request with no credential,
or with a short-lived token, is not a key resolution and is not counted. The counts are per replica.

`InstanceKeyExchangeHealthCheck` (`instance_key_exchange`) reads that tally:

| Reading | Verdict |
|---|---|
| no key presented yet | Healthy, "nothing measured yet" |
| no unavailable answer | Healthy, with the counts |
| the NEWEST resolution was unavailable, less than ten minutes ago | **Degraded**: callers are being told to retry right now |
| an unavailable answer, followed by a resolved or refused one, or ten minutes of no traffic | Healthy, with the counts and the last unavailable reason |

It is never Unhealthy and does not belong on readiness. An unavailable resolution is a slow or faulted
mesh read that the sibling replicas share, so taking this replica out of the Service fixes nothing.
Core defines the check. The portal host registers it on the census tag, so the reading prints
whatever the status (the MeshWeaver.Plugins half).

Pinned by `InstanceKeyExchangeHealthLineTest` (`MeshWeaver.Hosting.Test`, virtual time).

## What did not change

- The endpoints' contract: unknown key ⇒ the definite answer each surface already gives (401 on the
  catalog and bundle routes, 404 `NAME_UNKNOWN` on `/v2`, which deliberately looks like "no such
  repository"); no verdict ⇒ `503` with `Retry-After`, matched by the consumers'
  `RegistryResponseException.IsTransientFailure` (#2836).
- The `ReadOverride` test seam: a suite that drives "unavailable" / "absent" / "present" per path
  still bypasses the live read, so the classification tests in MeshWeaver.Plugins
  (`InstanceKeyUnavailableNotUnknownTest`) and here (`BuildPrincipalAuthenticationTest`) keep pinning
  the same rules.
- The plan ladder's fail-closed fallback. Since #7354 the ladder is one live listing of `Admin/Tiers`
  in the process-wide cache, no longer a query re-run after a one-minute snapshot (see below).

## The mirrors are HELD, so "hydrated once per process" holds (#7354)

Property 1 above was true only within the cache's idle window. A read entry with no live subscriber
is released after `ReadStreamIdleExpiration` (ten minutes), and every leg is a `Take(1)`. A consumer
that polls less often than that, as the registry safety net does every thirty minutes, found the
instance and grant mirrors released on every poll. It paid a fresh owner subscription under the
ten-second budget each time.

The authenticator now keeps one standing subscription, as System, on each mirror it has found
present (`InstanceRegistryAuthenticator.Hold`). The hold ends when the node is deleted (the mirror
completes), when the mirror faults (the cache evicts it and the next request re-holds a fresh one),
or when the mesh disposes the service. Nothing re-subscribes on its own. An unknown key opens no
mirror, so the set is bounded by the registered instances. The change feed still drives the held
mirror, so a disable or a grant change is still seen by the next request.

Pinned by `RegistryAuthenticationOpensNoReadPerRequestTest` (`MeshWeaver.Hosting.Test`) with a 500 ms
idle window. See [The Plugin Registry Index's Per-Request Cost](../PluginRegistryIndexCost) for the
503 shapes this closes.

Related: [Durable But Unreadable](../DurableButUnreadable) — the OTHER way this authenticator answers
503, permanently, when the node it needs was never readable at all.
