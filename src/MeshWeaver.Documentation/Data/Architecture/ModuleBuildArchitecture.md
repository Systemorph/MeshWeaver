---
nodeType: Markdown
name: Module Build Architecture
category: Architecture
description: THE unified build process — every repo, one shape. The platform image is the compiler and the reference set, everything shared is staged once per run, one Roslyn workspace builds the graph fail-fast, outputs are content-addressed in blob storage so unchanged means no compile, and every gate compiles against implementation frameworks.
icon: /static/NodeTypeIcons/code.svg
---

# Module Build Architecture

**This is THE build process, unified across every repo** (maintainer directives, 2026-08-31 →
2026-09-01: *"we want the memex to take care of the build"*, *"update once at beginning for
everyone"*, *"create one roslyn workspace, all plugins to be rebuilt plus dependencies"*,
*"maybe put in blob storage for build"*, *"unify buildprocess"*). The platform repo carries the
mechanics — the reusable lanes, the builder, this page; a consuming repo carries ONLY policy
(which modules, which pins). A repo whose CI deviates from this page is behind, not different.
Never hand-roll a repo's build.

## The pipeline, end to end

```
select ─┬─► prepare (ONCE) ──► build (ONE workspace) ──► pack ─┬─► verify
        │                                                      │
        └─► tests  (a lane of its own — publish: false) ────────┘
             …or INLINE inside pack, before the hand-over, when the call publishes
```

1. **select** — which bundles this diff can reach. The selector can say "these" or
   "everything", never "skip"; a workflow/pin change legitimately selects everything, because
   the compiler itself changed.
2. **prepare, once per run** — everything every job consumes identically is staged here, never
   per module: the platform image as a per-digest zstd tarball in the **actions cache (GitHub's
   blob storage, colocated with the runners)**, the tester-app and platform-refs extractions,
   the module-pack tool. On a warm digest the run touches **no registry at all** — measured
   live: `platform image: cache HIT — docker load, no ACR`. A cache miss falls back to pulling
   the same digest-pinned bytes, loudly: a perf fallback, never a verification fallback.
3. **build — one Roslyn workspace.** The builder compiles the selected modules **plus their
   in-repo dependencies as one graph**: every project shares the same
   `PortableExecutableReference` per path (each assembly read from the filesystem once), one
   body pass like csc (parse+declaration diagnostics up front, body diagnostics from the single
   `Emit`), **fail-fast** (the first red blocks every not-yet-started node by name — a sweep
   that must enumerate every verdict opts out of fail-fast instead).
4. **pack fans out from the build's outputs** — it consumes, it never recompiles.
5. **tests — beside the chain, or inline, and `publish` decides which.** See below.
6. **verify** — unconditionally pairs the selection against the receipts; a skipped pack with a
   non-zero selection is RED, and so is a delegated suite that produced no test receipt.

### Where a module's own suite runs — and why `publish` decides

A `needs:` on a `uses:` job waits for the **whole** called workflow, so anything inside the last
job of this lane sits on the critical path of every gate the caller hangs off it. Measured on
MeshWeaver.Plugins run 33656010754, a 39-minute pull request:

```
19.1 -> 32.5  Module bundles (floor) / Module bundle (MeshWeaver.AI)   <-- 13.4 min
                12.7 min  Run the module's tests, when it ships any
                 0.2 min  Build the module — and say which compiler produced it
32.5 -> 32.7  Module bundles (floor) / All selected bundles built
32.8 -> 37.0  Compile every NodeType (vs core)          (a REQUIRED context)
32.9 -> 38.6  test-repos / Compile + render node repos  (the Tests-area gate)
```

The bundle **artifact** — the only thing those two gates consume — was ready 0.2 minutes in. They
waited another 12.7 for a suite they never read. The floor call exists precisely so the gates do
not wait for the other 30 bundles (Plugins#892); the suite put the wait straight back. It also
lengthens the **recovery loop**: a run cannot be re-run until it completes, so a flaked shard's
retry waits out a suite whose verdict nobody is reading — every flake pays that.

So the suite's position is a function of `publish`, the input that already means *trunk* in this
lane's caller contract:

| `publish` | where the suite runs | what the position buys |
|---|---|---|
| `true` | **inline in `pack`**, after the bundle upload, before the hand-over | *a failing suite never publishes* — the registry serves what every installation reads |
| `false` | the **`tests` job**, in parallel with select → prepare → build → pack | nothing is publishable on such a run (the hand-over step's own `if:` is `inputs.publish && …`), so the ordering protected nothing and delayed everything |

It is a switch on the **input**, never on `github.event_name`: the platform's own `main-cd` calls
this lane with `publish: false` for the bake's compose set, and that call is not a pull request —
it wants the fast path for exactly the same reason.

**Moving the suite off the critical path does not move it out of the gate**, and three structural
things say so rather than a comment:

* the `tests` job is part of the called workflow, so a red suite fails the caller's `uses:` job
  exactly as a red inline suite did;
* `verify` — `All selected bundles built`, this lane's one stable context and the one a repo's
  branch protection requires — `needs:` the tests lane and goes RED when it did not succeed. **No
  context is renamed**, so no repo's protection changes;
* a suite that ran in **neither** lane is caught by receipts, not by re-reading two conditions.
  Two mutually exclusive `if:`s can both be false, and a suite that ran nowhere renders exactly
  like one that passed. So every pack receipt records which lane owns its suite
  (`tests: none | inline | lane`), the tests lane drops a receipt of its own, and
  `node-repo-pack-verify.py` fails when a delegated module has no test receipt, when a test
  receipt arrives for a module that delegated nothing, or when the tests lane skipped while
  modules delegated to it.

`bundles-built` is deliberately untouched by all of this: it still answers from the built markers
dropped *before* any suite, so a red suite still does not read as "bundle missing" to a gate that
only COMPOSES the bundle (#2710, Plugins#937). Those are two different questions.

## Every stage asserts its OWN postcondition — never the next one's

A stage's exit code answers *"did my work throw?"*. It does not answer *"did I produce what the
next stage requires?"*, and the two were read as one claim until it cost a day.

**The build is where this bites.** `build` reported success and the pack matrix then died, seven
jobs at once, with `the global workspace build produced no MeshWeaver.AI.OpenAI.dll` — on a pull
request whose entire diff was one XML doc comment. The message was accurate and one job too late:
the stage that knew the selection was the green one, and the stage that discovered the gap knew
only its own module. Two enumerators — the projects handed to the build and the modules the matrix
expands — were free to disagree, and nothing compared them.

So the build now checks, before it uploads anything, exactly what its consumers demand, **per
selected entry**:

| the consumer demands | the producer now asserts |
|---|---|
| `<Module>/<Module>.dll` | present and non-empty (a truncated emit reads as "the file is there") |
| `<Module>.closure.txt` | present — without it a pack job cannot know which in-tree siblings ride *this* bundle, and a glob would ride every other module's |
| `platform AssemblyVersion` in the build log | readable — it is the expected side of the binding-identity check every pack job runs |

Three properties make it a postcondition rather than a second opinion:

* **The same enumerator on both sides.** It is fed the selection the matrix expands, not a
  re-derivation and not a glob of the output directory. A postcondition derived from a *different*
  enumerator is the original defect, one level up.
* **A glob would pass the case it exists to catch.** The workspace legitimately holds every
  entry's assemblies side by side, so "there are DLLs in there" is true in exactly the failure
  being diagnosed. Only a per-selected-module check can see it.
* **No `if:` on it, and its self-test runs first.** The selection-with-no-container-entries case
  is handled *inside* the check (it asserts nothing and says so) rather than by a condition that
  would render as a grey tick indistinguishable from a pass. An unproven postcondition is the
  thing this section is about.

It deliberately never asks the reverse question: the workspace carries in-tree dependencies of
selected modules beside them, so "emitted but not selected" is normal and is not a finding.

**The generalisation, for any stage you add to this lane:** name the artifacts the next stage
opens, and assert them where they are produced. One accurate failure upstream is worth N accurate
failures downstream, and a downstream failure can only ever describe its own shard.

## A bundle states what it was built against — or it is not published (#3211)

A module's published version encodes its **content only**. Rebuild unchanged source against a new
platform and the hash is the same, the version is the same, and the bundle overwrites its own
registry slot with different bytes. So "already landed" can only mean *this content against this
framework*, which is why `ModuleUpdateDecision.Decide` compares **(version, framework identity)**
before answering `SkipUpToDate` (#3154, `Doc/Architecture/Modules`).

That comparison needs something to compare, and the producing end was not supplying it.

**Measured, MeshWeaver.Plugins run 33773265959 (2026-09-03):** every one of the 34 bundles packed

```
warning: MeshWeaver.Compiler.dll not found at …/MeshWeaver.AI.OpenAI/MeshWeaver.Compiler.dll
packed MeshWeaver.Plugin.OpenAI.1.1.11.module.nupkg — … built-against MVID (unrecorded)
```

on **both** compilers. The packer probes for the identity anchor *beside the module*, and where the
anchor is **moves with the platform**:

| how the platform arrives | where `MeshWeaver.Compiler.dll` is |
|---|---|
| pinned **image** (every satellite call) — `platform-refs`, the `docker cp` of its `/app`, passed to the sdk build as `MeshWeaverRefs` and compiled inside by the container build | in `platform-refs`; deliberately **not** in the module's output, which carries no platform assembly |
| built from **source** (core's own `main-cd` call, no image pinned) | beside the module — the platform ProjectReferences are real, so `dotnet publish` copies it |

Only the second case ever matched the default probe, which is why core CD records an identity
(measured: `built-against MVID ce81fd4e…`, run 33779812466) and every satellite records none. The
field was optional, so nothing was red; #3154 shipped into a fleet where every consumer of every
module was permanently in the "up to date — the identity could not be checked" branch.

The asymmetry is why this had to be fixed at the producer. An unknown on the **landed** side heals
after one fetch, because landing writes the identity back. An unknown on the **served** side never
heals: a registry that states no identity will state none next time either.

**Three refusals, all naming what is missing:**

| where | what it refuses |
|---|---|
| `module-pack` (`ModulePackCommand`) | exit 2 when neither `--framework-mvid` nor `--graph-dll` yields an identity — the bundle is not written at all |
| pack step | names the anchor explicitly (`--graph-dll`), picking the location from the table above — and **both** arms are RED when the anchor is not there. The branch decides *where to look*, never *whether to check*. The manifest is then read back and the field asserted on the bytes |
| **publish step** | RED before the POST when the bundle's manifest states none — on the bytes about to be handed over, so the **reuse** leg (an artifact an earlier run packed) is covered too |

**Which identity string lands in the manifest — and why it need not be the portal's.** The packer
reads the anchor with `FrameworkIdentity.ReadIdentity`, which sees only what is IN the assembly: the
stamped `MeshWeaverFrameworkIdentity` (`g<sha>` on every CI build) or, failing that, its MVID. It
cannot see a **surface manifest**, so it never answers the `s<hash>` a portal resolves for itself.
That is fine, because nothing compares a module bundle's value to a live process: `LandFromBundle`
only *records* it, and `ModuleUpdateDecision` compares **served against landed** — both the
producer's own string, so they converge after one landing whatever the flavour. It is also the
finer discriminator of the two: `s<hash>` is breaking-change-keyed and holds still across many
platform commits, while `g<sha>` moves with every one — and "the platform this module was compiled
against moved" is exactly the question being asked. (The `DeclineReason` identity gate lives on the
*bake* bundle path, `BundleReader.Read`/`SeedAll`, and never sees this value.)

The publish-step refusal is the load-bearing one: the inspection runs on the build leg only, and a
guard bound to it alone would pass while pre-#3211 bytes went to the registry. `RECIPE_VERSION`
moved `1` → `2` in `module-build-key.py` for the same reason — the lane now packs different bytes
for the same source, so every recorded key is invalidated and no run reuses a bundle whose publish
would be refused.

**Still to arm: the registry's own 400.** `ModulePublish.Validate` accepts a manifest stating no
identity, and `ShelveModule` shelves the null the index then advertises. Arming that refusal before
every producer's `node-repo-module-pack.yml` pin has moved would take the fleet's publishes down
rather than the nulls — the pin is bumped deliberately, per repo, in its own PR. Until then the
publish endpoint logs a warning naming the package, the module and the version, which is the
measurement the arming step reads. The criterion and the two repos it waits on are #3240.

## Content-addressed outputs = the module build ledger (as built, 2026-09-02)

*"For Plugins, merge as quickly as possible, as tolerant as possible; only hard conflicts flagged. We
should not start the same build multiple times ⇒ coordinate which packages are in progress; track
progress through memex. Build and test only when we have to: if Plugin X was built against Platform
version Y, we don't have to rebuild this."* (maintainer, 2026-09-02; Plugins#889, #931)

Every module build has a **content address**, and the fleet keeps a **ledger** of what happened at
each address — on the registry portal, as mesh nodes. The lane consults it before it compiles, and
writes it at every transition. This section is the contract; the two scripts that implement it are
`.github/scripts/module-build-key.py` and `.github/scripts/module-build-ledger.py`, both self-tested
on every lane run, and `ModuleBuildLedgerLaneGuard` pins the lane's wiring.

### The key — what a build is a function of

```
K = sha256( canonical JSON of {
      recipe          the lane's build-recipe version (a constant in module-build-key.py; bumped on a
                      byte-changing lane edit, never on a cosmetic one)
      package, module the matrix entry
      entry           {build: sdk|container, accept: <sorted tokens>}
      moduleVersion   <package>/manifest.lock → moduleVersion (the package's content hash, Plugins#878)
      closure         {project dir → tree hash} — the entry project, its sibling <dir>.Test (the lane RUNS
                      it) and every in-repo ProjectReference either reaches, transitively: module-owned
                      MeshWeaver.* siblings RIDE the bundle, so their bytes are the bundle's bytes
      packages        {package → moduleVersion} for every package whose module project is in that
                      closure, and every package the entry `requires`, transitively
      globals         {path → sha256 | null} for src/Directory.Build.props, src/Directory.Build.targets,
                      src/platform-shipped.txt, Directory.Packages.props, global.json, nuget.config …
                      (null records ABSENCE — an input too)
      testerDigest, platformDigest, platformRef
    } )
```

Design decisions, and why:

* **Tree hashes over `moduleVersion` alone.** The version derivation covers the module's own project
  (and, since Plugins#1118, its riders); the key hashes the whole compiled+tested closure directly,
  so it cannot inherit a blind spot in `gen-manifests.py` — `MeshWeaver.Blazor` riding in seven
  bundles unhashed (see [ModuleVersioning](../ModuleVersioning)) is exactly the shape the key must
  never have. A `$(MeshWeaverRoot)`-relative reference is the platform's and never an edge: the
  platform enters through the two digests and `platformRef`.
* **`platformRef` IS in the key, deliberately.** `dotnet test` builds core FROM SOURCE at that ref,
  so the verdict really depends on it. This is also the lever a satellite pulls: a caller whose
  `MW_PLATFORM_REF` tracks `main` gets a new key on every core commit; a caller that pins the ref to
  the promoted set's commit keys identical trees identically across runs. The lane cannot make that
  choice for the caller, and must not hide it.
* **Refusals, not guesses.** A package with no `manifest.lock`, or an entry whose project does not
  exist, cannot be keyed and fails the selection RED — keying on nothing would collide every build.

### The ledger — one node per key

`Admin/ModuleBuilds/<K>`, nodeType `ModuleBuild`, content `ModuleBuildRecord`
(`src/MeshWeaver.Graph.Contract/ModuleBuildRecord.cs`; the type ships in the framework, like `Build`,
so every registry portal has it without a content package): package, module, moduleVersion, version,
platformRef, both digests, **platformIdentity**, status ∈ {Claimed, Built, Tested, Published, Failed},
phase, blocking, attempts, run {repo, runId, attempt, url, event, lane}, claimedAt, heartbeatAt,
finishedAt, bundleSha256, bundleArtifact {repo, runId, name, expiresAt}, tests {passed, failed,
names[]}, failure, previous.

* **Why `Admin/ModuleBuilds` and not `Admin/Build/…`.** `Admin/Build` is the in-portal NodeType
  bake's coordination root ([BuildCoordination](../BuildCoordination)) — its hub arbitrates claims over
  its children with cluster-membership takeover. The CI ledger is a different protocol with a
  different writer, and must not sit where that arbiter enumerates chunks. It stays in the Admin
  partition because the subject of a build decision must not be able to write the decision for
  everyone; the CI user gets a **partition-admin grant scoped to exactly that root**
  (`Admin/ModuleBuilds/_Access/{user}_Access`, `MainNode = "Admin/ModuleBuilds"`) — never a
  global-admin one ([AccessControl](../AccessControl) → "The Admin partition").
* **The wire.** The lane speaks MCP JSON-RPC over HTTP to the portal's `/mcp` (`initialize`, then
  `tools/call` `get` / `create` / `patch`; JSON or SSE answers), `Authorization: Bearer <mw_ token>`
  — the same three tools every MCP client uses, no bespoke route.
* **`platformIdentity` is the PORTAL's.** `prepare` runs the tester's `framework-identity /app` verb
  with the platform image's own `dotnet` over the platform image's `/app` — the `s…` surface identity
  the adopting host resolves (#3022), not the tester image's own and not the `g…` provenance stamp of
  one assembly. That is #931's producer half: the record says which framework these bytes are for.
  The consumer half landed beside it: the registry advertises a module bundle's identity **per
  bundle** on `/api/plugins/bundles/index.json`, and `ModuleUpdateDecision` skips as up to date
  only when the version AND that identity match what is landed — so a rebuild of unchanged
  source against a new platform, which republishes under the same version, is no longer
  invisible to the updater ([Modules](../Modules) → "Already landed means this content against
  this FRAMEWORK").

### The protocol

| step | what it is |
|---|---|
| **claim** | CREATE the node. Creation fails on an existing path, so exactly one run holds a key; *"already exists" is the follower's success case*. After every create the claimant re-reads the node and holds the key only if the record names ITS run — a claim you cannot read back is a claim you do not hold. |
| **heartbeat** | the holder's sign of life: at claim, at pack-job start, after the workspace build, at every transition. A claim whose heartbeat is older than **45 min — the fleet's job cap** — is dead by construction (a job that cannot heartbeat inside its own cap has been killed) and may be taken over; the takeover is itself re-read for the same reason as the claim. |
| **reuse** | a terminal record (Built/Tested/Published) whose bundle **artifact** this run can fetch is not rebuilt. The pack job downloads that run's `module-bundle-<module>` artifact, verifies its sha256 against the record, and runs ONLY the phases the record lacks — tests if this run needs a verdict and none is recorded, publish if this run publishes and nobody has. It drops the same artifact, built marker and receipt a built leg drops, so a caller composing the bundle cannot tell the two apart. |
| **wait** | a fresh, unfinished claim by ANOTHER run: the follower polls the ledger every 30 s (bounded to 40 of `select`'s 45 min). The same key is never built twice at once. |
| **tolerance** | a `Failed` record blocks a later run of the same key only when the same inputs give the same result: a **compile** failure blocks (RED with the holder's run URL and the compiler lines); a **test** failure blocks from the **second** failed attempt on — one re-claim, so a flaky suite does not pin the fleet (`attempts` counts); pack, publish, workspace-abort and cancellation never block. |
| **degrade** | the registry portal answering 5xx or unreachable is **not** a verdict: after a bounded retry honouring `Retry-After`, `select` builds every affected module *without coordination* and says so in yellow in the job summary; every later write is a `::warning`. The ledger may cost a duplicate build, never a green (core #3119). |

**Why run ARTIFACTS, not the actions cache.** The first design keyed bundles in the actions cache
(`module-bundle-<K>`). The cache is **branch-scoped**: a run on `main` can restore only what `main`
created, so the one flow that matters most — the PR built it, the push to `main` reuses it — is
structurally impossible there. Run artifacts are repo-wide; the bundle artifact's retention is 7 days
(the reuse window; the record carries the expiry) and the caller's job needs
`permissions: actions: read` for `gh run download` — without it `select` observes the 403 and answers
*build*, loudly. A bundle in ANOTHER repository's run is never fetchable with this run's token, so
cross-repo keys (the platform CD packing Plugins) are rebuilt rather than reused.

### What this does to the scope

`node-repo-scope.py` still decides what a diff REACHES (a PR narrows; `push`, release-follow and
manual dispatch are FULL). The ledger decides what of that must be COMPILED. So the `push` row is no
longer "full by fiat": it is *every module whose key has no usable Published record* — which is
exactly the baseline Plugins#889 asked for, derived correctly: a cancelled, red or superseded run left
no Published record, so its modules are rebuilt and published; a PR that built and tested the same
bytes minutes earlier is reused and only the hand-over runs. Release-follow (`repository_dispatch` /
`schedule`) still builds everything, correctly: a new platform digest is a new key for every module.
The `repository_dispatch` is memex's event — `meshweaver-framework-released`, emitted by the
registry's `PlatformBuildInboxWatcher` from the build fact core CD POSTs into `Hosting/PlatformBuilds`,
to the repositories the `Hosting/Deployment` records name as registry sources; `schedule` is the
fallback. Core dispatches to no repository (maintainer, 2026-09-03: *"core publishes an event and
finishes"*).

### What a satellite passes

```yaml
    permissions:
      contents: read
      actions: read          # gh run download of an earlier run's bundle — without it, no reuse
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@<sha>
    with:
      ledger: required       # default `off` = today's behaviour; the summary says which on every run
      …
    secrets:
      publish-token: ${{ secrets.REGISTRY_PUBLISH_TOKEN }}
      ledger-token:  ${{ secrets.REGISTRY_LEDGER_TOKEN }}   # a mw_ ApiToken of the registry's CI user
```

One-time on the registry portal (as a global admin): create the `Admin/ModuleBuilds` root node,
grant the CI user the `Admin` role there (`MainNode = "Admin/ModuleBuilds"`), mint that user an API
token, store it as the satellite's `REGISTRY_LEDGER_TOKEN`. `ledger: required` with an empty token
is RED in `select` — a ledger that silently did not run and one that ran must never look alike.

## The compiler is the platform image

A module never runs against a source tree or a NuGet feed: it is loaded into the platform
IMAGE and bound by the assemblies in there. So the honest compiler is that image's own —
`mw-plugin-test build-project` runs INSIDE the pinned platform container; its `/app` is the
reference set, its runtime is the shared-framework surface. No SDK, no restore, no platform
source checkout. A declared mode that cannot run FAILS — there is deliberately no SDK
fallback, because a fallback makes "the container built it" and "the SDK built it"
indistinguishable in a green log.

Additional libraries resolve from the curated **module-libraries shelf**, and a
`PackageReference`'s compile surface is its **transitive closure** from the shelf's deps.json —
exactly what the SDK hands a consumer (`PackageReference Microsoft.Graph` lets code
`using Microsoft.Kiota…`; anything less re-creates that gap as a CS0234 wall).

🚨 **What a module COMPILES against and what its bundle CARRIES are two questions with two
answers.** For the compile the image is authoritative — a module binds the assemblies of the host
it is loaded into. For the bundle it is not: the image is a PORTAL, and a portal with a module
compiled into it also carries that module's private package dependencies, so "`/app` has the file"
is a fact about one host, never a platform guarantee. A bundle carries its own package closure
minus the SHARED FRAMEWORK, exactly as the SDK lane's `--deps-closure` does — see
[ModuleClosureAccounting](../ModuleClosureAccounting) for the rule and the two outages that came
from conflating the two questions.

## Gates compile against IMPLEMENTATION frameworks

Every compile-check extracts the image's `/usr/share/dotnet/shared` beside `/app` and, seeing
`System.Private.CoreLib.dll`, drops the SDK ref pack entirely: the check compiles exactly what
the mesh compiles. This is not a preference — a container-built module references
`System.Private.CoreLib`'s identity directly, which the ref pack cannot resolve (11 NodeTypes
false-red with CS0012 the moment the floor bundles were container-built) and which CS0433-s
beside it. Measured: 80/80 NodeTypes in 44s vs 161s + 11 false regressions.

## The NodeType bake and its gate run AS the platform image too (#3022)

The rule above — *the platform image is the compiler and the reference set* — applied to module
compiles since #2907 and did **not** apply to the NodeType bake until 2026-09-02. The node-repo
bake (`node-repo-publish-bake.yml`) and the node-repo gate (`node-repo-gate.yml`) ran the tester
image against **the tester's own `/app`**: reference set, framework identity and the environment
the per-type dependency records were computed against all came from the process the bake happened
to run in, while the bundles it produced are adopted by the **portal**. Measured on the two images
of one promoted set, `3.0.0-rc9.ci.7534`, both `linux/amd64`:

| | `mw-plugin-test` (baked) | `memex-portal-ai` (adopts) |
|---|---|---|
| assemblies in `/app` | 88 | 219 |
| `MeshWeaver.*` assemblies | 26 | 46 |
| `MeshWeaver.*` only in this image | 1 (`Hosting.Monolith`) | **21** — `Maps`, `AI`, `Markdown.Collaboration`, `ContentCollections.Indexing[.Graph]`, `Blazor[.Portal,.Views]`, `Hosting.{AspNetCore,Blazor,Orleans,PostgreSql,SignalR,Grpc,Embeddings}`, `Connection.Orleans`, `InstanceSync`, `Documentation`, `{Speech,Observability,Markdown.Export}.Contract` |
| the 25 assemblies both carry | byte-identical (25/25) — one build |
| surface-manifest lines | 26 | 46; the 25 shared names carry identical hashes |
| framework identity | `s8fe4902c0b2f5974f824be2867221dbd` | the same |

So the identity gate (#1814/#3041) was green — both hosts record the canonical set identically —
while the bake could not see 21 assemblies every portal compiles against. Five NodeType sources in
MeshWeaver.Plugins bind `MeshWeaver.Maps` (`Cornerstone/Pricing`, the AppleMaps, GoogleMaps and
OpenStreetMap galleries); the day Maps left the tester's closure (#2941) all four went RED in the
platform's `plugins-bake` with `CS0234 'Maps' does not exist in the namespace 'MeshWeaver'`, no
seal was written, no dependent was woken, and no portal could adopt any release since — with every
line of the verdict naming the CONTENT.

**The shape now — the same as the module lane, deliberately:**

* Both lanes take **`platform-image` + `platform-image-digest`** (required; resolved branch for
  branch as the tester's `image-digest`, so a framework release, an upstream's publication and a
  push can never pair two waves). The tester **executes**; the portal **supplies**.
* The lane pulls both images, extracts both `/app` trees, and asserts with the tester's own
  `framework-identity /portal --expect <tester identity>` that the two **resolve one identity** —
  they are one build — before it trusts anything else. On a mismatch the verb names the canonical
  assemblies each side lacks.
* It composes the **gate host** (`.github/scripts/compose-gate-host.sh`): the portal's `/app`,
  complete, with the tester CLI laid beside it. The portal's bytes win a shared file; the tester's
  `meshweaver-surface.manifest` never rides (the host's identity is the portal's) and the tester's
  `mw-plugin-test.deps.json` never rides (with an app-local deps.json the dotnet host builds the TPA
  from that file's entries only, and every portal-only assembly would be on disk yet unloadable —
  without it the host probes the directory and every assembly is in the TPA).
* `compile` runs from that host, started by the **portal image's own `dotnet`**, with
  **`--app /app --shared-frameworks /usr/share/dotnet/shared`**: the reference set is the portal's
  `/app` plus its *implementation* frameworks (the ASP.NET Core framework included — a console
  runtime does not have it), never the process's TPA; `framework-mvid.txt` carries the identity
  **the portal's directory resolves**; every dependency record is computed against the portal's
  manifest pairs and MVIDs. The same seam (`BakeHost`) serves the `build` verb.
* 🚨 **One invariant makes recording the portal's identity honest: the toolchain that ran must be
  the portal's own bytes.** The identity folds the implementation MVIDs of the toolchain closure
  (`MeshWeaver.Compiler`, `MeshWeaver.NuGet` and their MeshWeaver dependencies) in because their
  *code* shapes the compile input, and that code executes from the tester's copy. `BakeHost`
  compares the closure member by member against the portal's files and **refuses** a mismatch
  naming both MVIDs — a bake keyed to a host whose toolchain it did not run is never written.
* The gate runs from the same host with `--app /app`, which is a **precondition**, not a
  reference set: the process must resolve the portal's identity (`GateHostCheck`) or the run is
  refused before a mesh boots. A gate running as any other host would not fail — it would decline
  every bundle the bake addressed to the portal, compile the tree itself and exit green having
  judged none of the bytes that ship.
* A reference-set gap is **named in the verdict**. When a NodeType fails with CS0234/CS0246, the
  bake indexes the reference set and the host's `modules/**` (assemblies the image ships outside
  `/app` — not in any reference set unless composed) and appends `reference set lacks <assembly>
  (portal-shipped, not composed: modules/…)` or `no assembly in the reference set declares
  namespace '…'` with its two possible causes. A namespace some reference does declare adds
  nothing — that is a content error, and the compiler's line is the whole truth.

**What this does to the identity gate.** For the node-repo lanes the bake's identity is now the
portal's *by construction*, so "the bake's identity is the one the portal resolves" is an invariant
the lane asserts after the compile (cheap, kept so the day a lane edit drops `--app` is the day it
goes red) rather than a comparison that can lose; the check that *can* lose moved in front of the
composition and asks the honest question — **are the two images one build?** The platform's own
Doc bake (`main-cd.yml` `publish-bake`) still bakes inside the tester image and keeps the original
comparison against the promoted portal, where it is not tautological. `check-image-build-identity.sh`
is a different check (`MESHWEAVER_PLATFORM_VERSION` in the image config) and is unaffected.

**Cost.** One more image pull and `/app` extraction per bake or gate job (~1 min on a runner; the
portal's `/app` is ~300 MB) and a larger reference set for Roslyn to map lazily. Unchanged: what is
compiled, how the publication is sealed, every caller's other inputs.

## CI is silent — warn/error plus verdicts

Per-item narration (resource names, per-package resolutions) sits behind `--verbose`; the
default log carries verdicts (start/OK/FAIL, phase timings, the compiler declaration, test
names), warnings, and errors. Docker pulls are `-q`. In-run artifacts expire in 1–3 days.
No component ever publishes from the mesh router — the router names a spokesman
(`RouterCarrier` → the nodeops execution hub) and infrastructure speaks through it.

## Measured (2026-09-01, the night the shape landed)

| | before | after |
|---|---|---|
| MeshWeaver.AI compile (builder) | 161s local / 559s CI (double pass) | **80.6s local** (single pass); ~97% of the remainder is nullable flow analysis in a few very large methods — source-side work |
| NodeType gate | 161s + 11 false CS0012 reds | **80/80 in 44s** (implementation frameworks) |
| Registry trips per warm run | 2 pulls × N jobs (the `connection refused` stampede) | **0** |
| Module pack tool | 48–79s `dotnet build` × N jobs | ~5s download, built once |
| Orleans test suite | ~90 silo boots + disposal drain | **3 clusters** (the mesh pool; see WritingTests § The Mesh Pool) |

## One producer per (module, framework identity) — and the set is what is gated (#3175)

**A module's bytes have exactly one producer for a given framework identity, and every dependent in
a sealed publication was built against THAT build.** This is not a style preference; it is the
invariant the adoption contract already assumes, and on 2026-09-03 it was broken twice in one
morning, with no diff in any repo:

| where | what the record said | what the consumer held | outcome |
|---|---|---|---|
| every satellite gate (Reinsurance 33727661313, Manufacturing 33727661850) | `'MeshWeaver.Maps' built against mvid:4d04617…` | `live is ref:1D8FDE5B…` | 4 of 240 DECLINED, `GATE FAILED`, nothing sealed, memex-cloud `HOLDING` |
| memex.meshweaver.cloud on ci.7621 | `'MeshWeaver.Markdown.Collaboration' built against mvid:A` | `live is mvid:B` | SocialMedia adopted 0/4, `/Posts` rendered empty (#3174) |

The first row is a **second producer in space**: a portal host had taken a direct project reference
to `MeshWeaver.Maps`, a Store module. The bake composed Maps with `--module` — the id resolver puts
modules first, so every record said `mvid:` — while every portal and every gate host carried the
app-closure copy and resolved the name from its surface manifest as `ref:`. Two schemes, never
equal, every map gallery declined. The maintainer's ruling closes it structurally: *"all maps should
be in plugins and removed from core — move 100% to plugins."* The portal host references no module
project; a module is landed from the registry and composed into bakes, nowhere else.

The second row is a **second producer in time**: core CD's `plugins-modules` rebuilds
`MeshWeaver.Markdown.Collaboration` per platform release to feed the Plugins seal (mvid A), while
the registry's package endpoint serves the Plugins lane's last content-versioned publication (mvid
B) — the bytes a portal actually installs. This row is NOT closed by this change (core holds no
registry credential, so its bake cannot compose the registry's bytes); what changed is that the
availability gate now sees it (below) instead of rolling onto it.

### The two controls

1. **The bake refuses a double by name** (`BakeHost.ShippedByHostProblem`, run by `compile` /
   `--bake-output` under both `--app` and in-process). A module composed with `--module` whose
   simple name the host also ships in its application directory — or lists in its surface manifest —
   fails the bake RED: *"two builds of one assembly name in one bake … remove the assembly from the
   platform host's closure or stop composing it."* Caught where both provenances are in one hand,
   never sealed and discovered at the fleet. Pinned by
   `BakeAgainstPlatformHostTest.AComposedModuleTheHostAlsoShips_IsRefusedByName`.
2. **Availability asserts the SET** (`ReleaseAvailability.IsUpdatable` over the observation
   `PublishedBundleCatalogue.ArtifactsForIdentity` makes). For the candidate identity the registry
   reads every complete source's sealed module set — each module bundle's entry assembly, PE header
   only, for its MVID — and every sealed bundle's per-NodeType dependency record (manifest only,
   never assembly bytes). Then: every `mvid:` entry naming a module the set carries must equal the
   MVID sealed for it, and no module may be sealed at two MVIDs by two sources. A violation is
   `PackageAvailabilityKind.SealedSetInconsistent` — a HOLD that names the bundle, the NodeType, the
   module and both builds — and it is consumed unchanged by the self-update poll, CD's post-promote
   assertion and `/api/plugins/is-updatable`, because all three call the same predicate. Both
   failure directions are pinned (`SealedSetConsistencyTest`): an unreadable or pre-module-sealing
   module set is `Indeterminate` (hold, named — never "compatible"), and a module the set does not
   carry is not judged, because the gate cannot see the bytes a registry install landed.

### What the gate still cannot see

The registry's package endpoint (`/api/plugins/bundles/<pkg>/<version>`) is content-versioned
(Plugins #931): an instance that installed a module from it holds whatever that lane published last,
and a platform release that rebuilds the module for the seal produces a *consistent* sealed set whose
module MVID differs from the instance's landed one. The set check passes; the instance still
declines. Closing that needs ONE producer in time as well — either the seal composes the registry's
bytes for the root source (`registry-modules` in core CD, which needs a registry credential core
does not hold today), or the instance adopts module bytes for its identity from the sealed
publication it already adopts bundles from. Until one of those lands, `ShippedPrebuiltBundles`'
`dependency record mismatch … live is mvid:` line on a portal is that gap, not a new defect.

### Moving a type OUT of an image-shipped assembly into a module (the shadow set's blind spot)

The reference set for a container build is **the whole image's `/app`, minus
`Graph.ShadowedAssemblyNames`** — and that set holds only the assemblies *this run compiles from
source*. Membership is **reachability-driven**: a project joins the graph through a
`ProjectReference` edge under the source root.

That is exactly right until a type MOVES OUT of an image-shipped assembly into a module **in the same
repository**. Then:

- the module's source defines the type;
- the pinned image still ships the assembly that *also* defines it, because the image is one wave
  behind;
- and **the `ProjectReference` edge that would have put the old assembly in the graph — and therefore
  in the shadow set — is precisely the edge the move deletes.**

So the shadow set structurally cannot see the one case that needs it, and the compile fails
`CS0436: … conflicts with the imported type … in '<OldAssembly>'`. It is the duplicate-producer
problem of [One producer per (module, framework identity)](#one-producer-per-module-framework-identity--and-the-set-is-what-is-gated-3175)
one level down: two definitions of one TYPE rather than two producers of one ASSEMBLY, for exactly
one wave. Measured on MeshWeaver.Plugins#1268, moving three Razor views out of
`MeshWeaver.Blazor.Views` into the `MeshWeaver.Markdown.Collaboration` module.

**The remedy is a declaration, not an inference.** The pack lane takes
`superseded-image-assemblies:` (comma- or whitespace-separated), passed to `build-project` as
`--superseded-image-assembly <name>`; each name is added to the shadow set, so the image's stale copy
leaves the reference set for that run.

Three properties are load-bearing:

- **Declared, never inferred.** Resolving such a collision automatically in favour of the source
  would mask a genuine two-producers-of-one-assembly defect — the thing
  `BakeHost.ShippedByHostProblem` exists to make RED. The caller names the assembly.
- **Fails closed.** An empty name, a name the image does not carry (wrong — or the entry is STALE
  now the image dropped it), or a name the run already builds from source, each refuse the build and
  say which entry. An input that quietly does nothing reads exactly like an input that worked.
- **No "is anything else still needed from it?" check.** The compiler already answers that with
  `CS0246`/`CS0234` naming the missing type, which is a positive, specific signal. A second check
  would only be able to agree.

⏳ **Known gap:** an entry goes stale once a rebuilt image no longer defines the conflicting types —
the assembly still exists, so nothing detects it. The durable form is a type-name overlap assertion
(refuse when the image's copy defines none of the names this run's source defines) — tracked in
[#3223](https://github.com/Systemorph/MeshWeaver/issues/3223). Until it lands, remove the entry in the same change that moves the pin onto an image
built after the move.

## Adoption contract (every repo)

1. Pin the reusable lanes (`node-repo-*.yml`) at a MeshWeaver main SHA — never copy them.
2. **Scripts are centralized**: the lane fetches the platform's `.github/scripts/compile-check.py`
   at the pin and runs it against the caller's tree — a repo keeps ONLY its
   `scripts/compile-check.allow` (policy). Per-repo script copies are retired; three had
   already drifted apart when this landed. (*"We can ship in hosting"* — the endgame is a
   `compile-check` verb inside the tester image itself, where the reference set is the
   container's by construction; the fetched-script stage is the unified interim.)
3. Reference this page from the repo's AGENTS.md — the build section defers here.
4. Repo-specific policy (module lists, always-modules, allow-files, registry consumption)
   stays in the caller; mechanics never do.

## The release: the pipeline ends by calling memex

**The contract (maintainer, 2026-09-03: *"end of github pipeline must call memex, which must
register release and publish event"*) is three sentences:**

1. **Every publishing pipeline ENDS with one call to memex.** Core's CD, after the image set is
   promoted, POSTs the signed platform build (`event: platform-build`) into the control instance's
   `Hosting/PlatformBuilds` inbox (`notify-platform-update`). Every node repository's
   `node-repo-publish-bake.yml` run, after its bundles are sealed for an identity, POSTs the signed
   publication record (`event: bundle-publication` — source, identity, commit, tester + portal image)
   into the same inbox (`register-publication`, its last job). Nothing runs after that call, and no
   pipeline sends a `repository_dispatch` to another repository.
2. **memex REGISTERS the release** as a durable node — `Hosting/PlatformBuilds/<version>` for a
   platform build, `Hosting/Publications/<identity>/<source>` for a bundle publication — the source
   of truth for "what is published for which identity" (what the self-update availability check reads).
3. **memex PUBLISHES the event** from that registration: `FrameworkReleaseBroadcaster` sends
   `meshweaver-framework-released` (platform) or `meshweaver-upstream-published` (bundle publication,
   `client_payload.version` = the identity) to the subscribed repositories — the repositories the
   control instance's `Hosting/Deployment` records name as registry sources. The subscribers' CI
   receives it, resolves both images from the version, builds and publishes for that identity — and
   ends by calling memex (1).

```
 pipeline (core CD | a node repo's publish-bake)        memex (control instance)              subscriber CI
 ───────────────────────────────────────────────        ────────────────────────              ─────────────
 promote / seal ✅                                       WebhookInbox Hosting/PlatformBuilds
   └─ ONE signed POST ──(platform-build |──────────────▶│ verify HMAC
      bundle-publication)… and FINISH                    ├─ REGISTER  Hosting/PlatformBuilds/<version>
                                                         │            Hosting/Publications/<identity>/<source>
                                                         ├─ subscribers = Hosting/Deployment records'
                                                         │              pluginRepos[].isRegistrySource
                                                         └─ PUBLISH   repository_dispatch ─────────────▶ on: repository_dispatch:
                                                            meshweaver-framework-released |               types: [meshweaver-framework-released,
                                                            meshweaver-upstream-published                        meshweaver-upstream-published]
                                                                                                          → bake for the version → seal → POST memex
```

Where the pieces are: the POST steps in `main-cd.yml` and `node-repo-publish-bake.yml` (this repo);
the inbox watcher, registration and broadcast in the Hosting module's `PlatformBuildInboxWatcher`
(MeshWeaver.Plugins, `Hosting/Deployment/Source`); the broadcaster in `src/MeshWeaver.GitSync`.
`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository` refuses a dispatch SENDER in any workflow
under `.github/workflows` — there is no ledger — and
`UpstreamBuildGateGuard.TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody` pins the lane's call.

**In flight, in this order (the contract is complete only when all have landed):** MeshWeaver.Plugins#1241
wires the platform half (broadcast + system identity + subscribers from the records) and is
observed firing before core withdraws its dispatcher (MeshWeaver#3185, this change); a Plugins
follow-up makes the watcher REGISTER the nodes named in (2) and handle `event: bundle-publication`
(register + `meshweaver-upstream-published`, dependency-scoped through the registry's package
`requires` graph so a publication cannot wake its own upstream); each node repository passes
`webhook-url` / `webhook-secret` to the lane when it moves its pin (the lane is RED, naming them,
until it does — a sealed publication memex was not told about is silent drift). Once Plugins receives
the platform event and publishes its own bundles on it, core CD's `plugins-bake` job is a SECOND
producer of the same publication and is removed — a follow-up, not part of this change.

## Roadmap (agreed, in flight)

* **The one-workspace lane** — landed: one `build-workspace` job compiles the ledger's build subset
  as one graph; pack/tests fan out from it. The consumer half of #931 landed too —
  `ModuleUpdateDecision` compares the served bundle's framework identity, not the version alone.
  The producer half landed with #3211 — a bundle that cannot state what it was built against is
  refused at the packer, at the pack step and at the hand-over (see *A bundle states what it was
  built against — or it is not published*, above). Still open:
  arming the registry's own 400 in `ModulePublish.Validate`, which waits on every producer's lane
  pin having moved; and pinning satellites' `MW_PLATFORM_REF` to the promoted set so keys survive
  core commits.
* **`pack-module`, `compile-check` and test verbs inside the tester** — the last runner-side
  `dotnet`/python uses move into the image the fleet already ships.
* **Self-hosted runners with a mounted git mirror + warm image store** (MeshWeaver#2926): bare
  mirror volume, `git worktree add` per run at the release ref, worktree deleted at job end.

See also: [ModuleClosureAccounting](../ModuleClosureAccounting) ·
[ModuleVersioning](../ModuleVersioning) ·
[NodeTypeCompilation](../NodeTypeCompilation) · [PluginBuildContract](../PluginBuildContract) ·
[BuildProcess](../BuildProcess) · [InMeshBuildAndTest](../InMeshBuildAndTest).
