---
Name: Addressed Notifications
Category: Architecture
Description: Why the notification bell UNIONed every partition schema, the 2026-09-03 measurements of where notifications actually lived (97% of them nowhere near their reader) and of what that cost, and the design that fixed it — a notification is ADDRESSED, delivered to its addressee's partition, so the bell reads one pinned schema per bell instead of 201.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h16v12H5.5L4 18.5z"/><path d="M8 9h8"/><path d="M8 12.5h5"/></svg>
---

# Addressed Notifications

**The bell was the single largest cross-schema fan-out on the platform.** Measured twice, a day
apart, on memex-cloud:

| When | What was measured | Result |
|---|---|---|
| 2026-09-02, 8 pods | `[CrossSchema] SLOW` unions per 5 min | **444**, avg **4.0 s** each, Postgres 94–98 % CPU |
| 2026-09-03 16:47–16:58, ONE **idle** replica (0.13 cores, 2 GB) | one bell render | **4 476 rows across 201 of 201** schemas, **9–10 s**, filtered to **0 rows** in memory; ~60 such lines/min, on each of 7 serving replicas; `memexaks-pg` 87–91 % avg / 96–98 % max CPU |

The two agree on mechanism and differ only in magnitude and in what they rule out: the second was
taken on an idle pod, so it is not a GC artefact, and time-to-first-row ≈ total, so the cost is the
SCAN and not the caller's consumption. Issues: Systemorph/MeshWeaver#3156 (the write side),
[#3216](https://github.com/Systemorph/MeshWeaver/issues/3216) (the platform bell nobody could read),
[#3238](https://github.com/Systemorph/MeshWeaver/issues/3238) (the fan-out re-measured), and the
census in [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination), whose
plan 1 this is.

> **Status: SHIPPED.** §§1–3 describe what was wrong and what replaced it; §4 records the rulings
> taken and by what argument; §6 records the migration ruling, which was *not* to migrate.
> 🚨 **§3 corrects this page's original anchor**: the `namespace:A|B` alternation it proposed is
> measurably NOT the shape that reaches `Admin`. Two separately pinned reads are.

## 1. What the bell issues today, and why it cannot be anchored

The bell and its panel both bind to one declared constant — `NotificationQueries.Bell` in
`src/MeshWeaver.Blazor.Portal/Components/NotificationQueries.cs` (MeshWeaver.Plugins):

```text
nodeType:Notification sort:CreatedAt-desc
```

No `path:`, no `namespace:`, no `partitions:all` — so the Postgres router cannot pin it, and
`PostgreSqlPartitionedMeshQuery` UNIONs every row of `public.searchable_schemas`. Three call sites
issue that shape: `NotificationCenter.razor:45` and `NotificationCenterPanel.razor:247` (once per
CIRCUIT, live), and `NotificationTriageService.cs:75` (once per PROCESS, live). The
`portal-next` React client carries a fourth copy of the same literal
(`clients/portal-next/src/client/NotificationCenter.tsx:35`), issued over the API as a snapshot.

It cannot be anchored because **the notification is not written where its reader is**.
`NotificationService.CreateNotification` (`src/MeshWeaver.Graph/NotificationService.cs:46`) writes
at `{mainNodePath}/_Notification/{id}` — a satellite of the *entity the notification is about* — and
`mainNodePath` is chosen by whichever emitter raised it. Anchoring the bell to
`namespace:{viewer}/_Notification` would silently drop every notification whose entity lives
elsewhere: a truncation, which is the fail-open shape [Unanchored Security Reads](/Doc/Architecture/UnanchoredSecurityReads)
and #2011 exist to warn about.

### The guard state — measured 2026-09-03

MeshWeaver.Plugins #1231 made fan-out **opt-in**: a query that names no partition and did not ask to
span them is refused at runtime with an `UnanchoredQueryException` faulting the caller's stream.
Between #1231 and #1263 that made the bell a **hard failure**, not a slow query. MeshWeaver.Plugins
#1263 (merged 2026-09-03 13:45Z) then added a shrink-only grace list,
`src/MeshWeaver.Hosting.PostgreSql/unanchored-queries.allow`, whose **first entry** is:

```text
# NotificationQueries.Bell (MeshWeaver.Blazor.Portal — every circuit's bell, live) and
# NotificationTriageService.cs:75 (MeshWeaver.Notifications.Channels). Plan 1 of
# Doc/Architecture/CrossSchemaFanOutElimination — deliver to the RECIPIENT's partition — removes both.
nodeType:Notification scope:Exact
```

So between #1263 and this change the bell worked and still fanned out, served with a
`[FanOut] GRACE` warning naming the offender. The list may only shrink —
`scripts/check-unanchored-queries.py --base-ref` fails a PR that adds a line, and
`UnanchoredQueryAllowFileTest` fails when a listed shape has no caller left. That gave this work its
acceptance test for free: **delete that line and stay green.** ✅ **The line is gone.** Both callers
it named are now served: the bell because it is anchored, the triage watch because it DECLARES its
fan-out (`MeshWideQuery`) rather than being grandfathered — the allow list is for debt, and a
declared read is not debt.

### Access-narrowing does not rescue it

MeshWeaver.Plugins #983 already narrows a fan-out to the partitions the caller can read (the
per-schema `public.partition_access` clause is constant for a branch, so a branch the caller cannot
read can only ever produce zero rows and is dropped). The bell still measured "199 of 199 partition
schema(s)" because the two readers that trip it are exactly the ones access-narrowing cannot help:
a **platform admin's circuit**, which can read nearly every partition, and the **triage watcher**,
which is deliberately mesh-wide and runs as system. Anchoring is the only lever that works for both.

## 2. Where notifications actually live — the 2026-09-03 measurement

Sampled on memex.meshweaver.cloud via `nodeType:Notification sort:lastModified-desc limit:200` —
the newest 200 rows, spanning **2026-08-30 03:05Z → 2026-09-03 10:06Z**:

| Rows | Path shape | Emitter | Partition it lands in |
|---:|---|---|---|
| 124 | `Plugins/{package}/_Notification/{id}` — *"Update available: Store"* | `PackageUpdateReconciler.Notify` (core) | `Plugins` |
| 60 | `{space}/_Notification/{id}` — *"Startup import failed: Agent"* | `StaticRepoImporter.NotifyStartupFailure` (core) | the importing source's partition (`Doc`, a Space id) |
| 12 | `{partition}/…/_Thread/{t}/_Notification/{id}` — *"…is ready"* | `ThreadExecution.EmitCompletionNotification` (Plugins) | the thread's **context** partition |
| 4 | `Hosting/Instance{Action,Request}/_Notification/{id}` | instance lifecycle | `Hosting` |

Only **six** rows in that whole window live in a user's own partition — `rbuergi/_Notification/{id}`,
*"You've been given access to …"*, from `AccessGrantNotifier`. So on the live mesh, **~97 % of
notifications sit in a partition that is not their reader's**, and the dominant class is not
user-addressed at all: it is operator noise about plugin updates, re-emitted on every reconcile poll
(the same package appears at 04:46, 07:22, 08:15 and 10:06 on one day).

### Who can see one today

There is **no `SatelliteAccessRule` registered for `Notification`** — the registered set
(`GraphConfigurationExtensions.cs:121-124`, `KernelNodeType`, `ActivityNodeType`,
`UserActivityNodeType`, `ActivityLogSegmentNodeType`) does not include it. So `RlsNodeValidator`
falls through to the ordinary **path-based** permission fold on the notification's own path
(`RlsNodeValidator.cs:184-188`). A notification is therefore visible to *whoever can read the node it
was written under* — which is why an "Update available: Store" notification under `Plugins/Store`
reaches every viewer who can read the plugin catalog, and why one census line reported 4 203 rows.

🚨 [Notifications](/Doc/Architecture/Notifications) states that "access control resolves from the main
node"; that is **aspirational** — the rule that would make it true is not registered. The addressed
model below makes the path-based answer the *correct* answer, so no new rule is needed.

### 2b. 🚨 The bell has never shown a single platform-admin notification

`Admin` is **excluded from `public.searchable_schemas`**, which is the registry the cross-schema
fan-out UNIONs. Core says so twice, in the code that had to work around it:

> *"The Admin special case is GONE because `path:` anchoring is exactly what it needed: `Admin` is
> excluded from `searchable_schemas`, so the old namespace-only query never reached `admin.access`
> and platform-admin grants silently never loaded."*
> — `src/MeshWeaver.Mesh.Contract/Security/PermissionEvaluator.cs:882-885`

> *"`Admin` is excluded from cross-schema search, so a platform-admin grant is reachable ONLY
> through a path-anchored read."*
> — `src/MeshWeaver.Mesh.Contract/Security/SecurityQueries.cs:253-255`

**So the bell — an unanchored fan-out — cannot read `admin.notifications` at all.** Measured both
ways on 2026-09-03: the unanchored `nodeType:Notification sort:lastModified-desc limit:200` listing
returned **zero** rows whose first segment is `Admin`, while an anchored
`nodeType:Notification namespace:Admin scope:descendants` returned them immediately —

```text
Admin/_Notification/Oqk-mQPcp0GujvAQj59IlA   "Startup completed with 101 error(s)"   2026-09-03T10:00:25Z
Admin/_Notification/sunOdJ5zx0mdiwld6Mn1yw   "Startup completed with 103 error(s)"   2026-09-03T08:11:11Z
Admin/_Notification/gKVBJ6A4Okm0c-nhukCFNg   "Startup completed with  70 error(s)"   2026-09-03T07:13:27Z
```

— timestamps well inside the unanchored listing's own window (which reached back to 2026-08-30).

Every notification `StartupErrorNotifier`, `RegistryUpdateReconciler`, `ModuleDiscoveryService`,
`NodeTypeEnrichmentHelpers` and `ContentIndexingActivity` addresses to platform admins is therefore
**written, versioned, and shown to nobody**. It is the same defect `PermissionEvaluator` fixed for
grants — an unanchored read that cannot reach the one schema its answer lives in — still live on the
bell. Anchoring the bell does not merely make it cheaper: **the `Admin` leg is what finally
delivers those notifications.** Filed as #3216.

### The emitter census (core), as of 2026-09-03

| Emitter | `recipient` | `mainNodePath` | Lands in |
|---|---|---|---|
| `AccessGrantNotifier.cs:89` | the grantee | `recipient` | **the grantee's partition** ✅ |
| `StartupErrorNotifier.cs:104` | `null` | `"Admin"` | `Admin` ✅ |
| `RegistryUpdateReconciler.cs:462` | `null` | `"Admin"` | `Admin` ✅ |
| `NodeTypeEnrichmentHelpers.cs:2373` | `null` | `"Admin"` | `Admin` ✅ |
| `ModuleDiscoveryService.cs:789` | — | `Admin/_Discovery/{owner}.{repo}` | `Admin` ✅ |
| `PackageUpdateReconciler.cs:271` | — | `Plugins/{pkg.Id}` | `Plugins` ❌ |
| `StaticRepoImporter.cs:2206` | — | the source's partition | `Doc` / a Space ❌ |
| `CompileFailureNotifier.cs:24` | `RequestedReleaseBy`, else the ambient user, else `null` | `recipient ?? nodeTypePath` | the requester's partition, else the **failing type's** partition ❌ |

And in MeshWeaver.Plugins: `ApprovalActions.cs:65,127` address the approver / the requester ✅;
`EmailInboundProcessor.cs:440` addresses the mailbox owner ✅;
`ContentIndexingActivity.cs:314` addresses `Admin` ✅;
`ThreadExecution.cs:3565` passes `recipient: threadPath.Split('/').First()` ❌ — see §5.

## 3. The design: a notification is ADDRESSED

**One invariant.** A `Notification` has exactly one **addressee**, and it is delivered into that
addressee's partition:

```text
{addressee}/_Notification/{id}       MainNode = {addressee}
```

where `{addressee}` is either a **user partition** (a person's bell) or **`Admin`** (the platform
operators' bell). The entity the notification is *about* stays a reference **inside the content** —
`Notification.TargetNodePath`, which every emitter already sets and which the bell, the panel and the
React client already group by (`TargetNodePath ?? MainNode ?? Path`). So the grouping, the click
target and the badge's distinct-source count all survive the move unchanged.

Three properties follow, and they are the point:

- **The bell names its partitions — as TWO separately pinned reads, one per bell.**

  ```text
  namespace:{viewer}/_Notification nodeType:Notification sort:CreatedAt-desc
  namespace:Admin/_Notification    nodeType:Notification sort:CreatedAt-desc   ← only for a global admin
  ```

  Both are built by `NotificationService.BellQuery(addressee)` in core, so the Blazor bell, the
  Blazor panel and the shape censuses all read one definition. `NotificationQueries.For(viewer,
  viewerIsGlobalAdmin)` decides which legs a viewer gets; `NotificationFeed.ForViewer` merges them.

  🚨 **This page originally proposed a single alternation,
  `namespace:{viewer}/_Notification|Admin/_Notification`, and that was wrong — measurably.** The
  alternation does classify as *anchored* (`QueryRouteClassifier` says so, via
  `ExtractNamespacePatterns`), which is what the first measurement checked. But classification is
  not routing. The parser folds a SINGLE concrete `namespace:` into `ParsedQuery.Path`, and only
  then does `PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition` pin the query to one schema and
  skip the fan-out machinery entirely. An alternation leaves `Path` null, so it takes the FAN-OUT
  route, where namespace narrowing is applied as an **intersection** with the schema list from
  `public.searchable_schemas` — deliberately, and the code says why:

  > *"Resolving the derived names on their own would be one round-trip cheaper and is deliberately
  > not done: it bypasses `searchable_schemas`, whose ExcludedSchemas (auth, admin, …) would then
  > become newly visible to a namespace-anchored query."*
  > — `PostgreSqlPartitionedMeshQuery`, the namespace-anchored narrowing

  `Admin` is one of those excluded schemas. **So the alternation would have dropped `admin` again,
  silently, and #3216 would have read as fixed while changing nothing.** Pinned by
  `NotificationBellLegsTest`: `ResolvePinnedPartition` returns `rbuergi` and `admin` for the two
  legs and **null** for the alternation. This is exactly why `SecurityQueries.PartitionAssignments`
  is spelled with a single `path:` and why `PermissionEvaluator` combines a partition leg with a
  root leg instead of asking for both at once.

- **Visibility becomes correct by path.** With the addressee as the first segment, the ordinary
  path-based fold answers exactly "the addressee, plus whoever can read their partition" — no
  `SatelliteAccessRule` required, and no more plugin-update notifications leaking into every
  catalog reader's bell.
- **Mark-as-read writes into the reader's own partition** instead of into whatever partition the
  entity happened to live in, which is what makes the write RLS-trivial too.

### `Notification.Recipient`

Add the addressee to the content record (`src/MeshWeaver.Mesh.Contract/Notification.cs`) even though
the path already carries it. It is what makes the invariant **checkable** — a census test and a
create-time validator can both ask the node rather than parse its path — and it is what a migration
needs in order to be re-runnable. Additive, defaulting to `null` on legacy rows.

### `Dispatch` owns delivery; `CreateNotification` stops choosing

`NotificationService.Dispatch` already takes `recipient` and is the documented single entry point.
It should compute the delivery path itself:

```text
deliveryPath = recipient (a user partition)  ??  "Admin"
```

and keep the current `mainNodePath` parameter as *the entity*, feeding `TargetNodePath` as it does
today. `CreateNotification` keeps its signature for the in-mesh callers that use it directly
(`Approvals/Approval/Source/ApprovalActions.cs` is compiled in the mesh, invisible to `dotnet build`),
and gains an optional addressee so core's own direct callers can be moved without a breaking change.

## 4. The rulings, and how each was decided

These were product decisions, not implementation details — each one changes **who sees what**.

### 1. An operator notification is addressed to `Admin`, collectively — one row, not one per admin

"Update available: Store", "Startup import failed: Agent", "Type X is serving a fallback page", a
package feed that could not be reconciled: these address nobody in particular and, before the
change, were visible to whoever could read the plugin record or the space. They are now addressed to
`Admin`, whose read scope is exactly `hub.IsGlobalAdmin()` — an `AccessAssignment` granting
`Permission.All` in the `Admin/_Access` namespace.

**Why `Admin` and not a fan-out to each admin.** The alternative — write one copy per platform admin,
into each of their partitions — was rejected on three grounds, and the third is a security one:

| | `Admin`, one row | Per-admin fan-out |
|---|---|---|
| Write cost | one row per event | one row per event **per admin** |
| A newly promoted admin | sees the history | sees nothing before their promotion |
| A **demoted** admin | loses the bell immediately (the leg disappears, and the fold refuses the rows) | **keeps every copy already written into their own partition** — a standing disclosure with no revocation path |
| Enumeration | none | the admin set must be resolved at WRITE time, on the boot-error path |

**The cost of the choice, stated:** *read* is shared. One operator marking a platform notice read
marks it read for everyone. That is the right semantics for a shared operations inbox and the wrong
one for personal mail — which is why a personal notification is never addressed to `Admin`, and why
`AccessGrantNotifier`, the approvals and the thread-completion notice all keep naming a person.

The visible consequence: operator notices **leave ordinary users' bells** (184 of the sampled 200
rows were in this class). That is the intended behaviour change, not a truncation — nobody could act
on them, and they are the reason a catalog reader's bell filled with plugin-update noise.

### 2. A non-admin cannot read the platform bell — now established by test, not by assertion

The earlier open item was the half of the claim that needed a second identity: a platform admin can
read `Admin/_Notification`, but can anyone else? `NotificationAddressingTest` answers it with two
identities on one mesh — a platform admin (`Admin` role at scope `Admin`) and a user who owns their
own partition and nothing else:

- the admin's anchored read returns the row;
- the same query under the other identity returns a snapshot **without** it;
- and the permission fold is asserted directly, so a regression reports a *verdict* rather than an
  absence: `GetEffectivePermissions(notificationPath, plainUser)` has no `Read`.

🚨 **The negative was proven non-vacuous.** Granting that user `Viewer` on the `Admin` partition was
staged deliberately and the test FAILED — *"Expected the observable to emit a value matching the
predicate … leaking it to every user is a WORSE outcome than the admin seeing nothing"*. An
assertion that cannot fail is not an assertion, and this one can.

Belt and braces, because the asymmetry deserves both: the platform leg is also **not issued at all**
unless `hub.IsGlobalAdmin()` answers positively, and that gate fails CLOSED (seeded `false`, an
error or a never-answering fold leaves it `false`). RLS is the boundary; the gate decides what is
even asked for, and keeps a non-admin's bell down to a single schema.

### 3. The legacy rows are neither migrated nor deleted — they age out. See §6.

### 4. A space-scoped broadcast does not survive as a concept

The invariant says a notification has one addressee. Anything wanting "everyone who can see X" must
address the users explicitly or go to `Admin`. Nothing in either repository emitted such a broadcast,
so this is a ruling about the *future* surface.

## 5. Two defects the measurement exposed (not #3156, but caused by the same gap)

Both are filed: MeshWeaver.Plugins#1275 and #3213.

- **The ChatReady recipient is a partition name, not a user.** `ThreadExecution.cs:3565` computes
  `recipient = threadPath.Split('/').First()`, commented as "the thread lives under its owner's
  partition". The live data says otherwise: threads live under whatever node they were started
  from, so for `AgenticEngineering/ContextEngineering/_Thread/hello-87f0` the "recipient" is the
  **space** `AgenticEngineering`. `NotificationService.ReadSettings` then reads notification
  preferences for a non-user (falls back to defaults) and `MaybeSendEmail` resolves a space node as
  a `User` (no `Email`, so the mail is silently skipped). The real addressee is the thread node's
  `CreatedBy` — which also makes the migration in §6 derivable.
  `NotificationTriageService.cs:104` derives its recipient the same way and escalates into
  `{space}/_Triage`, with the same defect. **MeshWeaver.Plugins#1275.**
- **Plugin-update notifications are unbounded.** `PackageUpdateReconciler.Notify` raises a fresh
  `Notification` node on every reconcile that still sees an update — measured at four rows for the
  same package in one day, 124 of the newest 200 rows overall. There is no "already told you"
  suppression. This is the bell's row count, independent of where the rows live. **#3213.**

## 6. The migration: **neither move nor delete — the legacy rows age out**

This was the ruling asked for, and the answer is the third option. Legacy `_Notification` rows stay
exactly where they are; nothing moves them and nothing deletes them; the anchored bell simply stops
reading the partitions they sit in.

**Why not MOVE them.** The addressee is derivable for every class in the census, so a Repair vN
migration was possible on paper:

| Legacy shape | Derivable addressee |
|---|---|
| `…/_Thread/{t}/…/_Notification/{id}` | the thread node's `CreatedBy` |
| `Plugins/{pkg}/_Notification/{id}` | `Admin` |
| `{space}/_Notification/{id}` (startup import) | `Admin` |
| `Hosting/Instance*/_Notification/{id}` | `Admin` |
| `{user}/_Notification/{id}` where `{user}` is a user partition | already addressed |

But look at what it would buy. Rows 2–4 are operator noise, and **ruling 1 deliberately removes that
class from ordinary users' bells** — migrating it would faithfully preserve a visibility we just
decided was wrong. Row 1, the thread-completion notice, is the only genuinely user-facing legacy
class, it was **6 % of the sampled population**, and it is the single most perishable kind of
notification there is ("your response is ready", for a thread the person was watching). So a
migration of ~10⁵ rows across 201 schemas, run as a startup `Job` whose failure stops **every**
portal from serving (`DbVersionGate`), would buy back a few days of already-read notices. That trade
is not close.

**Why not DELETE them either.** It is cheaper, but it is irreversible, it destroys the versioned
history of every notification ever raised, and it buys nothing the bell can see anyway: the anchored
bell ignores those partitions whether the rows are there or not. Deleting data to make a query
faster, when the query has already stopped reading it, is cost with no benefit.

**What it costs to leave them.** Dead rows in per-partition `notifications` tables that nothing
reads. They are small, they are already paid for, and a general retention pass — which notifications
want on their own merits, addressed or not — reclaims them later without a schema migration.

🚨 **This is not the #2011 truncation.** That shape is a read that silently returns FEWER rows than
the caller's question implies. Going forward, no notification is dropped: every notification is
delivered INTO its addressee's partition, so a notification *about* an entity anywhere in the mesh
still reaches the reader's own bell — which is precisely what the addressing change buys and why the
write side had to land with the read side. What changes for pre-existing rows is a stated,
one-time, announced consequence of ruling 1, not a silent short read.

🚨 And if a future pass does move or remove them: **never a raw `psql UPDATE`** — it bypasses the
workspace cache. It is a Repair vN migration in `src/Memex.Database.Migration/Migrations/`
(MeshWeaver.Plugins) or a `MoveNodeRequest` pass; see
[Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).

## 7. What shipped

The write side and the read side landed as **one change set**, because each alone leaves the other
broken or dangerous: anchoring first would truncate the bell, and addressing first would leave the
platform bell unreadable and the fan-out in place.

**Core (`Systemorph/MeshWeaver`)**

1. `Notification.Recipient` — the addressee, on the content, so the invariant is checkable without
   parsing a path.
2. `NotificationService` gains `PlatformAddressee`, `ResolveAddressee`, `DeliveryNamespace` and
   `BellQuery` — one definition of where a notification goes and one of how it is read back.
   `CreateNotification` takes the addressee and writes at `{addressee}/_Notification/{id}` with
   `MainNode = {addressee}`; `Dispatch` resolves it once, and `recipient: null` means the PLATFORM.
   The addressee stays OPTIONAL on `CreateNotification` (falling back to the main node's partition),
   because that method is public surface which in-mesh source compiles against at RUNTIME — making
   it mandatory would break callers no compiler in this repository can see.
3. Core's mis-addressed emitters re-addressed to `Admin`: `PackageUpdateReconciler` (an update only
   an admin can apply — 124 of the newest 200 rows), `StaticRepoImporter.NotifyStartupFailure`, and
   `NodeTypeCompileParkRegistry`'s System-driven leg, which used to file a compile failure nobody
   asked for under the failing TYPE "so it is still visible — in every per-user bell that can read
   the type". `ModuleDiscoveryService` now says `Admin` explicitly instead of inheriting it.
4. 🚨 **A bug found on the way:** `NodeTypeEnrichmentHelpers.ReportStuckOverlayToAdmins` composed
   `NotificationService.Dispatch(...)` and **never subscribed it**. `Dispatch` is a cold observable,
   so the "instance stuck on a fallback page" notification was never written — while the log line
   beside it said "platform admins notified". Two independent silences over one signal: the row was
   never created, and #3216 meant the bell could not have read it if it had been.
5. `NotificationAddressingTest` — the addressing rule, the pinning property, and the access boundary
   (§4 ruling 2), with its negative control.

**Plugins (`Systemorph/MeshWeaver.Plugins`)**

6. `NotificationQueries` becomes `Bell(viewer)` / `Platform` / `For(viewer, isGlobalAdmin)` over
   core's `BellQuery`; `NotificationFeed.ForViewer` is the single live feed the bell badge and the
   panel both bind to, so they cannot drift on which partitions get read.
7. The `portal-next` React client anchors to the viewer. It deliberately does **not** read the
   platform bell: it has the viewer's id but no admin verdict on the wire, so the leg is omitted
   rather than issued unconditionally — the fail-closed choice, and it loses nothing, because the
   old fan-out could not reach the `admin` schema either.
8. `NotificationTriageService` DECLARES its mesh-wide watch (`MeshWideQuery`) instead of being
   grandfathered — a genuine process-wide watch, one live subscription per process — and reads
   `Notification.Recipient` instead of re-deriving the addressee with a per-notification query.
9. The `nodeType:Notification scope:Exact` line **leaves `unanchored-queries.allow`**, which was
   this work's acceptance test; `UnanchoredQueryAllowFileTest`'s census moves both readers from
   REFUSED to SERVED, and `ShellQueryShapesTest`'s `KnownFanOuts` loses its Bell entry.
10. `NotificationBellLegsTest` pins both legs to one partition each — and pins that the ALTERNATION
    does not pin, which is why the legs are separate (§3).

**Not in this change set, and why**

- The migration (§6): ruled out, with the argument recorded.
- **Narrowing the triage watch** to only the users who authored a `NotificationRule` — each leg
  anchored to that user's own partition — would remove the last mesh-wide notification read. It is a
  real improvement and a separable one: it changes what that service WATCHES, not where the bell
  reads, and the watch is one subscription per process against the bell's one per circuit per write.
- Surfacing `isGlobalAdmin` to the React client so it can carry the platform leg too (item 7).

## 8. What is still not established

- ~~The other half of the Admin claim — that a NON-admin cannot read `Admin/_Notification`.~~
  **Established** (§4 ruling 2), by a two-identity test whose negative was proven able to fail.
- **The production effect is not measured yet, and will not move on its own.** memex-cloud's
  self-update is PAUSED (`Admin/UpdatePolicy` policy=None since 2026-09-02) and its serving image
  predates the change, so the 60 SLOW-lines/min figure stands until that deployment is unpaused.
  What is established is the mechanism and the shape: the bell's 201-of-201 union is replaced by one
  pinned schema per bell, and `ResolvePinnedPartition` is asserted to return a single partition for
  each leg. The residual per-write cost is the triage service's declared watch — one subscription
  per process, not one per circuit.
- **Whether any deployment outside memex-cloud has a materially different distribution** — one mesh
  was sampled, on 2026-09-03.
- **The total legacy population size.** The listing API caps at 200 rows, so the class *proportions*
  are measured and the absolute count (~10⁵, estimated) is not. It no longer gates a decision: §6
  leaves the rows in place either way.

## Cross-references

- [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination) — the census this is plan 1 of.
- [Notifications](/Doc/Architecture/Notifications) — the pipeline as it works today.
- [Unanchored Security Reads](/Doc/Architecture/UnanchoredSecurityReads) — why truncating a read is worse than paying for it.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per partition, and how a satellite is routed.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the bell is a query and mark-as-read is a stream.
