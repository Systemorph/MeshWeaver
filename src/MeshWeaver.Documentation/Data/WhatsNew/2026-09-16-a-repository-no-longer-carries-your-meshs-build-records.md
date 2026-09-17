---
Name: A repository no longer carries your mesh's build records
Category: Fix
Description: Four records the platform measures on YOUR deployment — which bytes a node type's build produced, what it was built against, and what compile is in flight — were being written into repository files and read back out of them on the next import. A file could quietly replace a measurement it was never in a position to take.
Icon: ShieldCheckmark
Order: -20260916
---

# A repository no longer carries your mesh's build records

A node type in a Space has two kinds of content, and they belong to different people. The
**definition** — its configuration, its sources, its description — is yours, and it lives in the
repository the Space syncs from. The **build record** — did it compile, when, against what, which
assembly is serving it — is a measurement the platform takes *on your deployment*, and a repository
file has no business carrying one. A node type that arrives already claiming it compiled here, when
it never did, can end up unable to compile at all.

That split has been enforced at every seam for a while: an export removes the build record from the
file, an import keeps whatever this mesh last measured, and a file whose only difference is a build
record does not count as a change.

The list of records it applies to was missing four of them.

## What that meant

The four were all newer, all of them things the platform uses to decide whether a build is still
good:

- **which bytes the last build produced.** The platform checks the assembly it is actually serving
  against this, to notice when a node type is quietly running stale compiled code. A value that came
  out of a file could make that check pass when it should not.
- **what the build was compiled against** — the installed modules, and the exact dependency record.
  Both decide whether an existing build may be reused or has to be recompiled. A value carried in
  from a file could declare somebody else's build usable here, and suppress a rebuild that was
  needed — or make a fresh install conclude the work was already done and never install the bytes at
  all.
- **which compile is in flight.** A release request that matches the compile already running is
  deliberately folded into it. A value arriving from a file could match a request that nothing was
  actually compiling, and the release you asked for would simply never happen.

In the other direction they leaked outward: every export wrote them into the repository, where they
were noise in the diff — and because they sat in the file, a sync could see a "change" in a node
type whose definition nobody had touched.

## What changes

All four are now treated like the rest of the build record: stripped on export, kept from the live
node on import, and ignored when deciding whether content changed. The first sync after this update
will drop them from the files that still carry them — that one diff is the correction, not a change
to your content.

The guard that keeps the list honest had been able to check only one direction: it could tell you a
record in the list no longer exists, but not that a record exists which nobody put in the list. It
now checks both, and — because the member that had been missing longest was not even *named* like
the others — every member of a node type's definition must now be explicitly accounted for as yours
or as the platform's. A new one cannot be quietly forgotten again.

Background, with the measurements: [Who Owns a NodeType
Member](/Doc/Architecture/NodeTypeMemberOwnership).
