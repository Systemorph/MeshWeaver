# MeshWeaver.Graph.Contract

The shared vocabulary between the graph model (`MeshWeaver.Graph`) and the NodeType compile
pipeline (`MeshWeaver.Compiler.Pipeline`). **Neither half owns it, so neither half can pull the
other into a cycle.**

Before the split the two were mutually recursive: an 8-file / 11,059-line strongly-connected
component straddled the seam, so no assignment of those files to two projects was acyclic. Hoisting
the types both halves speak — rather than picking a winner — is what makes the dependency point one
way and keeps it pointing that way as the code changes.

What lives here:

- **`NodeTypeDefinition`** — the NodeType declaration record: sources, compile status, the compiled
  stamp, the latest release/assembly path.
- **`BuildState`, `NodeTypeRelease`, `ReleaseArtifact`, `ServedBuildIdentity`** — build and release
  state, and the identity a served build is pinned to.
- **`GraphNodeTypeNames`** — the node-type name literals the pipeline writes and reads
  (`Activity`, `CompletionMemory`, `Release`) without owning their registration extensions.
- **`ICompileFailureNotifier`** — the seam by which the pipeline reports a parked compile failure
  to a person. `MeshWeaver.Graph` registers the implementation in `AddGraph`; the pipeline resolves
  it OPTIONALLY, so a hub composed without `AddGraph` keeps a missing bell rather than a faulted
  compile.
- **`SyncedQueryDataSourceExtensions` / `SyncedQueryMeshNodes`** — the synced-query helpers the
  pipeline reads its source set through.

Everything keeps its original `MeshWeaver.Graph` / `MeshWeaver.Graph.Configuration` namespace: the
assembly moved, the namespace did not, so no in-mesh plugin source and no sibling repo sees an API
change.
