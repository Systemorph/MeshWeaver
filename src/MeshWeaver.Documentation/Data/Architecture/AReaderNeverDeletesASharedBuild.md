---
Name: A Reader Never Deletes A Shared Build
Category: Architecture
Description: The assembly loader deleted every build whose file was older than the running framework DLL. On the shared /data/assembly-cache that removed the current, compatible builds at the first boot of every new image, out from under the replicas still serving them. That one delete was the per-roll recompile wave, the Store/Plugin "stuck AGAIN" boot window (#6161) and the registry refetch that could never land (#6052).
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/><path d="M4 20 20 4"/></svg>
---

# A Reader Never Deletes A Shared Build

**The rule.** `NodeAssemblyLoadContext.LoadNodeAssembly` loads the path it is given. It does not
decide whether the build belongs to the running generation, and it never deletes a loadable file.
The one delete left in it is for a `BadImageFormatException`, where the bytes provably cannot load.

The generation is decided **by identity, before the path reaches the loader**:

| Owner | What decides the generation |
|---|---|
| `FileSystemAssemblyStore` | every file is named `v{version}-{FrameworkTag}-{hash}.dll`, and the store serves only its own tag (`NamedBuild`, `TryGetBuildPath`) |
| the NodeType record | `CompiledFrameworkVersion` gates the bind (`HasUsableBuild`) |
| the local disk cache | `TryGetLatestCachedDllPath` checks the framework time itself (Check 2) before it hands out a path |
| a release | its hash includes the framework (`NodeTypeRelease`) |

A write time is not a generation. Under the compatibility policy, a build of the same framework tag
made by an earlier image is exactly what may be bound.

## What happened

The loader compared the file's write time with `MeshWeaver.Compiler.Pipeline.dll`'s and deleted the
file "for regeneration" when it was older. A new image's framework DLL is newer than every build
compiled before the image existed, so on the ReadWriteMany `/data` the first replica of each roll
deleted the shared builds the old replicas were serving. Measured on memex.systemorph.com
(2026-10-08, governed `Logs` actions under `Ops/Actions/logs-6161-…`):

- **ci.10245 boot, 11:56Z, one pod:** about 180 lines of the form *"Failed to load assembly for
  AppleMaps/Gallery … The file at '/data/assembly-cache/AppleMaps_Gallery/v730-c003e001-….dll'
  (09:34:01Z) predates the framework (11:07:40Z) and was deleted for regeneration."* Then
  12:05–12:10Z: 57 *"Overlay self-heal: instance 'Manufacturing' is stuck on NodeType 'Store/Plugin'
  AGAIN …"* lines.
- **ci.10256 boot, 16:52:53Z, pod `…-grbh9`:** `Store_Plugin/v19020-c003e001-a48c83f8b0cf.dll`
  (15:00:20Z, written by the ci.10249 generation) *"predates the framework (15:54:09Z) and was
  deleted"*. Next came the store miss *"1 listed file(s) cannot be opened (evicted, still held open
  elsewhere on the share)"*, the registry refetch refusal, and two recompiles on the owner replica
  (`…-cwvzr`, still on ci.10249). The record moved from v19020 to v19028/19030.

## Why it produced three symptoms

1. **The recompile wave and the version climb (#6161, #5555).** Each deleted build is a store miss.
   The miss triggers a recompile on the owner and a new record version. That is why the
   `Store/Plugin` version rose in clusters at pod boots, and why every roll paid a compile per
   locally built type.
2. **"Stuck AGAIN" for about five minutes at boot (#6161).** While `Store/Plugin` recompiled, every
   installed package root overlaid. The overlay self-heal fired on the record's still-usable
   verdict, re-activated against bytes that were gone, and overlaid again until the new build landed.
   The heal budget then logged the non-convergence.
3. **The refetch that could never land (#6052).** `Store/Plugin` is built locally on purpose. The
   bundle's source fingerprint (`3d0df09d…`, module 1.20.4) is not the live source's (`dbbc1754…`,
   1.21), so the owner declines the adoption (#2813). The record therefore names a local MVID, which
   no shipped bundle carries. When the delete made those bytes missing, the registry refetch (#6262)
   correctly refused to bind a different build. The only remaining remedy was a recompile. The
   refetch is right for the case it was built for (bytes missing for a *shipped* build), but here
   the missing bytes were self-inflicted.

## The test

`AReaderNeverDeletesASharedBuildTest` (MeshWeaver.Compiler.Pipeline.Test):

- **Production path.** It puts real bytes into a `FileSystemAssemblyStore` and backdates the file to
  before the framework DLL, which is a build from an earlier image. It resolves the path through
  `TryGetBuildPath` and loads it through `CompilationCacheService.PinForScan`, as activation does.
  The build must load and the file must still exist.
- **Loader alone.** It loads the same bytes twice: once with a write time newer than the framework,
  as the control, and once with a write time older than it. Both must load, and nothing may be
  deleted.

**Negative control:** against the pre-fix loader, both stale arms answer `null` and delete the file,
while the control still loads.

## What this does not establish

- Whether every `bytesmissing` reading on memex was this delete. The public `/health` body does not
  name the type behind `bytesmissing=1 in Doc/…`.
- Why the registry labels the shipped Store bundle `Store@1.13.0` while the adoption log names
  module version 1.20.4.
- A client estate with a pod-local `/data` still cannot share a locally compiled build across pods.
  That is #6197's chart refusal, not this rule.

Related: [An Unloadable Build Is Never A Silent Default](../AnUnloadableBuildIsNeverASilentDefault),
[Stale State Until Recycle](../StaleStateUntilRecycle).
