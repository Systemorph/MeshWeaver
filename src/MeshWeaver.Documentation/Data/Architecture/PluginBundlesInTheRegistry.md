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
| one bundle | image manifest (`artifactType: application/vnd.meshweaver.bundle.v1+json`) with two layers: `<package>.zip` (`application/vnd.meshweaver.bundle.v1.zip`) and the package's module (`application/vnd.meshweaver.module.v1.nupkg`, absent for a content-only package), each titled with its publication-relative name. The module is matched to its bundle **case-insensitively**: the lanes write it lowercased (`modules/essentials.module.nupkg` beside `Essentials.zip`), and an exact-case match once attached no module to any bundle (43 of 43 `module=no` on the `plugins` publication of MeshWeaver.Plugins run 38215076376) | referenced from the index, resident in `plugins/<source>` by digest; also tagged as `cr.meshweaver.cloud/plugins/<source>/<package>`, tag `<identity>` |
| the sidecars | one image manifest (`artifactType: application/vnd.meshweaver.publication.v1+json`) whose layers are EVERY file of the publication that is not a bundle or a module riding with one — `_complete`, `source-commit.txt`, `repository.txt`, `architecture.txt`, `platform-surface.json`, `modules/_index`, a module whose package bakes no bundle (a module-only package, which has no bundle manifest to ride in; media type `application/vnd.meshweaver.module.v1.nupkg`), and any file a later publisher adds — each titled with its publication-relative name; its config blob (`application/vnd.meshweaver.publication.v1+json`) carries the facts: `identity`, `source`, `sourceCommit`, `layout`, `repository`, `architecture`, `release`, `bundles` (`package`, `digest`, `module`), `files` | referenced from the index, resident in `plugins/<source>` by digest |
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
| the Store, `RegistryUpdateReconciler`, `InstanceAutoRegistrationService` (a bundle adopted at runtime) | one bundle manifest by digest, named by the index's `artifact` (`cr.meshweaver.cloud/plugins/<source>/<package>@sha256:…`), then its `.zip` layer by digest | the instance credential `RegistryTokenResolver` already holds, presented at the registry's token realm as `Basic instance:<token>` | `PluginBundleClient` through `OciRegistryClient` (`MeshWeaver.PluginCatalog`, the same client `OciTagLister` lists image tags with) — the same landing path, entering `ModuleLandingService.LandCore`, gated by the `ModulePlatformLink` probe and the load; a bundle whose `artifact` is `null` takes the HTTP route |
| `memex-local` and every self-hosted install | as above | the instance key in its manifest, projected as `ContainerRegistry:DockerConfigJson` (`{"auths":{"cr.meshweaver.cloud":{"auth":base64("instance:<key>")}}}`, emitted only when the key decrypts — core #3722) | no second credential |
| the release-availability gate (`ReleaseAvailabilityService`), for a candidate release its published root has no marker for | the release record `plugins/releases:<version>` — its manifest by tag, then its config blob by digest | the instance credential `RegistryTokenResolver` holds for a registry mount (`ResolveDurableKey`), presented only to the artifact host that mount's bundle index declares | `RegistryReleaseIdentities` through `OciRegistryClient`; nothing is written — see "A release's identity on a registry-fed root" |
| the same gate, once the registry named that identity | each source's publication index `plugins/<source>:<identity>`, by tag, at the moment the gate runs | the same mount's durable key, presented only to the artifact host that mount declares | `RegistryLivePublications`; one manifest GET per source, nothing written — see "The sealed set is the live publication" |
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
| `bundles.imageIdentityPath` | where the portal image carries its own framework identity (`/app/meshweaver-framework.identity`, the default). The identity is READ FROM THE RUNNING IMAGE — see "The identity follows the image" below |
| `bundles.identity` | the FALLBACK identity, used only when the portal image predates the identity file — logged as such, and it does not follow a roll |
| `bundles.identityFile` | an operator-owned one-line file on the data volume; set ⇒ the identity is not derived from the image |
| `bundles.image` | the ORAS image, pinned by digest |
| `bundles.root` | the directory it fills; defaults to `config.memex_portal.PreWarm__PrebuiltBundleRoot`, so the fetch lands exactly where the pre-warm looks |
| `portal.imagePullSecret` | required when the registry is set: the `kubernetes.io/dockerconfigjson` Secret that pulls the platform image is mounted as ORAS's registry config, and there is no second credential |

#### Declaring it on the Deployment record (#6825)

A record-driven deploy declares the same block on the record itself — `bundles` on
`DeploymentContent` (`BundlesSpec`), in the chart's own shape:

```json
"imagePullSecret": "fleet-registry-pull",
"bundles": { "registry": "cr.meshweaver.cloud", "sources": ["plugins", "reinsurance"] }
```

| record field | renders |
|---|---|
| `bundles.registry` | `bundles.registry`; blank renders no section, so a record without the block renders byte-identically |
| `bundles.sources` | `bundles.sources`, trimmed, blanks dropped, in the record's order |
| `bundles.identity` | `bundles.identity` only when stated — the fallback for an image that predates the identity file |
| `bundles.root` | `config.memex_portal.PreWarm__PrebuiltBundleRoot` (blank → `/data/prebuilt-bundles`), through core's shared derivation `DeploymentPortalConfig.BundleRoot`; the chart's `bundles.root` defaults to that key, so the shelf the init container fills is the directory the pre-warm reads |

The Helm renderer (`HelmValues`, MeshWeaver.Plugins) refuses at plan time, naming the field, a
registry with no source, a registry with no `imagePullSecret`, sources with no registry, and an
`extraPortalConfig` `PreWarm__PrebuiltBundleRoot` that disagrees with the block — the typed value
wins, so that extra would otherwise be dropped without a word. Before the field existed, an estate
outside the fleet's own subscription could state its bundle source only in a hand-written values
layer, and every record-driven Roll was refused by `hosting-deploy`'s drop guard for dropping it.

For each source it resolves `plugins/<source>:<identity>` to its digest, pulls that digest's whole
graph into a staging directory (every bundle's `.zip` and `.module.nupkg`, every sidecar under
the name its layer is titled with — a sidecar a later publisher adds lands without a chart
change), checks `_complete` against what landed, and only then renames the staging directory
into place as `<root>/<identity>/<source>/`, so the pre-warm sees the publication whole or not at
all. An absent tag is an unsealed publication: one log line, nothing written for that source,
exit 0 — the pre-warm compiles that source as it does today. Any other failure — a denied pull,
a network error, a listed bundle that did not land — exits 1 and holds the pod, because a fetch
that could not complete must never read as "no bundles". The script's ConfigMap is hashed into the pod template, so an edit to it rolls the
pods as an image change would.

### The identity follows the image (#6052 ask 3)

The publication is addressed by framework identity — the platform compatibility key
`c<major:D3>e<epoch:D3>`. It used to come from `bundles.identity`, a value fixed per helm release,
so an in-portal self-update (or any roll that moved only the portal's image) left the init container
naming the previous image's identity.

* **The image states its identity.** `MeshWeaverSurfaceManifest.targets` writes
  `meshweaver-framework.identity` beside the binaries of every portal host and publishes it with them
  — the major from the referenced `MeshWeaver.Compiler` assembly's version, the epoch from the
  declaration `Directory.Build.props` reads — the same facts the runtime key is computed from.
  `FrameworkBuildIdentityFileTest` asserts the written value equals `FrameworkBuildIdentity.FrameworkVersion`.
* **The chart reads it from the image.** A `bundle-identity` init container runs `portal.image`,
  copies that file into an emptyDir, and `bundle-fetch` reads it as `BUNDLES_IDENTITY_FILE`. An image
  that predates the file uses `bundles.identity` as a named fallback; with neither, the init container
  exits 1 naming both. Invariant 13b (`check-chart-invariants.py`) asserts the container runs the
  portal's image, ahead of the fetch, over a volume both mount.
* **Whatever moves the portal's image moves this container too.** A helm render does. The operator
  (`run.sh`) appends `bundle-identity=<ref>` to a plan's `set image … memex-portal=<ref>` when the
  Deployment carries that container — one `set image`, one rollout — and refuses the move when it
  cannot read the Deployment. 🚨 **The in-portal self-patcher (`KubernetesDeploymentUpdater`,
  MeshWeaver.Plugins) still patches only the `memex-portal` container**; until it moves
  `bundle-identity` as well, a self-patched pod reads the previous image's identity (which is the
  behaviour this replaced, never worse).

### A release's identity on a registry-fed root

Policy `release-identity-from-registry` ([Policy Not Prose](../PolicyNotProse)).

The self-update gate judges a candidate release by its framework identity, which it reads from the
marker `<root>/_releases/<version>`. The publisher writes that marker to the storage it mounts. A
root filled by `bundle-fetch` is a different volume: an emptyDir holding
`<identity>/<source>/…` for the identity the running image reports, mounted read-only into the
portal, with no `_releases` directory. On such an estate no candidate ever had a marker, so every
one answered *"the framework identity of release … is not resolvable"* and the installation held
on the release it booted with — a hold with no failing component.

The gate therefore asks the registry when, and only when, the marker file is absent:

1. each registry mount (`PluginCatalog:Registries`), in order, is asked for its bundle index;
2. the artifact host that index declares (`artifactRegistry`) is asked for
   `plugins/releases:<version>`, with the mount's durable key;
3. the record's config (`{"identity","version"}`) is taken when its media type is the release
   record's, its bytes hash to the digest the manifest names, it names the version asked for, and
   its identity is a single path segment;
4. each source's publication under that identity is read **live** — see "The sealed set is the
   live publication" below.

| situation | answer |
|---|---|
| the marker file exists | the file and the bundles on the root; the registry is not asked |
| no marker, the registry names the identity | judged against each source's live publication `plugins/<source>:<identity>` |
| no marker and no mount declares an artifact host, the record is absent or malformed, or the read fails | *identity not resolvable* — the hold, as before |

"The marker file exists" means its presence was established. A marker whose presence cannot be
determined (access denied, an I/O fault) is neither read as absent nor overridden: the registry is
not asked and the read reports what it found.

#### The sealed set is the live publication

Policy `registry-fed-gate-reads-live-publication` ([Policy Not Prose](../PolicyNotProse)),
MeshWeaver#6622.

The bundles on a registry-fed root are the snapshot `bundle-fetch` pulled when the running pod
started. `plugins/<source>:<identity>` is a tag that moves whenever that source publishes again
under the same identity, and the pod that starts on the next release pulls the tag as it stands
then. A gate that counted the snapshot answered about a publication no future pod will pull: a
package withdrawn since this pod started still counted as sealed (the next pod compiled it at
boot instead of adopting it), and a package added since did not count (a release that would have
been fine was held).

So where the registry named the identity, the gate reads each source's publication **at the
moment it runs** (`RegistryLivePublications`), and that is the sealed set it judges:

1. the sources are the `<root>/<identity>/<source>` directories the root materialises — the
   chart's `bundles.sources` that held a publication when the pod started
   (`PublishedBundleCatalogue.SourcesOnRoot`);
2. for each source, the registry mounts are asked in order for the artifact host their bundle
   index declares, and that host for the manifest `plugins/<source>:<identity>`, with the mount's
   durable key and no other. The FIRST mount that answers Published ends the reading: no later
   mount can change that verdict, so asking it would only let a slow mount run the gate's answer
   budget out and turn a decided Published into Indeterminate (#6743);
3. the index is read bounded: a body larger than a publication index (`MaxIndexBytes`) is refused
   as it arrives, never after it has been held whole;
4. the publication counts only if `bundle-fetch` would materialise it: EVERY index entry is a valid
   OCI descriptor (media type, sha256 digest, positive size), there is exactly one sidecar entry
   (`io.meshweaver.role=sidecars`), and that sidecar manifest — one more GET, by digest — carries a
   non-empty layer titled `_complete`, the sentinel `bundle-fetch` dies without;
5. a package counts as sealed exactly when a bundle entry of that index (`artifactType`
   `application/vnd.meshweaver.bundle.v1+json`) names it in its `io.meshweaver.package`
   annotation — the entry `push-bundle-publication.sh` writes for every bundle.

Not checked from the index, and so still `bundle-fetch`'s own refusal: that every bundle `_complete`
LISTS actually lands (that needs the bundle manifests and their blobs, which is the pull itself).

The snapshot still supplies what only bytes can — dependency records, the sealed module set, the
declared ranges — for the packages it holds. The release record `plugins/releases:<version>` is
not changed and gains no field: it binds `version → identity`, and the identity's publications are
read where they live.

| live reading for a source | answer |
|---|---|
| the tag resolves to a publication index | its bundle entries are that source's sealed packages |
| every declared host answers `404 MANIFEST_UNKNOWN` | established absent: nothing is sealed for that source — a verdict, by package |
| no mount declares a host, a host refuses the key or fails, the tag names something that is not a publication index or is larger than one, an entry is not a valid descriptor, a bundle entry names no package, or the sidecar manifest or its `_complete` is missing | *could not look* — the observation is unreadable and the verdict Indeterminate; the snapshot is never used instead |

Unreadable is never read as "absent", and never as "the snapshot is current": either reading would
be a guess about the publication the next pod pulls. On a platform roll an Indeterminate package
answer is an advisory, as every package answer is (policy
`platform-rolls-independently-of-packages`).

**What stays out of reach.** A source the root never materialised (its tag was absent when the
pod started) is not asked about, because the chart's source list is not visible to the portal. A
release that moves the compatibility key (`c<major>e<epoch>`) is now judged by its live package
list too, but the root holds none of its bytes, so its module set is unobserved. The enumeration
of candidates (`PublishedBundleCatalogue.PublishedReleases`) still reads marker files only; the
poller supplies its own candidate list, so it does not depend on it.

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

**Wired (2026-09-08).** `.github/scripts/push-bundle-publication.sh --registry <host> --source
<name> --identity <id> --dir <publication dir> [--tag-run <run>] [--release <version>]
[--skip-if-published]` pushes one sealed publication — the directory `publish-bake-bundles.sh`
writes for one source and one identity — with ORAS as the publisher, in the order that is the
seal: every bundle manifest, tagged `<identity>` in `plugins/<source>/<package>` and copied by
digest into `plugins/<source>`; the sidecar manifest; the index by digest; then the tags,
`<identity>-<run>` (immutable) and `<identity>` LAST — the tag move is the seal, and until it
moves nothing above is visible under the tag. It refuses a directory without `_complete`, or
with a listed bundle or module absent, before the first push, and prints the index digest and one
`(package, digest)` line per bundle.

**Who calls it, and with what.** Three lanes, one shape — after the share seal succeeds, never
before and never instead:

| lane | source | call |
|---|---|---|
| `main-cd.yml` → `publish-bake` (the platform's own content, one leg per architecture) | `meshweaver-content` | `--tag-run <run id> --release <version> --skip-if-published` — the release identity lands at `plugins/releases:<version>` on every run, exactly as the share's `_releases/<version>` marker does |
| `node-repo-publish-bake.yml` (every satellite, MeshWeaver.Plugins included — the ONLY writer of `plugins` since #6143 removed core CD's `plugins-bake`) | the caller's `bake-source` | `--tag-run <run id> --skip-if-published`, registry and account from the `bundle-registry` / `bundle-registry-publisher` inputs (defaults `cr.meshweaver.cloud` / `publisher`) |

The directory pushed is the one `publish-bake-bundles.sh` **materialises itself**
(`BAKE_PUBLICATION_DIR`): every file of its verified upload plan, byte-checked against the
digests the shares were verified against, then `_complete` last — written only after every share
target sealed or already held the content, so the registry and the shares hold ONE set and the
registry never seals what a share refused. The lanes install ORAS pinned through
`.github/scripts/install-oras.sh` (the one pin; the harness below executes the publisher with the
same binary), and log in with `.github/scripts/registry-login.sh` as the publisher — the password
is `secrets.MW_REGISTRY_PUBLISHER_PASSWORD` on the calling repository, asserted RED by the lane's
preflight before the bake starts (never an `if:` that skips the push), and provisioned in BOTH
secret stores (Actions and Dependabot); core's `main-cd.yml` asserts the same secret in its own
`preflight` and passes it into the lane as `bundle-registry-publisher-password`.

🚨 **`--skip-if-published` is the registry's sealed-skip, and it needs positive proof.** The
shares skip a publication when the sealed directory's `source-commit.txt` equals this bake's
(content × framework); the registry applies the same rule to the tag: `plugins/<source>:<identity>`
resolves, its sidecar manifest's config blob is readable, its `sourceCommit` equals the
publication's, and its `layout` equals the publisher's current layout — then nothing is pushed and
the existing index digest is reported. Anything less (an absent tag, an unreadable index, a
different commit, an older or absent `layout`) pushes. The layout is part of the proof because a
change to WHAT is pushed for the same content (layout 2: a module matched to its bundle
case-insensitively, a module-only package carried as a sidecar) would otherwise never reach an
unchanged source, whose commit does not move — its torn publication would stay under the tag
forever. Bump `PUBLICATION_LAYOUT` in the script whenever that shape changes. Pushing is the
default because a push is idempotent and never leaves the registry worse, while a wrong skip would freeze the tag on a publication the run
was asked to replace. The reason the skip exists at all: a compile is not byte-reproducible, so
without it every core merge under an unchanged identity would push a full set of new blobs to a
registry that runs no garbage collection.

🚨 **`plugins/releases:<version>` is written by BOTH architecture legs of core's content bake,
last writer wins** — the same shape as the share's `_releases/<version>` marker today, whose
content is whichever leg's identity landed last. A reader that needs the identity per
architecture resolves `plugins/meshweaver-content:<identity>` for the identity its own image
reports, not the release tag.

**The index digest is not yet in the registration.** The record `register-publication` POSTs to
`Hosting/PlatformBuilds` (`{event:"bundle-publication", repo, source, sha, identity, image,
digest, platformImage, version, run, upstreams}`, parsed by `BundlePublication` in the Hosting
module) carries no field for it; the lane prints it (`fleet registry:
cr.meshweaver.cloud/plugins/<source>@sha256:…` in the job log and the step summary, from the
`bundle_index_digest` output) and fails RED when it published without one. TODO: add an
`artifact` field to the record and to `BundlePublication`, so the sealed set memex registers is a
list of `(package, digest)` pairs and the catalog index carries `artifact:
cr.meshweaver.cloud/plugins/<source>/<package>@sha256:…` per package — the lane then sends what
it already prints.

The registry's push notification reaches memex (`registry.notifications.url`), which shelves the
module set and proposes it exactly as the bundle POST does today; the POST stays as the metadata
path until the notification handler carries the same effect.

**Who reads which copy, so this page stays true.** The share copy under
`prebuilt-bundles/<identity>/<source>/` continues to be written beside the push until every
consumer reads the registry: the `bundle-fetch` init container reads cr (into the same directory
layout), while `ShippedPrebuiltBundles` — the pre-warm on an installation whose chart does not set
`bundles.registry` — still reads the share. A publication that reaches one target and not the
other is refused as unsealed, as it is today: the lane fails RED on either half.

## A declared module the registry cannot serve is a stated miss

A package whose record declares a module, asked for at a version whose module generation is not on
the registry's shelf (the package record is ahead of the module's own publish), is answered with
NO module section — the section is written only when files exist. The manifest then used to carry
nothing at all, the same bytes an empty archive that declared nothing carries, and every consumer
read it as `NoAssemblies` ("the bundle carried no assemblies") with no reason. The registry now
writes the decline as a `module '<name>': not served — …` entry in `misses`. The consumer's
classification is unchanged (it is still a miss: the module did not arrive at that version), but
the reason reaches the consumer's log and `/health` detail, and such an entry is not counted as
NodeType coverage (`BundleReader.IsModuleNotServedMiss`), so it can neither warn as a NodeType miss
nor fail a require-prebuilt adoption of types that did arrive. It clears when the module for that
version is published.

🚨 **The same miss on a MIXED package is a miss too** (#7401). A package that carries NodeTypes AND a
module arrives with its NodeType assemblies, so it never reaches the empty-bundle branch that names
the reason above; the module miss was filtered out of the NodeType coverage check and then dropped,
the attempt was recorded as a clean `Adopted`, and `/health` `bundle_adoption` could not see that
the bundle arrived without the package's runtime module. The adoption outcome now carries every
`module '<name>': not served — …` entry (`BundleAdoptionOutcome.ModulesNotServed`, built in one
place by `BundleOffering.AdoptedOutcome`), which makes the attempt a miss and names the module:
`<package>: adopted 3/3 — but its runtime module was NOT IN THIS BUNDLE (…): …`. That names a
DELIVERY miss, not an outage: a module generation landed earlier is kept when no bundle carries a
replacement (`PluginBundleClient.AdoptModuleOutcome`), so whether this instance runs the module is
the module activation's answer, never this line's.
The kind stays `Adopted`, because the NodeTypes that arrived were adopted, and a require-prebuilt
mesh still does not fail on it. The ledger's summary line now says the content "did not arrive"
rather than that it "is compiled here instead", which was never true of a module.

### The index advertises a version that can carry its module (MeshWeaver#7181)

The record running ahead is the ordinary state whenever the package repository's publish lane is
red: content reaches the registry through GitSync, which never waits for a green build, and the
module bytes reach the shelf only through that lane. On 2026-10-25, MeshWeaver.Plugins `main` was
red or cancelled for about twenty hours, and every consumer's `/health` `bundle_adoption` listed
`module '<name>': not served — … no retained generation at version …` for the modules merged in
that time.

Those misses did not mean the modules were missing. The index listed the plugin twice: the
record's version first, with no module section, then the shelved generation, with one. A consumer
reads the two halves of an install from different entries. The NodeType adoption
(`PluginBundleClient.Adopt`, `FetchShippedBuilds`, the held-type retry) takes the plugin's first
entry, and the module adoption takes its first entry that carries a module. So the module landed
at the shelved generation, while the NodeType half fetched the record's bundle, found no module in
it and recorded a miss.

The registry now withholds, from the index only, a version whose declared module it cannot serve
at that version, whenever the same plugin has a version whose module it can serve
(`PluginBundleEndpoints.CoherentlyAdvertised` in MeshWeaver.Plugins). Nothing is lost: a bundle
carries the plugin's current NodeType builds whichever version is asked for, so every consumer
path now reads one entry, whose bundle states the version of the module bytes it carries
(MeshWeaver#6067). The registry logs the lag, naming both versions. Two things are unchanged. A
plugin with no servable module version at all keeps its entries, because there is nothing to fall
back to and the `not served` miss is then the true answer. And the download route asked for the
record's version directly still serves it with no module section and the stated miss.

What this does not change: while the lane is red, a consumer runs the record's newer content on
the shelved module generation. Content does not wait for the module, and nothing checks that the
newer content works with the older module. It converges when the lane publishes the module and the
consumer's update pass lands it.

## What stays on the HTTP surface

* **`GET /api/plugins`** and **`POST /api/plugins/files`** — the catalog index and package files.
  The first-run setup wizard reads the index with a raw instance key and no mesh, and its shape
  (`{ packages: [...] }`, `storageType`) does not change; the index gains an `artifact` reference
  per package.
* **`/api/plugins/bundles/prebuilt/…` for the GitHub OIDC build principal.** A `pull_request`
  run has no instance key, and the registry edge validates only instance keys. Those gates keep
  the HTTP surface until the edge can validate a build principal.
* **`POST /api/plugins/bundles/{plugin}`**: the module publish, which lands on
  `ModuleLandingService.ShelveModule`. The endpoint **spools the upload to a temporary file** that
  deletes itself when the request ends. It then reads the manifest, the assemblies, the static
  assets and the natives from that file, and never holds the archive in the managed heap
  (#5501). The endpoint used to copy the body into a growing `MemoryStream` and then call
  `ToArray()` on it. A bundle of N bytes then needed up to about 3N of contiguous large-object
  heap, so large publishes died with `OutOfMemoryException` in `MemoryStream.ToArray` (and before
  that in `MemoryStream.set_Capacity`). `BundleReader` also allocates each entry once, at the
  length the archive declares, instead of growing a buffer and copying it out. That length is the
  producer's claim, so it is refused before anything is allocated when it exceeds what the
  compressed bytes can expand to (Deflate's ~1032:1). The body itself is bounded by Kestrel's
  `MaxRequestBodySize`; neither core nor the Plugins host raises that limit.
  `BundleReaderTest.AModuleReadFromDiskAllocatesEachEntryOnce` pins that per-entry bound; the old
  reader allocated about 5x the entry.
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
