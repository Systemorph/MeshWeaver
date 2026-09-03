---
Name: Image Pair Skew
Category: Architecture
Description: A promoted portal image pairs a core commit with a Plugins head resolved hours later; each half is green in its own repository and the pair is never executed. The 2026-09-03 sign-in outage, what every guard missed, the runtime fix that shipped, and the delivery gap left open.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="8" height="16" rx="1"/><rect x="13" y="8" width="8" height="12" rx="1"/><path d="M11 12h2"/></svg>
---

# Image Pair Skew

**A portal image is two commits from two repositories, and continuous delivery pairs them at
different moments. The core half is the commit the run was created for; the Plugins half is
`refs/heads/main` of MeshWeaver.Plugins at the moment the run's `gate` job finally ran. When the
run queues for hours — it did — the two halves can be hours apart. Each half was green in its own
repository, against its own pin. Nobody ever ran the pair.**

On 2026-09-03 that pair was core `e7f1d699` (08:05Z) with Plugins `12500c9` (13:31Z), tagged
`memex-portal-ai:3.0.0-rc9.ci.7658`, and it answered **503 to every signed-in request** on
memex.systemorph.com for twenty minutes. This page records the property, the timeline, why every
guard missed it, the runtime fix that shipped, and the delivery gap deliberately left open.

## 🚨 The one sentence

`Promote` and `Verify every image shipped` green means the images EXIST; it says nothing about
whether the pair inside them can serve a signed-in request. Only booting the pair proves that, and
**nothing in CD does.** That remains true as this is written.

## The property

| | the core half | the Plugins half |
|---|---|---|
| chosen by | the commit `gate` resolved (push or reconcile) | `refs/heads/main` of MeshWeaver.Plugins, resolved ONCE in `gate` and threaded (#2622) |
| chosen when | the run is created | the `gate` job actually runs |
| recorded as | `<core short sha>` in the image tag `<core>-p<plugins>` | `p<plugins short sha>` in the same tag |
| tested in | core CI, against core's own code | Plugins CI, against the platform pin the Plugins repo carries |

Threading the Plugins sha once (#2622) made a run *internally* consistent — the portal image and the
module bundles come from one Plugins commit. It did not make the run *externally* consistent: a core
commit that queued for five hours is paired with a Plugins main five hours younger than anything
that core commit was ever tested with. The Plugins pin moves the other way too — the Plugins repo
pins a platform sha, and on the same afternoon its own tests went red in both directions as that
pin crossed the same boundary (Plugins #1281 asserts both arms by detecting the platform at
runtime).

The class is: **a promoted set whose two halves encode different contracts.** Sign-in is where it
surfaced, because sign-in is the first read every request makes.

### 🚨 The sharper form: what is TESTED and what is SHIPPED read different refs

Measured in MeshWeaver.Plugins on 2026-09-03, and this is the durable defect rather than the queue
delay:

| lane | how it picks its core half | what it was on 2026-09-03 |
|---|---|---|
| `ci.yml` — the **test** lane | `vars.MW_PLATFORM_REF \|\| env.MW_PLATFORM_REF`, and the env default is a **literal pinned sha** | `e7f1d699…` — frozen |
| `portal-ai-image.yml`, `portal-next-image.yml`, `log-watcher-image.yml` — the **image** lane | `vars.MW_PLATFORM_REF \|\| 'main'`, and the variable is **unset** | core's moving tip |

So the Plugins repo tests against a frozen core and ships against a moving one. The two agree only
while the pin happens to equal the tip. Every hour the pin lags, the tested pair and the shipped
pair are different pairs — and no run anywhere reports the difference, because each lane is
internally consistent and green. Core's own CD adds the mirror-image gap: it pins its core half at
run creation and resolves the Plugins half when `gate` runs.

A pin is the right tool for a test lane: it makes a suite reproducible. The defect is that nothing
compares the pin to what the image lane actually used, and nothing executes the pair the image lane
produced. A stricter pin does not close it; only executing the pair the image lane built does.

## The timeline (UTC, 2026-09-03)

| time | event |
|---|---|
| 08:05 | core `e7f1d699` merges (#3177, apps home groups). |
| 08:37 | CD run #7658 is created by the reconcile schedule, target `e7f1d699`. |
| 11:05 | core #3206 merges: the sign-in reads are anchored (`OnboardingMiddleware.LoadUserRoles` asks three anchored questions instead of one unanchored `nodeType:AccessAssignment content.accessObject:"<user>" scope:subtree`). |
| 11:45 | Plugins #1263 merges: `PostgreSqlPartitionedMeshQuery` REFUSES an unanchored query unless its shape is on the shrink-only grace list `unanchored-queries.allow` — and the sign-in shape is not on it, because core had just anchored it. |
| 13:31 | Plugins `12500c9` (#1252) is the Plugins main when run #7658's `gate` resolves it. The image `e7f1d699-p12500c9` = `ci.7658` is built, promoted, verified. |
| 13:32 | memex's self-updater patches the deployment to `ci.7658`. `DbVersionGate` refuses — `db_version=54 < expected 55` — and the new pods crash-loop while the old `ci.7632` pods keep serving. Invisible from the front door: the URL answers 200 the whole time (see [The Self-Update Schema Wall](/Doc/Architecture/SelfUpdateSchemaWall)). |
| 13:32–17:00 | Every refused boot dies as an unhandled `OperationCanceledException` → SIGABRT → a 666 MB `createdump`; 45 of them exceed the 30 Gi `memex-dumps` emptyDir and evict a pod (Plugins #1290 turns that into a clean exit 1). |
| 17:00 | Another session runs the migration by hand (`memex-migration-v55-manual`, the helm Job template with the new tag). `Database migration completed. Version: 55` at 17:06. |
| 17:02 | The 7658 pods pass the gate; the rollout scales the 7632 ReplicaSet to zero. From this second every signed-in request faults in `LoadUserRoles` with `UnanchoredQueryException` and the middleware answers 503 "This is a temporary problem on our side" (issue #637's designed answer for an *infrastructure* fault). The Store dies on `nodeType:PluginCatalog`; GitHub sync, instance sync, notifications, outbound mail, model-credit and free-text search fault the same way. Loki, over the six hours ending 18:34Z: **156 refusals** on the two pods that served (100 + 56), plus 32 more on the pod evicted earlier — and **18 identity 503s attributable to this cause** (4 + 14). |
| 17:05 | Reported: "memex.systemorph.com is completely down". Anonymous `curl` of `/`, `/Doc`, `/healthz` all answer 200 — the shell renders; only the signed-in read fails. |
| 17:20 | `kubectl set image` to `ci.7693` (core `e36f04c`, which contains #3206, with Plugins `2d32a175`). The surge pod stays `Pending`: every silos node is CPU-full, the pool is at its maximum, and the old 7632 pod is holding a node in `Terminating` — deadlocked in dispose, 3.5 cores, 30-minute grace. It is force-deleted (it was already out of the Service and would have been SIGKILLed at 17:32 anyway); the surge pod schedules within seconds. |
| 17:22:56 | Rollout complete. Zero refusals, zero 503s on the new pods. |

The roll cleared more than sign-in. On the sister portal, measured across the same change, the
cross-schema fan-out storm went from **1,917 slow 201-schema unions per pod per 30 minutes** to
**0–1 per pod per 10 minutes** — the anchored reads of #3206 removing the unions themselves, not
merely the refusals of them.

`ci.7693` is **up but unsealed**: its `Plugins: bake + seal` job failed on the #3175 one-producer
guard (`MeshWeaver.Markdown.Collaboration` composed as a module and still shipped in `/app`), as did
every promoted build since #7683. NodeTypes binding module types decline at adoption on it. The
first sealable set is the first CD run after Plugins #1268 lands.

## Why every guard missed it

- **Plugins CI was green** — its tests ran against the platform pin, and the pin was on one side of
  the boundary or the other, never both. #1281 fixed the tests to assert both arms; that fixes the
  *tests*, not the image.

  🚨 **But the class DID announce itself, hours earlier and for free, in a PR check.** Two access
  suites in the plugin repository asserted the pre-#3206 query literals and reddened trunk the
  moment the pin resolved core's tip. Replacing them with the post-#3206 literals then failed in
  the OPPOSITE direction, because the pin had meanwhile been set to a commit that predates #3206.
  **That mirror-image red — the same test failing both ways within a day — is the cheapest
  diagnostic signature this class has**, and it appears in a check long before it appears as a 503.
  Read as "our tests are wrong" it produces a literal-chasing fix; read as "the pin moves both
  ways" it names the boundary. The shape a test must take to survive a pin that moves both
  ways is its own subject, and is being written up separately.
- **Core CI was green** — `UnanchoredQueryCensusTest` and `SignInReadsAreAnchoredTest` (#3206) judge
  core's *current* callers with the *current* planner rules. They cannot see an older core paired
  with a newer planner, because that pair exists only in ACR.
- **The grace list could not protect the window by construction.** It is regenerated from the
  census of callers still issuing an unanchored shape and fails the build when a listed shape has no
  caller. The moment core anchored the sign-in read, the list had to drop it — while every image
  built from an older core half still issued it.
- **`Promote` and `Verify every image shipped` were green.** They prove existence and provenance,
  not behaviour. The seal was red, but for an unrelated reason (#3175), and nothing surfaces the
  seal at the tag.
- **The front door lied twice.** During the crash loop the old ReplicaSet answered 200; after the
  cutover the anonymous shell answered 200. The failing leg needed a *signed-in* request.
  `/api/og` and `/healthz` are negative controls here, exactly as
  [The `/api/content` 503](/Doc/Architecture/ContentRoute503) already records for its own leg.

## The fix that shipped, and the one that did not

**Runtime — a refusal is a CI invariant, never a production answer** (MeshWeaver.Plugins #1300).
`PostgreSqlPartitionedMeshQuery` gains an `UnanchoredQueryPolicy`. The default is `Refuse`
(fail-closed: a host that never heard of the property keeps the CI invariant). The production host
opts into `ServeAndReport` in its own committed `appsettings.json`, baked into the image so no chart
or ConfigMap can forget it, and a contract test discovers the Postgres hosts from source so the rule
cannot silently cover nothing. Under `ServeAndReport` an unlisted unanchored shape is *served* by
the fan-out and logged at **Error** naming the offender, which the red-log ticketing turns into an
incident. The trade this reverses: a hard refusal converted a *performance* hazard (a cross-schema
`UNION` contending on the lock manager) into a total *availability* outage on the request path.
After the change the allow-file governs two different things: in CI the Grace-versus-Refuse verdict,
unchanged; in production the log level (listed → Warning, unlisted → Error). It may still only
shrink.

**So a repeat of THIS fault is caught at the layer where it fired.** An image whose two halves
disagree about anchoring no longer takes sign-in down; it serves the query and files an incident.

### 🚨 What is still open: nothing executes the pair before it is promoted

A CD gate that booted the run's own image pair, signed in through DevLogin and asserted a
**cookie-carrying** `GET /` and `GET /Store` was written and proven against both controls — it
passes the pair that shipped fixed and fails `ci.7658` on the signed-in `GET /` — and was
**deliberately not adopted**: a new required job in front of `promote` was judged not worth the
standing cost and the first-run risk to the release lane. That decision is recorded here rather than
argued: it is a real trade, and the residual it leaves is real too.

**The residual, stated plainly.** `promote` and `Verify every image shipped` still attest only that
the images EXIST. No step boots the pair, and none signs in. The pair's two halves are still chosen
at different moments from different refs, so a *different* disagreement — one the runtime policy
above does not cover, because it is not about unanchored queries — would ship exactly as this one
did, and the first thing to notice would again be production. Anyone reopening this should know the
proof already exists as a standalone script with a two-control verification; it is the adoption that
was declined, not the mechanism.

## Reading the signals next time

```
kubectl logs <pod> -n memex -c memex-portal --since=20m | grep -c 'UNAVAILABLE for'
kubectl logs <pod> -n memex -c memex-portal --since=20m | grep -oE "no partition could be determined from '[^']{0,100}" | sort | uniq -c
az acr manifest list-metadata -r meshweaver -n memex-portal-ai --orderby time_desc --top 30 -o json   # tags read <core>-p<plugins>
git merge-base --is-ancestor <fix sha> <core half>                                                     # is the fix in this image's half?
```

- 🚨 **Filter the 503 count BY CAUSE or you will over-attribute it.** `UNAVAILABLE for` with
  `UnanchoredQueryException` is this class. `UNAVAILABLE for` with `DeliveryFailureException` is an
  unrelated transient transport fault — and the *older* pods, running before this outage began,
  logged 23 of those. An unfiltered `grep "UNAVAILABLE for"` therefore hands you pre-incident noise
  as incident evidence; the cause-filtered query is the one quoted above.
- A pending surge pod on `Insufficient cpu` with the pool at its maximum: look for a `Terminating`
  pod holding a node — the dispose deadlock in `ci.7632` is still open (the pod ran 3.5 cores for
  18 minutes after its containers were told to stop).
- Rolling forward to the newest promoted tag is the right triage when it carries the fix, but check
  the seal first: an unsealed tag serves sign-in and the Store while module-bound NodeTypes decline.

## Recorded, not fixed

- **A portal host burning CPU long past SIGTERM — cause UNDETERMINED.** What is measured, with
  timestamps, on the sister portal on 2026-09-03:

  | time | observation |
  |---|---|
  | 18:15:59 | the pod is ~20 min past SIGTERM at **5,705 millicores / 13,040 Mi**, `ready=true` |
  | 18:15:59 | `kubectl logs --since=3m` shows real work — a platform release broadcast for a newer build, plus deserialization warnings |
  | 18:22:51 | the same pod has logged **zero lines in 2 minutes**, and nothing matching shutdown/stopping/drain/dispose in the preceding 25 |
  | 18:23:11 | the pod is gone; **no CPU sample was ever taken during the silent window** |

  Two earlier readings of this were both wrong and are both **withdrawn**. The first called it a
  dispose deadlock and named core `71a41b231` (the IoPool drain-cancel fix, in `ci.7693` and not in
  `ci.7632`/`ci.7652`) as the candidate — falsified by a control arm: the pod's own sibling, **on
  the same image**, drained normally over the same minutes (238 m, then 70 m). The second called it
  "still serving, not deadlocked" — overstated, because a 3-minute log window does not establish
  that those lines were concurrent with the CPU sample.

  So the verdict is **undetermined between "still serving late" and "spinning silently"**, and the
  image pairing is unsupported in either direction. The number is worth keeping; the label is not.
  What would settle it is one CPU sample taken during a silent window, or a stack.

  🚨 **And the obvious way to get that stack does NOT work here.** `kubectl debug` cannot attach an
  ephemeral container to a pod that already has a `deletionTimestamp`: the API accepts the command
  and prints its "Targeting container" line, and then creates nothing —
  `ephemeralContainerStatuses` stayed empty on both attempts. The published recipe for profiling a
  live portal pod therefore does not work on precisely this class of specimen. To capture a
  terminating host you must attach the probe **before** deletion is requested, or go in from the
  node. That, not the grace window, is why neither specimen produced a stack.

  🚨 **The grace period hides the whole shape.** `terminationGracePeriodSeconds` is 1800, so a host
  that burns CPU for twenty-nine minutes past SIGTERM and one that stops in two look identical from
  outside: the pod disappears, the rollout succeeds, nothing is logged. The only tell is CPU on a
  pod whose `deletionTimestamp` is set, and nothing watches that. The cost is real — during this
  incident's own rollout such a pod held the last schedulable node, which is why the surge pod
  stayed `Pending` and the recovery needed a force-delete.

- The silos node pool has no headroom for one surge pod when both portals are at full replica count.
  A rollout that must surge waits for a termination, and a wedged termination waits for the grace
  period.
- The self-updater cannot cross a schema bump on its own (the migration is a helm-revision Job) —
  [The Self-Update Schema Wall](/Doc/Architecture/SelfUpdateSchemaWall).

## See also

- [Unanchored Security Reads](/Doc/Architecture/UnanchoredSecurityReads) — why the sign-in fold reads
  the way it does, and the census this outage was the runtime twin of.
- [The Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — the same class at the source
  level: two halves that must land in an order, and the gate that holds the deleting half last.
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — all-or-nothing
  publication; verify the image, never the tick.
- [The Self-Update Schema Wall](/Doc/Architecture/SelfUpdateSchemaWall) — why the roll to `ci.7658`
  wedged silently for three and a half hours before it could fail loudly.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — which greens mean what.
