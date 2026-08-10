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
