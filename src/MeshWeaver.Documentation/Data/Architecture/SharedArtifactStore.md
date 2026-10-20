---
Name: Shared Artifact Store
Category: Architecture
Description: One control-owned store per environment for the built artifacts every portal used to keep its own copy of — prebuilt bundles first, then modules, the assembly cache and the NuGet cache. The build server is the only producer, portals mount it read-only, a client environment's control replicates it one way from ours, each control builds its own local modules into it, and each control's standing prune job sweeps it against one inventory.
---

# Shared Artifact Store

**Status: DESIGN, with the first increments landing** (maintainer, 2026-10-19). Each section says
what is built and what is a prepared maintainer step.

## The defect

`prebuilt-bundles/`, `modules/`, `assembly-cache/` and `nuget-cache/` are **content-addressed** —
keyed by framework identity or compatibility key, source and publication token, content hash, or an
immutable package version — yet every portal keeps its own copy on its own `/data` volume. The bake
publication uploads the same bytes once per volume (`BAKE_PUBLISH_TARGETS` lists one share per
portal), every portal compiles what another already compiled, and each volume fills on its own
schedule: on 2026-10-19 the public instance's share filled while the control instance held the same
artifacts (see [Storage Prune](../StoragePrune) for the measurement).

Per-instance state stays per instance: `dataprotection-keys/` (the instance's own keys) and dumps
(which belong on the declared dump volume, never on `/data`).

## The shape — maintainer decisions, 2026-10-19

| decision | consequence |
|---|---|
| *"shouldn't the modules cache be shared?"* | ONE store per environment for the content-addressed artifacts |
| *"must be built on build server and then shared"* | the build server / CI build lane is the ONLY producer; portals never compile a module locally when a shared build exists for their framework identity |
| *"should be owned by control"* | the store — its claim, its declaration, its retention and its prune job — belongs to the environment's CONTROL instance: declared on the control record, created and resized by control's operator, pruned by control's standing job |
| every environment has its own control | a client environment's control owns that environment's store and **replicates it one way from ours** (Systemorph's control is upstream) |
| local modules | sources an environment tracks itself are built by THAT environment's control, stored in its store, and mounted read-only into its other instances |

```
our build server ──publish──▶ our control's store ──one-way sync──▶ a client environment's control's store
                                   │   ▲                                      │   ▲
                       read-only   │   │ local builds              read-only  │   │ local builds
                                   ▼   │                                      ▼   │
                         our portals (memex, memex-cloud, …)         that environment's portals
```

Each control's standing prune job sweeps its OWN store against its OWN inventory, under the retention
contract and the protected-label set. A synced upstream artifact is protected while upstream still
advertises it OR a local portal still uses it.

## The store

**One Azure Files share per environment, provisioned by control's own record.** The control record
declares a volume like any other (`volumes[]`, `create: true`, a size, `azurefile-memex`, RWX)
mounted at `/artifacts`, plus `artifactStore: { role: Owner, volume: <that volume's name> }`. The
chart creates the claim in control's namespace; `hosting-pv-resize` grows it from the record's
`size`, exactly as for every other declared volume. Layout under the share root — the same layout the
data volumes carry today, so every reader keeps its paths relative to one root:

```
/artifacts/<rid>/prebuilt-bundles/<identity>/<source>/_current, <generation>/…, _complete
/artifacts/<rid>/prebuilt-bundles/_releases/<version>
/artifacts/<rid>/modules/…            (increment 3)
/artifacts/<rid>/assembly-cache/…     (increment 3)
/artifacts/nuget-cache/…              (increment 3; packages are architecture-neutral or carry their own runtimes/)
```

**The layout is architecture-aware.** The compatibility key (`c003e001`) names a platform major and
epoch and NO architecture, and the publisher refuses a second architecture under an existing key. On
a per-portal volume that never mattered — one volume, one architecture. A shared store serves every
build the environment runs: the AKS portals (`linux-x64`) and the editions built for Apple hardware
(`osx-arm64`) or arm nodes (`linux-arm64`). So the store holds ONE bundle store per runtime
identifier, `<rid>/prebuilt-bundles/`, each in exactly the layout every reader already reads:

- **The reader resolves its own.** A record that takes part in the store renders
  `PreWarm__PrebuiltBundleRoot=<mount>/{rid}/prebuilt-bundles`, and the portal expands `{rid}` once at
  boot to ITS process's runtime identifier (`<os>-<arch>` from the running process, never a configured
  value) before anything reads the key. Every reader below that key — the pre-warm, the seal arrival,
  the sync reconciler, the bundle endpoints — is unchanged. A missing `<rid>` store is a missing
  bundle root: nothing to adopt, compile as before.
- **The producer needs no new code.** A `BAKE_PUBLISH_TARGETS` entry is `<account>/<share>[/<base-path>]`
  and the publisher writes `<base-path>/prebuilt-bundles/…`, so the store is published as one target
  PER ARCHITECTURE the build server builds: `<account>/<share>/linux-x64`, and `…/osx-arm64` from the
  arm64 build. The publisher's existing refusal of a second architecture under one key now protects
  each per-architecture store instead of colliding in one.
- **A reader that serves OTHER runtimes keeps the template.** The expansion writes the unexpanded root
  to `PreWarm:PrebuiltBundleRootTemplate`, so a registry answering a client of another architecture
  (the portal's prebuilt-bundle endpoints) can resolve the CLIENT's store. 🚨 Until those endpoints do,
  the instance that serves the prebuilt surface to other architectures must not become a Consumer.
- **Aspire refuses a Consumer.** A consumer's binding is a Kubernetes static PV; a Docker volume of its
  own would hold none of the owner's publications, so the AppHost fails by name instead.
- **A consumer's teardown never deletes the store.** `hosting-pv-purge` deletes a namespace's shares,
  and a consumer's binding PV is claimed by the consumer's namespace — so it keeps every share that a
  binding labels `meshweaver.io/purpose=artifact-store` or that any other namespace's PV references,
  and removes only the stale PV object.
- **The prune job walks every per-architecture store** (`hosting-storage-prune --claim`, identities
  reported as `<rid>/<identity>`).

The per-portal volumes keep their single `prebuilt-bundles/` until they are retired; the store is
new, so it starts architecture-aware rather than migrating into it.


**Consumers bind the same share read-only.** A PVC is namespaced, so each portal namespace gets a
STATIC PersistentVolume + PersistentVolumeClaim pointing at control's share — the pattern the CI
platform volume has run since 2026-09-12 (Systemorph/Memex `deployments/aks/ci-runners/
ci-platform-runner-volumes.yaml`): `ReadOnlyMany`, `csi.readOnly: true`, a distinct `volumeHandle`
per binding (the kubelet stages a CSI volume under a hash of the handle), `persistentVolumeReclaimPolicy:
Retain` (deleting a consumer's binding never deletes the share). The consumer record declares the
volume `readOnly: true` at `/artifacts` and `artifactStore: { role: Consumer, volume: …, sourceNamespace:
<control's namespace>, sourceClaim: <control's claim> }`; the operator step that creates the binding
(`hosting-artifact-store-bind`) reads control's bound PV for the account and share — never a value
typed into a record — and refuses to bind a claim that is not bound.

**The portal reads it.** For a record that declares an artifact store, `PreWarm__PrebuiltBundleRoot`
renders as `<mount>/{rid}/prebuilt-bundles` (`DeploymentPortalConfig.BundleRoot`; the portal expands
`{rid}` at boot), so every reader of the bundle root — the pre-warm, the seal arrival, the sync reconciler, the bundle endpoints — reads the
shared store unchanged. A consumer's bundle retention sweep is disarmed (it cannot delete on a
read-only mount, and the store is not its to prune).

## Increments

| # | what | where | status |
|---|---|---|---|
| 1 | the store declared and created on control; the consumer binding command; the record fields; the read path for prebuilt bundles | core contract + chart + operator, Plugins plan, Memex records | **code: PRs open. Live: maintainer steps below** |
| 2 | the bake publication writes ONCE, to control's store | `BAKE_PUBLISH_TARGETS` (a repository variable) | maintainer step |
| 3 | modules, assembly cache and NuGet cache: a read-only shared tier in front of the per-instance writable one — a portal resolves a content-addressed artifact from the store first and compiles only when the store has no build for its identity | core `FileSystemAssemblyStore`, module loader | design (below) |
| 4 | one-way sync from our control's store to a client environment's control's store | control operator | design (below) |
| 5 | local-module builds on each control | control build role | design (below) |
| 6 | the standing prune job sweeps the one store | Plugins `StoragePrune` + core `hosting-storage-prune --claim` | command landed; sweep in the Plugins PR |
| 7 | drop the per-volume copies on the portals' `/data` | per instance | maintainer step, last |

### Increment 3 — a read-only tier, never a second writer

The assembly store is content-addressed (file name carries the content hash; the record names the
build by path and MVID), so a shared read-only tier is safe by construction: a reader that finds the
hash in the shared tier loads it; a reader that does not, compiles into its own writable tier exactly
as today. Nothing a portal does writes the shared tier. The producer (the build lane, and control for
local modules) writes it, sealing each publication with the same `_complete`-last rule the bundles
use. "Portals never compile locally when a shared build exists" is then a property of the lookup
order, not a policy a portal could forget.

### Increment 4 — one-way replication to a client environment

A client environment's control pulls from ours, never the reverse, and never writes our store. The
unit of replication is a SEALED publication (a rid/identity/source generation with its `_complete`, a
release marker); an unsealed generation is never copied. Replication writes the generation first and
moves `_current` last — the reader contract of [Sealed Publication Generations](../SealedPublicationGenerations)
— so a client portal never reads a half-copied generation. Transport: the fleet registry's existing
bundle surface (`cr.meshweaver.cloud`, the `bundles.registry` path a client estate already pulls
from), authenticated with that environment's registry key — no storage-account credential crosses the
estate boundary. Retention on the client side protects a synced artifact while our control still
advertises it or a local portal still uses it.

### Increment 5 — local modules

An environment's own sources (repositories it tracks that our build server does not build) are built
by its control's build role, at the platform its portals run, and published into its own store under
their own source names. They are swept by the same job, under the same contract, with the local
portals as the consumer inventory.

## Retention and the prune job

The store has ONE owner, so it has ONE sweep and ONE inventory: control's standing `StoragePrune`
job ([Storage Prune](../StoragePrune)) — the classes, the 30-day minimum, the last sealed set per source,
major AND architecture, the consumer inventory of every portal that mounts the store, the
protected-label set (core `.github/release-channels.json`, read through `release-channels.py`; a failed
lookup aborts), and the publication lease shared with the publisher. Until the consumer inventory is complete it deletes
no bundle; it measures and records.

## Maintainer steps (prepared, not executed)

Every step below changes a live mount, rolls an instance or touches an estate, so each is a
maintainer's act. In order:

1. **Create the store.** Merge the control record change (volume `artifacts`, `create: true`,
   `artifactStore.role: Owner`) and let control's next roll render it, or file a `Reconcile` of
   `Deployments/control` on the control instance. Confirm it with a `StoragePrune` instance action
   naming the claim (`hosting-storage-prune --namespace control --claim control-artifacts`): it refuses
   a claim that does not exist and reports the share's size from the mount.
2. **Seed it** from the newest complete copy (a data share whose seals are healthy): copy
   `prebuilt-bundles/<compatibility key>/` and `_releases/` into `linux-x64/prebuilt-bundles/` with a share-to-share `azcopy`, then compare a `StoragePrune` report of the store
   against one of the source share. *Break-glass:* there is no supported action for a share-to-share
   copy yet; the copy is a maintainer's command, and the report pair is its record.
3. **Publish there.** Append control's share (`<account>/<share>`, the value
   `hosting-artifact-store-bind` reports as `artifact_store_share` on the first consumer binding — or,
   before any consumer exists, read off the claim's PV as a recorded break-glass read) to the repository
   variable `BAKE_PUBLISH_TARGETS` in core and in each module repository; the CD identity needs
   `Storage File Data Privileged Contributor` on the account (it already holds it on the same account
   today). Publish one target per architecture: `<account>/<share>/linux-x64` (and `…/osx-arm64` from the arm64 build).
4. **Point each portal at it** — only once its platform expands `{rid}` in the bundle root (the
   Shared Artifact Store contract PR), since an older reader would look for a literal `{rid}`
   directory and find nothing. One instance at a time: merge its record change (volume `artifacts`
   `readOnly: true`, `artifactStore.role: Consumer`). Its next roll runs `hosting-artifact-store-bind`
   and renders `PreWarm__PrebuiltBundleRoot=/artifacts/{rid}/prebuilt-bundles`. Verify it behaviourally: the
   portal's `/health` `nodetype_bake` names the shared root, and its pre-warm adopts.
5. **Drop the instance's share from `BAKE_PUBLISH_TARGETS`** once it reads the store.
6. **Drop the per-volume copies** (`/data/prebuilt-bundles` on that instance) only after a week of
   adoption from the store. The `StoragePrune` report classifies them as legacy or superseded on the old
   share; deleting them is a destructive step and a person's decision.

## Related

- [Storage Prune](../StoragePrune) — the measurement and the prune of this store
- [Released Artifact Retention](../ReleasedArtifactRetention) — the contract
- [Sealed Publication Generations](../SealedPublicationGenerations) — the reader contract replication keeps
- [Prebuilt Bundle Retention](../PrebuiltBundleRetention) — the per-portal sweep this store retires
