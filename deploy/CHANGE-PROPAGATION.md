# How long does a change take to reach production?

Four kinds of change reach a running mesh by **completely different routes**, with costs that differ
by two orders of magnitude. Knowing which route a change takes is the difference between "ship it
now" and "wait for the window".

The short version:

| Change | Route | Time to live | Restart? |
|---|---|---|---|
| **a. MeshWeaver core** | CI → CD image → in-pod self-update → *(bake)* | **~30-45 min** + rollout + bake | yes, every pod |
| **b. Central plugin** (`Store`) | CI → registry → GitSync/Catalog → recompile | **~15 min** | no |
| **c. Peripheral plugin** | CI → registry → GitSync/Catalog → recompile | **~15 min** | no |
| **d. Downstream content** (`education`) | CI → GitSync → import | **~5-15 min** | no |

> **Numbers marked *(measured)* are real observations, dated. Everything else is an estimate and
> says so.** Do not quote an estimate as a fact. The bake figures below were estimates until
> 2026-08-10, when production Loki was read across all three portals — and the estimate was **37x
> too high** (see [the bake tax](#the-bake-tax)). Two deploy budgets had been sized from it.

---

## a. A change in MeshWeaver (the framework)

The only route that produces a new **image** and restarts pods.

| Stage | Cost | |
|---|---|---|
| `MeshWeaver Build and Test` (build + 6 test shards) | **22m19s** | *(measured 2026-07-28)* |
| `Continuous Delivery (main)` — multi-arch publish of portal (~2.2 GB), migration, **bake**, plugin-test | **28m38s** | *(measured 2026-07-28)* |
| In-pod self-update: detect tag → pull → patch its own Deployment | pull-bound | estimate |
| Rollout: `maxSurge 1` / `maxUnavailable 0` — the new pod must pass `/health` before the old drains | boot-bound | |
| **NodeType bake** — only if the framework MVID changed (below) | see [bake](#the-bake-tax) | |

**CI gates CD**: CD's `gate` job publishes only when `Consolidate test results` — the repo's single
required check — is `success` on the target commit. A red build publishes nothing, so a broken core
never reaches an image. (It keys on the required check, not the run's umbrella `conclusion`: one
starved job that never ran a step poisons that conclusion and would silently stop delivery.)

**CD publishes all-or-nothing, and reconciles.** Each image leg pushes only a non-selectable
`staging-<sha>-<run_id>` tag; a `promote` job applies the real tags once every leg has succeeded,
ending with `memex-portal-ai:<version>` — the one tag the in-pod self-updater acts on. So a leg
that dies can no longer leave a portal increment the bake gate never certified (#1026). And a CD
failure is no longer terminal: a 3-hourly schedule (plus `gh workflow run main-cd.yml --ref main`)
asks whether main's HEAD has the complete set in ACR and publishes only if it does not, bounded at
3 attempts per commit with every attempt written to the `ci-failure` issue. It heals **HEAD**, never
the commit that failed — the version tag comes from the building run's number, so re-publishing
older code would mint a higher `-ci.<n>` and roll installs backwards.

> **CD publishes four images**, and the `memex-bake` leg alone took **12m23s** of that 28m38s
> *(measured 2026-07-28)* — it is a full portal-sized dependency closure, because reference fidelity
> requires it to reference `Memex.Portal.Shared` (a NodeType compiles against the assemblies loaded
> in the process doing the compiling, so a leaner reference set could bake green and still fail in
> the portal). That is a real, recurring cost on **every** core release, paid whether or not the bake
> is enabled on any environment. If it ever needs cutting, the lever is publishing it on a schedule
> or only on release tags — not trimming its references.

### Every core release invalidates the compile cache — plan for the bake on all of them

This page used to claim the opposite: that a release touching only, say, `MeshWeaver.Blazor.Portal`
leaves Graph's bytes identical, so "every cached assembly stays valid and there is no bake at all".
**That is false, and it is false twice over.** Do not plan a release around it.

The compile cache keys on **Graph's MVID** (`NodeTypeCompilationHelpers.FrameworkVersion` — the
`MeshWeaver.Graph` module's content hash). Both halves of the old claim break down:

1. **The MVID is not content-stable in CI.** `Directory.Build.props` stamps the
   `InformationalVersion` property with `$([System.DateTime]::UtcNow.Ticks)` under `CIRun`, and that
   attribute is compiled *into* every assembly — Graph included. So **Graph's MVID changes on every
   CI build regardless of which source files the release touched**, and every cached assembly is
   invalidated on every core release. That stamp is deliberate and load-bearing (see point 2); the
   mistake was reasoning about the MVID as if it were a pure content hash of Graph's *source*.
2. **Graph's dependency closure is not the ABI boundary anyway.** A NodeType compiles against
   `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` — **every assembly in the image**, not just
   what Graph references (`MeshNodeCompilationService.GetDefaultReferences`). So a breaking change
   in an assembly Graph never references is still inside a NodeType's reference set, and a
   content-stable Graph MVID would happily declare those stale assemblies valid and load them.

Together: the ticks stamp is what keeps invalidation *conservative* over that wider reference set.
**Budget every core release as a full bake** — which, at the real measured cost below, is minutes,
not the hours this page used to imply.

---

## b/c. A change in a plugin (`MeshWeaver.Plugins`)

No image, no pod restart. A plugin is **mesh data**: node-per-file JSON plus C# the mesh compiles
live. Merging to `main` is the deploy — the registry (`memex.meshweaver.cloud` `/api/plugins`) serves
this repo's `main` to every installation's Plugin Catalog.

| Stage | Cost | |
|---|---|---|
| `Validate node repos` (JSON shape) | **9s** | *(measured 2026-07-28)* |
| `Compile every NodeType (vs core)` — the hard gate | **13m55s** | *(measured 2026-07-28)* |
| Apply: GitSync `update` on the Space, or Plugin Catalog install | seconds-minutes | estimate |
| Recompile of the changed type(s) | 2s-92s per type | *(measured, see below)* |

### 🚨 CI cost is FLAT — it does not scale with your change

The compile gate compiles **every** NodeType in the repo (149 of them), not just the ones you
touched. So a one-line fix in a peripheral plugin costs the same ~14 minutes as a rewrite of `Store`.
That is deliberate — a change to a shared source can break a type in a different plugin — but it
means **b and c have the same CI cost**. The difference between them is blast radius, not time.

### Where b and c actually differ: blast radius

- **Peripheral plugin** — one type recompiles; only that plugin's instances are affected.
- **`Store`** — `Store/Plugin` is the type **every plugin root is an instance of**. Recompiling it
  (**92s measured** on memex, ~30 source files) re-activates every plugin root's hub on the new
  assembly. Nothing else recompiles, but the fan-out is mesh-wide, so prefer a quiet window.

---

## d. A change in a downstream content repo (`education`)

Course content is **Markdown and exercise nodes** — no C#, so in the common case **nothing compiles
at all**. The Space GitSyncs from the repo; an `update` imports the changed nodes and they are live.

Cost is dominated by that repo's own CI, not by the mesh. Only if the change touches a NodeType's
`Source/` does a recompile enter the picture, and then it is a single type (b/c pricing).

> ⚠️ `education`'s "Install + execute on a disposable mesh" job is **`continue-on-error`**, so
> `Education Content CI` can report **success while every e2e shard fails**. Observed 2026-07-28:
> 3/3 shards red under a green run. Read the *jobs*, not the run badge, before trusting that gate.

---

## The bake tax

When the framework MVID *does* change, every dynamic NodeType must be recompiled against the new
framework — by design, for ABI safety (assemblies built against the old framework may reference
members whose signatures moved).

**This cost is not new and the bake does not add it.** Without the bake it is paid *lazily*:
unordered, on user requests, on a pod already serving, ~2.4 s at a time in front of whoever arrives
first. The bake moves the same work ahead of the rollout, where nobody is waiting.

### The real cost: ~2.4 s per NodeType *(measured 2026-08-10)*

Read from production Loki across **all three portals running the same image** — the per-type figure
agrees to within 3% across a 10x spread in mesh size, which is what makes it trustworthy:

| Portal | Types | Full warm-up (median) | Per type |
|---|---|---|---|
| **memex-cloud** | 237-241 | **567 s** (~9.5 min) | **~2.4 s** |
| **memex** | 72-81 | **179 s** (~3 min) | **~2.3 s** |
| **atioz** | 22-36 | **68 s** (~1 min) | **~2.3 s** |

Other measurements, for context:

| Observation | |
|---|---|
| Resident memory per compiled type | **~46 MiB** *(measured 2026-08-10)* |
| `Store/Plugin` alone (~30 source files) | **92 s** *(measured, memex)* — an outlier, ~38x the median |
| 4 small Doc types, full bake | **7.8 s** *(measured, local k3s, 2026-07-28)* |

**A full bake on the largest production mesh is ~10 minutes.** Not the "~1.5-2.5 h" this page
carried until 2026-08-10.

> #### 🚨 How the old number went wrong, and what it cost
>
> The retired estimate was **"~90 s per NodeType"** — **37x** the measured ~2.4 s. It came from
> extrapolating the single *largest* type (`Store/Plugin`, 92 s, ~30 source files) across the whole
> mesh. The page even flagged the alternative — "the local types averaged ~2s each" — and that end
> of the range turned out to be the right one.
>
> This was not a harmless doc error: **two production deploy budgets were sized from it** — the
> startup-probe budget (10 × 1080 = 3 h) and the bake Job's `activeDeadlineSeconds` (14400 = 4 h).
> A budget derived from a 37x-overstated constant does not fail loudly; it silently stops being a
> detector. Every real bake finishes inside ~10 minutes, so the remaining 2h50m of that probe window
> separated nothing: a wedged pod and a healthy one look identical for the rest of it. Both budgets
> have been re-derived from the measured figure — 30 min and 1 h respectively, with the arithmetic
> written next to each value in `deploy/helm/values.yaml`.
>
> The lesson worth keeping: **never extrapolate a population from its largest member**, and treat a
> budget whose constant was never measured as unsized rather than generous.

Properties that bound the damage:

- **Resumable** — an interrupted bake picks up from what is already on the share.
- **Shared** — a second pod inherits the first one's work instead of repeating it (one pod bakes,
  the rest follow a lease).
- **Off the critical path** — old pods serve throughout; new-image assemblies coexist with the old
  ones on the volume because the framework tag is part of the filename.

See `deploy/helm/values.yaml` (`bake`, `probes.startup`) — all bake flags default **off**, and
enabling the readiness gate requires raising the startup-probe budget in the same change.

---

# Bootstrapping a fresh mesh: nobody is a global admin

A brand-new mesh has **no global admin at all**, and the way that fails is circular enough to burn an
afternoon, so it is written down here rather than rediscovered.

- **`Admin` is a framework BUILT-IN partition** (`DefaultPartitionProvider`, listed among the reserved
  names), so it exists on every mesh from first boot.
- **A fresh mesh has no grants on it.** Verified against a disposable mesh's Postgres:
  `Admin/Partition/Admin`, `Admin/PlatformVersion`, `Admin/UpdatePolicy` — and **no `_Access` rows at
  all**. (`samples/Graph/Data/Admin/_Access/*` grants `Roland`/`Samuel`/`TestUser`, but that is dev
  sample data and does **not** ship in the portal image.)
- **`IsGlobalAdmin` is defined as `Permission.All` at scope `Admin`** — so with no grant on `Admin`,
  nobody holds it.

**The circularity:** anything that writes under `Admin/…` (seeding a Store coupon at the fixed
`Admin/Coupons/{CODE}`, say) is denied. The obvious escape — create the `Admin` Space, since a Space
grants Admin to its creator — **cannot work**: `Admin` already exists, so the create is a tolerated
no-op and no grant is ever issued. Writing under `Admin` requires a grant obtainable only by writing
under `Admin`.

**The supported way in is `Auth:GlobalAdmins`.** `GlobalAdminSeed` materialises a root-scope Admin
`AccessAssignment` for each configured id through `AddMeshNodes`, which bypasses exactly that
circularity because seeded nodes are not permission-checked:

```yaml
Auth__GlobalAdmins__0: "the-user-id"
```

Dev-login does **not** confer this. Self-provisioning gives a user their own space and nothing more —
"the first user becomes the platform admin" is folklore, and a harness that assumes it will fail on
the first write into a framework partition.

> ⚠️ **Env-var gotcha.** Logging categories are DOTTED keys, so
> `Logging__LogLevel__MeshWeaver__Messaging` silently creates a *nested* `MeshWeaver:Messaging`
> section and changes nothing. The category is `"MeshWeaver.Messaging"` — write
> `Logging__LogLevel__MeshWeaver.Messaging=Trace`. Symptom: you raise the level, re-run, and the
> trace file has zero `MESSAGE_FLOW` lines.
