---
Name: Publishing A File On A Shared Volume
Category: Architecture
Description: A name readers watch must appear holding the whole file or not appear at all — and File.Move(…, overwrite false) does not promise that. On a volume without hard links its failed rename COPIES into the final name, which then exists incomplete and exclusively locked for the length of the copy. The measurement, the primitive that replaces it, what it refuses, and what is still not atomic.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="m9 15 2 2 4-4"/></svg>
---

# Publishing A File On A Shared Volume

Every portal replica shares one `/data`, an Azure Files (SMB) ReadWriteMany volume, and several
things on it are published by NAME: a module's activation records
(`modules/activation.d/<Name>/…`), the module set index (`modules/sets/…`), a compiled NodeType
assembly, a container blob, a cached package. For each of them the rule is the same:

> **A name a reader watches appears holding the WHOLE file, or it does not appear.**

The staging-file-then-rename shape is how that is kept — `AtomicFileWrite`'s type remarks carry
the three torn reads that shape it. What this page is about is the step everyone believed was the
rename.

## `File.Move(staged, target, overwrite: false)` is not a rename

.NET's no-overwrite move on Unix is three steps, and only the first is atomic
(`FileSystem.Unix.cs` → `MoveFile` → `LinkOrCopyFile`):

1. `lstat` both paths; if the target is absent, `rename(2)` — atomic, and the intended path;
2. if that rename fails for **any** reason, `link(2)` — also atomic, where the volume has hard
   links;
3. if the link fails with `EXDEV`/`EACCES`/`EPERM`/`EOPNOTSUPP`/`EMLINK`/`ENOSYS`, **copy** the
   staged file into the target, opening it `FileMode.CreateNew, FileAccess.ReadWrite,
   FileShare.None` and filling it afterwards.

**Azure Files over SMB has no hard links**, so on the portals' volume step 2 always fails and every
failed rename lands in step 3. For the whole copy the final name exists, holds incomplete bytes,
and is held under `FileShare.None` — which .NET implements on Unix as an exclusive `flock`.

### Measured

With the portal image's own runtime (`3.0.0-ci.8323`, .NET 10), one `File.Move(staged, target,
overwrite: false)` of a 512 MiB file that could not be renamed, while a second thread polled the
target with the same sharing mode the activation reader uses
(`FileShare.ReadWrite | FileShare.Delete`):

| Reader | Opens that failed | Opens that read it INCOMPLETE | Opens that found it absent |
|---|---|---|---|
| Same kernel (lock visible) | **21,573** — *"The process cannot access the file … because it is being used by another process"* | 0 | **0** |
| Lock not visible (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1`) | 0 | **52,619** | **0** |

The second row is not a curiosity: the fleet's `azurefile-memex` StorageClass mounts with `nobrl`,
so a `flock` is local to one node's kernel. A reader on **another** node sees no lock at all — it
reads the half-copied file.

`absent = 0` in both rows is the point. The name is never missing during the copy; it is present
and wrong.

### What that cost

memex, 2026-09-16 13:19:55Z, pod `memex-portal-deployment-7cb6684584-jdw7v`:

```
fail: MeshWeaver.PluginCatalog.ModuleLandingService[0]
      Module record '/data/modules/activation.d/MeshWeaver.AI.Anthropic/…verdict' could not be read
      (IOException: The process cannot access the file … because it is being used by another process.)
fail: MeshWeaver.PluginCatalog.ModuleLandingService[0]
      Module 'MeshWeaver.AI.Anthropic' is NOT loaded from this read: 1 of its records could not be read.
```

The second line is the designed consequence, not a second defect: a module whose records cannot be
read WHOLE is dropped from the read
([Module Activation Head Ownership](../ModuleActivationHeadOwnership) → *Fail closed*), because
deriving from a partial log would load an uninstalled module or promote an older generation. At
BOOT that answer is the process's for its lifetime — the pod runs without the module until it is
restarted, and reports it as pending. The same window makes `ProposeModuleSet` refuse (a set must
never be proposed from a partial read), and makes a modules-GC pass skip every generation delete.

So the reader cannot be made to cope: **the writer must never create a final name any way but by
renaming a complete file.**

## The primitive: `NoReplaceMove`

`MeshWeaver.Utils.NoReplaceMove.TryMove(staged, target)` publishes a staged file under a name that
does not exist yet, and has no copy in it at all.

| Platform | Step | Outcome |
|---|---|---|
| Linux | `renameat2(…, RENAME_NOREPLACE)` | published · `EEXIST` → `false` · unsupported (`EINVAL`/`ENOSYS`/`EOPNOTSUPP`, or a C library without the call) → link |
| macOS | `renamex_np(…, RENAME_EXCL)` | as above (`ENOTSUP` → link) |
| Windows | `MoveFileEx` without replace | published · exists → `false` · a staged file on another volume → refused, never copied |
| any | `link(2)` + `unlink(2)` of the staged name | the same atomic no-replace create where the volume has hard links |
| any | neither available | **refused** — `IOException`, nothing written under the final name |

`RENAME_NOREPLACE` is the *native* CIFS behaviour (`fs/smb/client/inode.c`: *"No-replace is the
natural behavior for CIFS, so skip unlink hacks"*), so on Azure Files it is one server operation:
the name appears complete, or the call reports `EEXIST` having touched nothing.

**A refusal is the right answer, and it is loud.** Where the rename cannot be made, the landing
fails with the platform error in the message and `HResult`; its bytes and its previous records are
untouched, and the next landing or the next boot records the event again. The alternative — the old
behaviour — was to publish something no reader may see.

**`false` is not a failure.** Records, sets, blobs and cached packages are content-addressed, so a
name already taken already holds these bytes: the loser of the race deletes its staging file and
carries on. Nothing is ever replaced, so a reader holding the winner's file never sees the name
vanish or change under it.

## Where it is used, and what the guard keeps

Every no-replace publication in the tree goes through it:
`ModuleActivationSidecar.WriteOnce` (landing records, uninstall tombstones, platform verdicts,
older-image snapshots), `ModuleSetStore.WriteOnce`, `AtomicFileWrite.PublishBytes` /
`PublishAsync` (the assembly cache and everything else that publishes bytes),
`ContainerBlobCache.Publish`, `FileSystemNuGetPackageCache.SaveAsync`,
`ModuleLandingService.RestoreMissingFiles` and `EmitReferenceCapture`.

`NoReplaceMoveRatchetGuard` (in `test/MeshWeaver.Documentation.Test`) scans `src`, `tools`,
`samples` and `memex` for `File.Move(` without `overwrite: true` and fails on any site but the
primitive's own. There is no allow file.

`File.Move(…, overwrite: true)` stays allowed and is a different operation: a deliberate REPLACE,
implemented as a plain `rename(2)` whose only fallback is across devices — which a sibling staging
file cannot reach.

## What is still not atomic

- **Replacing an existing name on SMB.** The Linux CIFS client implements `rename(2)` over an
  existing target as *unlink the target, then rename*, so the name is briefly ABSENT. The per-module
  projection (`activation.d/<Name>.json`) and the marker files are written that way on purpose
  (`WriteAtomic`), and a reader that opens in that window sees "vanished", which it treats as
  absence. That is why nothing a current image DECIDES is read from those files — the records are
  the decision, and they are never replaced.
- **A mixed-image window.** A replica still running an image that predates this change publishes
  records with the old call, so the copy fallback is reachable from that pod until the roll
  completes.
- **A full volume.** A staged file can still be short if the share has no space;
  `AtomicFileWrite.WriteDurably` measures that before it publishes (see `ShortWriteException`), and
  the record writers inherit the same failure as a refused publication rather than a torn one.

## See also

- [Module Activation Head Ownership](../ModuleActivationHeadOwnership) — the records this protects, and
  the fail-closed read that makes a torn one cost a whole module
- [Module Set Convergence](../ModuleSetConvergence) — the set index published the same way
- [NodeType Compilation](../NodeTypeCompilation) — the assembly cache, where a torn read is a truncated
  PE image rather than an unreadable record
