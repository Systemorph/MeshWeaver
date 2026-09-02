---
Name: Rebake Waves
Category: Architecture
Description: Why an image roll can recompile every NodeType on every portal, what each rebake costs on the NodeType's own MeshNode, and the two measurements that separate the trigger from the cost.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><polyline points="21 3 21 9 15 9"/><path d="M12 8v4l2 2"/></svg>
---

# Rebake Waves

A **rebake wave** is a portal recompiling every dynamic NodeType it hosts, at once, because the
framework identity those types were built against no longer matches the one the process resolves.
On 2026-08-31 three waves ran in one night across two portals and took the shared Postgres
connection pool with them (issue #2895).

A wave has **two independent halves**, and conflating them is why it looked like one unexplainable
event:

| | Question | Answer lives in |
|---|---|---|
| **Trigger** | *Why is every type stale at once?* | the framework build identity |
| **Cost** | *What does one type's rebake write?* | the NodeType's own MeshNode |

They are measured separately below, because fixing either alone leaves the other intact.

## The cost: what a rebake writes, and what it wrote for nothing

Every write to a NodeType's MeshNode is expensive out of proportion to its size. The node is the
framework's highest-fan-out record: every instance of the type databinds to it, every version lands
a row in that partition's Postgres schema, and every write fans a change notification across the
mesh.

The framework already knows this and **suppresses a write that records nothing**:
`MeshNodeStreamExtensions.UpdateOwn` compares the updated node against the current one with
`MeshNode.SerializedEquals` and returns without applying anything when they agree. An `Update`
lambda that reproduces the persisted state costs a comparison and mints no version.

So the cost of a rebake is exactly the writes that *do* change something:

| # | Write | Changes |
|---|---|---|
| 1 | the stale-build kickoff flips `Ok → Pending` | the status |
| 2 | `HandleDispatchCompile`'s compare-and-swap flips `Pending → Compiling` | the status, plus the start stamp |
| 3 | the activity-path flip, once the compile-activity node exists | the activity path |
| 4 | the terminal write-back settles `Compiling → Ok` | the status and the whole build record |

Four versions per type per wave, three of them carrying a state transition that genuinely happened.
That is the honest price of a rebake — **and it is the arithmetic that explains the observed version
counts**: roughly four per rebake, one rebake per platform roll, and (for the reason in the next
section) one roll per merge to main. `Edu/CourseCatalog` at v1784 is on the order of 450 rebakes,
which is a few months of merges. The counts are the TRIGGER half's bill, not a runaway loop —
nothing recompiles because the version moved (`LastCompiledVersion` is only ever an `IAssemblyStore`
key; no predicate reads the node's own version to decide a rebuild).

### …and two writes that recorded something that had not happened

Two write-backs re-stamped a `DateTimeOffset.UtcNow` for a fact that had not moved. Each was wrong
on its own terms, and — because a fresh timestamp can never equal the persisted one — each also
carried an *otherwise byte-identical* write past the no-op gate:

- **`GetCompilationPathRequest`'s success write-back stamped `LastCompileSucceededAt` on a
  HYDRATE.** That branch is the published-release short-circuit: the record already reads `Ok`, the
  bytes are already in the assembly store under the key the record already carries, and *nothing is
  compiled*. Every other field on it resolved to what was already persisted, so the timestamp alone
  minted a node version, a change-feed fan-out and a Postgres row.
- **`RunCompile`'s activity-path flip re-minted `LastCompileStartedAt`**, which
  `HandleDispatchCompile`'s compare-and-swap had stamped moments earlier in the same dispatch. This
  one fires on **every compile** — during a wave, once per type.

Both are now gated on the fact they claim: only a real Roslyn run stamps a success time (the same
`freshCompile` predicate that already gates `CompiledFrameworkVersion` in that write), and the start
stamp belongs to the `Pending → Compiling` transition that mints it.

> 🚨 **The write saving is the second reason these are wrong; the first is that a gate downstream
> reads each stamp as evidence.** `DynamicTypePreWarmer.RebuildMissingBytes` and `WatchForRecovery`
> both prove "a compile is demonstrably FRESH" by requiring `LastCompileSucceededAt` to be strictly
> newer than a baseline they took — precisely so a replayed pre-existing `Ok` cannot green-light a
> share that never got its bytes back. A hydrate satisfied that proof having compiled nothing.
> And re-minting the START stamp after the activity-node create — a step bounded at ten seconds —
> moved the recorded start past any source written inside that window, discarding exactly the
> torn-snapshot evidence `SourcesMovedDuringCompile` exists to surface.

> ⚖️ **Scope, stated honestly.** `GetCompilationPathRequest` is the per-NodeType hub's **fallback**
> resolve contract — its own registration in `MeshDataSource` says so, and
> `NodeTypeService.ResolveViaStream` replaced it for the common activation path. No production
> caller in this repo or in MeshWeaver.Plugins posts it today; it is reached by cross-process /
> participating-client probes and is what several Monolith compile tests drive. So the *write*
> saving on that path is real but bounded, while the false-pass it removes is not. The activity-path
> flip's saving is likewise bounded: the activity path is genuinely new on each compile, so that
> write still lands except where the activity create produced nothing.

`HydrateIsNotACompileTest` pins both as EQUALITY against the input definition — the shape the no-op
gate consumes — so a field added to either stamp later is covered by construction.

## The trigger: the framework identity moves on every commit

A type is rebaked when `NodeTypeDefinition.CompiledFrameworkVersion` differs from the live
`FrameworkBuildIdentity.FrameworkVersion` and the assembly store holds no bytes under the live tag
(`NodeTypeCompilationHelpers.DecideStaleBuildAction`). So the wave's size is decided entirely by how
often that identity moves.

It is *designed* to move rarely. [CI Content Bake](../CiContentBake) describes the API-surface identity
`s<hash>`: the manifest records, per compile reference, the SHA-256 of its **reference assembly** —
byte-stable under body-only and private-member edits, changed by any public-surface change. "Rebuild
only when we need to."

**Measured, that property does not hold on any build this repository produces.** Building
`MeshWeaver.Utils` three times, changing one variable at a time:

| Build | Reference assembly SHA-256 |
|---|---|
| baseline, `SourceRevisionId=aaaa…` | `93bf962a45…` |
| **a method-body edit**, same revision | `93bf962a45…` — unchanged ✅ |
| **no source change at all**, `SourceRevisionId=bbbb…` | `67bcd4ac43…` — **changed** ❌ |

The first row pair is the design working: a body-only edit leaves the API surface, and therefore the
reference assembly, byte-identical. The second is the defect. Three build-provenance stamps are
compiled into **every** assembly and land in its reference assembly, and all three carry the commit
sha:

```text
AssemblyInformationalVersion  3.0.0-rc9+<sha>          # the SDK's source-revision suffix
AssemblyMetadata("CommitHash", "<sha>")                # Directory.Build.props → AddCommitHashMetadata
AssemblyMetadata("MeshWeaverFrameworkIdentity", "g<sha>")
```

Only 196 of 23 552 bytes differ between the two revisions — the three sha strings, the deterministic
MVID they feed, and the PE/debug stamps derived from it. None of them is API surface: nothing a
NodeType compiles against can observe an assembly-metadata provenance stamp, so **none of them can
change a byte Roslyn emits.** Yet each moves that assembly's manifest line, and the identity is a
hash over all of them.

The same stamps ride in the implementation assemblies, so the `FullMvidAssemblies` half of the
identity — the toolchain closure, which contributes full MVIDs rather than surface hashes — moves
per commit too.

**Consequence: on CI, `s<hash>` is a pure function of the commit — arithmetically equivalent to the
`g<sha>` commit identity it was introduced to replace.** Every merge to main mints a new framework
identity, so every roll finds every type stale. Content baked by a satellite repo against a pinned
platform image is declined the moment the portal rolls past that pin, and the pod recompiles it.
Three waves in one night is three merges, not three faults.

Nothing measured this before. The stability claim is asserted in prose in `Directory.Build.props`,
`MeshWeaverSurfaceManifest.targets` and `FrameworkBuildIdentity`'s own summary; every case in
`FrameworkBuildIdentityTest` operates on synthetic manifest dictionaries, so none of them can
observe a real reference assembly moving.

### Why this is not fixed here

De-contaminating the identity means keeping per-commit values out of compiled metadata across the
whole platform, and the identity is an **address**: a bake publishes bundles under the identity its
producer resolved, and a portal only ever looks under the identity *it* resolves. Getting that
disagreement wrong is #1814, where CD baked under one identity, both prod pods asked for another,
the bundles sat intact under an address nobody read, and every pod recompiled 269 types at boot.
The change also reaches every repository in the fleet through the shared
`MeshWeaverSurfaceManifest.targets`, and it moves the anchor two independent readers depend on
(`PlatformBuildInfo.SelectBuildAssembly`, which resolves the deployed portal's About page and
self-updater version, and `MeshWeaver.Plugin.Build.FrameworkIdentity`, which gates plugin bundle
adoption).

That is a scope call on the platform's build identity, of the same kind the
[Toolchain Re-evaluation Lane](../ToolchainReevaluationLane) already recorded rather than took when it
declined to demote the framework version. It is tracked on #2895 with the measurement above, which
is reproducible in three commands:

```bash
dotnet build src/MeshWeaver.Utils/MeshWeaver.Utils.csproj -c Release --no-incremental \
  -p:CIRun=true -p:GITHUB_ACTIONS=true -p:SourceRevisionId=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
shasum -a 256 src/MeshWeaver.Utils/obj/Release/net10.0/ref/MeshWeaver.Utils.dll
# …repeat with a different SourceRevisionId and no source change.
```

## Why the compile bookkeeping did not move off the node

The stated root cause on #2895 is that compile bookkeeping lives on the main NodeType node, and the
framework already ships phase 1 of the move: `NodeTypeCompileStateMirror` projects the operational
members onto a fixed-id satellite at `{type}/_Activity/compile-state`. Phase 2 — flipping readers to
the satellite — is blocked by two structural facts, both surveyed while diagnosing this issue:

- **31 pure predicates read those members with no hub in scope.** `HasUsableBuild` (13 production
  call sites), `NodeTypeBakeStatus.Classify`, `NodeTypeBuildState.HasLoadableBuild`,
  `IsCompileSettled`/`IsRecompileSettled` and the rest are static functions over a `MeshNode`,
  several of them invoked from stream `.Where` filters where only the type-node emission exists.
  They cannot read a second node without re-shaping a public reactive surface.
- **`CompilationStatus` is the compile lock, and the result members are re-checked inside the same
  swap.** `HandleDispatchCompile`'s `Pending → Compiling` transition is atomic only because the
  NodeType hub owns the node it swaps on; the stale-build kickoff, the restamp and the
  failed-verdict re-drive each re-read `CompiledFrameworkVersion` / `CompiledDependencies` /
  `LatestAssembly*` **inside the lambda that flips the status**. Splitting that pair across two
  owners is the double-compile the satellite's own header warns must never ship.

Moving the members would also not have removed the four versions a rebake costs: writes 1, 2 and 4
above each carry a status transition, so they bump the node wherever the bookkeeping lives, and
write 3 carries a genuinely new activity path. **The lever on the version counts is the trigger, not
the storage location** — which is why the section above is the one that matters for #2895's numbers,
and why this page separates them.

## Related

- [CI Content Bake](../CiContentBake) — the surface manifest, the canonical assembly set, and what a
  bake publishes under.
- [Toolchain Re-evaluation Lane](../ToolchainReevaluationLane) — regenerate the compile input and carry
  a build forward instead of recompiling, and why it stops short of the framework version.
- [Node Type Compilation](../NodeTypeCompilation) — the compile watchers, the status state machine and
  the assembly store.
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the shared handle, the serial write queue and the
  no-op upsert gate this change relies on.
- [Postgres Schema Architecture](../PostgresSchemaArchitecture) — where the version rows land.
