---
Name: Module source can stay out of the mesh database
Category: Feature
Description: Adopt-only meshes can set Modules:ImportSourceNodes to false — package installs and GitSync imports then skip persisting Source/ and Test/ files as nodes; the compiled form arrives in the prebuilt bundle and the text stays in the repo.
Icon: DatabaseArrowUp
Order: -20260825
---

# Module source can stay out of the mesh database

On a mesh that runs modules from prebuilt bundles, the C# under every type's `Source/` and `Test/`
folders has no runtime job — the compiled assembly is the artifact — yet every import kept writing
those files into the database as nodes, where they cost storage, sync traffic, and a second copy
of the truth that lives in the repo.

Deployments can now set `Modules:ImportSourceNodes: false`. Package installs and GitSync imports then
skip compile-input files entirely: they are not parsed, not persisted, and reported once per
import as a counted policy line — never as per-file noise. The default is unchanged (sources are
imported), and the mode is designed to be enabled together with `Modules:RequirePrebuilt`: a mesh
that still compiles on a miss but no longer holds the sources would fail its recompiles, so that
combination is honoured but loudly warned about.

This is the first sequenced step of moving module source out of the mesh database entirely —
imports must stop re-creating source before any existing source nodes can be retired.
