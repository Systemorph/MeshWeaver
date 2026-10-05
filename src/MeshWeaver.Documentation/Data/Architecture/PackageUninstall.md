---
Name: Package Uninstall
Category: Architecture
Description: One request — "uninstall package P on this instance" — in two phases. Phase 1 retires the module, closes the package's hubs, removes its install record, blocks every unattended re-install and previews exactly what would be destroyed; phase 2 drops the partition storage and its registry record only when the requester confirms with the exact partition name.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M6 6l1 14h10l1-14"/></svg>
---

# Package Uninstall

> **The rule (policy `package-uninstall-request`, [register](../PolicyNotProse)).** A platform admin
> uninstalls a package through ONE durable request, in two phases. Phase 1 needs no confirmation and
> destroys no data. Phase 2 — the irreversible drop of the package's partition — runs only after the
> requester repeats the partition name; without that the package stays uninstalled with its data
> retained, and the request says so.

It sits beside [Module Reload](../ModuleReload) and has the same shape: a node at
`Admin/_PackageUninstall/{id}` (`PackageUninstallRequest`), written only by `PackageUninstall.Request`
as System after the caller was authorised, executed by `PackageUninstallExecutor` on the request
node's own hub, every step a `stream.Update`.

## Phase 1 — uninstall, retain the data

Refused by name, before anything is touched:

- no `Plugins/{package}` install record here;
- the target partition is **shared** with another installed package;
- the platform never tears the partition down (`PartitionTeardown.Refusal`: a mirror, a configured
  partition, an invalid segment);
- the partition holds **user data** — any node whose `createdBy` is not the installer's system
  identity (the first five are named) — or more nodes than the uninstall verifies (20 000).

Then, in order: the **module** is retired (its landed generation disabled; unloaded live through
`IModuleLiveActivation.Retire` where the loader can, else exactly one automatic restart through the
self-update restart path, stamped first); the partition's **hubs are closed** (`DisposeRequest` to
every hosted hub at or under it, from the off-router issuing hub); the **install record is
removed**; and the request moves to `AwaitingConfirmation` with the **preview** — per partition:
whether a storage provider reports a per-partition store (Postgres: the schema), rows per table
(`mesh_nodes`, and each satellite segment `_Access`, `_Thread`, …), whether a sync configuration
(`{partition}/_GitSync`) is present, and what cannot be counted here, named:

- compiled assemblies cached on disk for the partition's NodeTypes;
- search / vector index entries kept outside the partition's own store;
- blob / content-collection storage kept outside the partition's own store.

From this point **no unattended pass installs the package again** — not the seed, not the platform
baseline, not a feature flag (`InstanceAutoRegistrationService.InstallAll` consults
`PackageUninstallExecutor.UninstalledHere`). A person installing it again lifts that: the block only
applies while no install record exists.

## Phase 2 — drop the data, on confirmation

`ConfirmationRequired` is the partition name. The requester sends it back (`PackageUninstall.Confirm`,
recorded with who and when). A different string, or a confirmation from anyone but the requester, is
refused by name (`confirmationRefusal`) and nothing is dropped. A matching one runs the platform's
governed whole-partition teardown, as System, after closing the partition's hubs again:
`PartitionTeardown.TearDownPartition` — the store dropped on every storage provider (Postgres
`DROP SCHEMA … CASCADE`, satellite tables with it, so the `_GitSync` configuration and the
partition's NodeType nodes go too), the cached queries anchored to it evicted, and the
`Admin/Partition/{partition}` registry record deleted. The request records each partition's
teardown sentence and ends `Done`. Never raw SQL.

## Surfaces

| surface | how |
|---|---|
| MCP `uninstall_package` (MeshWeaver.Plugins) | `MeshOperations.UninstallPackage(package, reason)` answers the preview and the exact confirmation string; `UninstallPackage(requestPath: …, confirmation: …)` confirms. Platform admins only. |
| the platform's own code | `PackageUninstall.Request` / `PackageUninstall.Confirm` |

## What is NOT established

- **The Store dialog is not wired to this.** The Store's `DialogAction.Uninstall`
  (`Localizer.Uninstall`, MeshWeaver.Plugins) removes a *viewer's localized copies* of a course from
  that viewer's own space — a different operation from removing a platform package, its module and
  its partition. Extracting it into this engine would change what the dialog does to every course
  learner, so it was left alone; a package-page entry for this request is owed.
- **A Postgres run.** The tests run on the monolith's in-memory store, where the teardown sweeps the
  partition's rows; the Postgres `DROP SCHEMA` path is the platform's existing one and was not
  exercised here.
- **Other replicas' hubs.** Hubs are closed on the executor's process; a hub another replica hosts
  under the partition is torn down by the store drop's write refusal and its next activation, not
  by a `DisposeRequest` from here.
- **A resumed phase 1.** If the executor's activation dies after removing the install record but
  before recording the preview, the resumed phase 1 reads no install record and fails by name; the
  partition is untouched and a new request is needed for phase 2.
