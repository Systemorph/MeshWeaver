---
Name: Who Owns a NodeType Member
Category: Architecture
Description: Every member of a NodeType's definition is owned by the repo or by the mesh, and the mask that encodes it was pinned in one direction only — so four runtime-state members were missing from it, one of them spelled outside the naming convention that was supposed to catch them.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2 4 6v6c0 5 3.4 9.4 8 10 4.6-.6 8-5 8-10V6l-8-4z"/><path d="M9 12l2 2 4-4"/></svg>
---

# Who Owns a NodeType Member

A NodeType node's content is a `NodeTypeDefinition`, and it has two owners at once. The **repository**
owns the authored definition — `Configuration`, `Sources`, `Description`, `Dependencies`. The **mesh**
owns the compile bookkeeping the framework writes as the type builds — the verdict, the timestamps,
the assembly coordinates, the source-version maps, the release triggers. One record, two owners, and
three seams where a node crosses between them:

| Seam | Rule | Where |
|---|---|---|
| **Export** | strips the mesh-owned members | `NodeTypeOperationalContent.StripOperational` |
| **Import / upsert** | preserves the LIVE node's values, absent when live has none | `PreserveLiveOperational`, applied by the owner inside the upsert merge |
| **Change detection** | ignores them — a node differing only in bookkeeping has not changed | `PartitionSourceFingerprint` |

All three read one list: `NodeTypeOperationalContent.MemberNames`. Getting a member into that list
is the whole of the ownership decision; leaving one out is silent in every direction.

Why it matters is on [Node Type Compilation](../NodeTypeCompilation) and in `ShippedNodeTypeStateTest`:
a node that adopts a compile verdict it did not earn on THIS deployment is unreachable by every
automatic compile path, and parks forever on a foreign machine's answer.

## The gap: a set pinned in one direction (#4480)

The list was pinned to the record by one assertion:

```csharp
foreach (var member in NodeTypeOperationalContent.MemberNames)
    Assert.True(properties.Contains(member), "…the list drifted from the record.");
```

That is `MemberNames ⊆ the record`. It catches a mask entry that names no property — a typo, a
rename — and it is **structurally unable** to see a runtime-state property that is MISSING from the
mask. That is the direction that loses data, and it stayed green through four omissions:

| Member | What it decides | What an authored value does |
|---|---|---|
| `LatestAssemblyMvid` | the IDENTITY of the bytes the build produced; bind time compares it against the bytes actually served | forges a MATCH and turns off the stale-build detector (the state in #2471: a portal serving stale compiled code while reporting `Ok`), or forges a MISMATCH and refuses a correct bind |
| `CompiledModulesHash` | `HasUsableBuild` invalidates a build stamped with a different non-null hash than the live module set | a hash matching the importing deployment declares a FOREIGN build usable and suppresses the recompile a module update requires |
| `CompiledDependencies` | the per-type dependency record; `HasUsableBuild`, the bake probe's `Classify` and `IsAlreadyAdopted` all validate it against the live environment | a record matching the bundle makes a FRESH install read as already-adopted — the bytes are never seeded and the type parks on a stamp nobody earned |
| `DispatchedBuildInputs` | what the compile IN FLIGHT was dispatched for; a request resolving to the same token is CONSUMED rather than queued | a token matching a live request absorbs that request against a compile nobody dispatched, and the release it asked for is lost |

The first three leaked in both directions: export left them in the repo file (and in the change
token, which then moved when only the runtime state had changed), and import let a file overwrite a
measurement taken on this mesh. `LatestAssemblyMvid` was incoherent on its own terms — the file named
bytes by an identity that exists nowhere on the importing mesh, while carrying no path to them.

## Why a naming convention cannot be the guard

`ShippedNodeTypeStateTest` bans runtime state from committed files by a **naming convention** —
`Compilation*`, `Compiled*`, `LastCompil*`, `LatestAssembly*`, `RequestedRelease*`, `Failed*` and so
on. It is fail-closed by name, which is exactly right for what it does: a member added tomorrow is
banned the day it is added, with nobody maintaining a list.

It is a *sufficient* condition and never a necessary one. `DispatchedBuildInputs` is compile-pipeline
state spelled outside it, and so are `BuildProvenance`, `ReleaseNotes`, `RequestedSourceStampAt` and
the whole `Adopted*` family. A reverse assertion built on the convention alone would have found three
of the four and been blind to the fourth — the same defect, one name away.

So the guards are three, and each covers what the others cannot:

1. **`MemberNames ⊆ the record`** — a mask entry that names no property.
2. **convention ⊆ `MemberNames`** — a member SPELLED as runtime state that nobody masked.
3. **the partition** — every serialised member of the record is classified as repo-authored,
   mesh-owned-and-masked, or mesh-written-but-deliberately-unmasked. Exactly one bucket, no member
   unclassified. This one does not depend on what a member is called, so a new property cannot be
   missed however it is spelled.

The convention lives in **one** place (`NodeTypeMemberOwnership` in the Graph test suite) and every
guard reads it from there. Two lists that both approximate the same set drift pairwise; that is the
failure this page describes, one layer up.

## The third bucket: mesh-written and deliberately NOT masked

Masking is not a synonym for "the runtime writes it". `PendingRetirement` is written by a
repository-driven import when the repo retired a type that still has live instances — and it is
deliberately outside `MemberNames`:

- it is stamped through the probe's own `stream.Update`, never through an upsert, so masking would
  not protect the write;
- **nothing in `src/` ever writes null back to it.** The one thing that clears it is the repo
  shipping the type AGAIN: an upsert replaces the node's content wholesale, so an unmasked member
  present in the live node and absent from the file simply goes away.

Mask it and a re-shipped type stays marked retired forever — and the bake gate reads a stamped type's
compile failure as `Retired`, i.e. as a verdict that must *not* hold a rollout. So the entry carries
its reason, and the committed-file ban covers it instead: a file must still never author it.

That is what the third bucket is for, and why an exclusion is a reasoned line rather than a deletion.

## What to do when you add a member to `NodeTypeDefinition`

Decide the owner, and write the reason down next to the entry:

- **The repo owns it** → add it to `NodeTypeMemberOwnership.Authored`. Nothing else to do; imports
  honour it and the change token sees it.
- **The mesh owns it** → add it to `NodeTypeOperationalContent.MemberNames`, with a comment saying
  what an authored value would forge — every entry there carries one. It must also be added to
  `NodeTypeCompileState`, which carries exactly the masked set (pinned by
  `NodeTypeCompileStateTest`), or the compile-state satellite silently drops it.
- **The mesh writes it but it must stay unmasked** → `NodeTypeMemberOwnership.MeshWrittenButUnmasked`,
  with the reason why dropping it on re-import is the point.

The partition guard fails until one of the three is true, and names the member.

## Related

- [Node Type Compilation](../NodeTypeCompilation) — the compile control plane that writes this state
- [CQRS and Content Access](../CqrsAndContentAccess) — why the live node, not a query, is the authority
- [Static Repo Import](../StaticRepoImport) — the seam where a repo file becomes a node
