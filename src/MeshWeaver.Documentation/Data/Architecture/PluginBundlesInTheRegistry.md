---
Name: Plugin Bundles in the Registry
Category: Architecture
Description: Plugin bundles are OCI artifacts in the fleet's own registry (cr.meshweaver.cloud), published by digest as one sealed index per framework identity and source, pulled with the instance key that already pulls the platform image. The plugin registry inside memex keeps the catalog index and every authorization decision; the registry edge asks memex on every token exchange. What the artifact is, how a publication becomes visible atomically, who pulls how, and what stays on the HTTP surface.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><polyline points="3.27 6.96 12 12.01 20.73 6.96"/><line x1="12" y1="22.08" x2="12" y2="12"/></svg>
---

# Plugin Bundles in the Registry

A plugin bundle — the `<package>.zip` a bake produces and the `.module.nupkg` beside it — is an
OCI artifact in the fleet's own registry, [cr.meshweaver.cloud](../ContainerRegistryInMemex),
next to the platform images. The registry serves bytes; the plugin registry inside memex serves
the **catalog** (what exists, what a caller may install) and makes every **authorization**
decision. One instance key pulls both the image an installation boots and the bundles it lands.

This page is the contract between three parties: the lanes that publish, the registry edge that
authenticates, and the consumers that pull. Read it with [Plugin Registry](../PluginRegistry)
(grants, plans, the catalog index), [Module Adoption Policy](../ModuleAdoptionPolicy) (what may
LOAD) and [Sealed Publication Reads](../SealedPublicationReads) (the consistency protocol this
replaces by construction).

## The artifact

A **publication** is one sealed set of bundles for one framework identity and one source. It is
represented as an **OCI image index** whose entries are the bundles:

| object | OCI form | name |
|---|---|---|
| the publication | image index (`application/vnd.oci.image.index.v1+json`) | `cr.meshweaver.cloud/plugins/<source>`, tag `<identity>` |
| one bundle | image manifest with two layers: `<package>.zip` (`application/vnd.meshweaver.bundle.v1.zip`) and `<package>.module.nupkg` (`application/vnd.meshweaver.module.v1.nupkg`, absent for a content-only package) | referenced from the index; also addressable as `cr.meshweaver.cloud/plugins/<source>/<package>` tag `<version>-<identity>` |
| the sidecars | the index's config blob (`application/vnd.meshweaver.publication.v1+json`): `sourceCommit`, `repository`, `architecture`, `platformSurface` (the whole `platform-surface.json`), `modules` (the module index), `release` | on the index, never a separate object |
| a release's identity | image manifest with an empty layer; config `{ "identity": "<identity>", "version": "<version>" }` | `cr.meshweaver.cloud/plugins/_releases`, tag `<version>` |

🚨 **The framework identity is part of every name.** A bundle's `version` comes from
`manifest.lock` and encodes content only; the same version rebuilt for another platform identity
is a different artifact with a different digest. Two lanes publishing the same source for two
identities push two indexes under two tags and never touch each other's objects. Two lanes
publishing the same identity push byte-identical layers (a no-op on a content-addressed store)
and, if their sets differ, a different index digest — the tag records which one won and the
other is still resident by digest. A mixed set cannot be written: an index names its bundles by
digest, and a digest is either resident or the push fails.

🚨 **The digest of the index IS the generation.** A consumer reads the tag once, holds the index
digest, and fetches every bundle it names by digest. Nothing it reads afterwards can move under
it, so there is no `If-Match`, no `412 GenerationMoved`, no `503 Retry-After` republish window,
and no restart loop. A tag that does not exist is an unsealed publication (`404 MANIFEST_UNKNOWN`
on the tag); a partially pushed publication is invisible, because the tag moves last.

Layers are content-addressed, so two identities that share a bundle's bytes share the blob, and a
`.zip` that did not change between publications is uploaded once.

## Who pulls what, and with which credential

| consumer | pulls | credential | how |
|---|---|---|---|
| an installation's pre-warm (`ShippedPrebuiltBundles`) | the index for its own identity and each mounted source, materialised under `PreWarm:PrebuiltBundleRoot` in the layout it already reads | the pod's `imagePullSecrets` credential | an init container runs the fetch before the portal starts; the pre-warm keeps reading a filesystem, and needs no mesh and no network |
| the Store, `RegistryUpdateReconciler`, `InstanceAutoRegistrationService` (a bundle adopted at runtime) | one bundle manifest by digest, named by the index's `artifact` (`cr.meshweaver.cloud/plugins/<source>/<package>@sha256:…`), then its `.zip` layer by digest | the instance credential `RegistryTokenResolver` already holds, presented at the registry's token realm as `Basic instance:<token>` | `PluginBundleClient` through `OciRegistryClient` (`MeshWeaver.PluginCatalog`, the same client `OciTagLister` lists image tags with) — the same landing path, entering `ModuleLandingService.LandCore`, gated by the `ModulePlatformLink` probe and the load; a bundle whose `artifact` is `null` takes the HTTP route |
| `memex-local` and every self-hosted install | as above | the instance key in its manifest, projected into a docker config entry for `cr.meshweaver.cloud` | no second credential |
| satellite CI on `main` (`node-repo-gate`, `compose-sealed-modules`, `memex build plugin`) | the index and bundles for the pinned identity | the repository's instance key (`REGISTRY_KEY`) | ORAS |
| satellite CI on a `pull_request` | **unchanged: the HTTP prebuilt surface** (`/api/plugins/bundles/prebuilt/…`) with the GitHub OIDC build principal | OIDC token | see "What stays on the HTTP surface" |

The registry edge validates every login by exchanging the presented key at memex's
`POST /api/instances/token`; the durable key is on the wire once per token lifetime, exactly as
for images, and a token lasts fifteen minutes.

## Authorization: memex decides, the edge enforces

The catalog's rule is unchanged: a caller may take a package when a grant entry within its term
names it and the instance's plan covers the package's tier. The registry edge enforces the same
rule at pull time without knowing what a plan is:

* On **authentication**, the auth server's external check exchanges the key at
  `/api/instances/token` with no scope; a `200` authenticates, a `401` refuses.
* On **authorization** of `plugins/<source>/<package>`, the external check exchanges the key
  again with `scope: ["<source>/<package>"]`. memex answers `200` with the effective scope when
  the licence covers it and `403` when it does not — the same predicate the catalog and the
  bundle download apply, tiers included. `plugins/<source>` (the index) is authorized by
  `scope: ["<source>/*"]`, which memex grants only to a plan-less whole-source entry, so a
  plan-scoped holder cannot take a publication whole.
* `plugins/_releases` is readable by every authenticated instance.
* Push is the publisher account and nothing else.

Every decision is memex's, made at the moment of the pull; revoking a grant or a key takes
effect at the next token exchange.

## Publishing

The bake lane pushes the publication with ORAS as the publisher after the bake and the link gate:
every bundle manifest by digest, then the index by digest, then the tag `<identity>` — the tag
move is the seal. It records the index digest in the publication it registers at
`Hosting/PlatformBuilds` (`register-publication`), so the sealed set is a list of
`(package, digest)` pairs and the catalog index carries `artifact:
cr.meshweaver.cloud/plugins/<source>/<package>@sha256:…` per package.

The registry's push notification reaches memex (`registry.notifications.url`), which shelves the
module set and proposes it exactly as the bundle POST does today; the POST stays as the metadata
path until the notification handler carries the same effect.

The share copy under `prebuilt-bundles/<identity>/<source>/` continues to be written beside the
push until every consumer reads the registry; a publication that reaches one target and not the
other is refused as unsealed, as it is today.

## What stays on the HTTP surface

* **`GET /api/plugins`** and **`POST /api/plugins/files`** — the catalog index and package files.
  The first-run setup wizard reads the index with a raw instance key and no mesh, and its shape
  (`{ packages: [...] }`, `storageType`) does not change; the index gains an `artifact` reference
  per package.
* **`/api/plugins/bundles/prebuilt/…` for the GitHub OIDC build principal.** A `pull_request`
  run has no instance key, and the registry edge validates only instance keys. Those gates keep
  the HTTP surface until the edge can validate a build principal.
* **`POST /api/instances/register`** and **`POST /api/instances/token`** — registration and the
  key exchange; the edge depends on the second.

## Verification that means something

* A publication pushed twice for one identity leaves one index digest, and a consumer that
  started before the second push finishes with the set it started with.
* A pull of `plugins/<source>/<package>` by an instance whose plan does not cover the package's
  tier is refused at the edge with the same answer the catalog gives.
* An installation whose `PreWarm:PrebuiltBundleRoot` is filled by the init container boots with
  every bundle the index names and no others, and `ShippedPrebuiltBundles` reports the same
  seeded set as from the share copy.
* A bundle whose bytes do not pass the `ModulePlatformLink` probe at landing leaves the previous
  version running (continuity), never an absent module.

## Related

[A Container Registry in Memex](../ContainerRegistryInMemex) · [Plugin Registry](../PluginRegistry) ·
[Module Adoption Policy](../ModuleAdoptionPolicy) · [Sealed Publication Reads](../SealedPublicationReads) ·
[Sealed Publication Generations](../SealedPublicationGenerations) · [CI Content Bake](../CiContentBake) ·
[Continuous Delivery Contract](../ContinuousDeliveryContract)
