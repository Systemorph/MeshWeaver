---
nodeType: Markdown
name: Dependency Graph
category: Architecture
description: Forward dependency edges computed by Roslyn at the end of every NodeType compile — static (bound NodeTypes, modules, platform assemblies, packages) and dynamic (reflection by name) — reconciled with what the tree declares, ratcheted where they reach into core, stored once per environment, and the reverse closure ("who depends on X") answered through the query surface.
icon: /static/NodeTypeIcons/code.svg
---

# Dependency Graph

**What a package or NodeType depends on is what its compilation actually USED — computed by
Roslyn, never declared and never inferred from names.** The forward edges are written once, where
the compile runs, carrying the identity of the build they were read from. The reverse direction
("everything that depends on X") is never stored as authored data: it is a closure the store keeps
by itself, read through the query surface.

This page is the implementation plan and the record of what landed. The design it implements is
MeshWeaver.Plugins `Hosting/CiAffectedClosure.md` → *Next: the dependency graph from Roslyn,
reversed in Postgres*; its step (4) — CI selection reading the graph — lives in MeshWeaver.Plugins.

## The forward edge (step 2)

### What an edge is

One node per compiled source, holding that source's whole forward edge set:

| | |
|---|---|
| path | `{NodeTypePath}/_Dependency/Forward` |
| node type | `DependencyEdgeSet` (registered by `AddGraph`, keeps no history) |
| content | `DependencyEdgeSet` — `Source`, `BuildMvid`, `StampVersion`, `InputDigest`, `Commit`, `Edges`, `IncludedWithoutUse` |
| one edge | `DependencyEdge` — `Target`, `Kind`, `TargetBuildMvid`, `Evidence`, `EvidenceClass`, `TargetKind` |

`Kind` is an open vocabulary (`DependencyEdgeKinds`, policy `open-vocabulary-string-constants`):

| kind | target | evidence class | read from |
|---|---|---|---|
| `NodeType` | the owning NodeType's path | Static | the `SemanticModel` walk (below) |
| `Module` | the module assembly's simple name | Static | the EMITTED assembly's `AssemblyRef`s ∩ the installed module files (`GetUsedAssemblyReferences()` only for a caller with no image) |
| `Platform` | a `MeshWeaver.*` host assembly's simple name | Static | the EMITTED assembly's `AssemblyRef`/`TypeRef` tables |
| `Package` | `package:{id}` — the package id (`#r "nuget:"`, or a shelf package for a module build) in its own namespace | Static | the emitted `AssemblyRef`s ∩ the package files |
| `Dynamic` | `{type}, {assembly}` — `*` for a whole-assembly load, `?` for an unknown part | Dynamic | a reflection call with a CONSTANT argument |
| `DynamicUnresolved` | `?{api}`, e.g. `?System.Type.GetType` | Dynamic | a reflection call whose argument is not constant |

Targets are unique within one set by construction (`DependencyEdgeTargets`): a NodeType target is
a path (contains `/`), a module or platform target an assembly simple name (one runtime identity per
name — when a module and a host assembly share one, the installed module is the edge), a package
target `package:{id}` (a package id can equal an assembly name: `Acme.Bundle` shipping
`Acme.Widget.dll` beside a module ASSEMBLY `Acme.Bundle`), a dynamic target `{type}, {assembly}` or
`?{api}`. `DependencyEdgeExtraction.UniqueTargets` proves it on every set written and fails LOUDLY
on a collision, so a target-keyed reader — the Postgres projection's `(source, target)` key — never
has to choose between two kinds. `TargetKind`
says what a dynamic target IS — `Platform`, `Module`, `Package`, `NodeType` (a type in the source's
own compilation), `Framework` (the BCL), or null when the compile cannot tell, which a reader must
read as "may be the platform".

- **One node per source, replaced whole.** A recompile writes the complete set in one write, so a
  dropped edge disappears in the same write that adds the new ones — never a half-state, and never
  a query to find which edges to delete.
- **The newest generation wins, whatever order the writes arrive in.** Two writers publish one
  source's set (the activation's compile settle and the cold-start batch bake), each AFTER its own
  stamp landed, so an older compile can land its stamp, pause, and write its edges after a newer
  build has stamped and written. `StampVersion` is the version of the NodeType record that the
  build's stamp landed at — minted monotonically per path, so it orders the two writers' builds.
  `DependencyEdgeSetWriter.Write` is a compare-and-set on the edge-set row
  (`IStorageAdapter.WriteIfVersion`, the batch stamp's own primitive): it reads the stored set,
  skips when that set is of a newer generation, otherwise writes conditioned on the row version it
  read, and on a refusal re-reads and decides again. A host with no storage adapter falls back to
  `CreateOrUpdateNode`. **A tie** — the same generation, a different build — happens because both
  writers can mint one version for two builds (the activation acknowledges v+1 in memory before
  its flush, the batch bake writes v+1 from the durable v). Then the source's record decides: it is
  read AFTER the edge row, and a set whose build the record does not name is not written. What is
  left open: while the activation's v+1 is still unflushed, the durable record can name the other
  build, so the stored set can describe that one. A reader then sees `BuildMvid` ≠
  `LatestAssemblyMvid` — "not current", never a silent pass — until the type's next compile.
- **A satellite, never a main node.** The node's `MainNode` is the source, so it does not answer
  `is:main` or list as an instance of anything.
- **Build identity on the set.** `BuildMvid` is the MVID of the emitted assembly the edges were
  read from (the same value as `NodeTypeDefinition.LatestAssemblyMvid` after the stamp), and
  `InputDigest` the generated-input digest of that compile. A consumer that finds a set whose
  `BuildMvid` is not the type's current build knows the edges are stale — the check step (4)
  needs.
- **In-memory compiles** (disk cache off) record the loaded image's MVID as `BuildMvid`, while the
  NodeType record leaves `LatestAssemblyMvid` unstamped there (it names a STORED build, and an
  in-memory build has none). A reader comparing the two on such a mesh sees "not current". That is
  loud, not silent. Whether in-memory builds should stamp an identity is a separate decision.
- **Only a compile that ran Roslyn writes one.** A cache hit, an adopted prebuilt or the hydration
  shortcut has no compilation to read, so it writes nothing and the existing set (with its own
  `BuildMvid`) stays as it is. "Extracted, and there are no edges" is an EMPTY set; "never
  extracted for this build" is a set whose `BuildMvid` differs, or no set at all. The two are never
  conflated.
- **Nothing on the NodeType node.** The edge set is its own node; the NodeType's record carries no
  dependency list (the existing `CompiledDependencies` record is the adoption-validity record and is
  untouched).

### How Roslyn computes it

`DependencyEdgeExtraction.Extract` (`MeshWeaver.Compiler`) runs on the compilation the emit just
used — after the source generators, before the emitted bytes are loaded — and is pure:

1. **Who owns each declared type.** Every Code file the compile consumed is parsed on its own (parse
   only, no binding) and its declared types' metadata names are attributed to the file's owner —
   everything before its first `/Source/` or `/Test/` segment (`CodeConventions.OwnerOfCodePath`,
   the rule `NodeTypeDependencyGraph.ForeignSourceOwners` already used). `@@` include text is
   attributed to the INCLUDED node's owner, not to the file it was spliced into. The emitted
   compilation is one concatenated tree with no namespace wrapper, so a type's metadata name in a
   per-file parse is its metadata name in the compilation.
2. **NodeType edges — a `SemanticModel` walk.** For every name in the authored trees, the bound
   symbol's outermost containing type is looked up in that ownership map. A reference located in
   code the compiling type owns (its own files, the generated skeleton, its `configuration`) to a
   type another NodeType owns is an edge `self → owner`, with the referenced type names as
   evidence. References inside a foreign owner's code are NOT this type's edges — that owner's own
   compile records them, and the closure joins the two.
3. **Module edges — the emitted image's `AssemblyRef`s.** Every `AssemblyRef` the compiler wrote
   that resolves (by full identity) to one of the mesh's installed modules is an edge
   `self → module assembly`, carrying the module's MVID as `TargetBuildMvid` — the same instrument
   as the platform and package edges (*Platform and package edges* below), for the same reasons.
   `Compilation.GetUsedAssemblyReferences()` is used only by a caller that hands no image. See
   *What the extraction costs* below for why it is not the default.
4. **Included but unused.** A foreign owner whose code was compiled in but never referenced from
   the compiling type's own code is listed in `IncludedWithoutUse`, not as an edge — the inclusion
   is a declared source query, the edge is what Roslyn bound. The candidates are read off the
   consumed FILES (roots and includes), so a foreign file that declares no type still counts.

What "binds" covers: every simple name, plus the bindings no name spells — a `foreach` enumerator
pattern (an extension `GetEnumerator`), an `await` awaiter, a deconstruction's `Deconstruct`, query
clause operators, a collection initializer's `Add`, a target-typed `new()`, user-defined unary,
binary and compound-assignment operators, an indexer (`Make()[0]`, `Make()?[0]`), the
`Dispose`/`DisposeAsync` a `using` calls on a value nobody names, a delegate invocation
(`Make()()`), the record clone a `with` expression calls, a `foreach`'s per-element conversion, and
a user-defined implicit conversion. A bound type counts together with every type its construction mentions (type
arguments, containing types' arguments, array elements), so an alias over an external generic
(`using AList = List<AThing>;`) is still an edge to `AThing`'s owner. A referenced type
is looked up from its innermost declaration outwards, so a foreign record spliced into the compiling
type's class (`Host+AThing`) stays the foreign owner's. A `using` directive is an import, not a use, and is skipped (hoisted directives
sit in the skeleton, outside every attributed type). An `@@` include spliced inside a namespace of
the including file is attributed under the name it carries there, by its span in that file.

Known limits, each failing towards an extra edge rather than a missing one:
- **A partial type whose parts two owners declare.** Its declaration is attributed to the compiling
  type. A reference inside the foreign part then reads as the compiling type's own.
- **Code outside any `Source/` or `Test/` segment** has no owner. A reference in it reads as the
  compiling type's own.
- **An include nested inside another include** is attributed by a separate parse of its own text, so
  when the outer snippet is spliced into a namespace, its inner snippet's types lose that namespace.
  A reference to them then reads as the compiling type's own.
- **Two owners supplying IDENTICAL include text.** An include is located in the root by its text, so
  both owners' texts match both copies. The declaration is attributed to every owner whose text
  covers it, and a reference to either copy is an edge to both — an extra edge, never the missing
  one the last-match-wins rule produced. When the compiling type is one of the suppliers, only the
  self owner is dropped from that set.

Telling these apart exactly needs each reference's file origin carried through source shaping. That
is a change to the emitted input, and it moves every content key.

The walk is skipped entirely when the compile consumed no foreign-owned code, so a self-contained
type pays two metadata-table reads and the dynamic scan's name filter, and nothing else. A node inside
a foreign owner's code is skipped BEFORE it is bound, so the walk binds only the compiling type's own
code.

### What the extraction costs

Edge extraction runs after EVERY NodeType compile — the portal's runtime compile, the batch bake and
the build-process bake (`mw-plugin-test compile`, which every gate shard runs) — so its price is paid
per type, per portal, per CI shard. The first version doubled the compile: on the MeshWeaver.Plugins
tree a gate shard's compile phase went from 530–554 s (platform set 3.1.11752/3.1.11754, before the
extraction) to 1631–1987 s (3.1.11792 and later, after it) for the SAME 131 and 165 NodeTypes. Two
costs dominated, measured locally over 30 compiled types (emit ≈ 6 s in total):

| phase | before | after |
|---|---|---|
| the `SemanticModel` walk | 5.4 s — it bound every node, foreign code included, and dropped foreign-located results afterwards | 2.4 s — the location is checked first, so only the compiling type's own code is bound |
| module edges | 3.4 s — `GetUsedAssemblyReferences()` re-binds and lowers every method body a second time | ≈ 0 — the emitted `AssemblyRef`s, already read for the platform edges |

The NodeType and platform/package/dynamic output is byte-identical before and after. The module
edges changed only where `GetUsedAssemblyReferences()` over-counted: a module the compile merely
looked into — its namespace imported, its extension methods considered — is no longer an edge of a
type whose emitted code references nothing in it (measured: MeshWeaver.Markdown.Collaboration, which
declares types in the `MeshWeaver.Graph` namespace, dropped out of 2 of 30 sets) — the same over-count the
platform edges were moved off for. What metadata cannot show — a constant (an enum member included)
inlined from a module, a `nameof` of a module symbol — IS still a module edge: a change to that
module changes or breaks the type's compile, and CI selection reads the module edge to know it. The
candidates are found by name (the constant names each not-yet-edged module declares, and every
`nameof`) and only those nodes are bound, so it never costs a second binding pass.

🚨 **Why not "a symbol whose containing assembly is another NodeType's assembly".** A NodeType
compile is never referenced against another NodeType's emitted assembly: its reference set is the
process baseline, the installed modules and `#r "nuget:"` packages
(`MeshNodeCompilationService.References`). Cross-type use is a `shared=@Other/Source` source query
that compiles the other type's code INTO this assembly, so the symbol's containing assembly is
always this one. Ownership therefore has to be read from where the declaration's SOURCE came from,
which is what step 1 does.

### Where it runs

| compile | extraction | written by |
|---|---|---|
| runtime NodeType compile (`MeshNodeCompilationService.EmitCompiled`) | after the emit, on the CPU lane; modules are the files of the SAME reference snapshot Roslyn compiled against, so a live module swap mid-compile cannot drop a used module | the compile settle, beside the terminal stamp, as the edge-set node |
| cold-start batch bake (`NodeTypeBatchBake.BakeOne`) | the same compile service | its own gated stamp (`WriteStamp`), the same writer, inside the same publication gate — and only when the stamp LANDED (a refused compare-and-set writes no edges over a newer build's) |
| build-process bake (`NodeSetCompiler.CompileWithModules`; the published `Compile` forwards with none) | after the emit; modules are the files the caller NAMES (`moduleAssemblyPaths`, from `BakeHost`) | carried on `CompiledNode.DependencyEdges`. `TreeBake` writes every attempted type's set to `dependency-edges.json` beside the bundles (`DependencyEdgesFile`), which `node-repo-publish-bake.yml` uploads as `dependency-edges-<identity>` for CI selection, AND into each bundle entry (`BundleWriter.AssemblyEntry.DependencyEdges`), so an adopting mesh can write it (below) |
| prebuilt ADOPTION (`ShippedPrebuiltBundles`, `PluginBundleClient` → `PrebuiltAssemblySeeder.WithAdoptedEdges`) | none — the mesh compiles nothing | the bundle's set for the bytes, via `DependencyEdgeSetWriter.WriteAdopted`: only for a COVERED outcome, only while the record's `LatestAssemblyMvid` equals the set's `BuildMvid`, under the durable record's version as the generation; offered to the `MeshPublicationGate` BEHIND the adoption stamp, so a stamp held while the process is provisional is released first and the edges follow it; idempotent across boots (`DescribesSameBuild`, which never skips a NEWER generation of the same build). An entry the image-bundle pass finds ALREADY CURRENT (it never reaches the seeding hook) still writes the edges its manifest carries, so a build adopted before its bundle carried edges is backfilled on the next pass |
| module workspace build (`tools/MeshWeaver.PluginTester/ProjectBuild`) | opt-in, `build-project --dependency-edges`: after each green emit, `GetUsedAssemblyReferences()` and the emitted image's metadata plus the dynamic scan, timed and printed as the phases `used assembly references` and `platform/package/dynamic edges` | `dependency-edges.json` in the output directory: one set per project, a `Module` edge to each IN-TREE project assembly the compilation used (a declared-but-unused `ProjectReference` is not an edge), plus its `Platform`, `Package` and dynamic edges. A `--prebuilt` sibling is an eligible target but gets no set of its own. The file is keyed by the host's compatibility key — the identity a bake writes — and a host that resolves none is refused before anything builds. Still opt-in — see *Module workspace builds* below for the measurement and the reason |

### The bake's edge file — the transport to CI (2026-10-27)

CI selection has no mesh to ask, so the build-process bake is where it gets the graph.
`dependency-edges.json` (`tools/MeshWeaver.PluginTester/DependencyEdgesFile.cs`, schema 1) holds
`frameworkIdentity`, `commit`, `sets` (one `DependencyEdgeSet` per type that compiled, each with
`commit` and `buildMvid`) and `failed` (types the bake attempted that produced no set). Every
attempted type appears exactly once; a type the bake did not attempt — a narrowed bake, a type with
nothing to compile — is absent and must be read as UNKNOWN. An empty `edges` list means "extracted,
depends on nothing". A reader refuses a schema it does not know.

The publish-bake lane uploads the file as the run artifact `dependency-edges-<framework identity>`
(30 days, never part of the publication). A missing file only warns; a failed upload is RED, like
every artifact transfer (a transfer failure is never swallowed), and the step runs before the
publication lock, so a red there has published nothing. A narrowed bake writes only what it
compiled, so a reader merges runs by source, newest first. The reader and its rules (currency
against the tree, unknown widens) are MeshWeaver.Plugins `scripts/dependency-graph.py` and
`Hosting/DependencyGraphSelection.md`.

## The three evidence classes

"Who breaks if X moves" and "what can leave core" need three different kinds of fact, and the edge
says which one it is (`DependencyEdge.EvidenceClass`, `DependencyEvidenceClasses`):

- **Static** — Roslyn BOUND it: a symbol reference (`NodeType`), a used module (`Module`), an
  `AssemblyRef` the compiler wrote into the emitted image (`Platform`, `Package`). Breaks at
  COMPILE time when the target moves.
- **Dynamic** — a reflection call names it by STRING (`Dynamic`, `DynamicUnresolved`). Compiles
  fine after the target moves and breaks at RUN time — the reason it needs its own ratchet.
- **Declared** — a declaration states it: a package manifest's `requires` and `module`, a
  `#r "nuget:"` directive, a `.csproj` `ProjectReference`/`PackageReference`. Never an edge in the
  file; the reconciliation report compares it with the other two.

A set written before `EvidenceClass` existed reads as Static, which is what every edge of that era
was.

### Platform and package edges

The extractor used to drop platform (TPA) assemblies and NuGet packages, so nothing recorded which
in-mesh code binds which core assembly — exactly what a core carve-out needs. Both are now edges,
read off the EMITTED assembly (`DependencyEdgeExtraction.EmittedReferenceEdges`): every
`AssemblyRef` the compiler wrote, classified by the compile's reference FILES (module / package /
host — `DependencyReferenceClasses`), with the `TypeRef`s resolved through it as evidence.

🚨 **Why the emitted metadata, and neither of the two obvious sources.**
`GetUsedAssemblyReferences()` counts an assembly whose namespace a `using` merely imports, and every
NodeType compile carries the skeleton's global usings: measured on core's doc tree, every type
reported the SAME 21 MeshWeaver assemblies — "who binds `MeshWeaver.Graph`" answered with
"everyone". A semantic walk that re-binds every name gives the right set, but measured on a 90-file
module it cost 792 ms against 621 ms for the emit itself, and the runtime compile runs on every
portal. The C# compiler writes an `AssemblyRef` only for an assembly the IL or metadata needs, so two
metadata tables give the bound set for about 50 ms. With it core's two trees report 3–8 platform
assemblies per type, differing by type. What metadata cannot show — a constant inlined from a
platform type, a `nameof` — is not an edge. A NodeType's emitted image also carries the foreign
owners' code compiled in through `shared=`/`@@`, so a platform assembly only that code binds is
still this type's edge: an extra edge, never a missing one, the direction every limit here fails in.
Each emitted `AssemblyRef` is matched to the compilation's references by FULL identity — name,
version, culture, public key (token) — because a host assembly and a package assembly can share a
simple name with different publishers and both stay referenced; when no reference matches the full
identity, every candidate of that name is classified (an extra edge, never a lost one).

**Why the BCL is not an edge.** Only `MeshWeaver` and `MeshWeaver.*` host assemblies are recorded.
The base class library and the host's third-party libraries are on every host, at the version the
image pins; nothing carves out of them, and no change in this tree moves them. Recording them would
add a dozen rows per source to the closure and answer no question anybody asks. A `#r "nuget:"`
package is different — the source chose it, so it IS an edge (`Package`).

### Dynamic edges

`DynamicBindingExtraction` scans the source's OWN code (the same ownership rule as the NodeType walk)
for the APIs that resolve a type or an assembly by name: `Type.GetType`, `Assembly.GetType`,
`Module.GetType`, `Assembly.CreateInstance`, `Activator.CreateInstance(string, string…)` and
`CreateInstanceFrom`, `AppDomain.CreateInstance*`, `Assembly.Load` / `LoadFrom` / `LoadFile` /
`LoadWithPartialName` / `UnsafeLoadFrom`, `AssemblyLoadContext.LoadFromAssemblyName` /
`LoadFromAssemblyPath` / `LoadFromNativeImagePath` / `LoadFromStream`, and the mesh's own
name-to-type surface, `ITypeRegistry.GetType` / `TryGetType` (any implementation). A call is matched
by its BOUND method, so `using static` cannot hide it; overloads that take a `Type`
(`Activator.CreateInstance(typeof(T))`) and `object.GetType()` are static and not matched. A method
group handed along (`names.Select(Type.GetType)`) is unresolved.

- **Constant argument** — a literal, a `const`, a constant interpolation, `new AssemblyName("…")`
  over one — is a `Dynamic` edge. The real shape that motivated it, MeshWeaver.Plugins
  `AppleMaps/Maps/Source/AmapsConnect.cs`, does
  `Type.GetType("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph")` and becomes the edge
  `MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph` with `TargetKind: Platform` and the call
  site as evidence (`Type.GetType("…") in Connect`). An unqualified type name is resolved against the
  compilation (`GetTypeByMetadataName`); `typeof(T).Assembly.GetType("…")` takes `T`'s assembly. A
  closed generic needs EVERY argument's assembly, so its arguments decide too:
  `List`1[[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib` is
  `Platform`, not `Framework`; an argument whose assembly the compile cannot classify makes the target
  unknown.
- **Conservative by construction — THE RULE.** Reflection's type-name grammar and the shapes a
  lookup can take are open-ended, so classifying every one exactly would never converge. The scanner
  is complete in the other direction: a call whose bound method is a reflection lookup — the API
  itself, an OVERRIDE of it (matched through the override chain), or an implementation of the
  registry surface — is RESOLVED only when its behaviour is the API's own (not an override, not an
  implementation) AND every input that decides its result is proven: the receiver it searches
  (`typeof(X).Assembly`/`.Module` of a named type, `AssemblyLoadContext.Default`,
  `AppDomain.CurrentDomain`), every resolver delegate provably `null`, and every other argument a
  compile-time constant. Anything short of that is `DynamicUnresolved`, naming the unproven input. A
  subclass overriding `Assembly.GetType(string, bool, bool)` and `Type.GetType(name, null, resolver)`
  are two instances of that one rule, not two special cases. A resolved lookup is then classified
  just as conservatively: The name is
  parsed with a strict grammar (`DynamicBindingExtraction.TryParseTypeName` — mixed qualified and
  bare generic arguments, array/by-ref/pointer suffixes); a name it refuses, any part whose assembly
  the compile does not reference (only `mscorlib`, `netstandard`, `System.Private.CoreLib` and
  `System.Runtime` are known without a reference), and a lookup in an assembly the compile cannot
  know (any `Assembly` value, `typeof(T).Assembly` of a type parameter) make the target UNKNOWN —
  `TargetKind` null, counted into core exactly like `DynamicUnresolved`. A new shape the scanner
  does not understand therefore costs a baseline line, never a silent pass.
- **Matched by position where names can vary.** `ITypeRegistry.GetType`/`TryGetType` are matched by
  name and their leading `string` parameter, never by the parameter's name, so an implementation
  that renames it stays on the ratchet — as `DynamicUnresolved`, since a registry's contents are
  never knowable at compile time.
- **What is scanned** is the source's own code AND the trees a NODE-SUPPLIED source generator (one
  the node `#r`'d) emitted — the author's code, as the impersonation scan already treats it; the
  platform's built-in generators' output is left out. Node-generated trees are owned by the
  compiling type by PROVENANCE (both compile callers pass them separately), even where they extend
  a partial type a foreign owner declares: no other compile ever sees that code.
- **Anything else** is `DynamicUnresolved`, one edge per API (`?System.Type.GetType`), the call sites
  as evidence. Unknown is recorded as unknown — never dropped.
- **Cost**: a syntactic prefilter on the invoked name means only candidate invocations are ever
  bound; a compile with no reflection binds nothing.

The `$type` polymorphic converter resolves type names that live in DATA (node content), not in code,
so no compile can see it; that, and the registry names a `TryGetType` receives at run time, are the
subject of phase 3 below.

### Old edge sets lack the new kinds — read that as unknown

An edge set or a `dependency-edges.json` written by an extractor that predates these kinds (an old
bundle, an older pinned tester, an edge-set node a portal wrote before it rolled) simply has no
`Platform`, `Package` or dynamic edges. A reader must treat their absence as **unknown, never as
"binds no platform assembly" or "uses no reflection"**. The tell: every NodeType binds the platform
through its skeleton (`MeshNodeProviderAttribute` lives in `MeshWeaver.Mesh.Contract`), so a NodeType
set with ZERO `Platform` edges was written before the kind existed. The change itself is additive:
new constants, new init-only members with defaults (`EvidenceClass` defaults to `Static`), new
overloads — every published signature of `Extract`, `ModuleReferences` and the edge records stays,
so it is binary-compatible under the platform compatibility ladder and moves no compatibility key.

**The file says what it measured.** `dependency-edges.json` carries `measured` — `referenced-assemblies`,
`dynamic-bindings`, and for a module build `declarations` (`DependencyEdgesFile.Measures`). A reader
judges a kind only when its measurement is listed; a schema-1 file without the field measured none of
them. The rule everywhere: **an unmeasured input reads as UNVERIFIABLE, never as a pass.**

## The reconciliation report

`mw-plugin-test dependency-report <dependency-edges.json> --tree <checkout>` — and, with no extra
step, every `compile` (the bake) — writes `dependency-report.json` beside the edges
(`DependencyReconciliation`). Per package (a node-repo top-level folder, for NodeType sources) or per
module (a project, for a module workspace build's sources):

| flag | NodeType package | module project |
|---|---|---|
| **used-but-undeclared** | a `NodeType` edge into a package outside its declared closure (`requires`, source queries, node types — `LocalNodeRepo.DependencyMap`, transitively); a `Module` edge no package in that closure declares as its `module`; a `Package` edge no `#r "nuget:"` names | a `Module` edge that is not a direct `ProjectReference` (a transitive use); a `Platform` edge no `PackageReference`/`ProjectReference` names; a `Package` edge no `PackageReference` names |
| **declared-but-unused** | never — see THE RULE below: each of `requires`, `module` and `#r "nuget:"` has a use channel no compile measures, so an unmatched one is unverifiable | a `ProjectReference` no `Module`/`Platform`/`Package` edge matches, when compiled code AND by-name loading were both measured clean |
| **dynamic into core** | every `Dynamic` edge whose `TargetKind` is `Platform` or unknown, and every `DynamicUnresolved` edge | the same |

Plus, per unit, the platform assemblies its sources bind and how many sources bind each — the
carve-out question's direct answer — and everything it could NOT judge, each named:

- a `requires` outside the tree;
- every unmatched declaration of a package with a source that was NOT extracted (that source could
  be the user — `notExtracted` lists it); "declared but unused" is a claim about every source;
- a kind the producer did not measure (`measured` lacks it);
- a module whose producer recorded no declarations. Module declarations are the PRODUCING build's
  evaluated ones (`ProjectFile.Load`: imports, conditions, property expansion), carried in the file
  as `declarations` — the report never re-reads raw `.csproj` XML. A used `ProjectReference` outside
  the source root resolves from the host and appears as a `Platform` edge, which counts as a use.

`#r "nuget:"` directives are read with `NuGetDirectiveParser` from the DECODED code — a `.json` Code
node's `content.code` string, not its escaped JSON text. A module-libraries shelf is a flat
directory, so a shelf `Package` edge is named by the shelf's deps.json ownership
(`ModuleLibrariesShelf.OwnerOf`), never by the DLL's file name. It is a report, never a gate.
Measured on core's trees when it landed: 0 used-but-undeclared, 0 declared-but-unused, 0 dynamic
edges.

**THE RULE for declared-vs-used** (the reconciliation's counterpart of the scanner's rule): a
declaration is "unused" ONLY when the extractor PROVABLY saw every way it can be used; otherwise it
is UNVERIFIABLE, naming the first channel nobody measured. Each kind lists its channels
(`DependencyReconciliation`):

| declaration | measured | not measured — so never provably unused |
|---|---|---|
| package `requires` | compiled code (NodeType, Module edges) | runtime use of the package's node types, data and layout areas |
| package `module` | compiled code (Module edges) | runtime hosting of the module with its package |
| `#r "nuget:"` | bound assemblies (Package edges) | host unification (the host's copy keeps no package provenance); source generation (no AssemblyRef) |
| `ProjectReference` | compiled code; by-name loading (clean only when the project has no dynamic edge) | — |
| `PackageReference` | compiled code (Platform or Package edge — a shelf `MeshWeaver.*` package is a Package edge) | analyzers and source generators the package ships |

So only a `ProjectReference` can ever be reported unused; every other unmatched declaration is
listed as unverifiable with its reason. A use is ANY edge naming it, including a `package:` edge.

## The ratchet

The one gate: a shrink-only allow-file of the dynamic edges INTO CORE, in the shape of
`--warning-baseline` (`Doc/Architecture/InMeshWarningStandard`). `compile --dynamic-baseline <file>`
(or `dependency-report --dynamic-baseline <file>`); lines are `<source> <Dynamic|DynamicUnresolved>
<target>`, the target being the rest of the line.

- An observed dynamic edge into core that is not listed is **NEW** and fails.
- A listed edge whose source compiled this run without it is **STALE** and fails — the line goes.
- A listed edge whose source was not extracted this run (failed, not attempted) is
  **unverifiable**, warned and kept: silence is not evidence.
- No `--dynamic-baseline` is **OBSERVE-ONLY**, printed by name in every run
  (`dependencies: dynamic-edge ratchet — OBSERVE-ONLY: <repo> has not adopted it …`) with the lines
  to paste; a path that was given and is missing is a refusal; an EMPTY file tolerates nothing.
- An edges file that does not record `dynamic-bindings` was never scanned: the ratchet prints
  `UNVERIFIABLE`, keeps every entry as unverifiable (never stale), and an armed baseline is RED —
  an empty baseline over an unscanned file is not a clean pass.

**Core** baselines its own two trees — `.github/doc-gate-dynamic.allow` and
`.github/samples-gate-dynamic.allow`, both empty (0 dynamic edges measured) — and
`bake-then-gate.sh` asserts the `ENFORCED` line, so a dropped argument is red, not quietly
observe-only. **Satellites** adopt with the `dynamic-baseline-file:` input on BOTH
`node-repo-gate.yml` and `node-repo-publish-bake.yml`; until then the lanes pass no flag (a pinned
tester that predates it would refuse an unknown option) and print a `::notice::` naming the
repository as observe-only. The publish-bake lane uploads `dependency-report.json` in the same
`dependency-edges-<identity>` artifact as the edges. There is no skip path: an armed baseline with no
edges file is red.

## Module workspace builds: still opt-in, now cheap

`build-project --dependency-edges` now also writes `Platform`, `Package` (shelf packages) and dynamic
edges, beside the in-tree `Module` ones (`ProjectResult.OtherEdges`). Measured on a 90-file module
(MeshWeaver.Plugins `MeshWeaver.Import`) against core's tester host: emit 375 ms, `used assembly
references` 58 ms, `platform/package/dynamic edges` 53 ms — about 30 % of the emit and under 10 % of
the project's build. The cost no longer argues for keeping it off; the reason it stays OPT-IN is that
`--dependency-edges` keys its file by the host's compatibility key and REFUSES a host that resolves
none, so flipping the default would turn every existing `build-project` call against such a host
into a refusal. The module-pack lane turning it on (MeshWeaver.Plugins) is the follow-up, not a
default flip here.

## The Postgres projection

MeshWeaver.Plugins `DependencyGraphSchema` projects every edge of a canonical `DependencyEdgeSet`
row into `public.dependency_edge(source, target, kind, build_identity)` with PK `(source, target)`,
`DISTINCT ON (target)` and `kind TEXT` — it filters no kind and assumes nothing about a target's
shape (`Module` targets were already assembly names). The new kinds therefore project unchanged, and
because a set's targets are unique by construction, `DISTINCT ON (target)` never has to choose
between two kinds. A non-path target (an assembly name, `{type}, {assembly}`, `?{api}`) becomes a
SINK node of the closure — it has no outgoing edges — so `dependents:MeshWeaver.Graph` lists every
source that binds it. Open: platform edges multiply the closure by roughly the number of platform
assemblies per source; whether the closure should keep them or the projection should restrict the
closure to `NodeType`/`Module` is a decision for the projection's owner, once the volume is measured
on a real portal.

## Phase 3 (follow-up): runtime observation

Compile-time extraction cannot see a type name that arrives at RUN time — the `$type` discriminator
in node content resolved by the polymorphic converter, a registry name passed into `TryGetType`, an
assembly-qualified string read from configuration. The next step is to observe type-name resolution
per replica (the converter, `ITypeRegistry`, the load contexts), record each `(caller, name,
resolved assembly)` as a `Dynamic` edge with an `Observed` evidence class, and fold it into the same
report — turning today's `DynamicUnresolved` edges into resolved ones where the running mesh has
seen them resolve. Not built.

## The reverse closure (step 3)

The query surface is the only reader: `dependents:<path>` (everything that depends on `<path>`,
transitively) and `dependencies:<path>` (everything `<path>` depends on). Each answer row is
`(node, depth, path)` — `path` the chain from the start to that node, so a caller can see WHY.

### Postgres (MeshWeaver.Plugins, `PostgreSqlSchemaInitializer`)

- **`public.dependency_edge(source, target, kind, build_identity)`**, PK `(source, target)`.
  A row trigger on each partition's `mesh_nodes` projects every CANONICAL `DependencyEdgeSet`
  row: the row must be `{source}/_Dependency/Forward`, and a row claiming another source projects
  nothing. The source's rows become exactly the set's edges, in the node write's own transaction.
  `mw_note_node_types_for` installs the trigger per partition (a new partition by its table
  script, an existing one by the boot sweep, with a one-time backfill). A dropped partition calls
  `mw_dependency_forget_partition` first, because `DROP SCHEMA … CASCADE` fires no row triggers.
- **`public.dependency_closure(ancestor, descendant, depth, next_hop, build_identity)`**, PK
  `(ancestor, descendant)`, indexes on `(descendant)` and `(ancestor, depth)`. The ANCESTOR is the
  dependency (the parent), the DESCENDANT depends on it (the child), so "all children of X" — every
  dependent — is the one indexed lookup `WHERE ancestor = X ORDER BY depth`, and "what X depends on"
  is `WHERE descendant = X`. An edge `s → t` (s depends on t) is the row `(t, s, 1, t)`. `next_hop`
  is the first step from the DESCENDANT towards the ancestor (the node it directly depends on along
  the chosen shortest chain), so `dependency_path(a, d)` walks `d → next_hop(a, d) → … → a`. Among
  chains of equal length the smallest `next_hop` (ordinal) is chosen, so the incremental and the
  full computation agree row for row.
- **One row trigger on `dependency_edge`, DEFERRED to commit** (`DEFERRABLE INITIALLY DEFERRED`).
  Every closure change happens under one advisory lock, so two compiles committing at once cannot
  each extend the closure without the other's edge. That lock is taken at commit, when the
  transaction already holds every row lock it will take. A lock taken mid-write let a batch and a
  concurrent writer each hold a row the other waited for. What the trigger does per event:
  - **An inserted edge `s → t`** gives every `A ∈ {t} ∪ (what t depends on)` and every
    `D ∈ {s} ∪ (what depends on s)` the row `(A, D)`. Its depth is
    `depth(A, t) + 1 + depth(s, D)`; its next hop is `t` when `D = s`, else `next_hop(s, D)`.
    `ON CONFLICT` keeps the smaller `(depth, next_hop)`.
  - **A dropped or re-pointed edge** triggers `rebuild_dependency_closure()`, once per transaction
    and from its final edges. Insert-only maintenance cannot shrink a closure, and a whole-table
    rebuild costs about 100 ms at the design size.
  - **A restamp** (same edges, new build) moves only `build_identity`.
  - **Self-pairs** are never stored: a node is not its own dependent, and a cycle shows as the rows
    `(a, b)` and `(b, a)`.
- **Readers**: `dependents(target, max_depth)`, `dependencies(source, max_depth)` and
  `dependency_path(from, to)` — STABLE SQL functions over the closure table, called only by the
  query provider through `IIoPool`.
- No materialized view: the closure table already answers "all dependents of X" as one indexed
  lookup, and nothing measured asks for a second structure.

### In memory (core, `DependencyClosure`)

`MeshWeaver.Mesh.DependencyClosure` (`Mesh.Contract/Query`) answers the same `(node, depth, path)`
rows from a set of forward edges: breadth-first distances, and the same next-hop rule (shortest
chain, then the smallest next hop under ordinal comparison), with every path rebuilt by following
the ancestor's next hops, as `dependency_path` does. It is pure and immutable, so an instance built
from one edge set is held by a mesh-scoped service (never a static) and replaced when an edge set
changes.

### Parity

One corpus both providers are held to: `test/MeshWeaver.Fixture/DependencyClosureCorpus`. It holds
the cycle `a → b → c → a` plus the leaf `d → a`, and a diamond with two equally short chains, and
asserts membership, depth AND path. Core's `DependencyClosureParityTest` runs every case in memory.
Plugins' `DependencyClosureTests` reads the same answers from `public.dependents` /
`public.dependencies`, and on Postgres also asserts that incremental maintenance equals
`rebuild_dependency_closure()`:

- after a chain is inserted edge by edge;
- after a cycle is created and then broken;
- when one compile's edges arrive in one statement versus one at a time;
- after every 10th step of 120 random inserts and deletes.

### What is not wired yet: the query terms

`dependents:<path>` / `dependencies:<path>` are not parsed yet. A query answers `MeshNode`s, and a
`MeshNode` has no slot for a row's `depth` and `path`. So the term needs a decision first: it either
returns the dependent nodes ordered by depth, with the chain only through a separate read, or it
returns closure rows as their own content type. Whichever it is, the Postgres side filters each row
by the reader's access to the node: the closure table spans every partition, the edge sets do not.

## Open questions

- **Partition of the edge set.** It lives under the NodeType, in the NodeType's partition, so a
  reader sees the edges of exactly the types it can read. The Postgres closure is one table across
  partitions; `dependents:` must therefore filter its answer by the reader's access to each node.
- **Retention of stale build identities.** The set is replaced whole on every compile and keeps no
  history, so only the current build's edges exist. If CI needs "the edges of build X" after X was
  superseded, history has to be kept — decide before step (4).
- **Adopted prebuilts — resolved: the bundle carries the edges and adoption writes them.**
  A portal that adopts a bake's bytes compiles nothing, so without the bundle's edges it has no
  edge set for that build: measured on two production meshes after the roll that shipped extraction, 0 edge sets,
  every type adopted (`buildProvenance: AdoptedVerified`). The bake now writes each assembly's set
  into its bundle entry, and the seeding pass writes it for the build the record serves. A bundle
  from an older producer carries none, and that type stays "never extracted" until its next
  compile or a newer bundle.
- **Included but unused.** Whether a `shared=` inclusion the type never binds to should count as a
  dependency for CI selection (a change there can still break this type's compile).
