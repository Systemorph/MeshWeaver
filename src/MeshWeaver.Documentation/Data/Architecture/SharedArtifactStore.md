---
Name: Shared Artifact Store
Category: Architecture
Description: One control-owned store per environment for the built artifacts every portal used to keep its own copy of — prebuilt bundles and landed module generations (the assembly cache stays per instance, because its key is an instance-local clock). The build server is the only producer, portals mount it read-only, a client environment's control replicates it one way from ours, each control builds its own local modules into it, and each control's standing prune job sweeps it against one inventory.
---

# Shared Artifact Store

**Status: increments 1–6 are code; nothing is live until the maintainer steps run** (maintainer,
2026-10-19; increments 3–5 built 2026-10-20). Each section says what is built and what is a prepared
maintainer step.

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
/artifacts/<rid>/prebuilt-bundles/.artifact-store-staging/…   (the owner jobs' staging, increments 4–5)
/artifacts/<rid>/modules/<name>@<id>/…, _complete              (increment 3)
(no assembly-cache/: per instance by design — see increment 3)
(nuget-cache/: not built)
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
- **The producer publishes each architecture lane to its own list** (increment 2). A
  `BAKE_PUBLISH_TARGETS` entry is `<account>/<share>[/<base-path>]` and the publisher writes
  `<base-path>/prebuilt-bundles/…`, so a store subtree is a base path (`<account>/<share>/linux-x64`).
  Core CD's platform bake runs one lane per architecture: the amd64 lane reads
  `vars.BAKE_PUBLISH_TARGETS`, the arm64 lane reads `vars.BAKE_PUBLISH_TARGETS_LINUX_ARM64` when it is
  provisioned (falling back to the shared list, unchanged, when it is not), and every step of the lane
  that names a target — the publish, the registry push's lease, the availability assert — reads the
  same per-lane value, so the lease and the publication cannot disagree. The FLEET REGISTRY push is
  linux-x64 only for now: it addresses a publication by identity alone and its skip check and lease
  are not architecture-aware, so the arm64 lane stops before it and says so. Module repositories publish
  linux-x64 only and keep `BAKE_PUBLISH_TARGETS`. The existing arm64 lane is `linux-arm64` (the
  publisher accepts Linux runtimes only); an `osx-arm64` subtree waits for a macOS producer.
- **A reader that serves OTHER runtimes keeps the template.** The expansion writes the unexpanded root
  to `PreWarm:PrebuiltBundleRootTemplate`, so a registry answering a client of another architecture
  (the portal's prebuilt-bundle endpoints) can resolve the CLIENT's store. 🚨 Until those endpoints do,
  the instance that serves the prebuilt surface to other architectures takes NO role in the store —
  neither Consumer nor Owner, since either role renders the `{rid}` root and switches its lookup to its
  own runtime's store. Provisioning the store on such an instance must not also switch its read root.
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
| 2 | the bake publication writes ONCE, to control's store, each architecture lane to its own `<rid>` subtree | per-lane target list in core CD's platform bake (`BAKE_PUBLISH_TARGETS_LINUX_ARM64`), then the variables | code: core CD. Variables: maintainer step |
| 3 | modules: a read-only shared tier in front of the per-instance writable one — a portal resolves a sealed generation from the store and lands its own copy only when the store has none; the owner publishes its landings there | core `SharedModuleTier`, `ModuleLandingService`, `Modules__SharedRoot` | **code** (below). Assembly cache: **not shared, by design** (below). NuGet cache: not built |
| 4 | one-way sync from an upstream registry into a client environment's control's store | core contract `artifactStore.upstream`, chart CronJob `memex-artifact-store-sync`, `artifact-store-sync.sh`; Plugins `HelmValues` | **code** (below). Live: maintainer step |
| 5 | local-source builds on each control, published into its own store | core contract `artifactStore.localSources`, chart CronJob `artifact-build-<source>`, `artifact-store-build.sh`; Plugins `HelmValues` | **code** (below). Live: maintainer step |
| 6 | the standing prune job sweeps the one store | Plugins `StoragePrune` + core `hosting-storage-prune --claim` | Plugins half merged (#3993); the command ships with MeshWeaver#7000 and works once its operator image is on `hosting-operator:main` |
| 7 | drop the per-volume copies on the portals' `/data` | per instance | maintainer step, last |

### Increment 3 — a read-only module tier, never a second writer (built 2026-10-20)

**What is shared, and why only that.** A landed module generation's leaf is its content address —
`<name>@<16 hex of SHA-256 over the bytes>` (#3656) — so two instances landing the same bundle compute
the same leaf for the same bytes, whatever platform build they run. That is the one part of `modules/`
that is safe to share. Everything else under `modules/` is per-instance STATE and stays on the
instance's own volume: the activation records, the link verdicts measured against THIS instance's
platform, the module sets, the instance manifest, and the legacy fixed `modules/<name>/` folder, which
is rewritten in place.

**The layout.** `<store>/<rid>/modules/<name>@<id>/…` plus `_complete`, written strictly last and
listing every file it seals. A record that takes part in the store renders
`Modules__SharedRoot=<mount>/{rid}/modules` (`DeploymentPortalConfig.ArtifactStoreModuleRoot`, every
role), and the OWNER also renders `Modules__PublishToShared=true`. The portal expands `{rid}` at boot
(`BundleRootRuntimeIdentifier.ExpandConfiguredKey`) and configures the tier OF ITS MODULE ROOT
(`SharedModuleTier.Configure(moduleRoot, …)`) before the activation sidecar resolves a single
directory. The setting is keyed by module root, never process-wide, so a second mesh in the same
process (a test host, a dev portal beside a run) keeps its own and cannot clear or redirect the first.

**The read.** `ModuleLandingService.ModuleDirectoryFor` — the ONE resolution rule boot, the pin, the
serving side and the install-completeness check already share — resolves the instance's own
`modules/<leaf>` first, then the SEALED and WHOLE shared copy, then the ordinary local miss
(`SharedModuleTier.Resolve`). A seal is a claim: it is honoured only while every file it lists is
there, so an unsealed directory and a sealed one that lost a file (an incomplete restore) are both
never resolved. A root still carrying an
unexpanded `{rid}` is ignored. So a store that is unmounted, empty or mid-publication is exactly the
pre-tier behaviour: nothing here can dead-page a portal (policy `platform-delivery-must-be-tolerant`).
The boot pin (`ModuleGenerationPin`) copies a resolved generation to process-local storage as before,
so a sweep of the store never pulls bytes out from under a running process.

**The write.** A landing first computes its generation, then runs the shared half FIRST — whether or
not the instance already holds the generation, so an owner whose generation predates the tier, or fell
back to its own volume while the store was down, publishes it once the store is back. If the store holds
it sealed and whole, a CONSUMER records the landing against it and writes nothing to its own volume. The
OWNER publishes it (`SharedModuleTier.Publish`): staged beside the target with flushed, length-checked
writes (`AtomicFileWrite`, so a full share that drops bytes on writeback is refused before anything is
sealed), one rename, the seal last and listing the files it carries; a sealed-but-incomplete target has
its missing files restored from the bytes in hand. A failed publication falls back to the owner's own
volume, logged. A local copy that exists keeps its local repair path, because resolution takes it first.
A consumer that finds no sealed copy lands locally, exactly as before. The
per-instance modules GC walks only `<root>/modules`, so no portal ever deletes from the store.

**Measured by:** core `test/MeshWeaver.Compiler.Pipeline.Test/SharedModuleTierTest` (11 cases: owner
publishes sealed and keeps no local copy; consumer records against the sealed copy and writes nothing;
consumer with no sealed copy lands locally and never writes the store; an owner that cannot write the
store still lands; local-first then sealed-and-whole-only resolution; only a generation is shared; the
GC never deletes from the store; a sealed-but-incomplete copy is not used and the owner repairs it; an
owner holding the generation locally still publishes it; the tier is per module root; a share that drops
bytes seals nothing) and `ArtifactStoreReplicationRecordTest` (the rendered keys per role). Each
was checked red against a reverted guard.

#### The assembly cache is NOT shared — a finding, not a deferral

The design above this section assumed the assembly cache is content-addressed across instances. It is
not. `FileSystemAssemblyStore` names a build `<type>/v<version>-<tag>-<hash>.dll`, and `<version>` is
the NodeType node's **persistence clock on that instance** (`IAssemblyStore`: "the version is the
NodeType MeshNode's Version"). Two instances hold unrelated clocks for the same type, so
`(type, version)` on one instance names different source than the same key on another. The
version-only lookup (`TryGetAssemblyPath`, "the newest file for this version") would therefore hand an
instance ANOTHER instance's bytes for a different source, with only the bind-time MVID check between
that and a wrong bind. The identity lookup (`TryGetBuildPath` by content path and MVID) is safe, but
it never hits a shared tier: an instance's record names only builds that instance compiled or adopted.

So the cross-instance shared form of compiled NodeType output is the **prebuilt bundle store**
(increment 1), keyed by framework identity, source and fingerprint — not the assembly cache. The
assembly cache stays a per-instance compile cache on `/data` (0.9 GiB measured on the public instance,
against ≈121 GiB of bundles). Sharing it would need a content key that does not include the instance's
clock; that is a separate change to `FileSystemAssemblyStore` and is not proposed here.

### Increment 4 — one-way replication to a client environment (built 2026-10-20)

A client environment's control pulls, never the reverse, and never writes our store. It is declared on
that control's record — the store's OWNER — as `artifactStore.upstream`:

```json
"artifactStore": {
  "role": "Owner", "volume": "artifacts",
  "upstream": { "registry": "cr.example.com", "sources": ["plugins"], "pullSecret": "upstream-pull",
                "identities": [], "schedule": "*/30 * * * *" }
}
```

`pullSecret` names a `kubernetes.io/dockerconfigjson` Secret in that control's namespace holding the
environment's own pull credential — a reference, never a value. No storage-account credential crosses
the estate boundary.

**The transport** is the registry's existing bundle surface (`<registry>/plugins/<source>:<identity>`,
the layout every `bundle-fetch` already reads — [Plugin Bundles in the Registry](../PluginBundlesInTheRegistry)).
The chart renders the CronJob `memex-artifact-store-sync` (Forbid concurrency) when the record states a
registry. Its init container copies the control image's own framework identity out of the portal
image, and `artifact-store-sync.sh` replicates that identity plus any declared `identities`:

- **The unit is a sealed publication.** The generation is named by the registry index digest
  (`upstream-<24 hex>`), so a publication that has not changed upstream is a no-op: no pull at all.
- **Pulled into `<root>/.artifact-store-staging/`** on the store's volume, never into a source
  directory.
- **Handed to `artifact-store-publish.sh`.** It checks the staged publication whole (`_complete`
  present, every listed file present, every module bundle `modules/_index` lists present), renames it into place as its own generation, and moves
  `_current` LAST. This is the reader contract of [Sealed Publication Generations](../SealedPublicationGenerations),
  so a client portal never reads a half-copied publication.
- **Outcomes.** An absent tag is "unsealed upstream": nothing is written and the run stays green. A
  denied or torn pull refuses that source, leaves the generation that was current serving, and turns the
  run red. **Nothing is ever deleted**: superseded generations are the owner's prune job's to collect.
- **Architecture.** It writes `<mount>/linux-x64/prebuilt-bundles` (`artifactStore.runtimeIdentifier`),
  because the registry publishes linux-x64 only.

The record refuses an upstream on a CONSUMER, a registry that is not a host, an upstream with no
sources, a source outside `[a-z0-9._-]`, a missing pull Secret and an identity outside `[A-Za-z0-9]`
(`DeploymentPortalConfig.ArtifactStoreProblems`); ANY stated upstream field — identities or a schedule
alone included — makes it an upstream that must be whole. The chart helper `memex.artifactStore` refuses
the same shapes at `helm template`, and refuses sources, a pull Secret or identities stated without a
registry. Aspire refuses an upstream by name: it has no scheduler for the job.

**Retention on the client side** is the client control's own prune job over its own store. A
replicated generation is protected while it is current or a local portal still uses it; whether
upstream still advertises it is not yet an input to that job (see "What is not built").

**Measured by:** core `deploy/aks/scripts/test-artifact-store-jobs.sh` (chart gate), cases 1–13 and 20
(publisher and sync, stub `oras`) and 19 (chart refusals), each checked red against a reverted guard.

### Increment 5 — local sources (built 2026-10-20)

An environment's own sources — repositories it tracks that our build server does not build — are
declared on its control's record:

```json
"artifactStore": {
  "role": "Owner", "volume": "artifacts",
  "localSources": [ { "source": "fabrikam-models", "repository": "https://git.example.com/fabrikam/models.git",
                      "ref": "main", "credentialSecret": "fabrikam-git" } ],
  "testerImageRepository": "cr.example.com/mw-plugin-test",
  "localBuildModuleSources": ["plugins"],
  "localBuildSchedule": "*/30 * * * *"
}
```

The chart renders one CronJob per source, `artifact-build-<source>`, that runs **in the portal image**,
so the compiler is the platform the environment's portals run. An init container copies the platform
tester's `/app` out of `<testerImageRepository>:<the portal image's tag>`, and `artifact-store-build.sh`
then:

1. **clones** the repository at its ref. The read token (key `token` of `credentialSecret`) travels as
   an HTTP header, never in a URL or a log line.
2. **names the generation by every compiler input**, not the commit alone:
   `local-<12 hex of the commit>-<8 hex of a fingerprint>` over the portal's `meshweaver-surface.manifest`
   (the platform build within the identity) and the composed module surface (each bundle's source,
   generation and digest). A new platform build or a new upstream module set therefore names a NEW
   generation and rebuilds; the same inputs already sealed and current build nothing.
3. **composes the gate host** with `compose-gate-host.sh`: the portal's `/app` with the tester CLI
   beside it. The chart carries a byte-for-byte copy of the platform's script, and the chart gate pins
   the equality.
4. **compiles against the module surface from the store itself.** An init container in the ORAS image
   (`artifact-store-modules.sh`, busybox `unzip`) unpacks every `modules/*.module.nupkg` that the CURRENT
   generation of each module source lists for this identity into `<Name>/` — the entry assembly, its
   closure, its static assets and native payloads, the layout node-repo-publish-bake composes — and the
   compile gets each ENTRY ASSEMBLY as `--module` (it loads an assembly, never the archive). When two
   sources list a name, the first wins; a listed bundle the store lacks stops the build. The module
   sources are `artifactStore.localBuildModuleSources`, else the upstream's sources. 🚨 On a control with
   no upstream — ours, whose store the build server fills — name the build server's sources there
   (e.g. `plugins`), or a local source that needs a module cannot compile.
5. **runs the tester's `compile` verb** against the portal's `/app` and its implementation
   frameworks.
6. **publishes or refuses.** A bake keyed to another identity is refused, because it would be inert.
   Otherwise it seals a publication — the bundles, `source-commit.txt`, `repository.txt`,
   `architecture.txt`, an empty `modules/_index` (a local source owns no module), the platform surface
   when the bake wrote one, and `_complete` — and hands it to `artifact-store-publish.sh`.

A local source name is a lowercase DNS label of at most 36 characters, because it also names the
CronJob. It may not be a name `upstream` replicates: one prefix, one producer. 🚨 On OUR control
(no upstream), it must not be a name the build server publishes either. The record cannot see that
list, so it is the declaring maintainer's check.

**Measured by:** `test-artifact-store-jobs.sh` cases 14–18 and 21–23 (stub `git` and `dotnet` — which
refuses an archive as `--module` — real module bundles, the real `compose-gate-host.sh`): the publication
and its markers, the compile on the composed host against the portal's `/app` with each module's entry
assembly and closure, the token never in a URL or a log, the no-op on unchanged inputs, a new generation
when the module surface changes, a short module surface failing closed, the refusal of a foreign-identity
bake, and the declared module sources reaching the job on a control with no upstream. 🚨 **Not measured:** a real compile in a
real cluster. The tester image name on a client estate, the portal image's user and the store's file
permissions are first exercised by the first scheduled run (maintainer step 9).

### What is not built

- **The NuGet cache** (`/data/nuget-cache`) is still per instance. Packages are immutable per
  `{id}/{version}`, so a read-only tier is safe, but `FileSystemNuGetPackageCache` is constructed in
  the Memex `Program.cs` and has no tier yet.
- **The upstream as a retention input.** The client control's prune job does not yet ask whether
  upstream still advertises a replicated generation. It protects by current pointer and consumer
  inventory only.
- **Module generations in the prune report.** `hosting-storage-prune --claim` classifies the bundle
  stores. A `<rid>/modules/` directory appears as a top-level directory with its size, not
  generation by generation.
- **A modules sync.** The upstream sync replicates bundle publications, which carry their composed
  `modules/*.module.nupkg`. A client control's own landings fill its shared module tier. Our control's
  landed generations are not replicated as such.

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
3. **Publish there.** Provision the repository variable `BAKE_PUBLISH_TARGETS_LINUX_ARM64` in core
   with ONLY arm64-compatible destinations — `<account>/<share>/linux-arm64`, never a per-portal share
   that already holds linux-x64 under the same key (the publisher refuses that target, and one failed
   target fails the whole step) — BEFORE adding the store to the amd64 list; otherwise the arm64 lane
   falls back to the amd64 list and publishes into the linux-x64 subtree, where the publisher's
   architecture guard refuses it. Then append control's share (`<account>/<share>`, the value
   `hosting-artifact-store-bind` reports as `artifact_store_share` on the first consumer binding — or,
   before any consumer exists, read off the claim's PV as a recorded break-glass read) to the repository
   variable `BAKE_PUBLISH_TARGETS` in core and in each module repository; the CD identity needs
   `Storage File Data Privileged Contributor` on the account (it already holds it on the same account
   today). Publish each lane to its own subtree: `<account>/<share>/linux-x64` from the amd64 lane and
   `…/linux-arm64` from the arm64 lane.
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
7. **Read the shared module tier** — only once the platform carrying `SharedModuleTier` is on the
   instance (core: the increment 3–5 PR). The record change that makes an instance Owner or Consumer
   already renders `Modules__SharedRoot`, so no second record change is needed. Verify it behaviourally:
   the boot line `[ModuleActivation] shared module tier: /artifacts/linux-x64/modules (…)`, and, on
   the owner, `PUBLISHED to the shared artifact store` on the next module landing.
8. **Replicate into a client environment's control** (increment 4). In that environment, and only by
   that environment's maintainer:
   1. create the pull Secret (`kubernetes.io/dockerconfigjson`, the environment's own registry key) in
      the control's namespace;
   2. make its control the Owner of its own store (step 1, in that environment);
   3. add `artifactStore.upstream` (registry, sources, `pullSecret`) to the control's record.

   The next roll renders `memex-artifact-store-sync`. Let its declared schedule run it (every 30
   minutes unless the record says otherwise) — there is no routine manual trigger. Confirm the result
   through that control's Hosting: a `StoragePrune` report of the store claim
   (`hosting-storage-prune --namespace <ns> --claim <claim>`) lists the replicated identity and source
   with a current `upstream-…` generation, and the CronJob's run log is in that estate's Loki. Then point
   that environment's portals at the store as Consumers (step 4, in that environment). *Break-glass
   only:* starting a run by hand (`kubectl create job --from=cronjob/…`) is a direct cluster write
   outside the control plane; there is no governed trigger yet.
9. **Build local sources** (increment 5). On the control that owns them: create the read-token Secret
   (key `token`), then add `localSources`, `testerImageRepository` and — on a control with no upstream —
   `localBuildModuleSources` to the record. The tester image must be pullable with the portal's pull
   secret at the portal's tag. Let the schedule run it, then read the first run's log end to end in that
   estate's Loki: it is the first real compile on that cluster. The same `StoragePrune` report shows the
   `local-…` generation. The break-glass note of step 8 applies to a manual run.

## Related

- [Storage Prune](../StoragePrune) — the measurement and the prune of this store
- [Released Artifact Retention](../ReleasedArtifactRetention) — the contract
- [Sealed Publication Generations](../SealedPublicationGenerations) — the reader contract replication keeps
- [Prebuilt Bundle Retention](../PrebuiltBundleRetention) — the per-portal sweep this store retires
