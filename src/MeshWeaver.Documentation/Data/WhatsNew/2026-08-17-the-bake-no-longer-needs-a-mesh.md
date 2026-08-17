---
nodeType: Markdown
name: The content bake no longer needs a mesh
Category: Feature
Description: mw-compiler grew a `compile` verb that resolves NodeType sources straight from a git checkout and compiles them with the MeshWeaver.Compiler toolchain — no in-process mesh, no import, no per-type activation — and an equivalence test pins its output against the mesh-driven bake it replaces.
---

# The content bake no longer needs a mesh

Producing a NodeType's assembly used to require standing up a mesh. The CI bake — the platform's own
and every content repo's — booted an in-process mesh, imported the whole checkout, let the **mesh**
compile every NodeType behind its hub scheduler, and then collected whatever the mesh had produced.
That is "compile through mesh nodes", and it is where the minutes went: mesh startup, message
routing, and one hub activation per type.

`mw-compiler` now carries a **`compile` verb** that does it as an ordinary build step:

```bash
mw-compiler compile <checkout-root> --output <dir> [--source-sha <sha>]
```

It reads the node files out of the tree, resolves each NodeType's `Sources`/`Tests` queries against
that in-memory set, resolves `@@` includes, compiles with the `MeshWeaver.Compiler` toolchain and
emits **DLL + PDB** into the same prebuilt-assembly bundles as before. No `MeshBuilder`, no
`AddGraph()`, no content import, no hub anywhere in the path.

**Nothing downstream changes.** Same bundle format, same framework-identity keying, same per-type
dependency records. The portals' shipped-bundle seeder, the plugin bundle client and
`PrebuiltAssemblySeeder` cannot tell which producer wrote a bundle — and must not.

**The gate keeps its mesh, because it earns it.** Rendering a layout area and executing a `Tests`
area are genuine runtime behaviours, so `mw-plugin-test` still stands up a mesh — it now just
*consumes* a bake instead of producing one. Bake is a build step; gate is a runtime check.

**And a live instance still compiles its own.** There will always be code that never went through
CI, so the emergency path stays exactly as it was. That is recovery, not a build lane.

## Why the boring part is the interesting part

A baker that resolves sources even slightly differently from the runtime emits assemblies that are
*subtly* not what the mesh would have built — and nothing fails. The bundle is well-formed, the
framework identity matches, every portal adopts it, and the first symptom is a page rendering empty
in production.

So the source-resolution rules are not re-implemented: query expansion, the `@@`-include walk, the
deduplication, the executable-cell filter, the join order, the generated skeleton and the emit are
all the runtime's own code, and the new resolver **refuses** any query it cannot evaluate rather
than quietly matching less. On top of that, a test bakes one content set both ways and asserts the
two producers agree on the resolved source set, the dependency records, the framework identity and
the emitted assemblies' type-and-member surface.

See [CI Content Bake](/Doc/Architecture/CiContentBake) for the full split and the equivalence
argument.
