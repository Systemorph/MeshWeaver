---
nodeType: Markdown
name: Gate Content Assets
category: Architecture
description: Why a package's committed content/** binaries are part of the node-repo gate's verdict, and the host shape that made them invisible to it for months.
icon: /static/NodeTypeIcons/box.svg
---

# Gate Content Assets

A package ships more than nodes. Its **binaries** — course videos and their posters, og cards,
fonts — are ordinary files committed under `{package}/content/**`, and installing the package is
only half-done until they are being *served*. The other half is
[`PackageInstaller.SyncPackageContent`](/Doc/Architecture/CqrsAndContentAccess): it classifies those
files with `ContentAssetMapper`, and posts **one `SyncContentFilesRequest` to the package's
partition ROOT**, where a portal mounts the `content` collection that children inherit.

This page is about the host shape that request needs, and about what the `mw-plugin-test` gate now
asserts because of it (issue #3424).

---

## The handler comes from exactly one place

`SyncContentFilesRequest` is handled by `ContentImportExtensions.AddContentImportHandler`, which is
reached **only** through `AddContentCollections()` / `AddContentCollectionsInfrastructure()`. So a
per-node hub either called one of those or it answers *"No handler found for message type
`SyncContentFilesRequest`"* — a `DeliveryFailure`, not a content verdict.

Three hosts, three different answers, and it is worth being precise about which is which:

| Host | Where the handler comes from | Where the `content` collection comes from |
|---|---|---|
| Portal (`memex`, `memex-cloud`) | `MemexConfiguration`'s `ConfigureDefaultNodeHub` maps `attachments` on **every** per-node hub, which calls `AddContentCollections()` | the same lambda mounts a writable `content` collection on the partition ROOT only (`!nodePath.Contains('/')`), `ExposeInChildren = true` |
| A `Space`-typed root anywhere | `SpaceNodeType`'s own `HubConfiguration` calls `AddContentCollections()` | nothing — the handler answers, then fails *"Target content collection 'content' not found"* |
| The gate mesh, before #3424 | nothing: `AddGraph()`'s default node chain does not call it, and neither does the `Store/Plugin` NodeType most package roots declare | nothing |

The distinction between the last two matters when reading a log. **"No handler found"** means the
publish was refused before the collection question was ever asked; **"collection not found"** means
the host heard the request and has nowhere to put the bytes. Both end as the same installer warning
— *"the package's nodes are installed but its binaries are not being served"* — because the publish
**logs and continues** rather than throwing (a package whose nodes landed and whose binaries lag is
strictly better than a half-written package).

## What that cost

Measured on two `MeshWeaver.Education` bakes on 2026-09-06, and independently on
`MeshWeaver.Reinsurance`'s `test-repos` job the same day: **15 packages, 30 refusals per run,
exit 0**. Every package that ships `content/**` — AppleMusic, BusinessRules, Chess, Collaboration,
DataModelling, DoublePendulum, Edu, Essentials, Feedback, FractalStars, Google, Publish, RolePlay,
ThreeBody, Training — installed its nodes and then had its binaries refused, twice (the installer
re-asks once after the root recycles). Four of them additionally surfaced as
`ContentDeliveryRefusedException`, because their largest asset exceeds the inline per-delivery
budget and the [out-of-band transfer](/Doc/Architecture/OutOfBandContentTransfer) has to ask the
owning node for its collection config first.

Two separate problems, and only one of them is about noise:

1. **The gate never exercised the path that serves a package's binaries.** A package whose
   `content/**` is broken — a wrong path, a video its course's `<video src>` points at that never
   lands — passed. The bake verified 101/101 assemblies and zero bytes of content.
2. **Thirty warnings a run that read as a production incident**, on every satellite, unchanged from
   run to run, saying nothing about the change under test.

The fix is (1). (2) follows from it, and *only* from it: the condition stops occurring because the
publish now succeeds. Lowering the log level would have been the forbidden move — the levels reflect
the production cost model, and on a real portal that warning is exactly right.

## The gate now hosts content the way a portal does

`PluginGateRunner`'s mesh gained one `ConfigureDefaultNodeHub` lambda that mirrors the portal shape,
both halves:

- **every** per-node hub calls `AddContentCollections()` — a child reads its Space's collection
  through its own hub, so the infrastructure cannot be root-only;
- a **partition root** (a single-segment node path) additionally mounts a writable, `IsStatic`,
  `ExposeInChildren` FileSystem `content` collection under the run's own temp root
  (`{runRoot}/content/{node}`), so two concurrent gate runs cannot see each other's bytes and the
  whole thing dies with the container.

Nothing here is gate-specific behaviour: it is the production mount, pointed at a throwaway
directory.

## …and holds a verdict on it

Exercising the path is not enough on its own. The publish logs-and-continues, so a gate that merely
*ran* it would go green on a host where nothing landed — which is precisely how #3424 survived. The
accounting is therefore structural, not a log line:

- `PackageInstaller.ContentPublication` carries the target **root**, how many assets the install
  **carried**, and the collection-relative **path of every one published**. It rides out on
  `InstallResult.ContentRoot` / `ContentAssets` / `ContentAssetsPublished`.
- The gate compares carried against published, and then **reads every published asset back through
  [`ContentFileResolver.Resolve`](/Doc/Architecture/ContentRoute503)** — the one server-side reading
  of a content reference, i.e. what a course's `<video src>` actually resolves through: the owning
  node's own hub answers with its collection config (gated by an ordinary node `Read`), and the
  collection then either has bytes at that path or does not. The probe reads the file's **size**,
  never its bytes — a course video is tens of megabytes and existence is the whole question.
- The result is a `content` check beside `install` and `idempotence`: `PackageResult.ContentError`
  plus the two counts, printed in the summary as `[N/M content asset(s) served]`, carried across the
  process boundary on `GateRunPackage.ContentError` for the combo verifier, and ratchetable in an
  allow file as `<package> content`.

🚨 **The counts are the point, not the absent error.** A run in which the content check looked at
nothing reports the same "no error" as a run that verified two files; only `0` versus `2` tells them
apart. This is the same rule `PackageResult.CountsMeasured` encodes one column over — a green check
has to say what it verified.

## What it does not cover

- **A package that ships no `content/**` at all** reports `0` carried and never fails. The check
  cannot red a package for not having binaries.
- **The incremental install path** syncs only the *changed* files, so its accounting is over that
  delta. An unchanged asset is neither carried nor re-published, and reporting it as missing would
  red a package that changed nothing.
- **`ROUTER_TRAFFIC: RawJson has the mesh hub as sender`** is a *different* defect and was not
  involved: the content sync posts through `NodeOperationIssuingHub()` precisely so the router is
  neither end of the pair, and a sweep of eleven job logs (~15,000 lines) across both repos on
  2026-09-06 found **zero** ROUTER_TRAFFIC lines beside these refusals. The historical sighting of
  that line came from the root-recycle `DisposeRequest` on a different repo's run, and was fixed the
  same way.

## Related

- [CI Content Bake](/Doc/Architecture/CiContentBake) — what the gate's other half verifies.
- [Out-of-Band Content Transfer](/Doc/Architecture/OutOfBandContentTransfer) — how an over-budget
  asset travels, and why it needs the destination collection resolved on the producer.
- [Content Sync Visibility](/Doc/Architecture/ContentSyncVisibility) — what a refusal reports.
- [Content Route 503](/Doc/Architecture/ContentRoute503) — the read path the gate verifies against.
