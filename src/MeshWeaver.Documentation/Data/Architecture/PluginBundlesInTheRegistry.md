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
| the publication | image index (`application/vnd.oci.image.index.v1+json`, `artifactType: application/vnd.meshweaver.publication.v1+json`) naming the sidecar manifest and every bundle manifest by digest | `cr.meshweaver.cloud/plugins/<source>`, tag `<identity>` (and an immutable `<identity>-<run>`) |
| one bundle | image manifest (`artifactType: application/vnd.meshweaver.bundle.v1+json`) with two layers: `<package>.zip` (`application/vnd.meshweaver.bundle.v1.zip`) and `modules/<package>.module.nupkg` (`application/vnd.meshweaver.module.v1.nupkg`, absent for a content-only package), each titled with its publication-relative name | referenced from the index, resident in `plugins/<source>` by digest; also tagged as `cr.meshweaver.cloud/plugins/<source>/<package>`, tag `<identity>` |
| the sidecars | one image manifest (`artifactType: application/vnd.meshweaver.publication.v1+json`) whose layers are EVERY file of the publication that is not a bundle or a module — `_complete`, `source-commit.txt`, `repository.txt`, `architecture.txt`, `platform-surface.json`, `modules/_index`, and any file a later publisher adds — each titled with its publication-relative name; its config blob (`application/vnd.meshweaver.publication.v1+json`) carries the facts: `identity`, `source`, `sourceCommit`, `repository`, `architecture`, `release`, `bundles` (`package`, `digest`, `module`), `files` | referenced from the index, resident in `plugins/<source>` by digest |
| a release's identity | image manifest with no layer; config `{ "identity": "<identity>", "version": "<version>" }` (`application/vnd.meshweaver.release.v1+json`) | `cr.meshweaver.cloud/plugins/releases`, tag `<version>` |

An OCI image index carries no config blob of its own, which is why the sidecars are a manifest
the index names rather than a field on the index; and every name is in the registry's grammar —
lowercase, a path component starting alphanumeric — so `<source>` and `<package>` are the
publisher's names lowercased, and the release repository is `plugins/releases` (`_releases`,
the share's directory name, is not a legal repository name and is refused by the registry and by
ORAS alike). A source may therefore not be named `releases`. The sidecar layers are the whole
reason a consumer needs no schema: `oras pull` of the index writes every titled layer of every
manifest it names back under its title, so the layout on disk is the publication, byte for byte,
and a sidecar added later lands without a change anywhere.

Manifests carry `org.opencontainers.image.created` pinned to the epoch: a publication is
content-addressed, and the wall clock would otherwise ride in the manifest bytes and give the
same publication a new digest on every push. When it was pushed is what the `<identity>-<run>`
tag and the registry's log record.

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

🚨 **Content addressing does not merge two bakes of one commit.** A compile is not
byte-reproducible: two publications of the same source commit at the same identity differ in
most payload files (measured: 40 of 45), so they are two complete indexes with two digests. The
tag records which one won; both stay resident by digest; a reader holding a digest finishes with
the set it started with, and a reader of the tag gets a complete set either way. Nothing may
assume "same commit ⇒ same digest".

## Who pulls what, and with which credential

| consumer | pulls | credential | how |
|---|---|---|---|
| an installation's pre-warm (`ShippedPrebuiltBundles`) | the index for its own identity and each source in `bundles.sources`, materialised under `PreWarm:PrebuiltBundleRoot` in the layout it already reads | the pod's `imagePullSecrets` credential (`portal.imagePullSecret`), projected into the init container as a docker config | the `bundle-fetch` init container (`deploy/helm/files/bundle-fetch.sh`, rendered when `bundles.registry` is set) runs ORAS before the portal starts; the pre-warm keeps reading a filesystem, and needs no mesh and no network |
| the Store, `RegistryUpdateReconciler`, `InstanceAutoRegistrationService` (a bundle adopted at runtime) | one bundle manifest by digest, named by the catalog index | the instance credential `RegistryTokenResolver` already holds, presented at the registry's token realm | `PluginBundleClient` — the same landing path, entering `ModuleLandingService.LandCore`, gated by the `ModulePlatformLink` probe and the load |
| `memex-local` and every self-hosted install | as above | the instance key in its manifest, projected as `ContainerRegistry:DockerConfigJson` (`{"auths":{"cr.meshweaver.cloud":{"auth":base64("instance:<key>")}}}`, emitted only when the key decrypts — core #3722) | no second credential |
| satellite CI on `main` (`node-repo-gate`, `compose-sealed-modules`, `memex build plugin`) | the index and bundles for the pinned identity | the repository's instance key (`REGISTRY_KEY`) | ORAS |
| satellite CI on a `pull_request` | **unchanged: the HTTP prebuilt surface** (`/api/plugins/bundles/prebuilt/…`) with the GitHub OIDC build principal | OIDC token | see "What stays on the HTTP surface" |

The registry edge validates every login by exchanging the presented key at memex's
`POST /api/instances/token`; the durable key is on the wire once per token lifetime, exactly as
for images, and a token lasts fifteen minutes.

### The `bundle-fetch` init container

The chart renders it on the portal pod when `bundles.registry` is set (`deploy/helm/values.yaml`,
gated by `templates/memex-portal/_bundles.tpl`, which fails `helm template` naming any key a
half-declared block lacks):

| key | meaning |
|---|---|
| `bundles.registry` | the registry host (`cr.meshweaver.cloud`); empty renders nothing, so an environment that has not opted in renders byte-identically |
| `bundles.sources` | the source names to materialise, as the publisher named them (`[plugins]`) |
| `bundles.identity` / `bundles.identityFile` | the framework identity to pull — the value the bake lane prints as `baked identity:`, or a one-line file on the data volume the init container reads at run time. One of the two is required: the identity is computed by the portal from its surface manifests (`FrameworkBuildIdentity`) and is not a file inside the image, so nothing in the ORAS container can derive it |
| `bundles.image` | the ORAS image, pinned by digest |
| `bundles.root` | the directory it fills; defaults to `config.memex_portal.PreWarm__PrebuiltBundleRoot`, so the fetch lands exactly where the pre-warm looks |
| `portal.imagePullSecret` | required when the registry is set: the `kubernetes.io/dockerconfigjson` Secret that pulls the platform image is mounted as ORAS's registry config, and there is no second credential |

For each source it resolves `plugins/<source>:<identity>` to its digest, pulls that digest's whole
graph into a staging directory (every bundle's `.zip` and `.module.nupkg`, every sidecar under
the name its layer is titled with — a sidecar a later publisher adds lands without a chart
change), checks `_complete` against what landed, and only then renames the staging directory
into place as `<root>/<identity>/<source>/`, so the pre-warm sees the publication whole or not at
all. An absent tag is an unsealed publication: one log line, nothing written for that source,
exit 0 — the pre-warm compiles that source as it does today. Any other failure — a denied pull,
a network error, a listed bundle that did not land — exits 1 and holds the pod, because a fetch
that could not complete must never read as "no bundles". A stale `bundles.identity` after a roll
that changed the image is inert: the pre-warm finds no directory for its own identity and
compiles. The script's ConfigMap is hashed into the pod template, so an edit to it rolls the
pods as an image change would.

## Authorization: memex decides, the edge enforces

The catalog's rule is unchanged: a caller may take a package when a grant entry within its term
names it and the instance's plan covers the package's tier. The registry edge enforces the same
rule at pull time without knowing what a plan is:

* On **authentication**, docker_auth's `ext_auth` hook (`deploy/helm/files/registry-validate.sh`,
  rendered into the auth server's ConfigMap) exchanges the key at `/api/instances/token` with no
  scope. A `200` authenticates; a `401` or `403` refuses; anything else — a timeout, a 5xx, a body
  without a `scope` array — is an error (exit 3), never a pass and never a quiet refusal that
  would read as "wrong key". The `scope` array the exchange answers — the caller's current
  licence entries, `Plugins/*`, `Reinsurance/UWDeepfield`, `Plugins/*@pro` — becomes the token's
  labels, in the registry's lowercase name grammar: `package` holds `<source>/<package>` per
  entry (`<source>/*` for a whole-source one), `source` holds `<source>` for a plan-less
  whole-source entry only.
* On **authorization**, the static ACL (`templates/registry/configmap.yaml`) matches those
  labels, first match wins: `plugins/${labels:package}` grants `pull` on the bundle repositories
  `plugins/<source>/<package>`; `plugins/${labels:source}` grants `pull` on the publication index
  `plugins/<source>` — so a plan-scoped `Source/*@plan` reaches its source's bundle repositories
  and never the publication whole, which carries every plan's bundles; every other object under
  `plugins/` is denied before the image rule can see it.
* `plugins/releases` and every image repository (`memex-portal-ai`, `memex-migration`, …) are
  readable by every authenticated account; anonymous matches no rule.
* Push is the publisher account — the one static user, checked before the hook — and nothing
  else.

The labels are the only carrier, because docker_auth's `ext_authz` hook receives the request —
account, type, name, actions, labels — and not the credential, so it cannot ask memex anything
at pull time; and a label never carries the key, because docker_auth logs every token's labels.
Which package a plan-scoped entry covers is decided where the package's tier is known — the
catalog and the landing — and the edge grants the source's bundle repositories, nothing above
them. Every decision is memex's, made at login; revoking a grant or a key takes effect at the
next token exchange, within a token's fifteen minutes.

## Publishing

`.github/scripts/push-bundle-publication.sh --registry <host> --source <name> --identity <id>
--dir <publication dir> [--tag-run <run>] [--release <version>]` pushes one sealed publication —
the directory `publish-bake-bundles.sh` writes for one source and one identity — with ORAS as the
publisher, in the order that is the seal: every bundle manifest, tagged `<identity>` in
`plugins/<source>/<package>` and copied by digest into `plugins/<source>`; the sidecar manifest;
the index by digest; then the tags, `<identity>-<run>` (immutable) and `<identity>` LAST — the
tag move is the seal, and until it moves nothing above is visible under the tag. It refuses a
directory without `_complete`, or with a listed bundle or module absent, before the first push,
and prints the index digest and one `(package, digest)` line per bundle. The bake lane calls it
after the bake and the link gate and records the index digest in the publication it registers at
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

## Registration records the owner

An instance registers once, with the ownership record the setup collects — company, owner name,
owner email — and the consent evidence (document hashes, acceptance time). Stated ownership wins
per field over the details of whoever minted the bootstrap key, so an open registration names the
person standing the instance up, not the registry admin. The key it receives is the credential
for every pull that follows (core #3722). The registration endpoint does not yet refuse an open
registration without consent evidence; that enforcement ships with a consent block on the
registration request that scripted callers send, the keyed lane staying as it is.

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
* `.github/scripts/test-bundle-registry.py` (chart-gate's `Bundle registry scripts (executed)`
  job) executes the publisher, the init container's script and the `ext_auth` hook against the
  real distribution and docker_auth images with the ConfigMap the chart renders, and a stub of
  the key exchange: the materialised layout is diffed byte for byte against the publication, and
  every account in the licence matrix is asserted for what it may pull and what it may not.

## Related

[A Container Registry in Memex](../ContainerRegistryInMemex) · [Plugin Registry](../PluginRegistry) ·
[Module Adoption Policy](../ModuleAdoptionPolicy) · [Sealed Publication Reads](../SealedPublicationReads) ·
[Sealed Publication Generations](../SealedPublicationGenerations) · [CI Content Bake](../CiContentBake) ·
[Continuous Delivery Contract](../ContinuousDeliveryContract)
