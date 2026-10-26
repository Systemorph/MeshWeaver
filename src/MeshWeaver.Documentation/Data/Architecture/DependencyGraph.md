---
nodeType: Markdown
name: Dependency Graph
category: Architecture
description: Forward dependency edges computed by Roslyn at the end of every NodeType compile, stored once per environment, and the reverse closure ("who depends on X") answered through the query surface — a closure table maintained by triggers on Postgres, and the same answer walked in memory.
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
| content | `DependencyEdgeSet` — `Source`, `BuildMvid`, `InputDigest`, `Commit`, `Edges`, `IncludedWithoutUse` |
| one edge | `DependencyEdge` — `Target`, `Kind`, `TargetBuildMvid`, `Evidence` |

`Kind` is an open vocabulary (`DependencyEdgeKinds`): `NodeType` and `Module` today.

- **One node per source, replaced whole.** A recompile writes the complete set in one
  `CreateOrUpdateNode`, so a dropped edge disappears in the same write that adds the new ones —
  never a half-state, and never a query to find which edges to delete.
- **Build identity on the set.** `BuildMvid` is the MVID of the emitted assembly the edges were
  read from (the same value as `NodeTypeDefinition.LatestAssemblyMvid` after the stamp), and
  `InputDigest` the generated-input digest of that compile. A consumer that finds a set whose
  `BuildMvid` is not the type's current build knows the edges are stale — the check step (4)
  needs.
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
3. **Module edges — `Compilation.GetUsedAssemblyReferences()`.** Every used reference that is one
   of the mesh's installed modules is an edge `self → module assembly`, carrying the module's MVID
   as `TargetBuildMvid`. Platform (TPA) assemblies and NuGet packages are not edges.
4. **Included but unused.** A foreign owner whose code was compiled in but never referenced from
   the compiling type's own code is listed in `IncludedWithoutUse`, not as an edge — the inclusion
   is a declared source query, the edge is what Roslyn bound.

The walk is skipped entirely when the compile consumed no foreign-owned code, so a self-contained
type pays one `GetUsedAssemblyReferences` call and nothing else.

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
| runtime NodeType compile (`MeshNodeCompilationService.EmitCompiled`) | after the emit, on the CPU lane | the compile settle, beside the terminal stamp, as the edge-set node |
| build-process bake (`NodeSetCompiler.Compile`) | after the emit | carried on `CompiledNode.DependencyEdges` for the bundle — writing it into a mesh at adoption is a follow-up |

## The reverse closure (step 3)

The query surface is the only reader: `dependents:<path>` (everything that depends on `<path>`,
transitively) and `dependencies:<path>` (everything `<path>` depends on). Each answer row is
`(node, depth, path)` — `path` the chain from the start to that node, so a caller can see WHY.

### Postgres (MeshWeaver.Plugins, `PostgreSqlSchemaInitializer`)

- **`public.dependency_edge(source, target, build_identity)`**, PK `(source, target)`. Kept by a
  trigger on each partition's `mesh_nodes` that projects a `DependencyEdgeSet` row's edges: delete
  the source's rows and insert the new set in the same statement, so one compile is one
  transaction.
- **`public.dependency_closure(ancestor, descendant, depth, next_hop, build_identity)`**, PK
  `(ancestor, descendant)`, indexes on `(descendant)` and `(ancestor, depth)`. `ancestor` depends on
  `descendant` (`ancestor → … → descendant`); `next_hop` is the first step from `ancestor` towards
  `descendant`, so a chain is rebuilt by following `next_hop` from the start.
- **AFTER INSERT** on `dependency_edge` (statement level, transition table): for every new edge
  `s → t`, insert `{ancestors of s, s} × {t, descendants of t}` with `depth = d1 + 1 + d2`, keeping
  the minimum depth and its `next_hop` (`ON CONFLICT … DO UPDATE … WHERE excluded.depth < depth`).
  A self-pair from a cycle is not stored: a node is never its own dependent, and the cycle stays
  visible as the pair of rows `(a, b)` and `(b, a)`.
- **AFTER DELETE / UPDATE**: insert-only maintenance cannot shrink a closure, so the affected rows
  are rebuilt — `rebuild_dependency_closure()` over the whole table, which at this size (hundreds of
  packages, about a thousand NodeTypes, at most ~10⁵ closure rows) is measured before anything
  narrower is written.
- **Readers**: `dependents(target, max_depth)`, `dependencies(source, max_depth)` and
  `dependency_path(from, to)` — STABLE SQL functions over the closure table, called only by the
  query provider through `IIoPool`.
- No materialized view: the closure table already answers "all dependents of X" as one indexed
  lookup, and nothing measured asks for a second structure.

### In memory (core, `QueryEvaluator`)

The same `(node, depth, path)` answer computed by a breadth-first walk over the forward edge sets,
reversed for `dependents:`, held in a mesh-scoped instance cache (never static), invalidated when an
edge set changes.

### Parity

One fixture both providers pass (Query Provider Parity): a cycle `A → B → C → A` plus a leaf `D`,
asserting membership, depth AND path; and, on Postgres, incremental maintenance equals
`rebuild_dependency_closure()` after a chain inserted edge by edge, after a cycle is created and then
broken, and after one compile's edges arrive in one statement versus one at a time.

## Open questions

- **Partition of the edge set.** It lives under the NodeType, in the NodeType's partition, so a
  reader sees the edges of exactly the types it can read. The Postgres closure is one table across
  partitions; `dependents:` must therefore filter its answer by the reader's access to each node.
- **Retention of stale build identities.** The set is replaced whole on every compile and keeps no
  history, so only the current build's edges exist. If CI needs "the edges of build X" after X was
  superseded, history has to be kept — decide before step (4).
- **Adopted prebuilts.** A portal that adopts a bake's bytes compiles nothing, so it has no edge set
  for that build. Either the bundle carries `CompiledNode.DependencyEdges` and adoption writes it,
  or step (4)'s check names those types RED. The design says RED and named; carrying the bake's
  edges would make it green.
- **Included but unused.** Whether a `shared=` inclusion the type never binds to should count as a
  dependency for CI selection (a change there can still break this type's compile).
