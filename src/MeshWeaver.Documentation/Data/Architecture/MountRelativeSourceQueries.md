---
Name: Mount-Relative Source Queries
Category: Architecture
Description: A NodeType's cross-type source reference is authored mount-relative, so the resolver asks for the mount-anchored spelling too — the defect that made present content report itself as broken C#, the rule it shares with @@ includes, and the four CS0246 shapes a reader has to tell apart.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M8 12h8"/><path d="M12 8v8"/></svg>
---

# Mount-Relative Source Queries

A NodeType names the Code nodes it compiles with a list of queries
(`NodeTypeDefinition.Sources` / `.Tests`, expanded by `CodeQueryResolver`). Two spellings behave
very differently, and the difference only shows up once the content is served from somewhere other
than where it was authored:

| Entry | Resolves to | Survives a mount prefix? |
|---|---|---|
| `namespace:Source scope:subtree` (the default) | `{$self}/Source` — **rebased on the owning type's path** | **Yes** — `$self` already carries the prefix |
| `shared=@Other/Type/Source` | `path:Other/Type/Source` + `namespace:Other/Type/Source scope:subtree` | **Not on its own** — the value is absolute |

> **A cross-type source reference is authored MOUNT-RELATIVE, exactly like an `@@` include.** The
> resolver therefore emits the **mount-anchored** spelling alongside the authored one, and the node
> is found from whichever mount it is served.

## The defect this closes

`samples/Graph/Data/Northwind/Product.json` declares:

```json
"sources": [
  "namespace:Source scope:subtree",
  "shared=@Northwind/AnalyticsCatalog/Source/Supplier",
  "shared=@Northwind/AnalyticsCatalog/Source/Category"
]
```

Correct at a root mount — in the Monolith the type *is* `Northwind/Product`. In a
statically-imported partition the same nodes are served under a prefix, so the type is
`MeshWeaver/samples/Graph/Data/Northwind/Product` and the sibling entity sources are at
`MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/…`. Resolved verbatim, the two
`shared=` entries matched **nothing**.

🚨 **And nothing said so.** The type's own `namespace:Source scope:subtree` *is* rebased and did
match its two files, so the merged source set was non-empty — which means neither of the two
mechanisms that exist to catch a short set could fire:

- `SourceSnapshot` measures emptiness on the **merged** set (see
  [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation)), so a non-empty union is
  "established".
- `PreWarmStatus.NoSources` needs the same emptiness.

Roslyn was handed a set short of the sibling entity sources and reported exactly what it saw:

```
Compilation failed for 'MeshWeaver/samples/Graph/Data/Northwind/Product':
CS0246 Error (line 45): The type or namespace name 'Supplier' could not be found …
CS0246 Error (line 49): The type or namespace name 'Category' could not be found …
--- Source discovery ---
Matched Code nodes (2)
```

A completely genuine-looking verdict about code that is fine, on a mesh whose content is entirely
present. Measured on memex-cloud 2026-09-19/20 (issue #4813): every pod that attempted the compile
failed the same way, and it does not self-heal — there is nothing to wait for.

## The rule, and why it is *one* rule

`CodeQueryResolver.AnchorToMount` rewrites the `path:` / `namespace:` value of an expanded query by
calling **`NodeCompileShaping.AnchorIncludePath`** — the function that already closed the identical
failure for `@@` include paths, where an unresolved include is left verbatim in the source so Roslyn
parses the `@@` line and reports on path segments as though they were symbols.

Calling that function rather than writing a second copy of the rule is deliberate: **a source query
and an `@@` include that name the same node must not disagree about where it lives.** The anchor is
the *deepest* occurrence of the value's first segment in the owning type's path — the most local
reading — so `Northwind/AnalyticsCatalog/Source/Supplier` read from
`MeshWeaver/samples/Graph/Data/Northwind/Product` anchors to
`MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog/Source/Supplier`.

Because `Expand` is the single funnel every consumer goes through — the runtime compile
(`NodeSources.GetSources`), the tree bake (`NodeSet.ResolveSources`), the Configuration side menu,
and `SourceCoverage` — producer and consumer cannot fork on which files count. That invariant is
what `NodeTypeSourceFingerprint` depends on, so the fix had to land there and nowhere else.

### The authored spelling is kept, never replaced

Both queries are emitted. Which one resolves is a property of the **mount**, not of the entry — the
same reason `ResolveCodeIncludes` tries the anchored include path first and keeps the authored one as
a fallback. A query that matches nothing costs a query and changes no result.

### What is deliberately NOT anchored

Anchoring is a no-op unless the value's first segment appears in the owning type's path **below the
root**, which excludes exactly the cases where a rewrite would do damage:

- **A root-mounted type** — the Monolith and every unprefixed deployment keep the queries they had.
  This is the control on the other side of the change.
- **A genuinely cross-partition reference** — `shared=@Store/Core/Source` read from
  `rbuergi/OperationRequest` has no `Store` segment to anchor to, so it stays verbatim. Rewriting it
  would turn a working reference into a missing one.
- **An already-absolute reference** — its first segment *is* the mount root, which sits at index 0
  and is excluded from the walk, so nothing is ever double-prefixed.

A group whose entries anchor keeps a usable `CodeQueryGroup.BaseNamespace`: a root and its anchored
form are the same root resolved two ways, so the source listing still shows files relative to it
rather than falling back to full paths.

## 🚨 Four reasons a NodeType reports `CS0246`, and they need different answers

A `CS0246` on an in-mesh type is not one defect. Reading the **source-discovery block** under the
diagnostics is what tells them apart, and the wrong reading sends the reader to module surfaces that
never carried the symbol.

| What the discovery block says | What it is | The answer |
|---|---|---|
| `Matched Code nodes (0)` | The source set is **gone** — a retired type whose sources were pruned, or an orphan the sync never removed | Finish the retirement: [Retiring a NodeType](/Doc/Architecture/RetiringANodeType). No recompile will change it |
| Matched nodes, and a declared entry reports `MISSING SOURCES` | One declared query answered and matched nothing — the `SourceCoverage` signal | Restore the source nodes, or correct the query |
| Matched nodes, entries all satisfied, **and the mesh is served from a prefix** | **This page** — a mount-relative reference resolved against the wrong root | Nothing to do; the resolver now asks for both spellings |
| Matched nodes, and the same failure across many types in one burst that goes green minutes later | A compile that read a source set mid-edit. Both re-arms are change-driven and already in place: `NodeTypeCompileParkRegistry.ShouldRetryForSourceChange` (a source arrival after the park) and `SourcesMovedSince` (the consumed set had already moved when the verdict was written) | Nothing. The green recompile **is** the mechanism working — do not add a retry or a delay |

🚨 **A fifth shape is not a `CS0246` at all**: a held type (`NodeTypeDefinition.PendingRetirement`)
whose shared library has moved on. Its own `Source`/`Test` nodes are kept while instances remain, so
a rename in the shared partition breaks the held type's **tests** and parks the type although its
assembly is still serving. The verdict is real, and the fix belongs to the migration, not to the
compiler.

## Verifying

The sweep is `search 'nodeType:NodeType content.compilationStatus:Error partitions:all' limit:200`
and its answer means nothing without a denominator — it runs as **you** and is RLS-filtered, so a
type parked in a partition you hold no grant on is silently not counted. State
*"0 of N over M readable partitions"*, taking N and M from the same instrument. Full rules, and why a
`Not found` from outside that denominator closes nothing:
[Search Coverage and Refusal](/Doc/Architecture/SearchCoverageAndRefusal).

Offline, the rule is a pure function over strings and is pinned by
`MountRelativeSourceQueryTest` — including a case on each side of the change (mirrored mount rewrites,
root mount does not) and a negative control that a genuinely absent sibling is still reported as a
missing source.

## See also

- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — the pipeline, and the source-set
  establishment contract
- [Retiring a NodeType](/Doc/Architecture/RetiringANodeType) — the `Matched Code nodes (0)` shape
- [Dangling NodeTypes](/Doc/Architecture/DanglingNodeTypes) — a node whose type resolves to nothing
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — how a tree arrives under a prefix
