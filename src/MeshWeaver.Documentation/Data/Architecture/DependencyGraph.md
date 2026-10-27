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
3. **Module edges — `Compilation.GetUsedAssemblyReferences()`.** Every used reference that is one
   of the mesh's installed modules is an edge `self → module assembly`, carrying the module's MVID
   as `TargetBuildMvid`. Platform (TPA) assemblies and NuGet packages are not edges.
4. **Included but unused.** A foreign owner whose code was compiled in but never referenced from
   the compiling type's own code is listed in `IncludedWithoutUse`, not as an edge — the inclusion
   is a declared source query, the edge is what Roslyn bound. The candidates are read off the
   consumed FILES (roots and includes), so a foreign file that declares no type still counts.

What "binds" covers: every simple name, plus the bindings no name spells — a `foreach` enumerator
pattern (an extension `GetEnumerator`), an `await` awaiter, a deconstruction's `Deconstruct`, query
clause operators, a collection initializer's `Add`, a target-typed `new()`, user-defined unary,
binary and compound-assignment operators, and a user-defined implicit conversion. A referenced type
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

Telling these apart exactly needs each reference's file origin carried through source shaping. That
is a change to the emitted input, and it moves every content key.

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
| cold-start batch bake (`NodeTypeBatchBake.BakeOne`) | the same compile service | its own gated stamp (`WriteStamp`), the same writer, inside the same publication gate — and only when the stamp LANDED (a refused compare-and-set writes no edges over a newer build's) |
| build-process bake (`NodeSetCompiler.Compile`) | after the emit; modules are the files the caller NAMES (`moduleAssemblyPaths`, from `BakeHost`) | carried on `CompiledNode.DependencyEdges` for the bundle — writing it into a mesh at adoption is a follow-up |
| module workspace build (`tools/MeshWeaver.PluginTester/ProjectBuild`) | not yet | follow-up: it runs in CI with no mesh, so its edges need a transport (the bundle, then the registry) that belongs with step (4); and its one-body-pass discipline means `GetUsedAssemblyReferences`' extra binding cost is measured there before it is added |

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
- **Adopted prebuilts.** A portal that adopts a bake's bytes compiles nothing, so it has no edge set
  for that build. Either the bundle carries `CompiledNode.DependencyEdges` and adoption writes it,
  or step (4)'s check names those types RED. The design says RED and named; carrying the bake's
  edges would make it green.
- **Included but unused.** Whether a `shared=` inclusion the type never binds to should count as a
  dependency for CI selection (a change there can still break this type's compile).
