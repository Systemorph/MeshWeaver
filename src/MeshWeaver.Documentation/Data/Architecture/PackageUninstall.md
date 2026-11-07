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
  identity (the first five are named) — or more nodes than the uninstall verifies (20 000);
- any node under it **cannot be read** while it is measured: a fault is not an absence, so the
  partition is not verified free of user data (the first five faults are named).

The module's activation is read **whole or not at all** (`ModuleLandingService.GetCheckedActivation`).
An unreadable activation record would make a landed module look like it was never landed, and phase 1
would remove the install record with the generation still enabled. So an incomplete read, or a
removal that fails, **fails** the request and keeps the install record. An entry that is **already
disabled** counts as done: a phase 1 resumed after its removal records `ModuleDisabled`, never "never
landed".

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

## The install record decides who may write it

An uninstall and the unattended lanes write the same node, the install record `Plugins/{package}`,
from different hubs. Every writer that is not the record's own hub runs its `stream.Update` lambda on
its own copy of the node, so a check like "is this package being uninstalled?" made there can be stale
([Conditional Writes Across Hubs](../ConditionalWritesAcrossHubs)). So no writer decides. Each writer
states a **fact**, unconditionally and under its own key (`PackageManifest.RecordFacts`). The
record's **own hub** folds every pending fact against current state (`InstallRecordGate.Fold`,
serialised there) and answers each one under the same key (`FactOutcomes`). The writer reads its
answer and removes it.

| fact | stated by | the owner |
|---|---|---|
| `Begin` | phase 1, after its refusals and the preview | grants `RetiringBy` (this request) unless another request holds it; phase 1 proceeds only when granted, and touches nothing otherwise |
| `Remove` | phase 1, just before it deletes the record | marks the retirement `Removing`; refuses when this request no longer holds it (a person installed the package meanwhile), and phase 1 then stops and keeps the record |
| `End` | phase 1, when it faults after `Begin` — and when `Begin`'s answer never arrived (the owner may have granted it) | releases the retirement, so the package is repairable again, and remembers the request for the retention window so a LATE `Begin` of it is refused |
| `Stamp` | every install-record write: the install funnel, the repair, the unattended update | applies it, or **refuses** it while a retirement is held. A person's stamp (`PackageRecordWriteIntent.Person`, the catalog click) clears a retirement that is not yet `Removing`, and is applied |

| `PersonClear` | the install funnel, at the end of every exit of a person's install — including the up-to-date skip, which writes no record | clears a retirement that is not yet `Removing`; refused while the record is being removed |

Every submission correlates its answer under a FRESH key, so an answer left behind by an earlier
attempt is never read as this one's; the owner is idempotent on the request, not on the key. A write
intent the gate does not know is refused by name — by the writer before anything is created, and by
the owner for a stamp — never given the default's semantics. A delete of the record racing a
submission (an ended reader, a routing NotFound, a failed stating write) is judged by whether the
record is gone: gone is the typed refusal, present is an unrelated fault and propagates.

A refused stamp reaches its writer as `PackageRecordWriteRefusedException`, carrying the owner's
words; the boot install records it as a skip, never as a silent success. A write through the gate is
**durable when it completes**. The writer removes its answer with a cross-hub `stream.Update`, which
acknowledges only after the owner has written the node to storage, fold included, and the writer
waits for that before it returns. So every later storage read of the record sees the stamp, as it did
when the record was written with a full-replace `CreateOrUpdateNodeRequest`. The gate issues every write from
the mesh's off-router execution hub, never from the record's own hub, even when that hub is the
caller: an own-hub update acknowledges the in-memory commit, not the durable write. If that
durability step fails for an applied stamp, the write **faults** instead of reporting it as applied.
A refused stamp stays a refusal. If no answer arrives within
`InstallRecordGate.AnswerBudget`, the writer re-reads the record. Absent means it was removed and the
write is refused; present means a fault naming the owner that did not answer.

**Re-creation is closed one level down.** A stamp is a `stream.Update`, and a delete wins over an
update that commits after it (`UpdateNeverResurrectsADeletedNodeTest`). An absent record is created
(create-only, `CreateNodeRequest`) only by `CreateAllowed` (the default install of a package the
instance does not hold) or `Person`. A create that collides falls back to a stamp. A `Restamp` (the
repair, the unattended update) is refused when the record is gone. This is how this page answers the
create-if-missing-plus-fold gap (#4928): the record is never written with a full-replace
`CreateOrUpdateNodeRequest`.

The partition's content is still written **before** an install stamps the record, and a stamp
refused at the owner does not undo it. The module no longer shares that gap; see the next section.

## The module lands through the install record

An install writes its record, then downloads and lands the module. A download that started before
an uninstall could finish after phase 1 had written the module's **tombstone**. Its landing then
re-enabled the module on every replica, because the module volume is shared. The decision to land
was taken on a snapshot read before the download and carried out after it.

So nothing outside the record's own hub lands or removes a package's module
(`ModuleLandingOwner`):

- **The writer stages and states.** The downloaded bytes cannot ride the node, so the writer stages
  them on the shared volume under `modules/.landing-requests/<key>/`. It then states a `ModuleLand`
  fact on the record carrying every other landing argument, and waits for the owner's answer before
  it does anything that follows a landing (proposing the module set). Phase 1's removal is a
  `ModuleRemove` fact through the same owner.
- **The owner queues, then decides one at a time.** The fold queues each operation in
  `PackageManifest.ModuleOperations`. It promotes exactly ONE to `Executing`, and decides it then,
  against the retirement as it stands at that moment. A landing while the package is being retired is
  refused. A person's landing (`PackageRecordWriteIntent.Person`, the catalog click) lands while the
  retirement is not yet `Removing`, and clears it, the same rule as a person's stamp. The owner also
  marks it `ReinstallByPerson`, so it lands even over a tombstone the displaced uninstall already
  wrote. Without that, the person would keep an install record whose module stays disabled. The
  plan agrees: for a person's install, `ModuleUpdateDecision` reads an entry an uninstall disabled as
  never landed. `SkipUninstalled` holds only the lanes no person asked for. A removal
  by a request that no longer holds the retirement is refused. An install record lands and removes
  only the module it declares (`PackageManifest.Module`); an operation naming any other module is
  refused.
- **Only an install record executes.** The Package node type is armed wherever a Package node lives.
  Only a hub at `Plugins/{package}` executes module operations, and it checks each operation again
  against its own current state just before running it: the module must be the record's, and a
  removal must still be the retirement holder's. So a persisted `Executing` entry never bypasses the
  decision, whether it was written by the owner before a crash or forged.
- **The owner executes.** The record's hub runs the landing or the removal itself, on its own replica,
  through the landing service's cap-1 lane. The execution belongs to the hub's activation and is
  disposed with it, so a recycled owner cancels its landing at the landing's own cancellation points. It writes the answer under the operation's key and
  promotes the next operation in the same update. Operations run one at a time and in the order they
  were stated, so a removal stated after a landing runs after it, and the tombstone is written last.
- **A landing is decided where it is written.** The landing reads the module's head in the same work
  item that writes the bytes, and refuses when the module's newest event is an uninstall. This is the
  rule `ModuleUpdateDecision` applies at plan time (`SkipUninstalled`), taken again after the
  download.
- **Resume.** The operation in flight is persisted as `Executing` before it runs. Its staged bytes are
  dropped by the WRITER, and only once it has an answer. The writer removes its answer from the
  record with a cross-hub update, which acknowledges only after the owner's state is in storage; it
  drops the bytes after that. So storage never holds an `Executing` operation whose bytes are gone. A
  submission whose outcome is unknown (a timeout, or an unreachable owner whose patch may still apply)
  leaves the bytes in place for the owner. When a replica dies mid-operation, the next
  activation of the record's hub finds an executing operation it did not start, and runs it again. A
  landing re-reads the same staged bytes, and a removal writes one more tombstone. Neither changes the
  outcome by running twice.
- **A refusal is an answer.** A refused landing reaches the adoption as its named `Failure`, with
  nothing landed. A removal the owner did not carry out faults with the owner's words.

**A person's install waits for a removal in flight.** A stamp or a person's clear is refused while
ANY removal of the module is `Executing`, including one whose uninstall has already released its
retirement (a writer that timed out and stated `End`). That removal may be waiting on the
shared landing lane behind another module, and would disable the module after the person's install
read it as current. Clearing the retirement then would keep the record and lose the module. The
person installs again once the uninstall finishes.

**Phase 1 always queues the removal.** When phase 1 finds no activation entry, the module may
still be landing for the first time on the owner. So phase 1 queues the removal anyway. The owner
decides whether a generation exists once every earlier landing has finished, and answers
`ModuleLandingOwner.NothingToRemove` when none does.

**No record path means the canonical one.** An adoption that names no install-record path belongs
to `Plugins/{package}`, so it never lands around that record's owner by omission.

**A landing that loses its record is refused.** The adoption notes whether the install record
exists when it STARTS, before the plan reads the registry index and the activation, and so before
the download. If the record existed then and is gone when the bytes arrive,
an uninstall removed it while the module was downloading, and the landing is refused. This holds even
for a module that never landed, where no tombstone exists to refuse it.

**A package with no install record has no owner.** The boot's modules-first wave lands a fresh
package's module before any record exists. A retirement lives on the record and ends when the record
is deleted, so a package with no record has no retirement in flight. That landing runs where it is,
through `ModuleLandingService.LandUnlessUninstalled`. It is refused when the module's newest event is
an uninstall, which covers a download released after an uninstall that has completed. It never
creates a record. It is never a fallback for a package that does have one: the writer reads the record
first and, if it exists, goes through the owner.

What the fresh path does not close: its uninstall check and its write are one work item on one
replica. A removal another replica writes **inside** that work item, between the read and the write,
would be ordered before the landing. For that to happen, a record must be created, a retirement
granted and the removal executed within that interval. The owned path has no such window, apart from
one case: when an owner is torn down after a landing has passed its commit rename, that landing
finishes its record write. On Orleans a successor activation starts only after the old one has been
disposed, and on a single process the successor's landing queues behind it on the same cap-1 lane.
A staged landing whose writer died before taking its answer, or whose submission's outcome is
unknown, is not collected by the modules GC.

## Phase 2 — drop the data, on confirmation

`ConfirmationRequired` is the partition name. The requester sends it back (`PackageUninstall.Confirm`,
recorded with who and when). A confirmation is accepted only while the request AWAITS one — it answers
the preview, so one sent before the preview exists is refused and records nothing, and phase 1 clears
any confirmation it finds and stamps `awaitingConfirmationAt`. The executor is the authority: a
different string, a confirmation from anyone but the requester, a request that names no requester, or
a confirmation older than the preview is refused by name (`confirmationRefusal`) and nothing is
dropped. A matching one runs the platform's
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
