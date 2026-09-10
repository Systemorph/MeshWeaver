---
Name: Adoption and the Sweep Count Different Things
Category: Architecture
Description: A cold boot adopted 78 prebuilt assemblies and the bake sweep reported 5 ten seconds later. Neither number was wrong and neither was a census of the share — one counts bundle entries whose bytes landed, the other counts NodeTypes judged from an eventually-consistent record snapshot. What each instrument counts, why the sweep could not see its own process's writes, and the ordering rule that fixes it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h7v12H3z"/><path d="M14 10h7v8h-7z"/><path d="M10 12h4"/></svg>
---

# Adoption and the Sweep Count Different Things

On memex, 2026-09-08, one cold boot printed these two lines ten seconds apart, from the same
process, on the same framework identity:

```
00:31:08  ShippedPrebuiltBundles: 78 prebuilt assembly(ies) from 38 shipped bundle(s) …
          are backed by the assembly store — 78 adopted now, 0 already current
00:31:18  DynamicTypePreWarmer: 204 of 209 dynamic NodeType(s) need building … — 5 already
          on the share.  framework=sb43f928 total=209 baked=5 pending=204 frameworkstale=201
```

It was filed as *"78 adopted, the sweep's store probe counts 5"*
([#3703](https://github.com/Systemorph/MeshWeaver/issues/3703)) — the natural reading, and the
wrong one. **The share was fine. Neither counter was lying. They were never the same
population, and one of them was reading a projection its own process had already superseded.**

The cost was real: 197 compiles instead of ~20, and it is what put four already-adopted `Doc/**`
NodeTypes on the compile path where a short source-discovery pass could turn them into four false
regressions and hold the portal out of rotation for three hours
([#3663](https://github.com/Systemorph/MeshWeaver/issues/3663)).

## What each instrument counts

| | adoption line (`ShippedPrebuiltBundles`) | sweep line (`DynamicTypePreWarmer`) |
|---|---|---|
| **unit** | assemblies — **bundle entries** | **NodeTypes** |
| **denominator** | entries across the bundles this mesh holds a NodeType for | the dynamic NodeTypes in ONE mesh-wide enumeration |
| **source** | the bytes it wrote, plus the record patch it posted | a CQRS query snapshot of each type's record |
| **claim** | "these bytes are on the assembly store" | "this type's RECORD names a live-framework build AND the store resolves bytes at the version that record names" |

Two bundles may legitimately name one NodeType, so even the units do not convert. But the sharper
point is the last row:

> 🚨 **The bake report is not a census of the assembly store, and cannot be.** The store is asked
> exactly ONE question per type — `TryGetAssemblyPath(typePath, definition.LastCompiledVersion)` —
> and the key comes out of the **record**. A reader whose record snapshot is behind asks the wrong
> key, gets a miss, and reports `FrameworkStale` no matter what is sitting on the volume.

So `baked=5` and `frameworkstale=201` are both statements about **records**, filtered through one
store lookup each. Reading either as "what the share holds" is a category error, and it is the
error the issue's own title makes.

### Why 5 and not 0 — the number that looked unexplainable

#3703 closed its reading with *"what is left unexplained by all three is the **5**: not 0, not 78"*,
and the answer is what turns this from a race into a defect. `baked=5` is over **all 209** types.
The 78 the seeding pass had just adopted contributed **zero** of them; the 5 were types already
current from an earlier boot. So the adoption's effect on the sweep was not *reduced* by a lag — it
was **absent**, completely, for every type it touched.

That matters because it rules out waiting. A probabilistic index lag would have shown up as *some*
of the 78 landing; a total miss is a missing ordering, and the only thing that can supply an ordering
between two sources is a fact one of them already holds.

## Why the sweep could not see its own process's writes

The seeding pass writes each adopted type's record through
`workspace.GetMeshNodeStream(path).Update(…)` — the authoritative mutation API, routed to the
type's owning hub. The sweep then reads
`meshService.Query<MeshNode>(MeshWideQuery.OfType(MeshNode.NodeTypePath)).Take(1)` — a CQRS read.

Those are different sources with no ordering between them.
[CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) states the contract plainly:
*"Queries route through a read-side index — a cached projection that is eventually consistent …
that window is long enough to break any pattern that requires read-your-writes"*, and *"a query's
answer for one path can be minutes old"*. On a cold boot the seeding pass issues 78 cross-hub
patches in 35 seconds, and the enumeration ten seconds later is free to answer with the records
those patches replaced.

**And nothing in the classification can tell that apart from a genuinely stale record.** A record
naming the previous framework because nobody has updated it, and a record naming the previous
framework because the reader has not caught up, are the same bytes. `NodeTypeBakeStatus` is pure
and correct on the input it is given; the input was behind.

That is why the mitigations that suggest themselves are all wrong:

| Tempting | Why it is not the fix |
|---|---|
| re-probe the store after the sweep starts | the store was never the problem; the key was |
| wait / retry / sleep before enumerating | there is no bound to wait for — the projection's lag is unbounded by contract |
| relax the classifier so a bytes-hit outranks the record | the record is what supplies the key, so there is no hit to outrank; and it would turn a real staleness into a stale serve |

## The fix: classify from the newer of the two facts

The process **knows** which records it wrote. That fact was being thrown away — the seeding pass
emitted a count and nothing else.

`NodeTypeAdoptionRegistry` — the mesh-scoped singleton that already existed to stop the first-build
kickoff from racing an in-flight adoption — now also carries a **ledger** of completed ones:

- `RecordAdopted(path, observedVersion, definition)` is called on the same branch that logs
  `Prebuilt assembly ADOPTED …`, never without it. `observedVersion` is the `MeshNode.Version` the
  adoption **read before its own write**.
- `OverlayOnto(definitions, nodes)` lays those stamps over an enumeration snapshot, and returns
  both the definitions to classify from and the list of paths it overrode.

### The ordering rule is a fact, not a heuristic

An adoption stamps the version it read, and its own write **bumps the node**. Therefore:

| snapshot's `MeshNode.Version` | means | who wins |
|---|---|---|
| `<= observedVersion` | the snapshot **cannot** contain the adoption's write | the process's own stamp |
| `> observedVersion` | the snapshot contains that write, or something after it — an owner refusal ([#2813](https://github.com/Systemorph/MeshWeaver/issues/2813)), a recompile, a source change | the snapshot, unchanged |
| no node in the snapshot at all | nothing to compare | the snapshot, unchanged |

The second row is what keeps this from becoming the stale serve the bake's conservatism exists to
prevent, and it is pinned by a test that fails against an unconditional overlay:

```
Expected value to be NeverBuilt because the record the owner left is what decides; serving the
adopted bytes over it would be exactly the stale serve #3703's fix must not introduce,
but found Baked.
```

Nothing is retried, nothing waits, and no bound moved.

## Making the two lines legible

A fix that silently corrected the sweep's input would be the next version of the same defect — the
operator reading a boot still could not tell whether the enumeration was behind. So all three
statements now name their own population:

- the adoption line says it counts **assemblies (bundle entries)**, and that the sweep's counts are
  over **NodeTypes judged from their records**, so a difference between them is not a disagreement;
- the sweep's line says `{Baked} need no build (record and share agree; … NOT a census of the
  assembly store)` — it used to read *"already on the share"*, which is what invited the reading in
  the first place;
- `NodeTypeBakeReport.ClassifiedFromLocalAdoption` counts the entries classified from this
  process's own write, and `Summary` appends `fromlocaladoption=N` whenever it is non-zero. A boot
  where the enumeration was behind now **says so** in the same line that reports the verdict.

## The second truncation, one level down

The same population had a second way to shrink in silence. `DynamicTypesOf` selected the sweep's
types with `n.Content is NodeTypeDefinition d` — a direct CLR pattern match on a payload that
crossed a query boundary. A row whose `$type` the serving hub could not resolve degrades to a raw
`JsonElement`, and such a NodeType **vanished**: out of `total=`, out of `baked=`, out of
`pending=`, with nothing anywhere saying a type had been dropped. The report's denominator moved
and the report read identically.

The read is now `ContentAs<NodeTypeDefinition>` — which **recovers** the JSON case rather than
dropping it — and whatever still will not type is counted and named by path in a warning. Excluded
because checked (a static NodeType with no compilable source) and never checked at all are
deliberately different states with different counters; collapsing them would be the same defect
again. See [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail).

## The general rule

> **Before comparing two numbers, make each one state its population, its unit and its source.**
> Two instruments that disagree are only evidence of a defect once you have established they were
> measuring the same thing — and an instrument that reads a projection must be able to say when its
> input was superseded, or a stale read and a stale subject are indistinguishable from the outside.

## Where the code is

| Piece | File |
|---|---|
| the ledger + the ordering rule | `src/MeshWeaver.Compiler.Pipeline/NodeTypeAdoptionRegistry.cs` |
| the stamp that feeds it | `src/MeshWeaver.Compiler.Pipeline/PrebuiltAssemblySeeder.cs` (`Write`) |
| the classification, unchanged | `src/MeshWeaver.Compiler.Pipeline/NodeTypeBakeStatus.cs` |
| the overlay + the population report | `src/MeshWeaver.Hosting/DynamicTypePreWarmer.cs` |
| the adoption line | `src/MeshWeaver.Hosting/ShippedPrebuiltBundles.cs` |
| controls | `test/MeshWeaver.Compiler.Pipeline.Test/AdoptedTypeIsNotJudgedFromAStaleSnapshotTest.cs`, `test/MeshWeaver.Hosting.Test/EnumerationSaysWhatItCouldNotTypeTest.cs` |

## See also

- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query is never the
  authoritative read for a node's content
- [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) — the family this belongs to
- [Install-Time Prebuilt Adoption](/Doc/Architecture/InstallTimePrebuiltAdoption) — the adoption lane
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — what the sweep is deciding about
