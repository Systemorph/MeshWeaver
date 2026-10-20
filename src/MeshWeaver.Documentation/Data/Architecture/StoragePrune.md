---
Name: Storage Prune
Category: Architecture
Description: The standing measurement and prune of the volumes that hold built artifacts — what fills them, which bytes the retention contract lets go and which it protects, what the operator command may delete on its own (almost nothing), and why the prebuilt-bundle store could not be pruned on 2026-10-19.
---

# Storage Prune

`hosting-storage-prune` is the operator command that MEASURES a volume holding built artifacts — a
portal's `/data` share today, the control-owned shared artifact store once it exists
([Shared Artifact Store](../SharedArtifactStore)) — and classifies every byte on it against
[Released Artifact Retention](../ReleasedArtifactRetention).

**Status: the command ships with MeshWeaver#7000; the schedule is wired and arms once both halves are live.** The
control instance's `StoragePrune` instance action and the standing job that enqueues one REPORT-ONLY
action per artifact volume in the `ops` queue, recording each run as an ActivityLog, are merged in
MeshWeaver.Plugins (#3993, `Hosting/StoragePrune`). A scheduled run succeeds only once the operator
image carrying this command is on `hosting-operator:main` and control runs a Hosting build containing
#3993; until then nothing measures the fleet on a schedule, and the action stops at "command not
found". Do not direct operators to the action before then.

## Why it exists — measured 2026-10-19

The public instance's 128 GiB `/data` share was **100% full** (1.2 GiB free); the control
instance's sibling was at 91%. Every core CD and module publication failed with *"The specified
share is full"*, and the skipped *Register the publication* step left portals un-updated.

| top-level directory on the full share | size |
|---|---|
| `prebuilt-bundles/` | ~121 GiB (827 identity directories, 1 531 release markers) |
| `modules/` | 4.3 GiB |
| `assembly-cache/` | 0.9 GiB |
| `nuget-cache/` | 23 MiB |
| `dumps/` (the data share's own directory) | 0 — the dump volume is mounted over it, and nothing is hidden underneath |

Inside `prebuilt-bundles/` the one compatibility key `c003e001` held **33.6 GiB** — the plugins source
alone 278 generations — and the ~800 legacy per-build identity directories (`s…`/`g…`, the layout
before compatibility keys) held the rest. Three Crm generations written that day were unsealed: the
publications that failed on the full share.

**The portal's own bundle sweep had collected nothing, and it was right.** `PrebuiltBundleRetention`
aborted every pass with *"the fleet consumer inventory is INCOMPLETE — 6 refusal(s) over 6 expected
instance(s)"*: five instances had never filed a `Hosting/ModuleInventory` report and the sixth filed
a legacy one. The contract says missing consumer inventory prevents deletion, and it did — but the
verdict was a log line in one pod, so nobody saw that the store could not shrink until it was full.
Making that verdict a record on a schedule is the first job of this command.

## The classes

Every item under `prebuilt-bundles/` gets exactly one class:

| class | what it is | disposition |
|---|---|---|
| `bundle-current` | the generation a source's `_current` names, or a sealed flat source of a compatibility key | **protected** — what portals serve |
| `bundle-young` | inside the 30-day minimum age | **protected** |
| `bundle-unsealed-generation` | older, never sealed, not current — a publication that never finished | eligible |
| `bundle-superseded-generation` | older, sealed, no longer current | eligible |
| `bundle-legacy-identity` | an `s…`/`g…` per-build identity directory older than the minimum age | eligible |
| `bundle-other` | anything the walker could not place | **protected** — unreadable is kept |

The release markers (`_releases/`) are counted and never touched.

**"Eligible" is not "deleted".** Deleting a bundle needs, per the contract, the complete fleet
consumer inventory, the protected-label set (below) and the publication lease
(`<identity>/_publishing.<source>`, the Azure Files lease `publish-bake-bundles.sh` holds) taken by
the cleanup itself. A pod has none of these, so the operator command never deletes under
`prebuilt-bundles/`. The control instance's sweep decides — and while any of the three is missing it
reports the eligible bytes and deletes none.

## What the command may delete on its own

Only with `--execute`, which mounts the claim writable (a report mounts it read-only):

- **`cache-tmp`** — `assembly-cache/**/.tmp-*` older than a day: an interrupted write of the
  content-addressed assembly store, which no reader resolves.
- **`misplaced-dump`** — files on the data share's own `dumps/` directory older than seven days, and
  only while the portal mounts a *separate* dump volume over `/data/dumps` (read off the portal
  Deployment). Then the portal cannot see those bytes at all.

Every deletion is re-checked at the moment it happens in the pod: the path must resolve under the
mount (a symlink cannot walk out), still be a regular file, and still be older than its window. A
file that fails any check is kept and reported as kept. `assembly-cache/` content, `nuget-cache/` and
`modules/` have their own collectors in the portal (`AssemblyCacheRetention`, `ModuleGenerationsGc`)
and are only measured here.

## The protected-label set

Images and builds carrying an edition or channel label — `3-latest`, `3-mac`, the enterprise and Mac
editions — are never pruned (maintainer rule). The label set is declared ONCE, in core
`.github/release-channels.json` (policy `declared-release-channels`, MeshWeaver#6985), and read through
`.github/scripts/release-channels.py protected-patterns` / `is-protected <tag>` — never hard-coded in a
pruner. Exit code 2 means the lookup failed, and a failed lookup ABORTS the sweep: an unreadable
protected set licenses no deletion. The control instance's sweep expands every labelled image to the
platform version and the publications it needs, and protects that closure; the operator command never
deletes a bundle at all, so it needs no label lookup.

## Running it

```
hosting-storage-prune --namespace <ns>                       # the portal's /data claim, read-only
hosting-storage-prune --namespace <ns> --claim <pvc>         # a named claim (the shared artifact store)
hosting-storage-prune --namespace <ns> --execute             # also delete cache-tmp / misplaced-dump
```

Machine lines: `storage_size_kib`, `storage_used_kib`, `storage_bundle_kib`, `storage_eligible_kib`,
`storage_deletable_kib`, `storage_deleted_kib`, and last `storage_report` (gzip + base64 JSON, inside
the operations lane's log tail).

## Related

- [Released Artifact Retention](../ReleasedArtifactRetention) — the contract every class above answers to
- [Prebuilt Bundle Retention](../PrebuiltBundleRetention) — the portal-side sweep and its inventory gate
- [Shared Artifact Store](../SharedArtifactStore) — the one control-owned store this command will sweep
