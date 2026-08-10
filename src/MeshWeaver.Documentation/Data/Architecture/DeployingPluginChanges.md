---
NodeType: Markdown
Name: "Deploying a plugin change — merging is not shipping"
Abstract: "The mesh-side tail of every plugin change: a merged pull request changes nothing on any instance, because a plugin repo has no image — an instance runs what its Space has GitSynced AND last compiled. Covers the three steps (check the delta, update, recompile), how to read 'Skipped (0 nodes)', how to prove a type is actually running your code (compiledSources vs currentSourceVersions), why a stale assembly keeps serving silently, the husk a failed provision leaves behind, and why core is the opposite case."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0e7490'/><path d='M12 5v9M8.5 11 12 14.5 15.5 11' stroke='white' stroke-width='1.8' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M6 17.5h12' stroke='white' stroke-width='1.8' stroke-linecap='round'/></svg>"
---

# Deploying a plugin change — merging is not shipping

> **A merged pull request changes nothing on any mesh.** The plugin repos have no image: an
> instance runs what its Space has **GitSynced** *and* **last compiled**. Until you pull it and
> recompile, `main` and the mesh disagree — and the mesh keeps serving the old behaviour while
> looking perfectly healthy.

This is the tail of every plugin change (`MeshWeaver.Plugins`, `MeshWeaver.Reinsurance`,
`MeshWeaver.SocialMedia`, `Systemorph/education`). The repo-side procedure is the `/pullrequest`
skill in each of those repos; this page is the mesh-side half.

## The three steps

```
git_hub_sync op=check  space=Store     # what is the delta? (read-only, asks GitHub live)
git_hub_sync op=update space=Store     # pull main → import into the Space
compile @Store/Catalog                 # 🚨 the step everyone forgets
```

Each op runs as an **Activity** — it returns a path immediately and finishes later:
`get @Store/_Activity/{id}` and wait for `content.status: Succeeded`.

### 1. `check` first, always

`update` pulls the branch HEAD, so if `main` advanced past the Space's
`_GitSync.lastSyncCommitSha` you are also shipping everyone else's commits in that subdirectory.
Know what is in the delta before you pull it.

The Space's `_GitSync.subdirectory` is the **blast radius**: `Store` syncs only `Store/`, so a
change spanning two plugins needs two syncs.

### 2. `update` — and read what it actually says

- **`Imported Imported (N node(s))`** — content changed and landed.
- **`Imported Skipped (0 node(s))`** — the partition's content **fingerprint already matched**, so
  the import was skipped. Normal, and usually means a system sync already delivered the content.
  **It does not mean your change is live** — see step 3.

### 3. Recompile — the assembly is the deliverable, not the commit

A NodeType serves its **last successfully compiled assembly**. A recompile happens on a release
request, *not* automatically on every import, and **a failed recompile keeps the last-good
assembly** — so the mesh runs your old code with no error anywhere.

Verify on the NodeType node itself (`get @Store/Catalog`):

- `content.compilationStatus` — must be `Ok`.
- `content.compiledSources` vs `content.currentSourceVersions` — **these must MATCH.** A source
  present in `currentSourceVersions` but missing from (or older than) `compiledSources` is code
  that is on the mesh but **not running**. This comparison is the only reliable proof.
- `content.lastCompiledVersion` vs the node's `version` — a large gap is the same smell.

Force it with `compile @Store/Catalog` (returns `Ok`, or `Error` with the Roslyn diagnostics
inline). Compile every type whose `Source/` your change touched — **including types that merely
share it** (`sources: ["shared=@Store/Plugin/Source"]`), because a shared file changes every
consumer.

### 4. Verify the behaviour, not the sync

"Activity Succeeded" only means the import ran. Open the surface you changed and use it.

## Worked example — 2026-08-03

Provisioning failed on memex with `Access denied: Create permission required for node
'AgenticPrimerDe/_Activity/…'`. The fix — a system-identity install engine — had been **merged for
hours and was already synced** onto the mesh, and `git_hub_sync update` answered
`Skipped (0 node(s))`, correctly: the nodes matched.

The button still ran the old code, because `Store/Catalog` sat at `lastCompiledVersion 1012`
against node version 1019 — `currentSourceVersions` listed sources the compiled assembly had never
seen. One `compile @Store/Catalog` (new assembly `v1022`, `compiledSources` now equal to
`currentSourceVersions`) and provisioning worked.

The failed provision had also left a **husk**: a partition root with `_GitSync` and `_Policy` but
no content. The catalog refuses to re-provision it, because it only offers Provision when the real
root is confirmed *absent*. It was completed in place — `git_hub_sync op=update` on that Space plus
a `compile` of its NodeType — rather than deleted and started over.

## Core is the opposite case

`Systemorph/MeshWeaver` **does** have an image, so merging to `main` *is* the deploy: `main-cd.yml`
publishes the image set to ACR and the portals self-update.

Two things to know before calling it deployed:

- **CD reacts to a `workflow_run`, not to the push.** It fires only after *MeshWeaver Build and Test*
  completes successfully on a `push` to `main` — so a hand-kicked `workflow_dispatch` of that
  workflow makes main look green while CD still skips. If no image appeared, re-drive CD directly:
  `gh workflow run main-cd.yml --ref main` (it also runs itself 3-hourly as a reconciler).
- **The tag the self-updater acts on is `memex-portal-ai:<version>`** (e.g. `3.0.0-rc1.ci.2470`) —
  `VersionSelect` requires `^\d+\.\d+\.\d+`, so the moving `:main` pointer and the per-run
  `staging-<sha>-<run_id>` tag are invisible to it by construction. Publication is all-or-nothing:
  every leg pushes only the staging tag, and the `promote` job applies the real tags last. Verify the
  IMAGE, never the green tick — `.github/scripts/check-image-set.sh <short-sha>` makes the same
  assertion CD does.

## Never fix it live

A GitSynced space is rewritten from `main` on every sync, so an `edit_content` / `patch` fix
survives only until the next sync and then silently reverts. Change the repo, PR it, sync it. A
live edit is a stopgap to stop bleeding, and it must land in the repo the same session.

See also: `Doc/Architecture/GitHubSync`, `Doc/Architecture/PluginRegistry`,
`Doc/Architecture/NodeTypeCompilation`.
