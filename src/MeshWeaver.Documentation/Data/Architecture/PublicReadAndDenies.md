---
Name: PublicRead and Denies
Category: Architecture
Description: A Public/Anonymous deny beats PartitionAccessPolicy.PublicRead on the SQL read path and not in the C# evaluator — a measured two-executor divergence, which is the paywall-bypass shape. What it exposes, why three components believed the deny protected them, and what each candidate remedy costs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/><line x1="3" y1="21" x2="21" y2="3"/></svg>
---

# PublicRead and Denies

**A `Public`/`Anonymous` Viewer DENY under a `PartitionAccessPolicy` that grants `PublicRead` is
honoured by the PostgreSQL read path and IGNORED by the C# `PermissionEvaluator`. The two read paths
disagree — which is the paywall-bypass shape, and the reason a submission inbox cannot currently be
closed.**

Three components assert that the deny protects the segment. One of them ships a protection built on
it. On the C# path it protects nothing, and on the SQL path it hides the content from every listing
while leaving it readable by exact path.

Confidence, stated separately because it differs:

- **MEASURED** (this repository, a real monolith mesh, 2026-09-20): the C# fold lets both well-known
  subjects read a child carrying their own Viewer deny. Pinned by
  `PublicReadIsNotSuppressedByADenyTest`.
- **MEASURED** (`memex.meshweaver.cloud`, read-only, 2026-09-20): the `Feedback` partition is
  world-readable including its submission inbox, and carries no deny at all.
- **READ, NOT EXECUTED**: the PostgreSQL projection's own comment states the opposite behaviour. No
  Postgres portal was exercised for this page, so the divergence is inferred from that comment plus
  the measured C# half. **Verifying it against a live Postgres portal is the first step of any fix.**

## The one line of the fold that decides it

`PermissionEvaluator.FoldScopes` walks the scope hierarchy of the path being evaluated. Two
independent accumulators come out of that walk:

- `roleIds` — the roles the subject holds, unioned per scope, and a **deny subtracts from this one**
  (`roleIds = roleIds.Except(deniedRoles)`).
- `publicGrant` — the policy-driven public grant, ORed in per scope
  (`if (policy.PublicRead) publicGrant |= Permission.Read`).

The result is the role-derived permission (capped) **OR** `publicGrant`. So:

> A deny removes a ROLE. `PublicRead` is not a role. There is nothing for the deny to take away.

The only thing that touches `publicGrant` is a permission CAP from a policy deeper on the chain
(`publicGrant &= scopeCap`) — i.e. a `PartitionAccessPolicy { Read = false }`.

### …and the one line of SQL that decides it differently

`PostgreSqlSchemaInitializer` (MeshWeaver.Plugins) projects everything into one row set,
`user_effective_permissions(user_id, node_path_prefix, permission, is_allow)`, resolved **per subject
by longest prefix**. A `PublicRead` policy becomes allow-`Read` rows at the policy's namespace for
`Public` and `Anonymous`; a Viewer deny becomes deny rows at the assignment's scope. Its own comment
spells out the consequence:

> Runs AFTER the policy-cap deny fold with `DO UPDATE is_allow = true` so at the **SAME** prefix the
> public grant wins — matching the live override order. (A deny at a **LONGER** prefix still wins the
> per-subject longest-prefix query fold; that is the store-gating shape and it is intentional.)

So the intended rule is *"a deeper deny beats an inherited public grant"*, the SQL path implements
it, and the C# path does not. Neither is a rounding error: the store/course paywall gating rests on
exactly this shape.

## Why the belief looked true

Because it **is** true of the other public shape, and the two are easy to conflate. The platform
opens a partition to everyone in two different ways:

| Shape | How the read is granted | Does a child deny close it, in the C# fold? |
|---|---|---|
| **Fully-public** — `{partition}/_Policy` with `PublicRead = true` (`PackageInstaller.EnsurePartitionPublicRead`, `PluginGate.OpenPolicy`) | a POLICY grant | **No.** The deny is inert. |
| **Scoped** — `Public`+`Anonymous` Viewer GRANTS at the partition root (`PackageInstaller.EnsureScopedPublicRead`, the Store's `CatalogGate`) | a ROLE grant, inherited downward | **Yes.** The deny removes the role. |

The rule was written from the second row and applied to the first, where the C# fold does not honour
it. Both rows are pinned by `PublicReadIsNotSuppressedByADenyTest`, deliberately in one file, so the
difference is visible rather than something the next reader has to infer.

## The workaround available today is a blackout, not a gate

A deeper `PartitionAccessPolicy { Read = false }` does suppress the inherited public grant in the C#
fold. But
`GetPermissionCap()` produces one mask that is ANDed into **every** subject's permissions, admins
included — the record says so in its own summary ("Caps effective permissions at a namespace scope
for ALL users (including Admins)"). Core's own `PublicReadPolicyScopeTest` already pins the cost:
`RolePolicy/Capped/Page` denies Read to a viewer holding a real Viewer grant one scope up.

So `Read = false` cannot express *"closed to the public, still readable by the people who triage
it"*. The only identity it does not bind is `System`, which short-circuits the fold entirely.

**So in the C# fold there is no way to withhold an inherited public grant without capping roles** —
while the SQL fold does exactly that, for the two well-known subjects only, which is precisely the
behaviour wanted. The divergence is the defect; everything below is a consequence of it.

## The case that found it

`Systemorph/MeshWeaver#4716`. The `Feedback` package's manifest declares both halves of the
intent explicitly:

```json
"preInstalled": true,
"protectedSegments": ["_Submissions"]
```

`Feedback/_Submissions/{id}` is where the `/feedback` flow files every submission, written as
System so a low-privilege user can submit. What was measured, read-only, on 2026-09-20:

| Read | Result |
|---|---|
| `Feedback/_Policy` | `PartitionAccessPolicy { PublicRead: true }`, `lastModifiedBy: system-security` |
| `Feedback/_Submissions/demo-preview` | exists, `nodeType: Feedback/Feedback` (a demo item — no real user data today) |
| `Feedback/_Submissions/_Access/Public_Access` | **Not found** — no deny exists |
| `Feedback/_Access/Public_Access` | `Public — Viewer` — reads back fine, so the absence above is the node's and not a filtered read |

Note `search` did not return `Feedback/_Submissions/*` while `get` returned the node — the known
"search can miss what get returns" asymmetry. The denominator came from `get`.

Three separate components assert the rule that only one of the two read paths implements, and the
third one acts on it:

1. `PackageInstaller.EnsurePartitionPublicRead`'s remarks stated it as a general rule, with no
   mention that one read path ignores it. **Corrected** by the change that added this page.
2. #4716's triage comment cited those remarks to conclude that a per-path deny *"is not
   hypothetical"*.
3. The Store's `PluginGate` pre-installed arm (in-mesh source in `MeshWeaver.Plugins`, so invisible
   to any `dotnet build` or `grep --include='*.cs'` over this repository) writes exactly that deny
   pair for every declared `ProtectedSegments` entry, under a comment naming this very inbox. On the
   C# path **it is a protection that cannot protect**; on the SQL path it produces the split-visibility
   shape. It is also not currently present on the live partition, so the exposure persists until it
   lands — a second and independent reason.

Core additionally never reads `protectedSegments` at all — `NodeRepoPackageSource.Peek` does not
carry it onto `PackageManifest` — so the installer could not honour the declaration even if a
mechanism existed. That is the same dead-metadata class `preInstalled`, `publicSegments`,
`contactEmail` and `tier` each were, except this one fails OPEN.

## What each candidate remedy costs

Nothing here is free, and none of it is a core-only change. This is the part that needs a decision,
not a patch.

**1. Reconcile the C# fold with the SQL one — make a deeper well-known deny withhold the inherited
public grant.** This is the fix that matches the intent, needs no new concept, no manifest change and
no data migration, and is **core-only**, because the SQL side already behaves this way. The segment
becomes gated and a reviewer's own grant still reads it, since a deny touches only `Public` and
`Anonymous`.

What it takes, concretely: `publicGrant` must stop being an OR-accumulator and become a
longest-prefix (last-writer-wins) chain over the well-known subjects' rows — allow where a
`PublicRead` policy or a Public/Anonymous grant sits, deny where a Public/Anonymous deny sits, caps
still ANDed in. 🚨 The state for that is not currently reaching the fold: `ComputeScopeRoles` filters
assignments to the EVALUATED subject, so a Public/Anonymous deny is absent from what
`ComputeRoleState` receives whenever the viewer is somebody else. So the change is not one line —
it widens the snapshot the long-lived fold consumes.

🚨 **Do this one with the evidence in front of you, and verify the Postgres half first.** This fold
has an incident history on exactly these lines: #974 (an empty seed fails OPEN), the 2026-08-05
paywall bypass (claim roles folded in here), and a 2026-09-11 correction whose four failures were all
"reads through a deeper read cap". It also changes permission outcomes for every subject on every
path, so it wants its own test matrix and a `MeshWeaver.Security.Test` run on the Plugins side.

**2. Retract `publicRead` on the partition and open it with root grants instead.** Uses only shipped
machinery, makes the denies work on both paths, and needs no evaluator change. Two costs. First, `PublicSurfaceCarriesApi` keys on the POLICY's
public grant, not on an `AccessAssignment` — it is what "keeps MCP tokens working on `Doc/`, `Agent/`
and every installed package partition, which `PackageInstaller` makes readable through exactly this
policy" — so an API client holding no role loses the partition's public half. Second, **it
ping-pongs**: `PluginGate.OpenPolicy` rewrites `{ publicRead: true }` on every reconcile for a
pre-installed partition, and core's boot pass re-asserts it too. `EnsurePartitionPublicRead`'s
remarks record what that fight already cost once — a CD seal, over 26 denies retired and re-written
between two components that each believed they owned the shape. Whoever takes this option changes
**one** writer, not two.

**3. Move the inbox out of the public partition** (#4716's own first option). Submissions go to a
partition with no public grant; the public catalog half stays public. Cleanest in the access model
and needs no new mechanism — but it is a change to where the Feedback plugin WRITES, plus a
migration of what is already filed, and it lives in `MeshWeaver.Plugins`.

**4. Decide the submissions were always meant to be public, and record that decision on the node.**
Still a legitimate answer, and the only one no amount of code reading can choose.

## What this page does not settle

Which of the four, and two things it deliberately did not establish:

- **The Postgres half was not executed.** Its behaviour here is its own comment, not a reading. Every
  remedy above assumes the SQL path denies a deeper-prefix deny under a public grant; confirm that on
  a live Postgres portal before building on it.
- **Whether any partition other than `Feedback` is exposed this way.** The sweep that would answer it
  is every `PartitionAccessPolicy` with `publicRead: true` whose package manifest declares
  `protectedSegments` — not run, because it reads across partitions on a live public instance.

## See also

- [Access Control](../AccessControl) — the permission model and the Admin partition
- [Query Provider Parity](../QueryProviderParity) — why a semantic honoured by one fold and not the
  other is a security defect, not an inconsistency
- `PublicReadIsNotSuppressedByADenyTest` (`test/MeshWeaver.Graph.Test/`) — the three shapes, executable
- `PublicReadPolicyScopeTest` (`test/MeshWeaver.Graph.Test/`) — scope precedence, incl. the cap's cost
