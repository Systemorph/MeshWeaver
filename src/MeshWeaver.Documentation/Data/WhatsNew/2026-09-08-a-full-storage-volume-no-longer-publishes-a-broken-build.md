---
Name: A full storage volume no longer publishes a broken build
Category: Fix
Description: When the shared volume behind the assembly cache ran out of space, a NodeType compile still reported success and published a release for a file the volume had cut short — so every instance of the type rendered empty until someone found the full disk. The compile now refuses to publish bytes the volume did not keep, says so on the type with the disk numbers, and the installation's health page names a volume that is running low before a compile has to find out.
Icon: HardDrive
Order: -20260908
---

# A full storage volume no longer publishes a broken build

On 8 September the shared volume that holds compiled NodeType assemblies on the Systemorph
installation had 3 MiB free of 16 GiB. Nothing said so. What people saw instead was one type —
the one behind a deployment restart — whose instances would not load: the page showed the raw
data, every action on it was gone, and the same happened on both replicas.

Underneath, each recompile of that type had gone through unchanged: the compiler produced the
assembly, the copy into the shared cache reported success, a release was recorded naming the copy,
and the first attempt to load it failed because the file was a fragment. A volume with no space
left accepts a write and keeps only what fits, and nothing in the pipeline checked. The failed load
then deleted the fragment, the next use of the type found nothing in the cache and compiled again,
and the cycle repeated — three releases in two minutes, none of them loadable.

**A compile now proves its bytes are on the volume before it publishes anything.** The copy into
the shared cache is flushed to disk and read back; when the volume kept fewer bytes than were
written, the publication is refused. That refusal is the compile's result: the type records a
compilation error that names the file, how many bytes were written, how many the volume kept, and
how much space the volume has left — and no release is recorded, no pointer moves, and whatever
build the type was serving before stays in place. Freeing space and pressing Compile is the whole
recovery.

A fragment that is already in the cache — from before this change, or from a writer this change
does not reach — is still removed so the next compile can replace it, and the error the type
records now carries the same numbers, so a full volume can be told from a genuinely bad file
without a debugger.

**The health page now names a volume that is running low.** `/health` on every replica reports
the volume behind the assembly cache as *degraded* when its free space falls below 256 MiB
(configurable as `AssemblyCache:MinimumFreeMiB`), with the path and the numbers in the detail. It is
never a reason to take the replica out of service: a replica on a full volume still serves every
page whose code is already loaded, and removing it would turn "cannot compile" into "cannot serve".

See [NodeType Compilation → A publication is durable and verified, or it is refused](/Doc/Architecture/NodeTypeCompilation)
for the mechanism.
