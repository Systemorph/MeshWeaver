---
Name: Compiled Against A Platform The Instance Does Not Run
Category: Architecture
Description: The source-level compile gate exists and it passes. It compiles a satellite's in-mesh Code nodes against the platform CI is about to ship, while the instance receiving that content is still running an older image — and content reaches a portal faster than a platform roll does. The measured window, why the diagnostic is easy to misread in both directions, and the baseline arm the same lane already implements for core's promote but not for a satellite's own PR.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h7l2 3h9"/><path d="M3 7v12h18V10"/><path d="M7 14h4"/><path d="M15 12l4 4m0-4l-4 4"/></svg>
---

# Compiled Against A Platform The Instance Does Not Run

On 2026-09-14, six NodeTypes on memex.meshweaver.cloud stopped compiling forty minutes after a
satellite merge. **Every gate was green, and correctly so.** This page is about the window they do
not cover, and about a compiler diagnostic that is easy to read in the wrong direction.

## What is and is not gated

It would be wrong to say in-mesh source is ungated — it is compiled in CI, exactly as the mesh
compiles it:

| gate | what it compiles / measures | against which platform |
|---|---|---|
| [The Module Platform Link Gate](../ModulePlatformLinkGate) | a module **assembly**'s linked types | **the platform actually running** — it refuses a generation the process cannot load |
| `node-repo-compile-check.yml` → `compile-check.py` (`compile-check / Compile every NodeType (vs core)`, a required context on most satellites) | every NodeType's **resolved Source**, concatenated with hoisted usings, the way the mesh does it | the **reference set of a resolved platform image** — for MeshWeaver.Plugins, `scripts/resolve-platform.py` picks *the newest SEALED set of core's main-cd* |
| `minMeshVersion` floor ([Module Adoption Policy](../ModuleAdoptionPolicy)) | a version string the author writes | advisory since 2026-09-07, and per module rather than per source node |

The second row is the one that matters here, and it is not a hole: it was introduced precisely so
that API-drifted Source could no longer merge green and park on a live mesh. **It ran on the commit
in question and it passed** — measured below.

The gap is narrower, and it is a gap of *target*, not of coverage:

> **No gate compares the delivered source against the platform image the RECEIVING INSTANCE is
> running.** The link gate does exactly that, for bytes. Its counterpart for source is not wired on
> the path the content actually travels.

## The measured window — 2026-09-14, memex.meshweaver.cloud

| when | what |
|---|---|
| **2026-09-12T09:30:22Z** | core commit `c84c6c055` — this becomes the image both portals run (rolled 12:34Z as `3.0.0-ci.8411`) |
| **2026-09-12T17:28:05Z** | core PR #4102 (`00fa2b69f9`) adds `PlanTierRefusal` (`src/MeshWeaver.Mesh.Contract/Security/PlanTierRefusal.cs`) and `RegistryPackageSource.ListCatalog` (`src/MeshWeaver.PluginCatalog/RegistryPackageSource.cs:95`) |
| **2026-09-14T06:19:40Z** | MeshWeaver.Plugins merges `dd9c96b4c8` (PR #1837), changing the in-mesh Code node `Store/Publishing/Source/RegistryPackages.cs` to call both |
| — | on that commit, `Compile every NodeType (vs core)` is **`completed/success`** (48 success, 11 skipped, 0 failures) — the resolved platform was a sealed set that carries `00fa2b69f9` |
| **2026-09-14T06:59:28Z** | the package lane has delivered the source to memex-cloud, still on `c84c6c055`. First failing compile — forty minutes after the merge |

Both portals answered `3.0.0+c84c6c05503228860df03c4a8b596e497e6d218c` to ten `/api/version` samples
that day (six on memex.meshweaver.cloud, four on memex.systemorph.com), so this was not one unlucky
replica. And the direction was settled on the **surface**, not on ancestry, exactly as the procedure
below asks: `PlanTierRefusal.cs` is a file `00fa2b69f9` ADDED, and `git cat-file -e
c84c6c055:src/MeshWeaver.Mesh.Contract/Security/PlanTierRefusal.cs` reports it **absent** at the
running commit.

**Blast radius**, from the incident's retained samples (ten, against `occurrences: 68`, between
06:59:28Z and 11:26:05Z, across three pods): `Store/Catalog`, `Store/Plugin`, `Store/Enrollment`,
`Store/Provision`, `Store/Maintenance`, `Hosting/InstanceRequest`. 🚨 **A floor, not a count** — the
sample list is a bounded ring, and the failing replica's own `/health` `bake-report` read
`previouslybroken=11`.

### Why Roslyn ran at all

A portal does not normally compile module content: `PrebuiltAssemblySeeder` adopts the CI-baked
NodeType assembly when the **framework identity matches**, and Roslyn is the fallback for a prebuilt
that is missing or declined. Here the bake was produced against the newer platform, so its identity
did not match `c84c6c055`, the prebuilt was declined, and the fallback compiled the new source
against the old reference set.

🚨 **That fallback is an opt-out.** `Modules:RequirePrebuilt` (`PrebuiltAssemblySeeder`) turns a
declined or missing prebuilt into a **named, early error** — it fails the install/update naming the
package, the registry, the framework identity and the architecture — instead of quietly attempting a
compile that cannot succeed. It defaults **OFF**, because compiling is the right fallback on dev,
CI and bake meshes. A production portal is exactly the place its own doc comment says should opt in:
*"the runtime artifact of a module is a baked DLL; a silent compile there is a distribution failure
being papered over."* The failures above are Roslyn compile failures, which is itself evidence the
key was not set on that deployment. Turning it on would not have prevented the skew — it would have
**named** it at install time instead of parking six NodeTypes with a CS-number.

## Reading the diagnostic — and the two ways to read it wrong

```
CS0246  The type or namespace name 'PlanTierRefusal' could not be found …
CS1061  'RegistryPackageSource' does not contain a definition for 'ListCatalog'
        and no accessible extension method 'ListCatalog' …
```

**What the `CS1061` does establish.** `RegistryPackageSource` *resolved*, so the platform assembly
is on the reference set and the fault is in **its surface**, not in a source node that failed to
land. That is the useful half, because it separates this from
[Missing Declared Sources](../MissingDeclaredSources) and [Install Completeness](../InstallCompleteness),
where a bare `CS0246` has a completely different cause and a completely different remedy.

🚨 **What it does NOT establish is the DIRECTION.** The same diagnostic appears when the platform is
*ahead* and the member was removed or renamed, leaving stale content behind; and an extension method
can be missing merely because its declaring class is out of scope. `CS0246` on its own is weaker
still. So "the platform is behind, roll it" is a conclusion to *reach*, never to read off the
compiler.

**Confirm before concluding:**

1. **The member is declared by the platform assembly**, and find the core commit that introduced it —
   `git log -S '<symbol>' --oneline -- src/`.
2. **The running commit** — `curl -s https://<portal>/api/version`, sampled more than once, since
   repeated calls land on different replicas.
3. **Compare the surface, not just the history.** `git merge-base --is-ancestor <adding> <running>`
   is an *ancestry* check, not a proof: a `false` also arises from diverged histories or a
   cherry-pick, and a `true` does not prove the member survived to the running commit. Read the file
   at the running commit — `git show <running>:<path>` — and prefer the instance's own answer where
   one exists.

**And distinguish it from `FrameworkStale`, which is a different condition** — there the bytes were
baked against a different framework identity and a rebuild cures it, which is what that overlay
promises. A NodeType parked on a genuine platform skew will never rebuild green; only a roll to a
set carrying the adding commit will move it.

## Why the NodeType sweep named none of the six

`search 'nodeType:NodeType content.compilationStatus:Error partitions:all' limit:200` answered **8**
at 2026-09-14T12:43Z, all of them `Hosting/*` — see
[Search Coverage and Refusal](../SearchCoverageAndRefusal).

With its denominator that reading is **8 of N ≥ 200 over M unknown**: the companion
`search 'nodeType:NodeType partitions:all' limit:200` came back `count: 200, truncated: true`, so N
is a floor, and the envelope carried **no `coverage` field at all** — the image serving this portal
predates the provider half that reports which partitions were read. By the sweep's own rule, `M
unknown` does not pass on its own. Here it was actively misleading, for two independent reasons,
neither of them a bug in the sweep:

1. **It is RLS-filtered, so it is not the denominator.** `nodeType:NodeType partitions:all` returned
   no `AgenticOffice/*` rows for this identity, while the same deployment's system-side `/health`
   counted **594 instances of three `AgenticOffice` types**.
2. **`compilationStatus` is one shared field over a per-replica compile.** A replica still serving an
   adopted prebuilt leaves the field `Ok` while another replica's fallback compile fails.
   `/health`'s `bake-report` read `previouslybroken=11` on the failing replica against the sweep's 8.

🚨 And `/health` answers about **one replica you did not choose**: six calls to the same host landed
on at least five replicas, whose bake sweeps ranged from 2026-09-12T12:55Z to 2026-09-14T08:44Z and
whose `content-types` lists were disjoint. For a per-replica census with no guesswork, use the
control instance's `{ "requestedAction": "Sample" }` — see [Operating From The Portal](../OperatingFromThePortal).

## What would close this class

Stated as options. None has been scoped by the maintainer, and the last one is a legitimate answer.

- **Wire the baseline arm onto the satellite's own PR.** `node-repo-compile-check.yml` already
  implements exactly this: `judge-against-baseline` compiles the content a second time against
  `baseline-image-digest` — *"the PREVIOUS platform image (digest) — what the fleet runs today"* —
  and `compat-verdict.py` classifies each failure. It is wired into the platform's satellite-compat
  lane, which runs when **core** promotes; *"a node repo checking its own PR leaves it false and
  keeps the plain gate."* The content half is the one that lands first and reaches portals fastest,
  so it is the half with no baseline reading.
- **Turn on `Modules:RequirePrebuilt` on production portals.** It does not close the window, but it
  converts a doomed fallback compile into a named refusal at install time, which is the difference
  between an operator reading `CS0246` and an operator reading which package, registry and framework
  identity disagreed.
- **Make the parked NodeType name the platform build it failed against.** `NodeTypeDefinition`
  already carries `CompiledFrameworkVersion` and `FailedBuildInputs` (`fw=<identity>`), so the fact
  is recorded — as a hash, not as something a reader can compare to a commit without extra work.
- **Nothing.** The condition clears on the next roll. But note that the compatibility rule the lane
  works to is deliberately coarse — *"a module built against platform X serves on platform Y unless
  one of them bumped its MAJOR"* — and the cost of a same-major surface change is paid by whoever is
  furthest behind. [#4083](https://github.com/Systemorph/MeshWeaver/issues/4083) is the closed
  instance of that assumption failing on a different axis.

## Related

- [The Module Platform Link Gate](../ModulePlatformLinkGate) — the same question, asked of bytes, against the running platform
- [CI Content Bake](../CiContentBake) — how the satellite lane is wired, and the workflow-ref pin
- [Platform and content — two layers, two cadences](../PlatformAndContent) — why content moves faster than the image under it
- [Node Type Compilation](../NodeTypeCompilation) — what compiles at runtime, and when a prebuilt is adopted instead
- [Missing Declared Sources](../MissingDeclaredSources) · [Install Completeness](../InstallCompleteness) — the other cause of a `CS0246` on a mesh compile
- [Search Coverage and Refusal](../SearchCoverageAndRefusal) — what the NodeType sweep can and cannot see
- [Operating From The Portal](../OperatingFromThePortal) — `Sample` for a per-replica reading
