---
nodeType: Markdown
name: Source Retirement
category: Architecture
description: >-
  When the repository deletes the folder a GitSync'd Space mirrors — a package renamed or removed —
  the sync retires the nodes that folder imported instead of refusing forever. How a deletion is told
  apart from a mistyped subdirectory, exactly what goes and what stays, and what is still a person's
  decision.
icon: /static/NodeTypeIcons/box.svg
---

# Source Retirement

**A package folder the repository deletes takes the nodes it imported with it** — the same rule as
deleting one file from the folder, applied to the last file too.

## The defect this closes

Deleting 24 of a package's 25 files pruned 24 nodes on the next sync. Deleting all 25 pruned
**nothing**, ever. The sync refused the empty listing — correctly, for the case the refusal was
written for (#1326: a mistyped or mis-cased subdirectory also lists nothing, and importing an empty
snapshot under `FullReplace` would mirror the whole Space away) — and a deleted folder produces the
very same empty listing. Nothing told the two apart, so a renamed or removed package left every node
it had ever imported live for good.

**Measured on both production meshes, 2026-10-06.** MeshWeaver.Plugins renamed `DeepSign` to
`Signature` (c3262d1e9). `DeepSign/_GitSync` kept following `subdirectory: DeepSign`, found nothing,
and recorded `lastSyncOutcome: Refused` on every commit since — 1,442 versions of that node on
memex.systemorph.com. Meanwhile `DeepSign/RequestSignatureMenu` and `DeepSign/SignaturesMenu` — two
`UiContribution` nodes — stayed in the mesh-wide contribution catalog, so every node's **⋯ More**
offered *Request Signature* twice and *Signatures* twice, the old pair pointing at `/DeepSign/…`
URLs the package had retired. The earlier investigation of the same refusal (#4499) read it as a
configuration fault ("the subdirectory was the wrong half"), which is what the refusal's own wording
says — and why it was never fixed at the source.

## How a deletion is told apart from a typo

By the **last commit this source imported** (`lastSyncCommitSha`): the sync reads the configured
folder there.

| At the last imported commit | Now | Verdict |
|---|---|---|
| files | none | **Retired** — git deleted the folder |
| none | none | **Refused** — the folder was never there (a typo) |
| — (first import, no base) | none | **Refused** |
| unreadable / truncated listing | none | **Refused** |

Every unknown answers *refuse*, the direction that deletes nothing. An operator who re-points a source
at a different (wrong) folder is still refused: the base proves only that the folder it was read
under existed.

🚨 It is a **read** of the folder at the base commit, not a git diff. The production repository
client (`GitProtocolRepoClient`) does not forward `GetChangedPaths` to the compare API, so it answers
every diff with `null` — a proof built on the diff would never fire where it is needed.

Once retired, a source **stays** retired under the same configuration: on a later commit the base is
the retirement commit, where the folder is already empty, so the recorded `Retired` outcome (scoped by
the configuration fingerprint) is what carries the verdict forward. If the folder comes back, the
listing is no longer empty and the ordinary import brings it back.

## What goes, and what stays

`StaticRepoImporter.RetireSource` removes exactly what the ordinary prune would remove for an empty
source — `ComputePrunableNodes` with every guard intact:

- **Provenance** — only paths the source's import manifest records. A node created in the partition
  at runtime was never the source's to delete.
- **Governance** (`_Access`, `_Activity`, `_Policy`, …) and **mesh-minted release records** stay.
- A partition decoupled with **"sync: none"** is left alone.
- A **NodeType that still has instances** anywhere on the mesh is **held** and stamped
  `PendingRetirement` (`NodeTypeInstanceProbe`), exactly as in an ordinary import.

Two differences, both deliberate:

1. **The partition root stays.** Deleting it is recursive and would take the sync source, the access
   grants and every runtime node with it. That is the governed package removal's job — the Store's
   `SystemRemoval` (Provision → Remove), which also cleans each viewer's installed copy and checks for
   dependent packages. The retirement's `lastSyncNote` says so.
2. **The source's own instances go first.** A package often ships an instance of its own type (a
   desk, a workspace). Deleting those before probing the types means a type whose only instances were
   the package's own is not held by them.

## What is still a person's decision

The retired partition keeps its root (`Store/Plugin`, so the catalog may still list it) and its
`_GitSync`. Whether the package is gone for good — and so whether its root, its grants and the copies
viewers installed should go — is decided through the governed removal, by a global admin. The sync
retires content; it never removes a package.

## Related

- [Sources Sync on Push](../SourcesSyncOnPush) — when a source imports, and the provenance rule the
  prune follows
- [Static Repo Import](../StaticRepoImport) — the importer and its prune guards
