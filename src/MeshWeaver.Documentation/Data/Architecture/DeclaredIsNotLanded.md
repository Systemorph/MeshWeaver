---
Name: Declared Is Not Landed
Category: Architecture
Description: An install record is a declaration, never an observation. Three exits can write it and until now only one of them looked at the mesh — so a node lost after an install could survive every subsequent update, each reporting success. The measured case on memex.meshweaver.cloud, the exit that was blind, and the sweep hazard that made the damage read smaller than it was.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 4h10l6 6v10H4z"/><path d="M14 4v6h6"/><path d="M8 14h5"/><path d="M8 17h3"/></svg>
---

# Declared Is Not Landed

An install record is a statement about the **source**. It names the files a bundle shipped and the
hash of each. Nothing in it observes the mesh, and nothing in the installer's own counters does
either — `InstallResult(Total, Written)` counts what the installer *decided* to write.

That gap has been closed one exit at a time, and this page is about the exit that was still open.

## The three exits an install can take

`CatalogLayoutAreas.InstallOrUpdate` chooses among three, on two questions: does the record's
`ModuleVersion` equal the candidate's, and does the record carry a file map?

| Exit | Taken when | Does it look at the mesh? |
|---|---|---|
| **Skip / heal** | the module hash is EQUAL | **Yes**, since MeshWeaver#3485 — `InstallCompleteness.Observe` reads every declared node path and `SkipOrHeal` runs a full install rather than skipping an incomplete one |
| **Full install** | no record, or a record with no file map | **Yes** — `DecideAndWrite` writes whenever `current is null`, so everything absent is restored |
| **Incremental update** | the module hash MOVED and the record has a file map | **It did not.** Fetched exactly `newManifest.DiffFrom(record.InstalledFiles)` |

The incremental exit's fetch set was a comparison of two *declarations* — the candidate's
`manifest.lock` against the record the installer itself wrote. A node lost after a previous install
has an unchanged file hash, so it never entered `delta.AddedOrChangedFiles`, was never fetched, and
never reached `DecideAndWrite` — the one place that would have restored it. **The
presence-awareness sat one layer below a set the absent file could not enter.**

## 🚨 And it is the exit an update always takes

This is what turns a one-off loss into a permanent one. #3485's heal lives on the module-hash-EQUAL
exit. An *update* never takes that exit by definition, and every new publication moves the hash
again. So a package that loses a node stays short across arbitrarily many updates, each one
reporting success, and the only escape is a boot on which the source's hash happens to equal the
record's **and** the install lane happens to run.

## The measured case

`Plugins/Hosting` on **memex.meshweaver.cloud**, read 2026-09-14T06:0xZ. All reads through `get` /
`search`; nothing was mutated.

**The morning: the files were there and they compiled.** The release node
`Hosting/TriageStatus/Release/20260913081235-bylQeIj_` — created on that portal at
2026-09-13T08:12:35.446Z, `status: Succeeded` — names its inputs:

```
sourceVersions:
  Hosting/Deployment/Source/TriageIntake                 639248839464110360   (2026-09-13T08:12:26.411Z)
  Hosting/TriageStatus/Source/TriageStatusContent        639248839461605390   (…08:12:26.160Z)
  Hosting/TriageStatus/Source/TriageStatusLayoutAreas    639248839464108660   (…08:12:26.410Z)
testVersions:
  Hosting/TriageStatus/Test/TriageStatusTests            …
  Hosting/TriageStatus/Test/TriageStatusTestsArea        …
```

A release records the source nodes a successful compile actually read. So those five nodes existed
on this portal, nine seconds before the compile that consumed them.

**The evening: the update ran, and wrote only what had changed.** The record was stamped at
2026-09-13T22:03:03.221Z — `installedNodeCount: 224`, `version 1.19.16`. Two nodes carry write
timestamps from that install:

| node | version | written |
|---|---|---|
| `Hosting/Deployment/Source/AksOpsResult` | 1 (new file) | 2026-09-13T22:02:54.601Z |
| `Hosting/Deployment/Source/PlatformBuildInboxWatcher` | 16 (changed file) | 2026-09-13T22:02:57.255Z |

Everything else in `Hosting/Deployment/Source` still carries its older timestamp — 08:09:41,
09-12, 09-09, 09-08, 08-28, 08-19. The update wrote exactly the two files whose content had moved.

**And eleven declared files had no node at all.** Anchored reads, each with a same-anchor positive
control:

| probe | result |
|---|---|
| `namespace:Hosting/Deployment/Source scope:subtree nodeType:Code` | **14**, `truncated: false` — no `TriageIntake` (the other 14 are the control: readable, same query shape) |
| `namespace:Hosting/Deployment/Test scope:subtree nodeType:Code` | **3** — no `TriageIntakeTests` |
| `namespace:Hosting/TriageStatus scope:subtree` | **1** — the Release node alone; no `Source/`, no `Test/` |
| `namespace:Hosting/TriageItem scope:subtree` | **1** — the Release node alone |
| `namespace:Hosting scope:children nodeType:NodeType` | **19**, `truncated: false` — the denominator |
| `namespace:Hosting scope:children nodeType:NodeType content.compilationStatus:Error` | **8 of 19** |

All eleven are named in the record's own `installedFiles` map. The record asserts them; the mesh
does not hold them; the update that stamped the record did not fetch a single one.

## 🚨 The sweep hazard: 8 of 19 understates it, and the 9th reads green

`Hosting/TriageItem` is **not** in the Error set. Its own node says:

```
compilationStatus:        Ok
lastCompileSucceededAt:   2026-09-13T08:12:35.5789283Z
lastCompiledVersion:      6
requestedReleaseAt:       2026-09-13T21:56:05.9665591Z
lastReleaseRequestHandledAt: 2026-09-13T21:56:05.9665591Z     ← handled without a recompile
lastCompileStartedAt:     2026-09-13T08:12:32.047279Z          ← still the morning's
compiledSources:          6 entries — TriageActions, TriageItemContent, TriageItemLayoutAreas,
                          Hosting/Deployment/Source/TriageIntake, and two Test nodes
currentSourceVersions:    {}
currentSourceFingerprint: e3b0c44298fc1c14                     ← SHA-256 of the EMPTY STRING
```

Every one of those six `compiledSources` is now absent from the mesh, and the type serves a cached
assembly at `compilationStatus: Ok`.

**So `content.compilationStatus:Error` is a sweep for types that have *attempted* a compile and
failed. A type whose sources all vanished but which has not been re-driven since is in the GREEN
population.** The honest statement of this portal's damage is not "8 of 19 are broken" but "8 of 19
report Error, and at least one more has lost every source it compiled from while reporting Ok".

This joins the list of reasons a `compilationStatus` sweep's number is not a census — alongside the
RLS filtering and the two-executor split already recorded in
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation). The cross-check that does see it is
the record-vs-mesh comparison on this page: `compiledSources` (or the release node's
`sourceVersions`) against what the mesh holds.

## The fix

`IncrementalUpdate` now asks the mesh before it fetches.

1. Derive the candidate manifest's declared node paths — `newManifest.Files.Keys` through
   `PackageInstaller.NodePathForFile`, the installer's own file→node rule and not a second
   implementation of part of it.
2. `InstallCompleteness.ObservePresent` — **one** batched `IStorageAdapter.ReadMany` over that
   bounded, known set. Never a query (a stale negative would re-fetch a file that is present) and
   never N point reads of possibly-absent paths (which is what opens a storm breaker on the owning
   hub).
3. `InstallCompleteness.FilesToRestore` — pure, so every arm is pinnable offline — returns the
   declared files the delta is not already fetching whose node is absent.
4. The fetch asks for `delta.AddedOrChangedFiles ∪ restore`. `DecideAndWrite` then writes them,
   because `current is null`.

It costs one batched read per incremental update — the same read
[Install Completeness](/Doc/Architecture/InstallCompleteness)'s post-install verification already
pays on this exit, moved to where it can still change the outcome instead of only reporting it.

### 🚨 Three answers, never two

`ObservePresent` returns `null` when the mesh was **not read** — no storage adapter, or a batched
read that faulted — and never an empty set. An empty set would say "the mesh holds none of them",
which on a Postgres read that faulted (a satellite table that does not exist answers `42P01`, not
"absent") would make an install re-fetch every file it ships, every time. `FilesToRestore` maps that
`null` to an empty restore set, and the caller **says so at Warning**: an unobserved mesh is not a
clean one, and the line is spelled so it cannot be read as a pass.

### The other silent hole in the same method

`IPackageSource.FetchPackageFiles(package, gitRef, paths)` filters — locally in the interface
default, server-side in `RegistryPackageSource` — and its contract is explicit that *"paths absent
from the package simply don't appear in the result"*. A requested path the source does not serve (a
serving-side casing difference, a stale registry cache, a bundle that disagrees with its own lock)
therefore vanished with no exception and no log line, after which the record was stamped with the
full declared map anyway. The returned set is now compared against the requested one and the
shortfall is named at Error. It reports only; it never fails the update — the install that did run
is a fact, and collapsing "landed short" into "failed" would make a working update a new way to
break.

## The rule this generalises to

> **A decision about what the mesh needs may not be taken from a record the writer wrote.** Any
> comparison whose two sides are both declarations — a lock against a record, a hash against a hash,
> a count against a count — can only tell you what the *source* did. If the decision is "does this
> have to be written", exactly one of the two sides must be a read of the mesh.

The same sentence is why #3485 exists, why the post-install verification exists, and why this page
does. Each of the three closed it on one exit; none of them closed it as a principle, which is how
the third exit stayed open through two fixes aimed at the same defect.

## What this does NOT do

- **It does not explain why the nodes went missing.** The release node proves they were written and
  compiled; something removed them between 2026-09-13T08:12:35Z and 21:56:07Z, and no instrument a
  session can run names it. This change makes the loss *self-correcting on the next update* instead
  of permanent — it is not a diagnosis of the loss.
- **It does not re-arm a PARKED NodeType.** A type that failed a compile serves its cache and does
  not re-drive itself when sources arrive; that is MeshWeaver#4208's subject. Restoring the content
  and recycling the type are two steps, and on an image without #4208 they stay two steps.

## See also

- [Install Completeness](/Doc/Architecture/InstallCompleteness) — the five verdicts, only one of
  which is a pass, and the post-install read-back that reports the outcome on every writing exit
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — what a compile reads, what a
  release records, and the other reasons a `compilationStatus` sweep is not a census
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the presence read here is
  a batched store read and never a query
