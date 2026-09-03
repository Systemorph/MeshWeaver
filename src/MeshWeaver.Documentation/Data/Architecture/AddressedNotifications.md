---
Name: Addressed Notifications
Category: Architecture
Description: Why the notification bell UNIONs every partition schema, the 2026-09-03 measurement of where notifications actually live (97% of them nowhere near their reader), and the design that fixes it — a notification is ADDRESSED, delivered to its addressee's partition, so the bell reads two schemas instead of 199.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h16v12H5.5L4 18.5z"/><path d="M8 9h8"/><path d="M8 12.5h5"/></svg>
---

# Addressed Notifications

**The bell is the single largest cross-schema fan-out on the platform** — 444 `[CrossSchema] SLOW`
199-schema unions per five minutes across eight pods on memex-cloud, averaging 4.0 s each while
Postgres sat at 94–98 % CPU ([Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination),
the 2026-09-02 census). This page is plan 1 of that census, worked out: the measurement of where
notifications actually live today, the design that lets the bell name its partition, the rulings the
change needs before it can land, and the migration it implies.

Issue: Systemorph/MeshWeaver#3156.

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

So **today the bell works and still fans out**, served with a `[FanOut] GRACE` warning naming the
offender. The list may only shrink — `scripts/check-unanchored-queries.py --base-ref` fails a PR that
adds a line, and `UnanchoredQueryAllowFileTest` fails when a listed shape has no caller left. That
gives this work its acceptance test for free: **delete that line and stay green.**

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

- **The bell can name its partitions.** `namespace:{viewer}/_Notification|Admin/_Notification
  nodeType:Notification sort:CreatedAt-desc` — a namespace alternation, which the parser keeps as an
  exact-membership filter and `PostgreSqlPartitionedMeshQuery.ResolveNamespaceAnchoredPartitions`
  narrows to **two schemas**. It passes the planner's `IsSufficientlySpecified` through
  `ExtractNamespacePatterns()`, so it needs no `partitions:all` and earns no allow-file line.

  🚨 **Measured, not reasoned** — fed to `QueryRouteClassifier` (the test-side reproduction of the
  planner's own `IsSufficientlySpecified || ResolvesByRoutingHint` gate) on 2026-09-03:

  | Query | Verdict | `sufficient` |
  |---|---|---|
  | `nodeType:Notification sort:CreatedAt-desc` (today's bell) | **Refused** | `False` |
  | `namespace:{viewer}/_Notification\|Admin/_Notification nodeType:Notification sort:CreatedAt-desc` | **Anchored** | `True` (via `ExtractNamespacePatterns`) |
  | `namespace:{viewer}/_Notification nodeType:Notification sort:CreatedAt-desc` | **Anchored** | `True` (the parser folds a single namespace into `Path`) |
  | `path:{viewer}\|Admin scope:subtree nodeType:Notification sort:CreatedAt-desc` | **Anchored** | `True` (via `Paths`) |

  The last row is the fallback if the flat `{addressee}/_Notification/{id}` shape cannot be reached
  for some class — a two-partition subtree read, still two schemas, but it re-admits anything written
  anywhere under the viewer's partition. The alternation is the tighter of the two and is what the
  invariant makes possible.
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

## 4. The rulings this needs before it can land

These are product decisions, not implementation details, and each one changes **who sees what**:

1. **Who is the addressee of an operator notification?** "Update available: Store" and "Startup
   import failed: Agent" address nobody today; they are visible to whoever can read the plugin
   record or the space. Under the invariant they go to `Admin` — visible to platform operators only.
   That is almost certainly right (only an operator can act on either), but it *removes* them from
   ordinary users' bells, and 184 of the sampled 200 rows are in this class.
2. **Is `Admin/_Notification` actually readable by a platform admin whose own partition is
   elsewhere?** Three separate comments assert that the Admin partition's RLS scopes it to platform
   admins (`StartupErrorNotifier.cs:32`, `RegistryUpdateReconciler.cs:437`,
   `NodeTypeEnrichmentHelpers.cs:2361`) — **there is no test or rule in this repository that
   establishes it**. The second bell leg depends on it entirely. Verify before anchoring: a wrong
   answer here is a silently empty admin bell.
3. **Do the legacy rows get migrated, or dropped?** See §6.
4. **Does a space-scoped broadcast survive as a concept?** The invariant says no: a notification has
   one addressee. Anything wanting "everyone who can see X" must address the users explicitly or go
   to `Admin`. Nothing in either repository emits such a broadcast today, so this is a ruling about
   the *future* surface, not a migration of an existing one.

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

## 6. The migration

Delivery is a *write-path* change; the ~10⁵ existing rows do not move on their own, and the bell
cannot be anchored while they matter. The addressee is **derivable** for every class in the census,
which is what makes a one-pass Repair migration possible rather than a guess:

| Legacy shape | Derived addressee |
|---|---|
| `…/_Thread/{t}/…/_Notification/{id}` | the thread node's `CreatedBy` |
| `Plugins/{pkg}/_Notification/{id}` | `Admin` |
| `{space}/_Notification/{id}` (startup import) | `Admin` |
| `Hosting/Instance*/_Notification/{id}` | `Admin` |
| `{user}/_Notification/{id}` where `{user}` is a user partition | unchanged — already addressed |

🚨 **Never a raw `psql UPDATE`** — it bypasses the workspace cache. This is a Repair vN migration in
`src/Memex.Database.Migration/Migrations/` (MeshWeaver.Plugins), or a `MoveNodeRequest` pass; see
[Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).

**The cheaper alternative deserves a hearing.** Notifications are ephemeral by construction, 97 % of
the population is operator noise re-emitted hourly, and the bell shows the newest first. Deleting
every `_Notification` row that is not in an addressee partition — a Repair that *removes* rather than
moves — is a smaller, faster, fully reversible-by-time operation, and it is what ruling 3 should
decide between. Moving ~10⁵ rows across 199 schemas on a live portal is the more expensive option and
buys back a few days of already-read operator notices.

## 7. Order of work

1. **Core** — `Notification.Recipient`; `Dispatch` computes the delivery path; core's own
   mis-addressed emitters (`PackageUpdateReconciler`, `StaticRepoImporter`, `CompileFailureNotifier`'s
   System-driven leg) re-addressed to `Admin`; a census test that pins each core emitter's delivery
   partition the way `SecurityQueryShapesTest` pins query shapes.
2. **Plugins** — `ThreadExecution`'s addressee becomes the thread's `CreatedBy` (§5);
   `NotificationTriageService` declares itself mesh-wide (`MeshWideQuery`) rather than being
   grandfathered, which removes half of the allow-file entry on its own.
3. **The migration** (§6), once ruling 3 is made.
4. **Anchor the bell** — `NotificationQueries.Bell` becomes the two-namespace alternation, the
   `portal-next` React copy follows, and the `nodeType:Notification scope:Exact` line leaves
   `unanchored-queries.allow`. `ShellQueryShapesTest`'s `KnownFanOuts` loses its Bell entry in the
   same change (it fails on a stale entry, by design).
5. **Suppress repeat plugin-update notifications** (§5) — independent of the rest, and it is what
   actually shrinks the row count.

Anchoring before 1–3 is the truncation this design exists to avoid: the bell would go quiet for
every notification still sitting in an entity's partition.

## 8. What is not established here

- **Ruling 2 above** — whether `Admin/_Notification` is readable by a platform admin from another
  partition — is asserted in comments only and was not verified against a running mesh. Everything
  in §3 that depends on the `Admin` leg depends on it.
- **The total population size.** The measurement is the newest 200 rows (the API caps a listing at
  200); the class *proportions* are measured, the absolute count is not, and it is what decides
  between moving and deleting in §6.
- **Whether any deployment outside memex-cloud has a materially different distribution** — one mesh
  was sampled.

## Cross-references

- [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination) — the census this is plan 1 of.
- [Notifications](/Doc/Architecture/Notifications) — the pipeline as it works today.
- [Unanchored Security Reads](/Doc/Architecture/UnanchoredSecurityReads) — why truncating a read is worse than paying for it.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per partition, and how a satellite is routed.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the bell is a query and mark-as-read is a stream.
