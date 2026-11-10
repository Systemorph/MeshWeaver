---
nodeType: Markdown
name: Boot Progress
category: Architecture
description: >-
  What a person sees while a fresh portal boots — one progress document recorded by the code doing
  the work, served at /_boot/progress, rendered by the boot page and the memex-local terminal — and
  why the module phase lands modules in parallel by dependency level instead of one at a time.
icon: /static/NodeTypeIcons/box.svg
---

# Boot Progress

The maintainer, 2026-11-09, watching a fresh Mac instance boot after first-run setup: *"can we bring
better progress bar?"* The portal pod sat NotReady for minutes while it landed its registry module
bundles one at a time, and the boot page showed a spinner with one fixed sentence.

## One source, two readers

| Piece | Where | What it does |
|---|---|---|
| `BootProgress` | core, `MeshWeaver.Mesh.Contract` | The ONE recorder. The code that does the work records phases, module steps and errors into an immutable snapshot (lock-free, `ImmutableInterlocked`). |
| Recording sites | core, `MeshWeaver.PluginCatalog` | `InstanceAutoRegistrationService` records the phases (`bake`, `register`, `resolve`, `modules`, `content`, `activate`). `register` names an open registration's wait for an administrator's consent, which used to show as nothing at all; `PluginBundleClient` records each bundle's size and download time. |
| `BootProgressReport.From` | core | The ONE renderer: derived values (seconds, ETA, headline) are computed here, so no reader re-derives a fact. |
| `/_boot/progress` | MeshWeaver.Plugins, `Memex.Portal.ServiceDefaults` | Anonymous JSON, `no-store`. Adds the readiness checks that still say no, read with the SAME predicate as `/ready` and shared for 3 s. |
| Boot page | MeshWeaver.Deploy chart, `memex-boot-page` | What the ingress serves while the portal has no ready endpoint. It proxies `/_boot/progress` to the portal pods through a Service that publishes not-ready addresses, and renders it. |
| `memex-local` | MeshWeaver.Deploy | Reads the same path through the API server's service proxy and prints it in the terminal. |

Kestrel listens about one second after the process starts, long before readiness, so the
document is reachable for the whole boot. Before the process starts (image pull, init containers) the
proxy has nothing to reach and the page says so.

## The document

`schema` 1. `phase` is the running phase id (`ready` once ready, `starting` before any phase).
`headline` is one line. `etaSeconds` is the running phase's own measured throughput,
`(total − done) × elapsed ÷ done`. It is null until one unit has finished, because a guess before the
first measurement is not an ETA. Because the rate is measured, it already accounts for how many
modules land at once. `phases[]` carries state, start, end, seconds, done/total/failed, concurrency and
a detail sentence. `modules[]` carries package, module, version, state (`queued`, `running`, `landed`,
`current`, `skipped`, `failed`), level, bytes, download seconds and error. The list is dropped once
the portal is ready, because it is boot detail, not state. `checks[]` lists the readiness checks that
are not Healthy, in their own words. `errors[]` holds every error named during the boot (at most 50).

**Across the activation restart.** A fresh instance lands its modules and then restarts to load them
(restart-as-activation). The snapshot is written every 2 s, best effort, on the file-system pool, to
`{Modules:Root}/boot/{host}.json`. The next process of the same pod reads it back as `previousBoot`,
so the page can say "Restarted to activate 48 landed modules" instead of starting its story over.
Without a configured `Modules:Root` nothing is written, because the default root is the read-only
app directory.

## Why the module phase was slow — measured

These are the bundle transfers in the Loki of a local Mac instance on 2026-11-09, as
`PluginBundleClient` logs them (`Bundle for X@v: N byte(s) over HTTP … in T ms`):

| Bundle | Bytes | Seconds |
|---|---:|---:|
| LearningRoadmap | 1,209 | 6.7 |
| Maps | 28,113 | 13.2 |
| OgCard | 50,990 | 17.6 |
| DefaultViews | 575,596 | 16.2 |
| Mcp | 19,970,639 | 20.2 |
| Hosting | 58,917,182 | 27.9 |

The transfer time barely depends on size: a 1 KB bundle costs about 7 s and a 59 MB one 28 s. The
registry's fixed per-request latency dominates, so landing ~48 modules back to back costs
~14 minutes. Two consumer-side defects multiplied it:

1. **One at a time with no dependency reason inside a level.** `LandModulesFirst` used
   `Concat()` over every carrier. A dependency reason exists only BETWEEN levels: a module is
   link-probed against the modules already landed (`ModuleLinkObservation` →
   `ModulePlatformSurface.WithSiblingModules`), so a provider that references `MeshWeaver.AI` must
   find AI landed. The phase now runs `LevelledWave`: levels from
   `PackageDependencyGraph.DependencyLevels` over the WHOLE selection, each level a barrier, and up to
   `PluginCatalog:ModuleLandingConcurrency` modules at once within a level (default 4, clamped to
   1…16, where 1 restores the old behaviour). The volume writes still serialise on
   `ModuleLandingService`'s own cap-1 lane. The levels honour every DECLARED `requires`. A module
   whose link probe needs a sibling its package never declared used to land only because the
   one-at-a-time order happened to put that sibling first, so every fresh module the wave could not
   land is retried ONCE, one at a time, after every level has landed. A registry 503 gets its second
   chance there too.
2. **Every module-only bundle downloaded twice.** The node install's `PluginBundleClient.Adopt`
   downloaded the same bundle again only to log "declares no NodeTypes". For example, AzureFoundry
   (22,988,649 bytes) took 22.0 s, then 22.4 s. `BundleOfferingMemo` now remembers, for 10 minutes
   (one pass), what the module landing read. The key is the registry, package, version and the
   index's per-bundle module identity, so a rebuild re-advertised under the same version is a
   different key. `Adopt` answers from the memo without a request. A bundle that states a miss or
   carries NodeType assemblies is never remembered and is downloaded as before. `AModuleOnlyBundleIsDownloadedOncePerPassTest` pins
   both directions.

## Not established

- **What the registry spends ~7 s on per request.** The download route reads the authoritative
  activation list fresh per fetch (`PluginBundleEndpoints.Bundle` → `AuthoritativeActivation`), which
  measured 7–9 s on the fleet registry's file share on 2026-09-26. That is the likely floor, but no
  per-stage timing of a successful download was read here.
- **How the registry behaves under 4 concurrent downloads per booting instance.** The registry
  answers a stalled request with 503 and `Retry-After` (`RegistryAnswerDeadline`), and the consumer
  treats a 503 as transient.

Pinned by `BootProgressTest` (levels, the wave's barrier and degree, ETA, headline, persistence across
a restart, the memo), `AModuleOnlyBundleIsDownloadedOncePerPassTest`, and, in MeshWeaver.Plugins,
`BootProgressEndpointTest`.
