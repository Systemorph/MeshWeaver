---
nodeType: Markdown
name: The Plugin Build Contract
category: Architecture
description: Four steps — take the image, install dependencies, build your plugin, test it. What each step means, why "install" must never be a build, and the measured state of every plugin repo against it.
icon: /static/NodeTypeIcons/box.svg
---

# The Plugin Build Contract

Building a plugin is four steps, and they are plain:

1. **Take the MeshWeaver image.**
2. **Install all my dependencies** — as *built artifacts*, served by MeshWeaver.
3. **Build my own plugin.**
4. **Test it.**

Everything on this page follows from one rule inside that list: **step 2 is an INSTALL, never a
build.** A repo that compiles its dependencies has not installed them — it has rebuilt somebody
else's product, once per mesh, on every run, and it is testing bytes nobody will ever ship.

---

## One thing builds and tests

The tool is **`mw-plugin-test`**, shipped in the `mw-plugin-test` image. It is not a gate that
happens to compile; it is the producer *and* the consumer, split deliberately (#1763):

| flag | role |
|---|---|
| `--bake-output <dir>` | **produce**: compile every NodeType in the mount, emit one bundle per package plus `framework-mvid.txt` |
| `--seed <dir>` | **consume**: read a bake *before the mesh boots*, and stand the mesh up on **those bytes** |

`BakeSeed`'s own summary states why the split matters:

> The producer emits assemblies with no mesh; the gate stands one up and proves the **BAKED BYTES**
> render and pass their `Tests` areas — which is a strictly stronger claim than the fused pass ever
> made, because the bytes it judges are the bytes that ship.

So step 3 and step 4 are the same tool, twice: build once, then test what you built. Nothing else in
a plugin repo should compile in-mesh C#.

### The failure this prevents, and how it hides

`BakeSeed` also names the trap, and it is the reason `--seed` must be wired rather than assumed:

> 🚨 A gate pointed at a bake it cannot consume **silently compiles everything and passes** —
> indistinguishable from a gate that consumed the bake perfectly.

A green gate is therefore not evidence that anything was seeded. Only the seed's own report is.
Consuming a bake is checked **before the mesh boots**, and a problem there is a *usage error*
(exit 2), not a red gate — precisely so "I could not consume this" can never be mistaken for
"I consumed it and all is well".

---

## Measured state, 2026-08-27

Counting `--seed` and `--bake-output` across every repo:

| repo | produces (`--bake-output`) | **consumes (`--seed`)** |
|---|---|---|
| **MeshWeaver** (core) | 21 | **9** |
| MeshWeaver.Plugins | 1 | **0** |
| MeshWeaver.SocialMedia | 1 | **0** |
| MeshWeaver.Reinsurance | 1 | **0** |
| MeshWeaver.Education | 2 | **0** |
| MeshWeaver.Manufacturing | *(unmeasured — API rate limit; not to be read as zero)* | |

**Core uses the split. Not one plugin repo does.** Every plugin repo produces a bake and then throws
it away, standing its mesh up on source and compiling the world instead.

What they do at step 2 today, verbatim from their workflows:

- **SocialMedia**, **Reinsurance** — `stage-repo`: the required modules are sparse-checked-out and
  copied into the gate mount **as source**.
- **Education** — *"Stage the required Plugins modules (cross-repo requires of the courses)"*, then
  `build-package-repo.mjs <out> <plugins-checkout> <education-checkout>` fuses both checkouts into
  ONE package repo, which the mesh installs and Roslyn-compiles end to end.
- **Plugins**, **Manufacturing** — nothing to stage (Plugins *is* the dependency source).

None of them reads a built artifact. The package source is `GitPackageSource` — a local git tree —
everywhere.

---

## What it costs, measured

Education's disposable-mesh e2e boots **five meshes per run**, and each one compiles `Store` (17
NodeType assemblies), `Edu` (8), `Publish`, `Video` and every course type from scratch. Those are
someone else's already-built packages.

On 2026-08-27, shard 1 of that suite showed the whole chain in one log:

```
06:33:38  prebuilt bundles: 37 in /home/runner/work/_temp/prebuilt
06:38:40  Store surface: "⏳ Compiling… — Catalog Running Roslyn against 9 source binaries"
06:51:14  Install: prebuilt adoption attempt failed — the installed types compile instead
06:51:14  ShippedPrebuiltBundles: 27 prebuilt assembly(ies) from 37 shipped bundle(s) …
          0 adopted now, 27 already current
```

Read the order: **Store began compiling thirteen minutes before anything tried to hand it a prebuilt
binary.** The bundle existed the whole time — `Store.zip`, 17 assemblies — and the framework identity
matched *exactly* (`s26852fcf0ddfd18497201fb723505c4b` on both the bake and the portal, `DECLINED: 0`).

The downstream symptom is a test failure that names none of this: `the cover must offer the install
step`. The Store cover's CTA is `PluginLayoutAreas.InstallCtaArea` — a plugin area, correctly
authored — and it cannot render because its own type is still in Roslyn.

> **A plugin's UI is only as live as its assembly. Starve the assembly and the symptom appears in the
> UI, three layers from the cause.**

---

## Why mounting bundles is not enough

Bundles reach a mesh through two lanes, and **neither serves a package that is not installed yet**:

- **Boot lane** — `ShippedPrebuiltBundles.SeedAll`, from `DynamicTypePreWarmerHostedService`. It
  filters each bundle to **the NodeTypes the mesh already holds**. On a fresh mesh that is a handful
  (the platform's own `Doc` content). `Store`, `Edu` and every course type arrive *later*, through
  the install — so their bundles are skipped, and **zero adopted is the correct reading**, not a
  failure.
- **Install lane** — `PackageInstaller.SeedPrebuiltAssemblies`, the only lane that can serve a
  package as it arrives.

So the install lane is load-bearing, and it carries a guard that inverts its own purpose:

```csharp
return consumer.SeedForTypes(nodeTypePaths)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(60))
    .Catch<int, Exception>(ex => {
        logger?.LogWarning(ex, "Install: prebuilt adoption attempt failed — the installed types compile instead");
        return Observable.Return(0);
    })
```

🚨 **The cap does not save time; it duplicates work.** It abandons the *result*, not the *work* — the
seed runs on and reports its coverage afterwards, as the log above shows. Meanwhile the install has
already fallen back to compiling the very types the seed was about to deliver. A timeout on an
operation that is by construction **cheaper than its own fallback** can only ever make things worse:
when it fires, the system does both.

That is the same inversion `DisposeHubsReactive` removed one layer down (#1317) — *a join must not
out-run the answer it is joining*. A bound belongs here as a **stall detector**, not as a duration
cap shorter than the alternative it guards.

The observable consequence is variance that looks like flakiness: across one run of five meshes, on
identical bundles and identical pins, coverage read **17 · 19 · 19 · 17 · 0**. That is not the
seeding being unreliable — it is how far each mesh got before a 60-second stopwatch fired.

---

## Where the build is served from

Step 2 says *served by MeshWeaver*, and that is the half still missing.

- **Compiled .NET modules** (`MeshWeaver.*.dll`) already have this lane: `Plugin Catalog CI`'s
  `modules` job packs each module and POSTs it to `/api/plugins/bundles` on the registry
  (`memex.meshweaver.cloud`). That works.
- **NodeType assemblies** — the Roslyn output of in-mesh `Source/` — are a *different artifact*, baked
  to the portals' storage as `prebuilt-bundles/<identity>/<source>/*.zip`, sealed with `_complete`.
- **No plugin repo fetches either one.** The consumer code exists — `RegistryPackageSource` and
  `PluginBundleClient` in `MeshWeaver.PluginCatalog` speak `/api/plugins/bundles` — but nothing in
  a plugin repo's CI calls it: the gates read `GitPackageSource`, a local git tree, and the e2e
  meshes fuse checkouts into one. (An earlier draft of this page attributed the gap to core #805
  removing the catalog UI; #805 is about space write access, and the consumer was never removed.
  The gap is that it is not *wired*, which is a smaller and more fixable claim.)

So today the artifacts exist, the identities match, the consumer exists, and no plugin repo's step
2 is wired to *fetch* rather than *rebuild*.

🚨 **And the gate cannot fetch on a pull request until a credential exists for it.** The OIDC
identity that reads the portals' storage (`github-actions-bake`) holds one federated credential
per satellite, every one scoped to `ref:refs/heads/main` — correct for publish-bake, which runs on
main. A gate on a PR presents the subject `repo:Systemorph@<org>/<Repo>@<id>:pull_request`, and
Entra answers `AADSTS700213: No matching federated identity record`. Measured 2026-08-27 on the
first PR to wire `upstream-seed`. The fix is a credential per repo for the `pull_request` subject
(both subject formats, as the existing `main` pair) — a system change that belongs to a
maintainer, not to a workflow edit.

**The target:** a plugin's build is published to MeshWeaver, and every consumer — another plugin
repo's gate, a disposable e2e mesh, a production portal — **installs it as a plugin at image boot**,
the same way any other plugin arrives. Not staged as source. Not recompiled.

---

## The build principal — an identity the mesh TRUSTS, with no secret to keep

Step 2 needs a credential: something has to prove *this build may fetch that publication*. The
first attempt to wire `upstream-seed` showed where that proof lives today and why it is the wrong
place: the publications sit on Azure Files, only an Azure OIDC identity can read them, and that
identity's federated credentials are maintained in the Entra tenant — four of them, all scoped to
`ref:refs/heads/main`, none for `pull_request`, so the gate cannot fetch on the one event it exists
for. Nothing in the mesh knows those credentials exist, which repos hold one, or who authorised
them. That is the shape of the plaintext-provider-key incident: a security fact with no record a
reader can point at.

**The concept: a BUILD PRINCIPAL — GitHub's build identity, recognised by the mesh directly.**
Every GitHub Actions run already carries a passkey for services: a short-lived OIDC JWT from
`token.actions.githubusercontent.com`, signed by GitHub, with `repository`, `ref`, `event_name`
and `job_workflow_ref` as claims. Azure's federated credential is nothing more than *Azure
verifying that JWT against a rule*. The mesh can verify it itself — and then the build principal is
a **trust rule on a node**, not a stored secret. Nothing is minted, rotated, pasted into a repo's
secrets, or leaked.

**It is one more branch of a fork that already exists.** The registry's
`InstanceRegistryAuthenticator.AuthenticateToken` already verifies *signed* tokens — the `mwa_`
sync-access JWTs, checked against `SyncTokenSigningKeyService` with key rotation, refusing when it
cannot verify ("a registry that cannot verify a signature must refuse the token, never accept it
unverified"). A GitHub OIDC token is the same operation with a different key source: GitHub's
published JWKS instead of the mesh's own keys. The portal already references
`Microsoft.Identity.Web`, so the JWKS fetch and signature check are library calls, not new
dependencies.

**The rule lives on a `BuildPrincipal` node** — a control node in the shape every admin action here
already takes (`Store/Provision`, `Store/Enrollment`). It records what a stored secret never could:

| field | meaning |
|---|---|
| `repository` | `Systemorph/MeshWeaver.SocialMedia` — the `repository` claim it must match, exactly |
| `repositoryId` | optional pin on GitHub's IMMUTABLE numeric id — a name can be renamed and re-registered, an id cannot |
| `events` | which `event_name`s may act — `push` may *publish*; `pull_request` may only *fetch* |
| `eventRefs` | optional per-event `ref` pin, so "`push` **on main** may publish" is expressible rather than merely intended |
| `scopes` | `publish:socialmedia`, `fetch:plugins` — what this repo may bake INTO the registry and install FROM it |
| `issuedBy` / `issuedAt` / `lastSeen` | the audit trail |

A global admin `create`s it; `requestedAction: Revoke` ends it. `search nodeType:BuildPrincipal`
lists every repo the mesh trusts and exactly what each may do. There is no key to lose because
there is no key.

> 🚨 **It is a CORE node type, `BuildPrincipal`, at `Admin/_BuildPrincipal/{owner}--{repo}` — not a
> `Store/…` one.** The issue drafted it under `Store/` beside the other control nodes, but the
> verifier that reads it is the registry's own `InstanceRegistryAuthenticator`, which is platform C#
> and cannot depend on a package's in-mesh node type. It sits in the **Admin partition** for the same
> reason `PluginGrant` does: the subject of an access decision must not be able to write the
> decision, and that partition's access control IS the global-admin gate.

**The security tie is the scope split.** The identity that publishes a source is the identity that
may fetch what it depends on, and it can do neither outside its scopes: SocialMedia's principal
holds `publish:socialmedia` + `fetch:plugins`, so it can never publish *as* Plugins and never fetch
a source it does not declare in `requires`. Both facts are on one node the mesh owns — not split
between a GitHub secret and an Entra credential that no query can join.

**The mechanism: the registry SERVES publications, so the gate never touches Azure.** The portal
already mounts `/data/prebuilt-bundles`, and the bundle route already has a GET side (`FetchIndex`,
`ModuleFetchCommand`). Add `GET /api/plugins/bundles/prebuilt/<framework-identity>/<source>/` under
the same authenticator, requiring `fetch:<source>` on the presenting principal — and, beside it,
`…/<source>/modules` (the module bundles that publication was sealed against, #2698) so a gate
composes the bytes its upstream's assemblies were built with, never the package endpoint's. `upstream-seed` then
presents its run's OIDC token — `ACTIONS_ID_TOKEN_REQUEST_TOKEN`, available to any job with
`id-token: write` — and receives the sealed publication. PR or push is a claim the rule reads, not
a credential someone had to remember to create.

| | Azure OIDC federated credential | Build principal |
|---|---|---|
| what is stored | a subject rule, in Entra | a subject rule, on a mesh node |
| who can see which repos may fetch | whoever has tenant access | `search nodeType:Store/BuildPrincipal` |
| PR vs main | one credential per event subject, per subject *format* | one node; `event_name` is a claim it reads |
| secret in the repo | none (already) | none |
| tied to the build queue | no — a storage reader | yes — the same identity that publishes, scoped |
| verified by | Azure | the mesh, the way it already verifies `mwa_` tokens |

The two are the same idea; the difference is **where the rule lives and who can read it**. A rule the
mesh owns is a rule the mesh can audit, list, and revoke as a node — and it cannot silently drift
into the state the first `upstream-seed` run found, where every credential covered the wrong event.

**What has landed.** The registry serves publications under its own authenticator (#2487), the
verifier's GitHub leg and the `BuildPrincipal` node type shipped with #2483, and a build principal is
admitted on the prebuilt routes requiring `fetch:<source>` — the full mechanics, the refusal matrix
and the fail-closed rules are in
[Access control](/Doc/Architecture/AccessControl) → *Build principals*.

**What has not.** `upstream-seed` still logs in to Azure rather than presenting its run's OIDC token,
so on a pull request it still hits `AADSTS700213`; that is recorded on the PRs that hit it. The
workflow change is a satellite-repo edit (`ACTIONS_ID_TOKEN_REQUEST_URL` with
`audience=<registry>` → `Authorization: Bearer` → the prebuilt GET), and the registry side is now
waiting for it rather than the other way round. Provisioning a `pull_request` credential in Entra
would also make it work — and would be one more rule in the wrong place.

---

## Three rules that follow, stated so they cannot be re-derived wrongly

**1. The platform never bakes a plugin.** Core's CD builds the platform and bakes the platform's
OWN shipped content (`meshweaver-content`). It does not check out `MeshWeaver.Plugins` to compile
it, and it does not publish a `plugins` source. Until 2026-08-27 it did both — `main-cd.yml` synced
a Plugins checkout, Roslyn-compiled every module, and published the result to the same
`prebuilt-bundles/<identity>/plugins/` shelf that Plugins' own `publish-bake` writes. Two
producers, one shelf, whichever sealed last winning; and a full compile of source the platform
does not own, on every platform push. Plugins bakes Plugins. (The two checkouts that remain in
core CD fetch the portal HOST and migration WORKER projects, which live in that repo since #2293
— source the image is built FROM, not a plugin being baked.)

**2. Nothing syncs plugin source to bake it.** "Sync the source and compile it" is the pattern
every violation shares: core's `plugin-gate`, the satellites' `stage-repo`, Education's fused
checkout. The consumer of a plugin receives a **publication** — bundles carrying node definitions
and compiled assemblies, sealed under a framework identity — and installs it. If a lane needs a
plugin and reaches for `git clone` of its repo, that lane is wrong.

**3. A source install is git on disk, never rows in a database.** When a mesh installs a plugin
from source (a local dev loop, an e2e mesh, a GitSynced space), the package source is a git
checkout the mesh READS — `GitPackageSource(git, repoPath)`, `git ls-tree` / `git show` at a ref —
so the repo stays the only source of truth and a re-sync rewrites the space from it. An importer
that copies plugin nodes into the store as durable rows creates a second copy that the repo no
longer owns: the live edit that "sticks" until the next sync silently reverts it, the
`MonotonicWriteGuard` refusals when a committed `version` collides with the store's clock. The
mesh reads the tree; it does not ingest it.

---

## The contract, restated as checks

For any plugin repo, these are answerable and should be answered:

| step | the check |
|---|---|
| 1. Take the image | the image is pinned by **digest**, not a moving tag |
| 2. Install dependencies | the gate passes `--seed`, and the seed **reports** what it covered — a green gate alone proves nothing |
| 3. Build my plugin | exactly one `--bake-output` run, over this repo's own packages |
| 4. Test | the gate stands up on the **baked bytes**, so what is tested is what ships |

A repo that cannot answer (2) with a number is compiling its dependencies, whatever its workflow
says.

---

## See also

- [CI Content Bake](/Doc/Architecture/CiContentBake) — the producer, and how the image ships what it bakes
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — what the runtime compile actually does
- [Build Server](/Doc/Architecture/BuildServer) — image-only CI and the dependency cascade
- [Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — how a build reaches an instance
