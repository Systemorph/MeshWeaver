---
Name: Rolling-Update Build Tolerance
Category: Architecture
Description: Why a rolling platform update recompiled Store/Plugin ten times in four minutes and blanked every instance of the type behind "build did not settle within 30s" — two platform generations, one NodeType record, one framework identity per record — and the tolerance rules that now let an instance render on the last build its process can load instead of waiting for a settle that another generation cannot deliver.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.2-8.6"/><path d="M21 3v6h-6"/><path d="M12 7v5l3 3"/></svg>
---

# Rolling-Update Build Tolerance

> **Maintainer directive, 2026-09-12 (relayed by the coordinating session):** *"it should all be tolerant"* — a page of an instance whose type has a last-good compiled assembly must render on that assembly while a recompile is in flight; the 30-second *"did not settle"* fallback is itself the defect when a loadable build exists. That directive is what this page implements.
>
> Two further statements reached the investigating session mid-task from an **unattributed, unverified** source (not the maintainer's verbatim words, not a doc): *"compile must happen on CI — at runtime you must resolve a package version, not try to compile"* and *"dynamic compilation should be disabled for CI-built plugins."* They agree with the existing [#3583](https://github.com/Systemorph/MeshWeaver/issues/3583) rule (a stale-but-compatible page beats a refusal), and rule 3 below leans on them; the structural remainder in the issue at the end is conditional on the maintainer confirming them.

## The incident, measured

On 2026-09-12 at 06:25Z `https://memex.meshweaver.cloud/DeepSign` — an instance of the `Store/Plugin` NodeType — rendered:

> This item's type has not finished compilation yet, so its page couldn't be built. **NodeType 'Store/Plugin' build did not settle within 30s.** Instance 'DeepSign' is rendering this fallback until the type's build settles.

`get @Store/Plugin` at the same moment said `compilationStatus: Ok`. Every other instance of the type on the new pods (Edu, RolePlay, Chess, ClaudeCode, Voice, WebSearch, DataModelling, LearningRoadmap, Hosting, Approvals, DoublePendulum, AgenticEngineering, …) logged the same `TimeoutException: NodeType 'Store/Plugin' recompile did not settle and no compile is in flight (within 30s)`, and the `content-types` health check reported *Degraded: Store/Plugin ×116 … this replica cannot type*.

What the mesh recorded:

| Time (UTC) | Fact | Where |
|---|---|---|
| 06:10:48 → 06:14:48 | Rolling update `ci.8372` → `ci.8399` on `memex-cloud/memex-portal-deployment` (`maxSurge 1, maxUnavailable 0`); the three old pods get `Killing` at 06:12:22, 06:13:35, 06:14:48 | `kubectl get events` |
| — | `terminationGracePeriodSeconds: 1800` with a `preStop` that waits on `/drain` for up to 1680 s — **the old generation keeps serving hubs for up to 30 minutes after `Killing`** | Deployment spec |
| — | `ci.8399` announces framework identity **`sa74cfbd…`** (`DynamicTypePreWarmer: … framework identity sa74cfbdd57e3a4f4440792fd1800aa3d, build g4c99ec26…`); `ci.8372` (still running as memex.systemorph.com) announces **`s6649734…`** | pod logs |
| 06:17:48 / 06:17:50 / 06:18:02 | Three concurrent `Update Store to the built commit 38ebf08e` activities (one per new pod) | `Store/_Activity` |
| 06:18:38 | Release `X3ICiS9-`: an **adoption** under `sa74cfbd` (`v13825-sa74cfbd-…dll`, no compile activity) — the CI bundle for the live identity was on `/data/prebuilt-bundles/sa74cfbd…` | `Store/Plugin/Release` |
| 06:18:58 … 06:23:08 | **Eight compiles, all producing `s6649734`-tagged assemblies** (`v13834`, `v13839`, `v13843`, `v13847`, `v13855`, `v13859`, `v13863`) — the per-NodeType hub's owner grain was on a *draining old-generation pod*, and every activation on a new pod read the record as ABI-stale, flipped it Pending, and the old owner rebuilt it under its own identity | `Store/Plugin/_Activity/compile-*`, Release artifacts |
| 06:23:12 | One compile producing **`sa74cfbd`** (`v13872-sa74cfbd-…dll`) — the owner grain had moved to a new pod | same |
| 06:24:53 | The record settles for the new generation: `compiledFrameworkVersion sa74cfbd`, `adoptedSourceFingerprint == currentSourceFingerprint == c4730d4bf8f180fb`, `buildProvenance AdoptedVerified`, version 13874 | `get @Store/Plugin` |
| 06:25 → 06:44 | Activations on the new pods **still** time out — the instance page stays on the fallback; `get @DeepSign/layoutAreas/` times out | pod logs, MCP |
| ≈ 06:45 | The last old pod's grace expires; `DeepSign` resolves again (its page still shows the overlay it bound earlier until the instance is recycled) | MCP |

The same shape on 2026-09-11 18:47–18:55Z (`ci.8323` → `ci.8372`) and on 2026-09-10 14:40–14:47Z. `Store/Plugin` on memex is at node version 13 870; on systemorph, which rolls less often, 2 895.

The seeder's line names the second defect. On every new pod:

```
Prebuilt assembly for Store/Plugin DECLINED before writing (#2813): the bundle records source
fingerprint c4730d4bf8f180fb but the live sources are bf501962b9ea4c2a (bundle module version
221c6c286785ddf2, current 1.10: Incompatible) — the owner would not verify the adoption …
ShippedPrebuiltBundles: adopted no prebuilt assembly for 1 of 2 requested NodeType(s) — Store/Plugin.
A bundle entry under /data/prebuilt-bundles/sa74cfbd… DID name 1 of them … so those bytes were
present and did not land.
```

`221c6c286785ddf2` is `manifest.lock`'s content hash (the bake wrote `ReleasedVersion ?? ModuleVersion ?? Version`, i.e. the hash whenever no released version was recorded); `ModuleVersionCompatibility.MajorOf` read its leading digits as *major 221* against the root's `1.10` and classified the pair **Incompatible** — the one verdict that refuses. The bundle's fingerprint (`c473…`) was the fingerprint the new generation itself computed; the "live" `bf50…` it was compared against had been stamped by the *old* generation's toolchain.

## The mechanism

Three things compose:

1. **A rolling update runs two platform generations against one mesh for the whole termination grace.** With a 30-minute drain, that is not a race window; it is the normal operating state of every roll.
2. **A NodeType record holds ONE `compiledFrameworkVersion`.** Each generation reads the other's build as ABI-stale (`HasUsableBuild` is an equality on the live identity). The record cannot be "usable" for both.
3. **The activation path was intolerant.** On a foreign stamp it flipped the type Pending and *waited* for a usable build (`TriggerRecompileAndRetry … requireUsableBuild: true`); on an in-flight status it *waited* for settle (`IsCompileSettled` rejects Pending/Compiling) and then painted the progress overlay; when the compile ran on the other generation the wait never satisfied and the 30-second no-progress budget produced the fallback. Every activation is a fresh attempt, so the "bounded" retry (`MaxRecompileAttempts = 1`) bounded nothing across a fleet of instances — that is the storm.

After the record settled for the new generation (06:24:53) the activations still failed. Storage was usable; the node the activation decided on came off the mesh-hub **mirror** — the workspace's per-path replay subject — which had replayed the old owner's snapshot after that owner drained and its sync stream went silent. This is the shape [#2409](https://github.com/Systemorph/MeshWeaver/issues/2409) fixed on the *in-flight* branch (`ConfirmInFlightAgainstStorage`); the framework-stale branch had no such confirmation. (The mirror latch is the inference that fits every measurement; the ENRICH-DIAG lines that would show the mirror's value are logged at Information and were not retained at production level. The fix below does not depend on the inference: it consults storage on that branch regardless.)

## The tolerance rules (implemented)

**1. An in-flight type that still names a loadable last-good build is BOUND, not awaited.** `NodeTypeEnrichmentHelpers.IsBindableWhileCompiling` — Pending/Compiling *and* `HasUsableBuild` for this process — is admitted by the slow path's settle predicate (`IsBindableOrSettled`) and routed to `ApplyStreamResult`, whose `HasUsableBuild` branch precedes every status branch and arms `WithStaleAssemblySelfHeal`. The instance renders on the previous bytes now; when the rebuild publishes a different usable path, the watcher offers (or, on a converging portal, takes) the new build. An in-flight type with *nothing* loadable keeps the grace-then-progress-overlay behaviour: there is nothing to render on.

**2. The framework-stale branch confirms against STORAGE before it asks for a rebuild.** One `AuthoritativeTypeRead` (the same read the in-flight branch already makes); when storage holds a build for the live identity, the activation binds it and writes nothing. Only when storage agrees the build is foreign does the Pending flip happen — and on a mesh where module content resolves from bundles that flip is the next thing to remove (see the issue).

**3. A compatible CI bundle beats a local compile** (motivated by the unverified statements above; also the tolerant reading of #3583 — clearing a record and compiling leaves no page in between). `PrebuiltAssemblySeeder.DecideAfterStaleDecline` is the rule as a table: when the record's build does not load on this process, no compile is in flight, and a bundle for the live identity is in hand whose module version is compatible or unknown, it is adopted as the last build the mesh holds (`StaleAdopted`) — a page on CI-built bytes immediately — instead of clearing the record and dispatching Roslyn. A compiling mesh still converges (the stale-adopted record reads dirty and the next release request rebuilds it behind a page that is already up).

**4. A content hash is never a SemVer major.** `ModuleVersionCompatibility.MajorOf` yields a major only for a version-shaped value (digits followed by `.`, `-`, `+` or the end); a hash reads Unknown, which never refuses. And the bake now records `ReleasedVersion ?? Version ?? ModuleVersion`, so new bundles carry the SemVer the rule was written for.

Tests: `InFlightLastGoodBuildBindsTest` (predicates, virtual-time wait), `ForeignFrameworkStampOnTheMirrorTest` (real mesh: foreign stamp on the mirror, usable build in storage ⇒ bound, version unchanged, no Pending flip), `StaleDeclinePrefersBundleTest` (the decision table), `ModuleVersionCompatibilityTest` (the hash pair that refused Store/Plugin).

## What this does not fix (tracked)

- **One identity per record.** Two generations will still alternately stamp the record for the whole drain; with the rules above nobody *waits* on the foreign stamp and nobody compiles when a bundle exists, but the record still flips. The structural fix is a build record **per framework identity** (the store already keys bytes by identity tag) or, equivalently, the unverified statement's rule: a CI-built type's record names a *package version*, and each process resolves `(package version, own identity)` from the bundle store — never a compile.
- **Runtime compile for module content.** The release-request watcher still compiles a dirty module type on a mesh that may compile. If that statement is confirmed it should resolve the sealed bundle for the synced commit and hold (`BuildDeliveryHold`) until one exists.
- **The source fingerprint depends on the toolchain.** The two generations computed different fingerprints for the same sources (`bf50…` vs `c473…`), so a cross-generation record always reads "source moved". The seeder should compare a bundle against the fingerprint *this* process computes from the live snapshot, not against the stored one.
- **The drain.** A 30-minute grace with two generations serving is a policy the platform tolerates now; it is still the window in which every cross-generation mismatch happens.

Tracking issue: [#4071 — Rolling-update compile storm: per-identity build records and no runtime compile for CI-built plugins](https://github.com/Systemorph/MeshWeaver/issues/4071).

## Operator notes

- A type that compiles every 15–30 s with alternating `-s…-` tags in its `Release/` artifacts is a **generation overlap**, not a source problem. Read `kubectl get rs` and the pods' `framework identity` log line before touching the type.
- An instance already on the *"did not settle"* fallback stays there until it is recycled (its self-heal is version-gated on the type record). **Recycle the instance**; do not recompile the type.
- `Prebuilt assembly … DECLINED … Incompatible` with a 16-hex "module version" was the classifier defect above; after this change the same bundle reads Unknown and is adopted.
