---
Name: Source Nodes Have No Link Gate
Category: Architecture
Description: A module's compiled bytes are measured against the running platform before they are adopted. An in-mesh Code node is text — it is never link-probed, it compiles at runtime on whatever image the replica happens to be running, and a satellite may merge source that calls a core API the target's image does not have. The measured case, the compiler diagnostic that tells platform skew from a content bug, and why the NodeType sweep did not name any of it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/><line x1="2" y1="2" x2="22" y2="22"/></svg>
---

# Source Nodes Have No Link Gate

[The Module Platform Link Gate](../ModulePlatformLinkGate) measures a module's **bytes** against the
platform actually running — the set of types the assembly is linked against, read out of its own
metadata — and refuses a generation the process cannot load.

**An in-mesh Code node is not bytes. It is text.** It carries no assembly metadata, nothing probes
it, and it is compiled at runtime, on each replica, against whatever image that replica is running
(see [Node Type Compilation](../NodeTypeCompilation)). So a satellite repository can merge source that
calls a core API, the package lane can deliver it to a portal within the hour, and the first thing
that ever measures the requirement is the Roslyn compile — which fails.

Nothing in the fleet gates this direction:

| gate | what it measures | does it see an in-mesh source node? |
|---|---|---|
| [Module Platform Link Gate](../ModulePlatformLinkGate) | a module assembly's linked types vs. the running platform | **no** — source nodes have no assembly |
| `minMeshVersion` floor ([Module Adoption Policy](../ModuleAdoptionPolicy)) | a version string the author writes; advisory since 2026-09-07 | no — and it is per module, not per source node |
| `Cross-repo pair (public surface)` (core CI) | core **removing** public surface a dependent may call | no — this is the opposite direction: content **requiring** surface core has only just added |

## The measured case — 2026-09-14, memex.meshweaver.cloud

Six NodeTypes stopped compiling on that deployment within forty minutes of a satellite merge.

**The core half landed first, and is not in the running image.** Core PR
[#4102](https://github.com/Systemorph/MeshWeaver/pull/4102) (commit `00fa2b69f9`, merged
2026-09-12T17:28:05Z) added `PlanTierRefusal`
(`src/MeshWeaver.Mesh.Contract/Security/PlanTierRefusal.cs`) and `RegistryPackageSource.ListCatalog`
(`src/MeshWeaver.PluginCatalog/RegistryPackageSource.cs:95`). Both portals serve
`3.0.0+c84c6c05503228860df03c4a8b596e497e6d218c`, whose commit is dated 2026-09-12T09:30:22Z —
**five hours earlier**:

```
$ git merge-base --is-ancestor 00fa2b69f9 c84c6c055   → false   ← the image predates the API
$ git merge-base --is-ancestor c84c6c055 00fa2b69f9   → true
```

Ten `/api/version` samples — six on memex.meshweaver.cloud, four on memex.systemorph.com — all
answered that same sha, so this is not one unlucky replica.

**The content half landed two days later.** MeshWeaver.Plugins
`Store/Publishing/Source/RegistryPackages.cs` — an in-mesh Code node, shared into many NodeTypes'
source sets — was last changed by `dd9c96b4c8`, **2026-09-14T06:19:40Z**, and now calls both
symbols. The first failing compile on the portal is **2026-09-14T06:59:28Z**: forty minutes.

**Blast radius.** Read off the incident's retained samples (10 of 68 occurrences, all on 2026-09-14
between 06:59Z and 11:26Z, across two pods): `Store/Catalog`, `Store/Plugin`, `Store/Enrollment`,
`Store/Provision`, `Store/Maintenance`, `Hosting/InstanceRequest` — **six distinct NodeTypes**, and
nine of the ten samples carry the identical diagnostic pair. One shared source file, every NodeType
whose source set includes it.

🚨 **The retained window is not the denominator.** Ten samples against 68 occurrences is a bounded
ring, so "six NodeTypes" is a floor, not a count.

## Reading the diagnostic: skew, or a content bug?

The two errors are not equally informative, and the second one is the fingerprint:

```
CS0246  The type or namespace name 'PlanTierRefusal' could not be found …
CS1061  'RegistryPackageSource' does not contain a definition for 'ListCatalog'
        and no accessible extension method 'ListCatalog' …
```

**`CS1061` on a PLATFORM type is conclusive.** The type *resolved* — the reference set holds
`MeshWeaver.PluginCatalog`, so the assembly is there — and only the member is missing. That can only
mean the running build of that assembly predates the member. No recompile can cure it, because
recompiling produces the same reference set; only a roll can.

**`CS0246` alone is ambiguous.** A type that "could not be found" may equally be an in-mesh type
whose Code node never landed — see [Missing Declared Sources](../MissingDeclaredSources) and
[Install Completeness](../InstallCompleteness), where the same shape has a completely different cause
and a completely different remedy. It becomes skew evidence when it appears *beside* a `CS1061` on a
platform type, or when the symbol is traced to a core commit that is not an ancestor of the running
one.

**Neither is `FrameworkStale`, which is the opposite condition.** There the bytes were baked against
a different framework identity, a rebuild cures it, and the overlay says so in as many words. A
NodeType parked on the skew above will never rebuild green; its overlay's "it will recompile"
reassurance is wrong for this cause.

### The three commands

```bash
curl -s https://<portal>/api/version                  # the running commit — sample it more than once
git log -S '<symbol>' --oneline -- src/               # the core commit that added the symbol
git merge-base --is-ancestor <adding> <running>       # false ⇒ the image predates the API ⇒ skew
```

The remedy is a **roll** to a sealed set containing the adding commit. Until then the affected
NodeTypes serve their cached error and their instances render empty.

## Why the NodeType sweep did not name any of it

The sweep that reads the same field the readiness gate reads —
`search 'nodeType:NodeType content.compilationStatus:Error partitions:all' limit:200`, see
[Search Coverage and Refusal](../SearchCoverageAndRefusal) — answered **8** at 2026-09-14T12:4xZ, all
of them `Hosting/*`, and named **none** of the six.

Stated with its denominator, that reading is *8 of N ≥ 200 over M unknown*: the companion
`search 'nodeType:NodeType partitions:all' limit:200` came back `count: 200, truncated: true`, so N
is a floor, and the envelope carried **no `coverage` field at all** — the image serving this portal
predates the provider half that reports which partitions were read. Per the sweep's own rule, a
reading with `M unknown` does not pass on its own. Here it did not merely fail to pass; it was
actively misleading, for two independent reasons, neither of them a bug in the sweep:

1. **It is RLS-filtered, so it is not the denominator.** `nodeType:NodeType partitions:all` for this
   identity returns no `AgenticOffice/*` rows at all, while the same deployment's public `/health`
   counts 594 instances of three `AgenticOffice` types. A partition you hold no grant on is silently
   not counted.
2. **`compilationStatus` is one shared field; the compile is per replica.** A replica still holding
   a good cached assembly leaves the field `Ok` while another replica fails the compile. On the
   failing replica, `/health`'s `bake-report` read `previouslybroken=11` against the sweep's 8.

🚨 And `/health` answers about **one replica you did not choose**. Six calls to the same host landed
on at least five replicas, whose bake sweeps ranged from 2026-09-12T12:55Z to 2026-09-14T08:44Z and
whose `content-types` lists were disjoint. For a per-replica census with no guesswork, use the
control instance's `{ "requestedAction": "Sample" }` — see
[Operating From The Portal](../OperatingFromThePortal).

## What would close this class

Stated as options, not as a decision — none of these has been scoped by the maintainer:

- **Measure the source set the way the link gate measures bytes.** A package's declared `.cs` node
  files could be compiled against the target instance's reference set *before* the install commits,
  and the install refused with the missing symbol named — the shape
  [the link gate](../ModulePlatformLinkGate) already uses for assemblies.
- **Make the parked NodeType say which platform build it failed against.** The definition already
  carries `CompiledFrameworkVersion` and `FailedBuildInputs`; a compile that fails on a platform type
  could name the running build beside the diagnostic, so the reader is not left to reconstruct the
  ancestry by hand.
- **Nothing.** The condition is self-healing on the next roll, and the cost of a gate on every
  install may exceed the cost of the window. That is a legitimate answer — but it should be chosen,
  not arrived at by never having named the failure mode.

## Related

- [The Module Platform Link Gate](../ModulePlatformLinkGate) — the same question, asked of bytes
- [Platform and content — two layers, two cadences](../PlatformAndContent) — why the two cadences exist
- [Node Type Compilation](../NodeTypeCompilation) — what compiles at runtime, and when
- [Missing Declared Sources](../MissingDeclaredSources) · [Install Completeness](../InstallCompleteness) —
  the other cause of a `CS0246` on a mesh compile
- [Search Coverage and Refusal](../SearchCoverageAndRefusal) — what the NodeType sweep can and cannot see
- [Operating From The Portal](../OperatingFromThePortal) — `Sample` for a per-replica reading
