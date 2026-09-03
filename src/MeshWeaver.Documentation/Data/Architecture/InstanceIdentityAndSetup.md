---
Name: Instance Identity and Setup
Category: Architecture
Description: An instance is a partition on the registry, authenticates with a JWT, carries its licence (plan) on its own node, and sets itself up through the Hosting app — the design, what already exists, and the slices that deliver it
Icon: Server
---

# Instance Identity and Setup

**Status: design, 2026-08-30. Delivered in slices (see the end of the page); each slice links back here.**

This page is the design for four maintainer directives given on 2026-08-30, quoted so the intent is
not paraphrased away:

1. *"we must really change environments through the hosting plugin. we must have an app for it …
   it should then ask us which db we want (pg preselected) and ask us where (default suggests to
   set up a pg via docker and use it). it allows to select modules. default all free pre-selected,
   availability of others depends on license of the instance. each instance has an id ⇒ registers
   in meshweaver.cloud. default: all instances free. global admins can promote licenses to other
   tiers. if higher tier, we must be able to select these modules at startup. write e2e tests."*
2. *"an instance id (id of the mesh node; every client gets its own partition, like user but Memex
   instance)"*
3. *"we authenticate via JWT and check licenses"*
4. *"all must be set in this spirit"* — including the state that was hand-set before the
   directive (slice 2 below).

## What exists today, and where it falls short

The survey behind this design (three read-only sweeps of core, `MeshWeaver.Plugins/Hosting` and
`deploy/`) found that most of the *parts* ship; what is missing is the shape that joins them.

| Concern | Ships today | Falls short |
|---|---|---|
| Instance record | `MeshWeaverInstance` (`src/MeshWeaver.Mesh.Contract/Security/MeshWeaverInstance.cs`) hashed into an index under the registry's admin tree; grant at `Admin/_PluginGrant/{id}` | A row, not a place: nothing else about the instance can live "with" it, and the id is not a node id |
| Registry authentication | Opaque `mwi_` bearer key on every fetch (`InstanceRegistryAuthenticator`); an HMAC `mwa_` short-lived token exists for the **sync** lane only (`SyncAccessToken`, `POST /api/instances/token`) | Not a JWT; the long-lived secret travels on every request; the token lane is not used for package fetches |
| Licence | `PlanTierRanks` + `PluginGrantEntry.Tier` (`Plugins/*@free`) decide `/api/plugins` and every bundle route; `Admin/Tiers/*` is the ladder | *"A plan-less entry covers every tier"* — every instance registered before the plan lane (memex-cloud included: `Plugins/*`, `Education/*`, `Crm/*`) can pull `pro`/`enterprise` bundles. The plan is encoded in grant strings, N places to get wrong, and "no suffix" silently means "everything" (#2804) |
| Registration | `POST /api/instances/register` with an `mwr_` key minted for a plan, or open (un-keyed → the registry's configured free-plan key); first-boot auto-registration (`InstanceAutoRegistrationService`) | Works; keeps working unchanged in spirit — only where the result lands changes |
| First-run setup | `InstanceManifest` (`instance.json` on the writable root; `AwaitingStorage → AwaitingModules → Complete`), `InstanceSetupDefaults` (`PostgreSql` preselected, `Plugins/*`, the required modules), `MeshBuilder.IsAwaitingSetup` (#2550) | **Nothing reads `IsAwaitingSetup`** — the host has no setup surface; `memex-local` asks no questions at all |
| A setup wizard | `Hosting/FleetConsole/Source/SetupDialog.cs` — six steps (Identity · Main database · Main storage · Boot modules · Packages & repos · Review), files a `Hosting/InstanceRequest` | Provisions *another* instance in a fleet, not the one you are on; its module step is a free list with no plan |
| Environment changes | `Hosting/Deployment` (the full instance shape, ~60 fields, all `[Description]`+`[Translation]`), `HelmValues.Render` (a values file that `hosting-deploy` **refuses unless** it carries `# GENERATED from the Hosting/Deployment record`), `Hosting/InstanceAction` verbs run by the in-cluster `hosting-operator` Job | `Hosting/Deployment/*` is **empty** on every mesh we run. memex-cloud's configuration is hand-maintained in Key Vault (`helm-values-memexcloud`) and a Memex-repo overlay, applied by a hand-dispatched workflow (Systemorph/Memex#152) |
| The app | `Hosting` is a Store package with `app: true`, `entryPoint: Hosting/Console` — it has a home tile | `tier: enterprise` — the one package every instance is supposed to run cannot be installed by a free instance |
| E2E | Plugins `e2e/{instance-lifecycle,instance-request,setup-dialog}.spec.ts` (dry-run, `.invalid` DNS); core `MeshWeaver.Testcontainers` (#2790, a disposable memex with `WithPostgres`); `memex-local instance up` (mint → migrate → deploy → register → install) | No suite joins install → wizard → registered instance → plan-limited modules → promotion |

## The design

### 1. An instance is a partition

On the registry mesh (memex.meshweaver.cloud) every client instance is a **partition root node**,
exactly as a user is: node type `Instance`, node id = the instance id, path = the id. The instance
id therefore obeys the partition-id alphabet (`3–48` chars, `[a-z0-9-]`, no leading/trailing
hyphen — the rule `MeshWeaverInstanceService.IsValidInstanceId` already enforces).

```
memex-cloud/                       ← the registry's own instance partition (IsPluginRegistry)
roland-rolands-macbook-pro/        ← a Homebrew install
  (root node, nodeType Instance)   ← DisplayName, HomeUrl, Owner*, Plan, Status, CreatedAt, LastSeenAt, IsDisabled
  _PluginGrant/grant               ← which SOURCES/packages this instance may see (never which tiers)
  _Credential/{kid}                ← the credential(s) the instance authenticates with (hash / public key, never the secret)
  _Activity/…                      ← registrations, promotions, token issues — the audit trail, where activities always go
  Deployment                       ← the Hosting/Deployment record for THIS instance (slice 2 puts memex-cloud's here)
  Inventory                        ← the module inventory the instance reports (today Hosting/Modules inbox → moves here)
```

What this buys, beyond tidiness:

- **One place for everything about an instance** — licence, grants, credentials, deployment record,
  inventory, audit — under one access boundary. The instance principal (the JWT subject, below)
  is granted **write on its own partition and nothing else**, which is what lets an instance change
  its own environment through Hosting without anyone handing it cluster credentials.
- **Promotion is one field.** A global admin sets `Plan` on the root node (the Instance grants
  admin tab gains a plan column with a dropdown fed by the `Admin/Tiers` ladder, never free text);
  the next fetch sees it — the authenticator caches for one minute.
- **The registry's own instance is not special.** memex-cloud is the partition `memex-cloud/` with
  `IsPluginRegistry: true` and plan `dedicated`; the same code that describes a Homebrew install
  describes it.

Content of the root node (a new record in `MeshWeaver.Mesh.Contract`, replacing
`MeshWeaverInstance` in the registry lane; the old record stays as the wire shape the consumer
reads back from `POST /register` until slice 1 is everywhere):

```csharp
public record InstanceContent
{
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string HomeUrl { get; init; } = "";
    public string OwnerUserId { get; init; } = "";
    public string OwnerUserName { get; init; } = "";
    public string OwnerUserEmail { get; init; } = "";
    /// <summary>The licence: a tier id from Admin/Tiers. Absent reads as "free" — never as "everything".</summary>
    public string Plan { get; init; } = PlanTierRanks.Baseline;
    public bool IsPluginRegistry { get; init; }
    public bool IsDisabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}
```

**Migration.** Existing `MeshWeaverInstance` index entries become partitions with the same id;
`Admin/_PluginGrant/{id}` moves to `{id}/_PluginGrant/grant` with its `@plan` suffixes **removed**
and the plan written on the root — the *highest* plan any entry named, else `free`. memex-cloud and
the maintainer's own instances are promoted explicitly (`dedicated`, which is all-access). The
migration is a Repair vN migration, never a raw SQL update ([Postgres schema architecture](../PostgresSchemaArchitecture)).

### 2. The licence lives on the instance and is checked on every fetch

`PluginGrant` entries keep saying **which sources and packages** an instance may see; they stop
saying which tiers. Every registry decision becomes:

```
Allows(instance, source, package, packageTier) =
    grant matches (source, package) ∧ entry within its term
  ∧ PlanTierRanks.Covers(instance.Plan, packageTier)
```

with the ladder read from `Admin/Tiers/*` as today (`PlanTierLadder`), `Covers` unchanged (a
package with no tier is baseline and covered by every plan; unknown ids fail closed; an all-access
plan covers everything). Consequences, all of them the point:

- A plan-less grant entry no longer means "every tier" — it means "these packages, at my plan".
  `PluginGrantEntry.Tier`, `IsPlanScoped`, `Covers` and the `@plan` parse/format go away; a
  `RegistrationKey.Tier` stays, because a key still says which plan the instance it registers
  *starts* on.
- Whole-source publication fetches (the `prebuilt/{identity}/{source}` bake routes) still require
  an explicit whole-source entry **and** are capped by the plan.
- A not-covered package is still a byte-identical 404 on the bundle routes. On the **listing**
  route the registry now also serves what the caller is *not* entitled to, marked, so a module
  picker can show "requires pro" instead of an empty shelf — see the wizard below. Nothing a
  caller is not entitled to is ever *downloadable*; the listing is metadata.

### 3. Authentication is a JWT

The long-lived instance credential is used **once per token lifetime**, never on a package fetch:

1. At registration the registry returns the credential exactly once, as today (`mwi_…`, stored
   `enc:`-protected under the install's master key — the #2585 pre-flight stays).
2. The instance exchanges it at `POST /api/instances/token` for a **JWT** (RFC 7519; `alg` HS256
   under the registry's signing key from the existing signing-key node, `kid` set; `iss` = the
   registry's base URL, `sub` = the instance id, `aud` = `plugins`, `iat`/`exp`, `exp − iat ≤ 1 h`).
   `SyncAccessToken` — already an HMAC-signed `{InstanceId, KeyHash, ExpiresAt}` payload with a
   15-minute default — becomes this JWT rather than a sibling format; the `mwa_` prefix is retired.
3. Every `/api/plugins/*` request presents `Authorization: Bearer <jwt>`. The authenticator
   verifies signature and expiry, resolves the partition root by `sub`, refuses a disabled instance,
   and attaches `(Instance, Grant, Ranks)` for the licence decision. **The plan is read from the
   node, not from a claim** — a promotion takes effect within the cache window, not at the next
   token.
4. Verification is one code path with **one verifier, two issuers**: registry-signed instance
   tokens here, and GitHub's OIDC for the build principal (#2483 — `token.actions.githubusercontent.com`
   against its JWKS, one trust node per repo). The verifier takes an issuer → key-material
   resolver; adding RS256 + a published `/.well-known/jwks.json` for instance tokens is a later
   step on the same path, not a redesign.

Transitional: until every consumer runs slice 1, the authenticator accepts the raw `mwi_` credential
on the fetch routes too, logs it as legacy, and the consumer's `RegistryTokenResolver` prefers the
JWT the moment its registry advertises the token route.

### 4. The Hosting app — setup on the instance itself

The first-run wizard is a **platform** surface, not a plugin: it runs before there is a database,
so nothing installable can host it. It lives in core (`memex/Memex.Portal.Shared`), is served when
`MeshBuilder.IsAwaitingSetup` is true — *"a host that reads this true must serve the SETUP surface
and nothing else"* is what the property's own doc comment already demands — and it **writes the
same artifact the fleet route writes**, `InstanceManifest`, so the interactive and the fleet
provisioning cannot drift. It reuses `SetupDialog`'s step model and pure composition
(`InstanceComposition.Compose/Describe`) rather than forking it.

The steps, in the maintainer's order:

| Step | Asks | Default | Writes |
|---|---|---|---|
| Database | Which storage backend — the list is the keyed `IStorageAdapterFactory` registrations this image ships (*"discovered, never hardcoded: an image without the Cosmos module must not be able to record Cosmos here"*) | **PostgreSQL** (`InstanceSetupDefaults.StorageType`) | `InstanceStorageSelection.Type` |
| Where | *Set one up for me* (a `pgvector/pgvector:pg17` Postgres via Docker — the `memex-postgres` service of the generated compose topology `deploy/compose`, or the chart's in-cluster StatefulSet when the host is Kubernetes) · *Use an existing server* (connection string, **write-only**, `enc:`-protected — the `NewInstanceRequest.PrepareSecret` rule) | **Set one up for me**, when a Docker socket or an in-cluster Postgres is reachable; otherwise the connection-string form | `InstanceStorageSelection.ConnectionString / SecretName`, and for Docker the compose fragment the host applies |
| Instance id | The id, defaulting to `<user>-<host>` (the `memex-local` rule), validated with the partition alphabet; a registry URL (default `https://memex.meshweaver.cloud`); optionally a registration key | Open registration → **free** | `PluginCatalog:InstanceId / RegistryUrl / BootstrapKey` |
| Modules | The registry's listing **with entitlement marks**: every `free` package pre-selected; packages above the instance's plan shown, disabled, with the plan that unlocks them | All free selected | `InstanceManifest.ProvisionPackages` (+ `BootModules` from `InstanceSetupDefaults`) |
| Review | The composed instance, secrets never shown | — | `InstanceManifest` with `State = Complete`; then registration, the default installs, restart |

Two rules the wizard inherits from the platform rather than restating: the first user who signs
in on a fresh mesh is the global admin (DevLogin self-provisioning), and per-user first-run
guidance is a `LogonAction` ([Logon actions](../LogonActions)), not part of instance setup.

**Promotion at startup.** *"if higher tier, we must be able to select these modules at startup"* —
the Modules step reads the entitlement-marked listing with the instance's current plan, so an
instance promoted on the registry before (or while) it sets up sees its `pro` packages selectable.
An instance promoted *after* setup gets the same picker on the Hosting app's *This instance* page:
selecting a newly covered package there is the ordinary Store install.

**The app's tier.** `Hosting` splits into the free `Hosting` (this instance: its `Deployment`
record, plan, modules, inventory; the setup wizard's post-setup twin) and the enterprise
`Hosting.Fleet` (the Fleet Console, `InstanceRequest`, the operator). Both keep `app: true`; every
instance installs the free one by default (`installByDefault`), which is what *"see that we can
now install the app"* asks for.

### 5. Environments change through the record

The `Hosting/Deployment` record is the **only** description of an environment; the operator's
`hosting-deploy` already refuses a values file that was not rendered from it. What this design
adds is that the record lives in the instance's partition, that secrets are named on the record
and resolved by the operator at render time (the `KeyVaultSecretsSpec` shape that already exists —
*"the Key Vault fields name secrets; they never hold them"*), and that a change is an MCP `patch`
of the record plus an `InstanceAction` — reviewable in the partition's activity log, never an
`az keyvault secret set` or a hand-dispatched workflow.

For a Homebrew install the same record renders the `memex-local` values layers instead of AKS
values: `memex-local up` becomes "render the record, apply the chart", and the three hand-written
values files (`values.local.defaults.yaml`, `values.local.self-registry.yaml`, the user overlay)
become the record's defaults.

### 6. E2E — what proves it

A joined-up suite in `MeshWeaver.Plugins/e2e/` (Playwright, the existing `CAN_MUTATE` /
`.invalid`-DNS guards) driven by two disposable memexes from `MeshWeaver.Testcontainers`:

1. **Registry** boots with an open-registration key minted for `free` and a `pro` package in its
   catalog.
2. **Consumer** boots empty → the setup surface is served (and nothing else) → Database: PostgreSQL
   preselected → Where: the Postgres the harness provides → id: unique per run → Modules: the free
   packages are pre-selected, the `pro` one is listed disabled with "requires pro" → Review → the
   instance partition exists on the registry with `Plan = free`.
3. On the wire: `GET /api/plugins/bundles/index.json` with the consumer's JWT lists the `pro`
   bundle as not entitled; `GET /api/plugins/bundles/<pro>/<version>` is 404; a raw `mwi_` on the
   route is accepted only while the legacy window is open.
4. A global admin promotes the instance to `pro` on the registry → within the cache window the
   index marks the bundle entitled, the download is 200, and the consumer's *This instance* page
   shows it selectable; an un-promoted second consumer still gets 404.
5. A `Hosting/Deployment` record change on the consumer's partition + an `InstanceAction`
   restarts the consumer with the new configuration (the #2801 checksum rollout on Kubernetes; a
   container restart under Testcontainers).

CI runs the static half everywhere (`tsc --noEmit` + `playwright test --list`, as `e2e-static`
does today) and the live half in the lane that already pulls the platform image by digest — no
`continue-on-error`, no `if: secrets…` trapdoor on the input step ([CI signals](../ReadingCiSignals)).

## Slices

Each slice is one PR (or one per repo, platform first), merged in this order; each links here.

### Slice 1 — the licence on the instance, the instance as a partition, JWT (#2804)

Core. `Instance` partition type + `InstanceContent.Plan`; registration and open registration create
the partition; `PluginGrant` loses tiers; every registry decision is plan-vs-tier; the admin tab
promotes; the migration; `POST /api/instances/token` issues the JWT and every `/api/plugins/*`
route accepts it (legacy `mwi_` on the fetch routes for one release, logged). Tests:
`PluginBundlePlanTest` gains *"legacy plan-less grant + pro package → 404"* and *"promotion widens
within the cache window"*; a JWT verifier test with a forged signature, an expired token, an
unknown `sub`, and a disabled instance.

### Slice 2 — memex-cloud is described by a record (Systemorph/Memex#152)

The first `Hosting/Deployment` record anywhere: `memex-cloud/Deployment` on the registry, absorbing
the Key Vault values (secrets by **name**) and the Memex-repo overlay; `HelmValues.Render` produces
the values `hosting-deploy` accepts; the operator applies it as an `InstanceAction`; the Key Vault
values secret and the overlay file are deleted, and `helm-release.yml`'s `capture/adopt/deploy`
retire into the action's verbs (the self-updater's direct Deployment PATCH retires with them — MeshWeaver.Plugins#979 already removes its dead migration half). This is also what makes today's open-registration key live — the
pods roll because the record changed (#2801), not because someone ran a workflow — and therefore
what brings the maintainer's local instance back as a registry consumer (#2417's last mile).

### Slice 3 — the setup surface ✅ DELIVERED

Core. **Shipped — see [First-Run Setup](../FirstRunSetup) for what was built and why.** The host
serves the wizard when `IsAwaitingSetup`; storage-backend discovery from the keyed adapter
registrations; writes `InstanceManifest`, restarts into `Complete`. `memex-local up` stops writing
auth and storage values by hand and hands over to the wizard (`memex-local setup`).

Three things landed differently from this design, each for a measured reason:

- **The default backend is SQLite, not "set up a Postgres for me".** A laptop install should need
  nothing else running, and SQLite is a full backend here — partitions, durable event log, local
  vector search. It needed a keyed `IStorageAdapterFactory`, without which
  `Graph:Storage:Type=Sqlite` answered `Unknown storage type` and the wizard could not offer it.
- **It is a SEPARATE host, not a branch in the portal's pipeline.** Two attempts to serve the wizard
  from inside ordinary startup died before reaching it (a permission-evaluator assertion, then a
  module-registered `IHostedService` with no `IMeshService`). See the delivered page.
- **The manifest gained `SignIn` and `Ai` sections** — the platform change `SignInSetupTab` named as
  missing — plus a projection into configuration, inserted at index 0 so deployment configuration
  still wins.

### Slice 4 — the app and the proof

Plugins. `Hosting` (free) / `Hosting.Fleet` (enterprise) split; the *This instance* page over the
partition's record with the plan-aware module picker; the Playwright suite in §6 and its CI lanes.

## Related

- [Plugin Registry](../PluginRegistry) — today's grant, plan and token mechanics, which slice 1 reshapes
- [Instances](../Instances) — what an instance is operationally; self-update
- [Deployment on AKS](../DeploymentAKS) · [Memex Cloud deployment](../MemexCloudDeployment)
- [Apps home](../AppsHome) — how an `app: true` package gets its tile
- [Disposable mesh E2E](../DisposableMeshE2E) — the harness shape the suite in §6 follows
- Issues: #2804 (licence on the instance), Systemorph/Memex#152 (memex-cloud record, transferred from #2805), #2417 (a local install as a registry consumer), #2483 (build principal — the second issuer of the shared verifier), #2550 (the manifest the wizard writes)
