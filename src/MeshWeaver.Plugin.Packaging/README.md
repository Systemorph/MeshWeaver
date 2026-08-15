# MeshWeaver.Plugin.Packaging

The plugin **package format**, in one place: the archive layout, the nuspec projection of the mesh
manifest, and NuGet version ordering.

Two callers produce plugin packages — the CI tool (`MeshWeaver.Plugin.Build`, from files on disk)
and the portal's feed (from node content plus the assembly store). They must emit the **same
package for the same plugin**, so the format lives here rather than in each.

## What the format encodes

- **Assemblies under `meshweaver/assemblies/`, never `lib/net10.0/`.** A plugin's units compile
  separately at runtime and may declare the same type names; under `lib/` NuGet would surface them
  all as compile-time references and collide them.
- **The nuspec is a projection, not an invention.** `version` comes from `manifest.lock` (the number
  tagged `<Module>/vX.Y.Z`); `"requires": ["Store@^1.0.0"]` becomes a caret range; the framework is
  a **minimum**, because the bake recompiles against whatever the consumer resolves.
- **Version ordering is semver, not string.** Continuous framework builds are
  `3.0.0-rc3.ci.<run>`, and as text `"…ci.900"` sorts above `"…ci.3758"`.
