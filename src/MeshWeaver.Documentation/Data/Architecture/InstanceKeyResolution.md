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
budget, and the verdict was memoised for one minute per key. Measured on memex-cloud, 14:05–15:25Z:
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
3. **An unknown key never opens a point read on an absent path.** The listing is empty-on-absent, so
   the definitive 401 is served from memory and the storm breaker is never armed — the failure mode
   that page's table warns about cannot start here.

The ten-second budget still exists and is deliberately unchanged, but it now bounds a **cold
hydration** only: the subscriber that gives up does not tear the entry down, the hydration completes
behind it, and the retry the 503 asks for reads the frame from memory. The `Unavailable` reason
names the path and says so, so the log line beside a 503 can no longer be mistaken for "the owning
per-node hub never answered".

## What did not change

- The endpoints' contract: unknown key ⇒ the definite answer each surface already gives (401 on the
  catalog and bundle routes, 404 `NAME_UNKNOWN` on `/v2`, which deliberately looks like "no such
  repository"); no verdict ⇒ `503` with `Retry-After`, matched by the consumers'
  `RegistryResponseException.IsTransientFailure` (#2836).
- The `ReadOverride` test seam: a suite that drives "unavailable" / "absent" / "present" per path
  still bypasses the live read, so the classification tests in MeshWeaver.Plugins
  (`InstanceKeyUnavailableNotUnknownTest`) and here (`BuildPrincipalAuthenticationTest`) keep pinning
  the same rules.
- The plan ladder (`PlanTierLadder.Read`) is a listing with its own one-minute snapshot and a
  fail-closed fallback; it was not the measured cause and is untouched.

Related: [Durable But Unreadable](../DurableButUnreadable) — the OTHER way this authenticator answers
503, permanently, when the node it needs was never readable at all.
