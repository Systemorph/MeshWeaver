---
Name: Protected Segments on a Public Partition
Category: Architecture
Description: >-
  A partition that is public except for one inbox cannot be expressed with PartitionAccessPolicy.PublicRead —
  the C# evaluator and the SQL projection resolve a deeper deny under it differently, so the segment is
  readable by exact path and absent from every listing. Root Viewer grants plus a deny are the shape both
  folds agree on, and why the boot heal may never retire a deny it could not have written.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/><path d="M3 4h7"/></svg>
---

# Protected Segments on a Public Partition

A package can be public and still hold something that is not. The `Feedback` plugin is the shipped
example: its catalog half — issue mirrors, source, releases, a cover — is meant for anyone, and
`Feedback/_Submissions` collects what users write in. Its manifest says exactly that:

```json
{ "preInstalled": true, "protectedSegments": ["_Submissions"] }
```

**There are two ways to publish that partition, they differ in one node, and only one of them can
express the exception.** Getting it wrong published a submission inbox
([#4716](https://github.com/Systemorph/MeshWeaver/issues/4716)).

## The one node that decides it

| Shape | What opens the public half | Does a deeper Public/Anonymous deny withhold the segment? |
|---|---|---|
| **Policy** — `PartitionAccessPolicy { PublicRead = true }` at `{partition}/_Policy` | the policy | **C# evaluator: NO. SQL fold: yes.** |
| **Grants** — Public + Anonymous Viewer `AccessAssignment` at `{partition}/_Access` | two role grants | **Both: yes.** |

`PermissionEvaluator.ComputeRoleState` subtracts denied *roles* from `roleIds` and then ORs the
public grant in separately (`p |= publicGrant`). A deny removes a ROLE; `PublicRead` is not a role,
so there is nothing for it to take away. The Postgres projection reaches the same field by a
different route — it emits the policy as allow-`Read` rows for the well-known `Public` and
`Anonymous` subjects at the policy's prefix, and the read side then resolves
`DISTINCT ON (user_id) … ORDER BY LENGTH(node_path_prefix) DESC`, so a deny row at a longer prefix
simply wins.

**One shape, two answers, and the disagreeing pair is the paywall-bypass shape**: the node is
readable by exact path and correctly absent from every listing. That is not a hypothetical — the
evaluator carries its own account of the last occurrence, 79,650 characters of paid course content
served to an unentitled caller while `search` denied the same node.

Under the GRANT shape both folds are computing the same thing: a per-subject longest-prefix
resolution over role rows. So the remedy is not to pick a winner between the folds — it is to stop
using the mechanism they disagree about for a partition that needs an exception.

### Why not just cap the segment

A deeper `_Policy { Read = false }` *does* suppress an inherited `PublicRead` in the C# fold — it is
ANDed into the effective permission (`publicGrant &= scopeCap`). But the same cap is ANDed into every
role-derived permission too, so the reviewer who triages the inbox loses it as well. That is a
**blackout, not a gate**. It cannot say "closed to the public, open to the people who read it", which
is the whole requirement. `PublicReadIsNotSuppressedByADenyTest` measures that arm.

A deny is the right instrument precisely because it names only two subjects: everybody else's own
grant is untouched, and `System` — which every submit path impersonates, so a low-privilege user can
file without holding write access — short-circuits the fold entirely. **Submittable and not publicly
readable** is what a feedback inbox needs, and only the grant shape delivers it.

## What the installer does

`PackageInstaller.EnsureDeclaredAccess` picks the shape from the manifest, and a declared
`ProtectedSegments` takes precedence over both older branches, pre-installed included:

- **`protectedSegments` declared** → root Public + Anonymous Viewer GRANTS, a Public + Anonymous
  Viewer DENY on each declared segment, and a `_Policy` that explicitly **withholds** `PublicRead`.
  The policy node stays for two reasons: it is the declared-access post-condition marker
  (`DeclaredAccessMarker`), and it records the decision so the next reader finds the partition saying
  "not public-read" instead of inferring it from the grants.
- **Policy first, then the denies, then the root grants** — and that order is the fail-closed one,
  which the obvious "denies first" is *not* on the single transition that matters. With an existing
  `PublicRead = true` policy, writing the denies first and failing before the flip leaves the blanket
  grant live and the fresh denies inert: exactly the state the change exists to end. Flipping the
  policy first removes that grant while no root grant exists yet, so the partition is momentarily
  **closed** rather than momentarily open; the denies land while it is closed, and the grants re-open
  only the public half with the gate already in place. A failure anywhere leaves the partition closed,
  and the next pass completes it. (The first draft had the order the other way round *and* claimed the
  fail-closed property — review caught the gap between the two.)
- **A deny is written on presence OR on the wrong shape.** Plain create-only asks only whether a node
  is there, so a `Public`/`Anonymous` assignment at a declared-protected scope that happens to be a
  **grant** would survive and the root grant would then publish the segment — the protection reading
  as established without being it. So the protected-segment denies supersede an existing assignment
  for those two subjects that is not already an all-denied role set, which is the predicate the
  Store's gate already uses (`StillNeedsDeny`). It can only ever narrow, and it is deliberately not
  applied to the scoped shape's `publicSegments`-derived gating of *ordinary* children, where the same
  overwrite would take away a publication an operator chose.
- The access nodes are **create-only**; a policy that declares `PublicRead` is **rewritten**, because
  it contradicts the protection being established. Every other field it carries (a `RedirectOnDenied`
  funnel) is preserved.

🚨 **The shape MOVES a publication off the policy; it never ADDS one.** The denies are written on every
partition that declares a protected segment — a deny can only narrow, so establishing it needs no
permission to widen anything. The policy flip and the root grants only happen where this step would
have published *anyway*: no policy yet (its original create), a policy that declares `PublicRead`, or
the legacy fingerprint the heal deliberately opens on. On a partition whose policy already withholds
public read, the step writes the gate and nothing else.

That distinction is not decoration. On the control instance `memex.systemorph.com`, `Feedback/_Policy`
carries no `publicRead` and the partition holds no `_Submissions` at all; without the rule, the
declaration would have handed an anonymous reader a partition somebody had closed — a widening
introduced by the fix for an exposure, which is the worst shape a security change can take.
`ADeclaredProtectedSegment_OnAPartitionThatWithholdsPublicRead_IsGatedButNotOpened` pins it, and
reverting the rule fails it on the grant it must not write.

## Two things core could not see, and the second is the one that bites

**`protectedSegments` was dead metadata in core.** `NodeRepoPackageSource.Peek` dropped it — the same
defect class `preInstalled`, `publicSegments` and `contactEmail` each had — while the Store's
`PluginGate` authored *and* honoured it in mesh source that no `dotnet build` and no
`grep --include='*.cs'` over this repository can see. So the partition took the policy shape, and the
protection the Store wrote under it protected nothing on the C# read path.

🚨 **And the boot heal DELETED that protection.** `EnsurePartitionPublicRead` carries a legacy heal
for the pre-#902 gate: a policy withholding public read *plus* Public/Anonymous denies on the
children is read as damage, the denies are retired and the policy is healed to `PublicRead`. The
plugin machinery's `_Submissions` deny pair matched that fingerprint exactly, so a boot repair pass
retired live security state and republished the inbox — logged at Information, as a migration.

**The fingerprint is now narrowed by construction, not by taste: this sweep may only retire what this
sweep's own past self could have written.** `GatedChildRoots` skips every `_`-prefixed segment, so no
version of the installer has ever written a deny at a satellite scope; a deny that is there can only
be a live protection. `IsSatelliteScopedDeny` is that split. The partition's *own* `_Access`
container is deliberately not a satellite scope in this sense — a deny there gates the partition
root, which IS the shape the heal exists to undo.

### The declaration does not always reach the code that needs it

The boot repair pass re-drives the declared-access step from the **install RECORD's** stored
manifest, and every record stamped before `ProtectedSegments` was read carries no declaration at all.
A fix that only read the manifest would therefore leave every already-installed portal republishing
its inbox on the next boot — and the policy write **alone** is enough to do it, with no delete
involved, because under `PublicRead` the surviving denies are inert.

So the step has a second, **evidence-driven** arm: a satellite-scoped well-known deny that this
installer cannot have written takes the partition off the blanket policy even when the manifest says
nothing, and the partition is published through grants instead. The warning it logs names the scopes
and says what to do if they were meant to be public — retire the denies, never add `PublicRead`.

### An unread deny set is not an empty one

🚨 **Both arms read that evidence off the query's `Initial` snapshot, not off its first emission** — the
query can emit pre-`Initial` `Added`/`Updated` frames, and both decisions taken off this read fail
*open* on a short frame: a missing satellite deny leaves the protected set empty, so the evidence arm
does not fire and the heal proceeds to retire denies and write `PublicRead`, republishing the very
segment the arm exists to protect. Filtering for `Initial` is the established idiom here
(`DeploymentReportService`, `PlanTierLadder`, `GitHubSyncService`, `PathResolutionService`, …). Review on
this change surfaced it; the legacy retire had been taking a **delete** decision off the same unfiltered
read.

🚨 **But that filter is only safe because the failure is now reported as UNKNOWN, and this is the sharper
half.** The query shape is a *measured live stall*: `path:<partition> scope:subtree
nodeType:AccessAssignment limit:2000` as `system-security` is the verbatim query in the fan-in's
20-second stall warning, with 200+ occurrences in a 400-minute window on `memex` (truncated at the log
limit), because `StorageAdapterMeshQueryProvider.DefersToNativeProvider` is false for satellite reads and
the pedestrian walk emits nothing at all until every per-path read completes. So on a Postgres portal the
`Initial` can genuinely never arrive — and a filter that turned that into *"the read says there are no
denies"* would be worse than the unfiltered read it replaced, deterministically rather than occasionally.

So the listing answers `Ok: false` on a failure or a timeout, and **every arm declines on it**: nothing
retired, and no blanket policy written over a shape the pass could not see. That restores a rule the
method's own remarks had always stated — *"healing on an unknown deny set is the one outcome worse than
not healing"* — which the original-create arm had quietly broken by writing the policy without consulting
the listing at all. A reduced host with no query surface at all is deliberately a *different* answer
(`Ok: true`, empty): there is demonstrably nothing to enumerate, which is not the same as not knowing.

**The two rules compose.** A partition can carry both a live satellite protection and genuine
pre-#902 damage on its ordinary children, so the legacy denies are still retired (pre-installed only,
which is the restriction that keeps core out of the gating reconcile's ping-pong) and only then is the
partition published.

## What was measured, and what was not

Against a real monolith mesh, `AnonymousCannotReadAProtectedSubmissionTest` pins both sides of the
change with one case on each: under the policy shape a logged-out visitor **reads** a submission
carrying their own subject's deny, and under the grant shape the same deny pair withholds it while
the cover stays public and the reviewer and `System` keep reading. Each half carries its own
public-half control, so a denial cannot pass for the right answer on a partition nobody could read.

`ADeclaredProtectedSegmentSurvivesTheBootHealTest` pins the installer: the shape a declaring manifest
produces, and — the case that would have caught this — an undeclared-but-protected partition in the
live shape, whose denies must survive and whose policy must not gain `PublicRead`. Reverting the fix
fails that on the assertion naming the delete.

`AProtectedSegmentDeclarationSurvivesDiscoveryTest` covers the hop nothing else looks at: the
declaration travelling from an authored `index.json` through `NodeRepoPackageSource.ListPackages` into
the manifest the access step reads. Every other test builds the manifest in-process, so without it the
first half of the defect — core dropping the field on the way in — could come back and red nothing.
Disabling the read fails it; a manifest that declares nothing still comes back empty, which is the
asymmetry that matters.

**The SQL fold is not executed by any of it, and cannot be from this repository — core has no
Postgres test lane** (no `Testcontainers`/`Npgsql` reference in any core test project). What is known
about that half is read from executable SQL in `MeshWeaver.Plugins`
(`PostgreSqlSchemaInitializer`'s projection, `PostgreSqlSqlGenerator`'s read-side fold) and from the
tests there that exercise the deeper-deny-beats-shallower-public-grant shape against a real Postgres
(`PerSubjectAccessFoldTests`, `AccessControlQueryTests.PaywalledContent_StaysInvisibleToAnonymous`).
Those seed the rows through `access_control` rather than by writing a `PublicRead` `_Policy` node, so
the projection's own arm is inferred from its SQL, not from an executed case. **No test anywhere
writes a real `PublicRead` `_Policy` at a shallow namespace together with a real deeper deny against
Postgres** — that pairing is the gap this page leaves open, and it belongs in the repository that has
the lane.

**The `Ok: false` arm is not covered by a test.** Forcing the access listing to fail or to withhold its
`Initial` needs a query-provider fault seam this suite does not have, so that one `if` is reasoned from
the measured production stall rather than executed here. Everything else on the page is pinned.

Also not established: whether a partition published through grants alone keeps the same
`public.partition_access` membership for an anonymous fan-out as one published through a `PublicRead`
policy. The projection derives those rows from `user_effective_permissions` where
`permission = 'Read' AND is_allow = true`, which the root grants satisfy, and the live `Feedback`
partition has been in the grant shape since the Store's gate converged on it — but that is a read of
the SQL plus a live observation, not a measurement.

## See also

- [Access Control](../AccessControl) — the permission model, `PartitionAccessPolicy`, and the
  "Public policy grants and deeper read caps" section this page qualifies.
- [Granting Access](../GrantingAccess) — how an `AccessAssignment` is placed and what a role grant
  or deny means at a scope.
