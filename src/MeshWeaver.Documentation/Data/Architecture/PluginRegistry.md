---
Name: Plugin Registry
Category: Architecture
Description: One MeshWeaver instance acts as the plugin registry — it holds the source credential, syncs plugins from git, and re-serves them over a token-gated REST surface so any registered installation's platform admin can browse and install plugins without its own GitHub access. The credential is encapsulated in the registry, npm/NuGet-style.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3h18v4H3z"/><path d="M5 7v14h14V7"/><path d="M10 12h4"/></svg>
---

# Plugin Registry

For the step-by-step how-to (author → publish → install → own registry) see the [Plugin Manual](/Doc/Architecture/PluginAuthoring).

[Plugins](/Doc/Architecture/Plugins) are folders of mesh nodes in a git repo, each rooted in a
node-native `<Plugin>/index.json` **Store/Plugin** node carrying a `PluginContent`
([anatomy](/Doc/Architecture/PluginAuthoring)) — a `package.json` manifest is the alternate,
non-default source format. Installing one means importing its nodes and compiling its node types
live. But you do **not** want *every* installation to hold GitHub credentials for a private plugins
repo just to receive a plugin.

The registry solves that. **One** MeshWeaver instance (memex.meshweaver.cloud) is the registry: it
alone holds the source credential, reads the plugins repo, and **re-serves the catalog over an
authenticated HTTP surface**. Every other installation browses and installs from the registry, never
from git. The credential is **encapsulated in the registry** — exactly like npm or NuGet, where the
registry has source access and clients just speak HTTP.

```text
  Systemorph/MeshWeaver.Plugins (git, private)
                │  the registry reads it with its ONE GitHub App credential
                ▼
        ┌───────────────┐   GET  /api/plugins            ┌────────────────────────┐
        │   registry     │◀───────────────────────────────│ installation (consumer)│
        │ memex.mesh…    │   (Bearer mwi_ instance key)    │  the Store's catalog   │
        │ holds the cred │   POST /api/plugins/files {id}  │  + boot-time default   │
        │                │────────────────────────────────▶│  install               │
        └───────────────┘     {packages} / {files}         │  no GitHub credential  │
                                                            └────────────────────────┘
```

## The surface — registered instances only, curated

Two endpoints, mapped by `PluginRegistryEndpoints` (`memex/Memex.Portal.Shared/Api`). The surface is
**not public**: it serves only **registered MeshWeaver instances**. A caller presents its instance
key as `Authorization: Bearer mwi_…` (its `PluginCatalog:RegistryToken`, or per-registry
`Registries:N:Token`); `InstanceRegistryAuthenticator` resolves that key to a `MeshWeaverInstance`
node and the admin-owned `PluginGrant` that says which `(source, package)` pairs it may read, and a
request without a valid key is **401**.

The gate **fails closed**: `PluginCatalog:RequireInstanceKey` defaults to `true`, so a registry that
configures nothing refuses anonymous callers rather than serving them everything. The anonymous mode
must be asked for explicitly (`RequireInstanceKey=false` — local dev / the e2e stub) and warns on
every request.

> 🚨 The flat `PluginCatalog:RegistryTokens` allowlist this replaced is **obsolete and no longer
> read** (`PluginRegistryTokens.SectionName`/`.Validate` are `[Obsolete]`). It was *open when unset*,
> which is how this registry served its private sources to anonymous callers until 2026-08-06, and a
> flat list could never express WHICH plugins an instance may pull. Adding a token to it today gates
> nothing.

Registration itself is self-service (Settings ▸ Instances issues an `mwi_` instance key), but it is
**identity, not entitlement**: what an instance may pull is decided per `(source, package)` by a
platform admin in `Admin/_PluginGrant/{instanceId}` nodes the instance's owner cannot write
(Settings ▸ Administration ▸ Instance grants). The one qualification: the registry operator may opt
specific sources into every **new** registration via `PluginCatalog:DefaultGrants` (a list of
`Source/Package` entries, e.g. `["Plugins/*"]` so a fresh install gets the platform plugin repo
with no admin step). Registration then *seeds* those entries into the grant node — the node stays
the single authority, so an admin can still revoke or extend per instance — and private/paid
sources are never listed there. With no defaults configured, registering grants exactly nothing.

**First-startup auto-registration** removes the remaining hand-off. A platform admin mints a
*registration key* (`mwr_…` — Settings ▸ Administration ▸ Instance grants ▸ Registration keys;
reusable, revocable, optionally expiring) and puts it in the deployment scaffold. A new install
configured with `PluginCatalog:BootstrapKey` + `PluginCatalog:InstanceId` then registers **itself**
on first boot via `POST /api/instances/register`: the bootstrap key resolves to its minting admin,
the instance is created under that identity (exactly as if that admin had registered it by hand,
`DefaultGrants` seed included), and the response carries the instance's own `mwi_` key **once** —
the install persists it (`Admin/PluginRegistryCredential/…`, `enc:`-protected at rest when a master
key is configured) and presents it on every catalog call from then on. Nobody copies a token.
Revoking the bootstrap key stops further registrations without touching the instances it already
created; a bootstrap key is never accepted on the catalog surface, and an instance key is never
accepted for registration (`mwr_` vs `mwi_` — disjoint by shape).

**And it installs.** A grant is entitlement, not content — so a registered instance would still show
an empty (if authorized) catalog until an admin clicked Install on each package. Two keys close that,
and they are independent:

- **`PluginCatalog:InstallPreInstalledPackages`** (default **`true`**) reconciles the packages whose
  manifest declares `PreInstalled` — the platform's own baseline (Agents, Skills, Essentials, …) — on
  **every** boot, because that baseline is what the platform needs to function and what must survive
  a self-update. On an up-to-date instance the content-identity gate turns it into one catalog
  listing and no writes. It is also the only mechanism that can heal an instance whose baseline
  partition was lost.
- **`PluginCatalog:InstallByDefault`** (default empty) *seeds* a fresh deployment once: on startup an
  installation with **no install records yet** installs every catalog entry matching its
  `Source/Package` patterns, through the same path the Install button uses. Our deployments set
  `["Plugins/*"]`, so a new portal comes up with the platform plugins — the Store included — already
  present and (per `AutoUpdateByDefault`) tracking their repo.
- **`Features:Flags:{name}:Packages`** *asserts* a per-environment policy, reconciled on **every**
  boot: what THIS deployment always has, with a declared-but-disabled flag EXCLUDING what it names.
  🚨 Use this, not `InstallByDefault`, whenever the portal already has install records — the seed is
  ledger-gated and can say nothing about an already-populated installation, which is every live
  portal. Full treatment: [Environment Composition](/Doc/Architecture/EnvironmentComposition).

> 🚨 The selection is **source-scoped, and that is a security property, not a convenience**. An
> instance is routinely granted the platform repo *and* paid course content; "install everything I'm
> entitled to" would auto-install the paid content. Matching is against the catalog entry's
> `Source` — stamped by the registry as it merges its sources — so `Plugins/*` can never reach an
> `Education` package. A registry too old to stamp `Source` matches nothing and installs nothing,
> failing closed. The default install runs only while the installation has no packages at all, so it
> seeds a new deployment rather than re-asserting itself against an admin who uninstalled something.

What a registered instance can then read is **curated plugins** only:
by default the node-native repos the [`MeshWeaver.Plugins`](/Doc/Architecture/Plugins) repo ships —
`<Plugin>/index.json` **Store/Plugin** roots carrying a `PluginContent`, node-per-file — via a
`NodeRepoPackageSource` (`PluginCatalog:SourceFormat=node-repo`, the default). A `package.json`-manifest
repo can be served instead with `SourceFormat=package-json`. Nothing outside a published plugin is
exposed, and the registry's own credential never leaves.

| Verb | Body | Returns |
|---|---|---|
| `GET /api/plugins` | — (`?ref=` advisory) | `{ packages: [PackageManifest…] }` |
| `POST /api/plugins/files` | `{ id }` (`ref` optional/advisory) | `{ files: [{ relativePath, content }…] }` |

A package is addressed by its **id** only; the registry resolves what that plugin ships from its
configured source — the consumer never supplies a folder path.

Both are backed by the registry's configured git [`IPackageSource`](/Doc/Architecture/Plugins)s. A
registry can serve **several sources** — e.g. the plugins repo *and* an education-content repo —
via the `PluginCatalog:Sources` list:

```json
{
  "PluginCatalog": {
    "Sources": [
      { "Name": "Plugins",   "RepoPath": "https://github.com/Systemorph/MeshWeaver.Plugins", "Ref": "main" },
      { "Name": "Education", "RepoPath": "https://github.com/Systemorph/Education",          "Ref": "main" }
    ]
  }
}
```

Each entry takes `RepoPath` (a URL → via GitSync's client, or a local path), optional `Subdir`,
`Ref` (default `HEAD`) and `Format` (`node-repo` default / `package-json`). A source lists only
folders matching its format — the Education repo's courses (DataModeling, AgenticEngineering, …)
appear once each course folder gains a `<Course>/index.json` Store/Plugin root carrying a
`PluginContent`, the same node-repo convention `MeshWeaver.Plugins` uses. `GET /api/plugins`
merges all sources' packages (on an id collision the first configured source wins); `/files`
resolves an id in the same order. With several sources one failing repo degrades to an empty
contribution (logged), never a broken catalog. The legacy single-source keys
(`PluginCatalog:SourceRepoPath`/`SourceSubdir`/`SourceRef`/`SourceFormat`) keep working when no
`Sources` list is set. The registry is authoritative on each source's git ref; a consumer's `ref`
is advisory. The wire shapes are produced by `PluginRegistryPayloads` and parsed by
`RegistryPackageSource`, one place each, so producer and consumer cannot drift.

## The sync licence — what a grant now carries

A `PluginGrant` IS the **sync licence**: the right of a registered instance to REPLICATE a package
from this registry. Its subject is a `MeshWeaverInstance` and its verb is *sync*, which is
deliberately not the user-facing entitlement — that grants a person the use of a package on their
own portal and says nothing about a deployment holding a copy of it.

Each entry carries the terms the right was issued under, so "may this instance pull this package"
and "under what licence, until when, on whose authority" are ONE record rather than an ACL sitting
next to a licence that could disagree with it:

| field | meaning |
|---|---|
| `ExpiresAt` | end of the term. Null = perpetual — which is why every grant written before licences existed keeps working unchanged. **Per ENTRY**, because one instance routinely holds a perpetual licence for the platform repo alongside a termed one for a paid package. |
| `IssuedUnderLicense` | SPDX id, resolving to a `License/{SpdxId}` node whose text can be shown. Null stays null — a licence nobody granted is never invented. |
| `IssuedVia` | an order id, a coupon code, a ticket. The audit trail. |
| `IssuedAt` | when it was issued. |
| `IsRevoked` *(on the grant)* | the instance-wide stop. Entries survive it, so a revocation stays reviewable — and liftable. |

`Allows(source, package, now)` is granted **and** within term **and** not revoked. `Matches` ignores
the term on purpose, so a caller can tell *expired* from *never licensed* — the two need different
remedies (renew, versus buy).

**`SyncLicenseService` is the one writer.** Issue, revoke, revoke-all, reinstate. Issuing is
idempotent — re-issuing REPLACES an entry, so renewing a term is the same call as granting it — and
an issuance with no issuing principal is refused rather than written anonymously. It records an
authorization; it does not make one: whether the licence may be issued (a global-admin gate, a
verified payment, a validated coupon) belongs to the caller, exactly as `PackageEntitlement` puts
its check on the action.

> 🚨 Before this, a grant had exactly two writers — the `DefaultGrants` registration seed and an
> admin typing into the Instance-grants tab — and no way to express a term at all. That is workable
> for three instances and not for a catalogue: a consumer needing ONE package had no smaller thing
> to ask for than a standing credential to a whole repository.

### Plans — the licence on the instance

The Store sells **plans** (`free` · `personal` · `pro` · `dedicated` — your own instance — ·
`enterprise` — self-hosted; the ladder and the purchase rule are the Store's own, `Store/Subscriptions`
in the MeshWeaver.Plugins repository), and every package names the plan it belongs to in its root's
`content.tier`. **The instance carries its plan on its own record** (`MeshWeaverInstance.Plan`,
#2804): a new registration starts on `free` (a registration key minted for a plan puts it on that
plan), and a global admin raises it in one field (Instance grants ▸ Plan). Every registry decision
— `/api/plugins`, `/files`, the bundle index and download — is then:

> granted for the source/package by some entry within its term **and** the package's tier is
> covered by the instance's plan (`PlanTierRanks.CoversInstance`), narrowed by the entry's own cap
> when it names one (`Narrower`).

| instance plan | may pull |
|---|---|
| *(none on the record)* | the baseline: packages declaring `free` or **no** tier — a record that predates the field is a free instance, not an unlimited one |
| `free` | the same |
| `personal` | … plus `personal` |
| `pro` | … plus `pro` |
| `dedicated` | everything — the tier node is flagged `allAccess` ("no limit on packages"), which its rank alone (25 &lt; enterprise's 30) would not give it |
| `enterprise` | everything, by rank |

A grant entry says **which** sources and packages an instance may see — `Plugins/*`,
`Reinsurance/UWDeepfield` — never at what level. An entry may still carry a plan
(`Plugins/*@free`) and then acts as a **cap** for that source: a free-capped entry on a pro instance
licenses free packages from that source; an enterprise-suffixed entry on a free instance licenses
free. A cap can only lower, never raise — which is precisely what the previous rule got wrong:
*"a plan-less entry covers every tier"* let every instance registered before plans existed pull
`pro` and `enterprise` bundles, and a `@pro` suffix licensed pro on any instance that carried it.

**The ladder is the Store's data, not a table in the platform.** The registry reads its own
`Admin/Tiers/{id}` nodes (`content.rank`, `content.allAccess`) once a minute (`PlanTierLadder`) and
hands the snapshot to the authenticated caller, so every surface decides the same way and there is
no copy of the Store's `PlanTiers` to drift. The rules at the edges are deliberate and never widen:

- a plan the ladder does not know — a typo, or a plan this registry never seeded — reads as the
  **baseline**: free and untiered packages, nothing above. "Nothing at all" would not be safer, it
  would be a portal without its Store; the fail-closed property that matters is that an unknown id
  cannot license MORE. A registry with no tier nodes at all (a local self-registry, the e2e stub)
  therefore serves its free and untiered packages to every instance and refuses every paid tier —
  `free` ranks at the baseline by definition, ladder or not;
- a package tier the ladder does not know is covered by **nothing**;
- a caller that does not know the package's tier (the tier-blind `Allows(source, package)`) is
  never answered by a plan-scoped entry — otherwise every plan would be all-access at exactly the
  call sites that forgot to ask.

One asymmetry with the Store's *purchase* rule is deliberate: a package that declares **no** tier
(Store, Agents, Skills, Essentials) is the platform **baseline** and is covered by every plan. For a
person buying a package "no tier" means "not sold under a plan"; for an instance replicating the
registry it means "ships with the platform", and a Pro instance without the Store is not a smaller
portal, it is a broken one.

**Promotion takes effect on the next request.** `InstancePlanService.SetPlan` writes the field and
tells the process's `InstanceRegistryAuthenticator` to forget its cached verdict for that instance;
other replicas follow within the cache minute. `PluginBundlePlanTest` pins both halves — a legacy
plan-less grant is capped at free, and a promoted instance pulls its pro package on the very next
request.

**A registration key carries the plan.** Mint it for a plan (Instance grants ▸ Registration keys ▸
Plan) and every install that registers with it lands ON that plan and is seeded one plan-less
`<source>/*` entry per configured source, on top of `DefaultGrants` — "a key for Pro customers" is
minted once and never typed per instance. A key with no plan seeds the `DefaultGrants` alone and
the instance starts on `free`. Fetching a source's **sealed publication whole**
(`/api/plugins/bundles/prebuilt/…`, the node-repo CI gates) still needs a plan-less `<source>/*`:
the publication carries every plan's bundles.

**Open registration — the free tier by default.** A local install (`memex-local registry
https://memex.meshweaver.cloud`, no key) registers with **no** bootstrap key at all. The registry
accepts that only where its operator minted a registration key for the plan un-keyed callers enrol
into — `free` on memex.meshweaver.cloud — and configured it as `PluginCatalog:OpenRegistration:Key`
(the chart's `secrets.memex_portal.PluginCatalog__OpenRegistration__Key`). The registration then runs
exactly as if the caller had presented that key: owned by its minting admin, on the `free` plan,
seeded `<source>/*`, revocable by revoking the key. Everywhere else an un-keyed registration is
refused with the same 401 an invalid key gets, and the caller never learns which. Raising an
instance to a higher plan is a platform admin's edit of the instance's plan on the registry — never
something the instance can ask for. `InstanceOpenRegistrationTest` pins the closed default and the
plan the registration lands on.

## Short-lived tokens — `POST /api/instances/token`

An instance exchanges its durable `mwi_` key for a short-lived, scoped JWT access token:

```
POST /api/instances/token
Authorization: Bearer mwi_…
{ "scope": ["Plugins/Publish"], "lifetimeSeconds": 900 }

→ { "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6…", "tokenType": "Bearer", "expiresIn": 900,
    "scope": ["Plugins/Publish"] }
```

Both body fields are optional; an empty body means "a default token for everything I am licensed
for". The response's `scope` is the EFFECTIVE scope — what was asked for intersected with what is
licensed — returned explicitly so a consumer sees it got less than it requested rather than
discovering it one 404 at a time.

Three properties, and each is what makes the token safe to hand out:

- **Identity and scope, never authority.** The live grant is re-read on every request, so revoking a
  licence takes effect at once instead of surviving until the token expires.
- **It can only narrow.** A token minted for more than its licence covers grants nothing extra —
  scope is an additional filter, never an alternative source of permission.
- **A token cannot mint its successor.** Only the durable key may exchange; otherwise a
  minutes-long credential becomes perpetual by renewal. The check is on the credential's SHAPE
  (`mwi_` vs JWT, disjoint by construction), not on a claim the presenter could edit.

The token is **signed, not stored** — minting writes no node, so there is no per-issue write
amplification and no expiry sweep to maintain. That is only safe *because* authority is re-checked
on use. It carries the hash of the key it was exchanged from, which is how it routes to its instance
through the same index the raw key uses, and which makes re-issuing an instance key invalidate every
outstanding token for free.

### The signing key is a mesh node, minted once

The HMAC key lives at **`Admin/SyncTokenSigningKey/current`** — one node per registry,
`enc:`-protected at rest by the same envelope as `PluginRegistryCredential`. Not configuration: a
signing key has to be IDENTICAL on every replica (a token minted on pod A must verify on pod B) and
should never pass through a human's hands. Configuration achieves the first only if an operator gets
it right in every environment, and fails the second by construction. It is minted on first use, so
there is nothing to provision.

🚨 **Uniqueness is the NODE's, and the RESPONSE cannot be trusted to report it.** Measured
2026-08-18: under a genuine concurrent create, **both** callers are told `created=1, existing=0` —
the response's exists-check lags, exactly as `IMeshService.CreateOrUpdateNode`'s remarks warn —
while **storage keeps the FIRST create and discards the second**. So the store enforces uniqueness
and the response does not describe it. The rule that follows is absolute: **create, then read back,
and sign only with what is actually stored.** Signing with locally minted material is precisely how
two replicas end up on different keys. A lock would not help — it cannot span pods.

Minting happens only AFTER the caller authenticates, so an anonymous request cannot provoke a node
write; the verify path reads without minting (a token cannot verify against a key created after it
was signed anyway).

**Rotation keeps the outgoing key.** `RotateAfter` on the node is the due date — data, so it is
visible and adjustable without a deploy. A rotation writes a fresh key and moves the old one to
`ProtectedPrevious`, verifiable until `PreviousValidUntil`; verification tries current, then
previous. The window is exactly one maximum token lifetime, because nothing signed before the
rotation can still be unexpired after that. Without it, rotating would invalidate every token in
flight mid-run.

⚠️ **Rotation is not yet automatic.** `Reminder`/`ReminderSchedule` — the platform's durable,
claim-guarded recurring trigger, which is the right home for this — exists as contract plus unit
tests with **no runner**: no registered node type and nothing that reads reminder nodes. So the due
date is recorded and reported, and rotation is invoked explicitly. When the reminder runner lands,
registering a recurring reminder against this due date is the whole of the remaining work; nothing
here needs to change. Deliberately NOT filled in with an opportunistic "rotate when someone
notices it is due" — two replicas rotating at once would each retire the other's key.

## Licence acceptance — enforced, not merely recorded

`LicenseContent.RequiresAcceptance` is enforced on the install path, beside the entitlement check
and for the same reason: on the ACTION, so the unattended paths (default install, the update
watcher) are gated identically to a click. A permissive licence asks nothing and gates nothing —
Apache-2.0 and MIT install exactly as before.

The `LicenseAcceptance` record's **body hash is checked, not merely stored**: an acceptance given
against earlier terms does not satisfy revised ones, which is the entire reason the record carries a
hash. Normalization folds line endings and trailing whitespace — what a round-trip through git or an
editor changes — and nothing else, so consent is never revoked by a difference no reader could see.

A licence the catalog does not hold (including every SPDX *expression*, like `Apache-2.0 OR MIT`,
which names a choice rather than one node) demands no acceptance: terms the platform cannot display
cannot meaningfully be consented to. That is a consent decision, never an access one — what a caller
may install is decided by `PackageEntitlement` and, for an instance, by its sync licence.

## The consumer — the Store's catalog area, not a Space

On every installation the catalog is a **layout area** (`CatalogLayoutAreas`), rendered as the
Overview of a `PluginCatalog` node — browsing and provisioning is the **Store's** job. It is **not** a
browsable `Plugins` Space: a Space partition would (correctly) deny read to everyone else — the very
"Access denied on 'Plugins'" a Space produced.

> The platform-admin **Settings ▸ Administration ▸ Plugin Catalog** tab that used to consume this
> same rendering was **retired**. What remains under global settings is the read-only installed
> inventory on the **About** tab (`CatalogLayoutAreas.ObserveInstalledManifests`). Install / Update /
> Remove actions still gate on `hub.IsGlobalAdmin` where they need to.

The catalog reads `PluginCatalog:RegistryUrl` (e.g. `https://memex.meshweaver.cloud`) — or, to consume
**several registries**, a `PluginCatalog:Registries` list of `{ Name, Url, Ref, Token }` entries,
rendered as one titled catalog section each. It lists each registry's packages via
`RegistryPackageSource` (an `IPackageSource` over HTTP, on the mesh's Http I/O pool), and joins them
against this instance's install registry — the `Package` nodes under the `Plugins` partition — to
render **Install / Update / Installed** per module.

### Categories first, packages per category

> 🚨 **The catalog must not load the full thing — only categories first.** (maintainer, 2026-09-03,
> after `/Store` on the cloud took minutes to paint.)

The page a visitor lands on lists the source's **categories** — one tile per
`PackageManifest.Category` with its package count, plus an *All packages* entry — and reads
**nothing but the source's manifest listing** to do so: no install record, no permission
evaluation, no activation-state read. Picking a tile (`?category=…`) renders that category's cards
and joins **only its members** against the install registry — one exact-path read per member,
batched into one request (`CatalogLayoutAreas.InstalledRecordQueries`), never the registry's whole
children listing. An install record carries the package's whole installed-file baseline, which is
exactly the payload a page rendering one category must not load for every package on the instance;
the click's dependency closure gets the installed *ids* from a `select:`-projected listing that
leaves the `content` column out of the read. The flat list stays reachable behind `?all=true`, and
it is the one page that can show the orphaned records below. The pure seams a test pins this with
are `CatalogLayoutAreas.Plan` (the landing's card set is empty by construction) and
`CatalogLayoutAreas.Categories`.

Two findings from the same investigation, recorded so the next reader does not re-derive them:

- **`/Store` — the storefront every user sees — is not this view.** It is in-mesh source in
  MeshWeaver.Plugins (`Store/Catalog/Source/StoreCatalogLayoutAreas.cs`). It already opens on a
  category grid, but it still derives that grid from the full mesh-wide `nodeType:Store/Plugin`
  synced feed (content included, plus one per-node Read probe per subscriber in
  `SyncedQueryDataSourceExtensions.FilterByReadPermission`) before the tiles can paint; its
  `Store/FirstFrame.md` keeps the feed off the first frame but does not make it cheaper. The same
  rule applies there: a landing needs a `select:` shell projection (category, name, icon, order —
  every one a `MeshNode` shell field) or a category summary written once by the owning hub, and
  per-viewer facts only for the category actually opened.
- **A `path:a|b|c` alternation is not yet a live query.** The storage provider derives ONE
  change-feed scope filter per query string, keyed on the FIRST path, so a change under `b` or `c`
  never re-runs it. The multi-`Queries` request (one `path:` per member) gets a scope filter per
  query and is what the category page uses; a synced consumer that needs the single-statement
  `IN (…)` form also needs that relevance rule widened to every path first.

## Installing

An admin clicks **Install** (or **Update**). No GitHub credential is involved on the consumer:

1. `POST /api/plugins/files {id}` on the registry → the package's folder files.
2. `PackageInstaller.Install` **on the consumer** parses the files into MeshNodes and upserts them —
   a **Content** package imports its folder into the target partition; a **Code** package synthesizes
   its `NodeType` node from the manifest's `nodeTypeConfiguration`, imports its `Source/*.cs` as Code
   children, and requests a release so the mesh [compiles](/Doc/Architecture/NodeTypeCompilation) the
   type live. No app rebuild, no NuGet.
3. An install record (a `Package` node) is written under the `Plugins` partition so the tab flips the
   card to **Installed** and can offer **Update** when the registry's version moves on.

Re-installing is an upsert (create-or-update by path); installing one module never disturbs another
in a shared partition.

## Free syncs freely, commercial needs a Global Admin

Who may bring a package onto an installation is decided by its **price**, and the decision is made on
the **action**, not on the screen that triggered it:

| Package | Access / sync |
|---|---|
| **Free** — `price` null or `0` | Installs and auto-updates with **no special permission**. This is what lets a fresh installation pick up the platform baseline unattended. |
| **Commercial** — a non-zero `price` (positive = purchasable, negative = coupon-only) | Requires **Global Admin** on the installing instance to install or update. |

`PackageEntitlement.Authorize` is the single rule, and it runs inside `PackageInstaller.Install` /
`InstallNodeRepoDelta` — so the machine paths are gated exactly like a click:

- **The catalog click** captures the clicking user *before* the install's system impersonation and
  passes it as the authorizing principal (the install itself must run as System — it is provisioning).
- **The install record remembers the authorizer** (`Package.authorizedBy`), and the
  [update watcher](/Doc/Architecture/PluginUpdateOnGreenBuild) re-verifies that principal is *still* a
  global admin before applying a commercial delta. Revoking the admin stops the syncing.
- **Unattended paths carry no principal**, so a commercial package cannot ride in on the boot-time
  default install — it fails closed.
- **A refusal is never silent**: it logs a speaking reason and, on the auto-update path, raises a
  notification on the install record. The manual Update button stays.

The registry side needs nothing extra: `/api/plugins` already requires a registered instance key and
scopes every listing and file fetch to that instance's `PluginGrant`, so an unauthenticated caller
enumerates nothing at all — commercial or otherwise.

## Removing an orphaned install record

`Plugins/_Policy` caps `create/update/delete` at `false` for **every** caller — a platform admin
holding an Admin assignment on the `Plugins` partition included. That is correct: install records are
written only by `PackageInstaller`, under system impersonation.

The consequence used to be a dead end. When a package leaves the registry — a course folder renamed,
so it becomes a new product id — its record `Plugins/{oldId}` had no card (cards come from the
registry's package list), hence no Uninstall, and no user identity could delete it. It rendered
publicly forever as a phantom "installed" product.

The catalog therefore also lists **install records the source no longer offers**, with a removal
action for global admins that calls `PackageInstaller.RemoveInstalledRecord` — the same
system-impersonated identity that wrote the record. Two properties matter:

- The list is computed **only against a non-empty available list**. An empty one means "the registry
  offers nothing" and "listing it failed" indistinguishably, and an unreachable registry must never
  offer to remove every record.
- Removing the record does **not** delete the content it installed — that is a separate partition
  lifecycle.

## Why this shape

- **Credential encapsulation.** GitHub access lives on exactly one instance. Onboarding a new
  installation is "point `PluginCatalog:RegistryUrl` at the registry," not "provision it a GitHub App."
- **Not a Space.** The catalog is a layout area reading a remote registry; there is no partition for a
  non-admin to navigate into and be denied.
- **Registered + curated.** Only registered instances (their `mwi_` key, scoped by an admin-owned
  `PluginGrant`) can read, and only published plugin folders are exposed — a `<Plugin>/index.json`
  Store/Plugin root, or a `package.json` on a `package-json`-format source. The catalog lists real
  modules (Publish, Edu, …), not every partition that happens to define a type.
- **Capability, not data.** A package ships its `NodeType`/`Code`/content folder — never a partition's
  user data — so installing a plugin gives you the types and their code, not anyone's records.

## Relationship to GitSync

GitSync is how the registry itself reads plugins — the registry's `IGitHubRepoClient` fetches the
plugins repo with its one credential. The registry adds the *fan-out*: git → registry (credentialed,
once) → many installations (credential-free, over HTTP). See [Plugins](/Doc/Architecture/Plugins) for
the node-native plugin model and [Static Repo Import](/Doc/Architecture/StaticRepoImport) for the
import pipeline both paths share.

## Related

- @../RepositoryTopology — why credential encapsulation is a *general* rule: the plugins repo is private, so access to any of its content is mediated by this registry rather than by GitHub credentials handed to each consumer.
- @../Plugins — the node-native plugin model this registry serves.
- @../InstanceIdentityAndSetup — the 2026-08-30 design that moves the plan from grant strings onto the instance's own partition node, replaces the `mwi_` bearer with a JWT on every fetch, and adds the first-run setup app.
- @../StaticRepoImport — the import pipeline both the registry and direct GitSync share.
