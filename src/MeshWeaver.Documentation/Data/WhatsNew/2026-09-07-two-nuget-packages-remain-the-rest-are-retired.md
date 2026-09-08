---
Name: Two NuGet packages remain, the rest are retired
Category: Feature
Description: MeshWeaver now publishes exactly two NuGet packages — the Aspire integration and the dotnet new template. The other forty-three are unlisted; existing version pins keep working, and startup configuration moves onto the Aspire adapter instead of new packages.
Icon: Sparkle
Order: -20260907
---

# Two NuGet packages remain, the rest are retired

MeshWeaver used to publish more than forty NuGet packages. It now publishes two:

- **`MeshWeaver.Aspire.Hosting.Memex`** — the Aspire integration. `builder.AddMemex()` wires
  Postgres, the migration and the portal, and publishes to Docker Compose, Kubernetes/Helm or Azure
  Container Apps.
- **`MeshWeaver.MemexTemplate`** — the `dotnet new` template that writes a portal solution.

Those two are how you *start* using MeshWeaver. Everything else was a library, and the platform
stopped being delivered as libraries some time ago: in-mesh source compiles against the platform
image, module bundles carry their own closures, and plugin repositories build inside
`mw-plugin-test`. Keeping the rest listed offered packages that nothing builds against and nothing
patches.

## Nothing you have pinned will break

Retirement means **unlisting**, not deletion. A `PackageReference` at an exact version still
restores exactly as before — that is measured, not assumed: an internal deployment pins about
thirty-five already-unlisted MeshWeaver packages and has kept building throughout. What changes is
that the packages no longer appear in search, in `dotnet add package` completion, or in
latest-version resolution.

Two commands do stop working, and neither had a user in any pipeline:
`dotnet tool install -g MeshWeaver.Cli` and `dotnet tool install -g MeshWeaver.Compiler.Cli`. Both
tools are reached through the `mw-plugin-test` container image instead, which is what CI has always
run.

## Configuration goes on the adapter, not into new packages

The ground rule going forward: **anything with a startup dependency is configured through the Aspire
integration.** The AppHost is not the portal — the portal is a prebuilt image — so a package
referenced from your AppHost could never add a driver to it anyway. Instead, options on
`MemexOptions` map one-to-one onto portal config keys, which is already how the storage backend and
the Orleans clustering provider are chosen, and is how plugin selection will work.

Full detail, including what the retirement tool does and how to restore a listing:
[NuGet Package Retirement](/Doc/Architecture/NuGetPackageRetirement).
