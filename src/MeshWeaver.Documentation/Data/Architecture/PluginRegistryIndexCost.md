---
Name: The Plugin Registry Index's Per-Request Cost
Category: Architecture
Description: >-
  The authenticated bundle index evaluates entitlement per package and runs mesh queries on every
  request, measured at 12-19 s to first byte for an 8.7 KB document. That one cost surfaces as two
  unrelated-looking incidents because it meets two different ceilings — a 10 s auth budget that
  answers 503, and an index-assembly stage with no bound at all that answers nothing. Why widening
  a budget moved the symptom rather than fixing it, and the hypothesis that was refuted on the way.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>
---

# The Plugin Registry Index's Per-Request Cost

**`GET /api/plugins/bundles/index.json` evaluates entitlement per package and runs mesh queries on
every request.** For an 8.7 KB document that was measured, from inside two production portals, at
~0.03 s to connect and then **12-19 s to first byte** — and the cost grows with the number of
published packages. The measurement is recorded in the code that had to work around it,
`memex/aspire/Memex.Portal.ServiceDefaults/ServiceDefaults.cs`.

This page exists because that single cost produced **two incidents that look unrelated**, and
because the obvious remedy for each is the one thing that must not be done.

## One cost, two ceilings, two symptoms

The handler is `memex/Memex.Portal.Shared/Api/PluginBundleEndpoints.cs` — `/index.json` mapped at
`:149`, `Index(...)` at `:1009`. Note the tree: it is **core, but under `memex/` rather than
`src/`**, which is why searching `src/` for it finds nothing.

It runs two sequential stages of live mesh reads:

| stage | work | ceiling | what a stall looks like |
|---|---|---|---|
| **auth filter** | `InstanceRegistryAuthenticator.Resolve` — **three mandatory legs** (a children listing plus two per-node mirrors), then an **optional fourth**: the plan ladder (below) | **10 s per leg** (`ReadBudget`, `src/MeshWeaver.PluginCatalog/InstanceRegistryAuthenticator.cs:54`), and a **separate** 10 s for the ladder | a real **503 + `Retry-After`** (`InstanceAuthResponses.cs:41`), at ~10,000 ms |
| **index assembly**, only after auth succeeds | `Servable()` `:1192` → `InstalledPackages` `:2149` and `HeldPartitions` `:1325`, both `IMeshService.Query<MeshNode>` awaiting `QueryChangeType.Initial`, plus per-package entitlement `Decide(...)` — and two reads of the module-activation record (see *Since this page was written*) | **none** when this page was written; a query stall is now 503 + `Retry-After` (#5454) | **nothing at all** — no status line, no headers — until the *caller's* budget tears the connection down |

`grep -c '\.Timeout(' PluginBundleEndpoints.cs` returns **`0`**. Not one bound in 2,194 lines.

### The optional fourth auth read, because it is easy to undercount

After the grant leg, `Resolve` chains
`.SelectMany(result => result.Instance is null ? Observable.Return(result) : Ladder().Select(…))`
(`InstanceRegistryAuthenticator.cs:406-446`), so **every successful authentication** takes a fourth
read — unless the host registers no ladder, in which case `Ladder()` is
`Observable.Return(PlanTierRanks.Empty)` and there is no read at all.

When it is registered, `PlanTierLadder.Read()` is an **`IMeshService.Query<MeshNode>`** over
`namespace:{Namespace}` awaiting `QueryChangeType.Initial`, with **its own** `.Timeout(ReadTimeout)`
where `ReadTimeout` is 10 s — a *separate* budget from the authenticator's `ReadBudget` — and it is
cached per mesh for a `CacheDuration` of one minute, so the cost falls only on a cold or expired
cache.

Two reasons this matters beyond the count:

- it is a **`Query` listing**, not a point read, so it is a *second* instance of the same
  CQRS-on-the-hot-path shape this page is about, not merely another leg;
- the worst case before index assembly even begins is therefore **four live mesh reads at 10 s each**,
  which sharpens the point rather than softening it.

So the same slow dependency reads as either *"the registry returned 503, retries exhausted"* or
*"operation timed out after 180001 ms with **0 bytes received**"*, depending purely on which stage it
happened to stall in. Filed as two issues, triaged separately, with different-looking causes.

🚨 **The `~10,000 ms` in the first symptom is not a coincidence and not a client timeout.** It is
`ReadBudget` elapsing server-side. A client-side timeout would surface as a cancelled attempt, not as
a recorded `Result: '503'` — a resilience pipeline records a status only for a response that actually
carried one.

## The fast path is fast because it never touches the mesh

`AuthenticateToken` returns `Observable.Return(InstanceAuthResult.Resolved(null))` **synchronously**
for a missing or malformed key. That is why an unauthenticated probe answers 401 in ~0.13 s while the
authenticated request hangs for three minutes — and why "the host is up, so this is not the server"
is the wrong inference. **An unauthenticated probe of this endpoint measures nothing about the
authenticated path.** Both share a hostname and almost no code.

The corollary for diagnosis: `/api/version` answering in 0.12 s, and the 401 answering in 0.13 s, are
both consistent with the authenticated handler being completely stuck.

## 🚨 Why this is a CQRS violation, not just a slow endpoint

`Query` / `ObserveQuery` are eventually consistent, and
[CQRS and Content Access](../CqrsAndContentAccess) limits them to listing children, searching by
predicate and autocomplete — *anywhere a stale negative is harmless*. Assembling a per-request HTTP
response is none of those: the answer must be correct, it must be prompt, and the caller has no way
to tolerate a slow negative. Two `IMeshService.Query<MeshNode>` calls awaiting their `Initial` frame
on the hot path is the defect, and the absence of a timeout only decides which symptom it produces.

## The remedy, and the two that are forbidden

**Take the work off the per-request path.** Assemble the servable index once per publication /
entitlement change and serve that, rather than evaluating entitlement per package and querying the
mesh per request. Then **give every live read that remains a declared bound**, so a slow dependency
fails fast and visibly instead of holding a connection open with nothing written.

Forbidden, and one of them has already been tried:

- **Widening a client budget.** The attempt budgets were **already** raised to 30 s
  (`plugin-registry`) and 120 s (`plugin-registry-bundles`) at `ServiceDefaults.cs:140,150`,
  precisely so the default 10 s client timeout would stop firing on a 12-19 s TTFB. That widening
  worked, in the sense that the client stopped cancelling — and the result was that the *server's*
  503 became visible instead, at scale, for weeks. **The bound that was widened did not fix
  anything; it changed which symptom got filed.** That is the strongest available argument against
  widening the next one.
- **Raising a retry count.** The server answers a genuine 503 after its own budget. Three more
  attempts meet the same ceiling.
- A watchdog re-polling a frozen catalog, or a `catch` that continues, would each bury it further.

## Since this page was written: two things that changed the table above

**The assembly stage's queries are bounded now (#5454).** A query-fan-in provider that never
delivers its `Initial` ends the read with `QueryProviderStalledException` (policy
`query-fanin-stall-terminal`), and both bundle routes map that — through
`InstanceAuthResponses.UnavailableOnAStalledRead` — to **503 + `Retry-After`** instead of an
unhandled 500. So a stall in `InstalledPackages` / `HeldPartitions` no longer holds a connection
for the caller's whole budget. The consumer honours the header: the `plugin-registry` and
`plugin-registry-bundles` clients use `AddStandardResilienceHandler`, whose
`HttpRetryStrategyOptions.ShouldRetryAfterHeader` defaults to `true` and is overridden nowhere, so
a retry waits what the registry asked for rather than a blind backoff.

**A third per-request cost, which no stage of the table names: the activation read queued behind
every landing (#4963).** Index assembly reads the module-activation record TWICE per request
(`WithPublishedModules` and `ServableModules`), and every bundle download reads it once, through
`ModuleLandingService.GetActivation()`. That read ran on the service's **cap-1 landing pool** — the
same lane `ShelveModule` lands a published module on. So on the registry:

- every index request and every bundle download waited, **with nothing written to its caller**,
  for every publish landing in flight — and a shelf landing is not short: it is one SMB round trip
  per file on the shared `/data` volume (DefaultViews alone declares 254 static assets);
- and for every OTHER request's read, one at a time, **process-wide** — a single-server queue whose
  service time is itself a walk of every module's record directory over SMB, so it grows with the
  number of modules and with how many consumers poll at once.

That is exactly the shape this page could not explain from the mesh reads alone: a cost that
"grows with the number of published packages", and a stall that writes zero bytes while
`/api/version` and the unauthenticated 401 answer in ~0.1 s. The serialisation was justified as
"so a read never observes a landing halfway through its read-modify-write" — but there has been no
read-modify-write since #2090/#4026: a landing writes its bytes into a fresh generation directory
and then an immutable record of its own, and the list is DERIVED from the records present, which is
why a reader on another replica (whose landings this pool never serialised) was always safe. A
reader on another thread of the same process is the same reader.

**Fixed:** `GetActivation()` reads on `ModuleLandingService.ReadPool` — the mesh's file-system
`IIoPool` in production — and the cap-1 lane serialises landings only.
`ActivationReadIsNotQueuedBehindALandingTest` parks a landing inside its recording window (the
#4026 seam) and requires the read to answer within a third of the park's bound: with the read back
on the landing lane it fails (`Total: 1, Failed: 1`, "the observable emitted nothing at all"); with
the fix it passes, and the read shows the parked module as not yet recorded — no torn read.

What this does **not** establish: that this queue was the stall behind the 2026-09-20
08:02–08:08Z occurrence. That window does coincide with three satellite `publish-bake` runs hitting
the registry (MeshWeaver.Crm 08:01:54–08:05:25Z, MeshWeaver.Manufacturing 08:01:38–08:05:14Z,
MeshWeaver.Reinsurance 08:04:06–08:11:00Z), each of which reads the index — concurrent readers on
one lane — but none of them POSTed a module to the shelf, and the registry's own log for the window
was not read. The mesh-read costs above and the entitlement evaluation are untouched, and nobody has
profiled the stages against each other.

## The stages measured against each other — and the index's activation list made a maintained snapshot

The paragraph above ended on "nobody has profiled the stages against each other". That has now been
done, live, on the fleet registry itself. A read-only script
(`rbuergi/Script/bundle-index-stage-timing` on memex.meshweaver.cloud, activity
`rbuergi/_Activity/89bbc75a3e4f45e09b4e513ac3c061f6`, pod
`memex-portal-deployment-7777cfc77-slvwl`, 2026-09-26 18:15Z, core `4c8530d7dd`) timed each stage
the index runs, in the running process, against the real `/data` share:

| stage | per index request | measured |
|---|---|---|
| `ModuleLandingService.GetActivation()` — derive the list from every module's record directory (44 modules) | **twice** | **7,266 ms**, then **8,725 ms** |
| `ModuleBundleSource.CollectVersion` for every declaring package | once per package | 991 ms total over 44 (15–60 ms each) |
| `InstalledPackages` query (`namespace:Plugins nodeType:Package`, 94 rows) | once | 278 ms |
| `HeldPartitions` query (127 rows) | once | 228 ms |
| `PackageOriginAnchor.Read()` | once | 0 ms (cached, `Authoritative`, 104 origins) |
| `IPublicationArtifacts.Read()` | once | 0 ms (`NoPublicationArtifacts`) |

Sum: **~17.5 s**, against the **17.8 s** `ModulePinAudit` measured for one index read from the same
portal at 10:53Z the same day (triage finding `rbuergi/Feedback/self-registry-bundle-index-18s-20260926T1108Z`).
**The mesh queries are not the cost; the activation derivation is — about 90% of it.** Each
derivation lists four record kinds per module, reads every record, probes the unloadable marker and
each generation directory, one SMB round trip each; and nothing about the list had changed between
any two requests. N consumers polling at once ran N such scans of the same share, which is how a
busy registry reached the zero-byte 180 s stalls of #4963.

**Fixed: the index reads a MAINTAINED list, not a scan.**
`ModuleLandingService.GetServedActivation()` holds the last derivation and answers from it:

- a landing, shelving, uninstall or removal made by **this** process invalidates the snapshot
  **before** it is announced, so the replica that took a publish serves it on its very next index
  read;
- a record **another replica** wrote on the shared volume is absorbed within
  `ServedActivationFreshness` (30 s): the first read past the bound still answers at once from the
  snapshot and starts ONE re-derivation that every concurrent reader shares;
- a derivation that started before a local write is never published over it (a write generation
  guards the store);
- the in-flight derivation is OWNED by the service, not by the request that started it: its slot is
  claimed by compare-and-swap before the work starts, its result is an `AsyncSubject` fed by the
  service's own subscription, and it releases the slot pair-exactly on success and on fault. So an
  aborted request can never leave a fault cached for every later reader, and a derivation that
  finishes late can never evict its successor (both raised in review on #5768; the second is
  pinned by `ALateDerivationDoesNotEvictItsSuccessor`, red at three derivations instead of two
  when the release is made unconditional).

The index therefore costs one snapshot read — the two reads per request collapse into it — and at
most one derivation is in flight per replica, whatever the poll rate. **The bundle download keeps
the authoritative `GetActivation()`**: it decides which bytes a consumer lands, so it must resolve a
version another replica published a moment ago; the index lagging by one freshness bound only means
the consumer sees that version on its next poll. `ServedActivationIsMaintainedNotScannedTest` pins
all three rules with two landing services on one root standing in for two replicas and a test
clock for the bound: a record the other replica wrote is on the volume (the authoritative read
sees it) and the served read does not report it while fresh — so it did not rescan; the own-replica
landing is served at once; and once due, the re-derivation lands the other replica's module.

What this does **not** change: `CollectVersion`'s per-package directory listings (~1 s) and the two
mesh queries (~0.5 s) are still per request; and the download route still derives the list twice
(`Servable` and `ModuleFiles`), so a single bundle download on this registry still costs ~16 s of
activation derivation. Both are the next candidates if the index or the download is still slow
after this rolls — measure first, with the same script.

## The second defect: exhaustion leaves no readable mark

When the attempts do exhaust, nothing an operator can see records it:

- `InstanceAutoRegistrationService.AdoptPrebuilt` → `AbsorbUnlessPrebuiltRequired` catches everything
  except `PrebuiltRequiredException`, logs at **`LogInformation`** — *"no prebuilt assemblies adopted
  — compiling instead"* — and returns 0;
- `RegistryUpdateReconciler.ReconcileModules` is failure-tolerant per package and leaves the module
  set at its current version.

So the catalog is left **frozen at its last-known-good state with no incident, no node and no
readable status** — an installation quietly serving a stale catalogue, visible only as an
Information line in a pod log. This is separable from the cost and survives fixing it: a registry
that cannot serve should leave a mark somewhere an operator reads.

## A hypothesis that was refuted, recorded so it is not re-run

A drift check measured `PluginCatalog__RegistryUrl` as **live-empty** on the same namespace as the
503s, which looked like a strong candidate cause. **It cannot be**, and the argument is worth keeping
because it generalises:

- no `BaseAddress` is configured for the named clients, so an empty base yields a **relative URI**;
- `HttpClient` throws `InvalidOperationException` **synchronously**, before any network I/O;
- a resilience pipeline records a **status** only for a real HTTP response. An exception is a
  different outcome and is not the same telemetry field.

So an empty registry URL **cannot** produce a recorded `503`. It is nevertheless a real, separate
configuration defect — and note the shape: **a key present with an empty value is invisible to any
"is it configured?" guard that tests for null or absence.**

The generalisation: *before accepting a cause, check that it can produce the exact observation you
have* — not merely a failure. "Something is misconfigured and something is failing" is not a
mechanism.

## Reading an incident record of this, with care

The 503 log lines carry **no exception**, so per
[Red-Log Watching & Ticketing](../LogWatchTriage) the folding identity falls back to the masked
**log** message — and masking replaces the resilience-pipeline source name and the execution time
with placeholders. Every distinct shape therefore folds into **one** fingerprint: two named pipelines
at ~10,000 ms and an unnamed one at ~1.4 ms became a single incident with a single count.

🚨 So a large occurrence count on this fingerprint is **not** a count of one defect, and a fix
verified against one shape can leave another firing with the counter still climbing. Read the
samples, not the total.

## Related

- [CQRS and Content Access](../CqrsAndContentAccess) — what `Query` is and is not for
- [Plugin Registry](../PluginRegistry) — the endpoints, the credential model, and who may read the catalog
- [Red-Log Watching & Ticketing](../LogWatchTriage) — the folding identity, and why masking merges these shapes
