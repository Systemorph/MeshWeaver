---
Name: DeploymentInventory
Category: Architecture
Description: Every instance reports what it runs — platform build, commit, framework identity, update policy and every module's pinned coordinate — to the control instance's fleet inbox, automatically and hourly
Icon: ClipboardTaskListLtr
---

# Deployment Inventory — every instance reports what it runs

**The question "which instance runs what?" is answered by the instances, not by whoever remembers.**
Each portal carries a hosted reporter (`DeploymentReportService`, in `MeshWeaver.PluginCatalog`) that
composes one report — the platform build it serves, the framework identity its bundles are keyed on,
the self-update policy it follows, and one row per module it carries with that module's pinned
coordinate — and files it with the control instance once the default install has settled and then on a
fixed cadence. The control instance folds every report into the **Fleet** view and the Deployments
board (`Hosting/ModuleInventory` in MeshWeaver.Plugins), where drift between instances is a table row,
not an investigation.

## Why the instance reports itself

An instance is the only party that can answer this about itself: the control instance cannot read
another installation's nodes, and giving it a way to would be a far larger permission decision than a
fleet inventory deserves. The previous design had the same shape but a human in the loop — a Code node
on the control instance (`Hosting/Script/report-modules`) an operator ran by hand. Measured on
2026-08-19, it had never run on any instance: zero inventory records, nine days after the type shipped.
A report nobody sends is an inventory nobody has. This service is that report on a clock.

## Configuration

| Key | Meaning | Default |
|---|---|---|
| `Hosting:Deployment` | This instance's id — **must equal the id of its `Hosting/Deployment` record** on the control instance. Unset ⇒ the reporter is **inert** (a report under a guessed id would overwrite another instance's inventory, and the fleet page renders that as fact). | — |
| `Hosting:ReportTo` | Base URL of the control instance. Unset ⇒ this instance **is** the control instance and writes the record locally. | — |
| `Hosting:ModuleReportSecret:{deployment}` / `Hosting:ModuleReportSecret` | The HMAC key the report is signed with — per-deployment first, fleet-wide fallback, exactly as the receiver resolves it. `ReportTo` set with no secret ⇒ a **warning** and nothing sent: the receiver drops unsigned reports, so sending one would only look like reporting. | — |
| `Hosting:OperationalSpace` | The partition a local write lands in. Must resolve the same way on both ends or the control instance files its own record beside, not among, the reported ones. | `Ops` |
| `Hosting:ReportInterval` | Cadence, as a `TimeSpan`. | `01:00:00` |

Deployment configuration lives with the deployment — for the installations Systemorph runs, in the
private `Systemorph/Memex` repo's per-environment values, never in this repo.

## Cadence

1. **Boot:** the first report waits for `InstanceAutoRegistrationService.Completed` — the default
   install has settled. Reporting mid-install would file "this instance carries nothing" for an instance
   about to carry the baseline.
2. **Then every interval.** Reports run one at a time. A failed tick is a **warning carrying the
   fault** (a rejected POST names the status and the receiver's answer), never a silent skip and never
   the end of the schedule — the next tick reports again.
3. **On demand:** `DeploymentReportService.Report()` composes and delivers one report now and emits
   where it went (`Skipped` with the reason, `Written` with the path, `Sent` with the URL and status).

## What a report carries

The module list is `InstanceComboReader`'s combo — the **one** reader of "what does this instance
carry", the same read the candidate-release deploy gate verifies an image against
([CandidateReleaseProtocol](/Doc/Architecture/CandidateReleaseProtocol)). The fleet page and the deploy
gate therefore cannot disagree about an instance's modules. Both recorded shapes are read, always: the
`{Space}/_GitSync` config and the `Plugins/{id}` install record. An inventory read off one of them looks
healthy and reports almost nothing (memex, 2026-08-10: 42 sync configs, zero install records).

| Field | Source |
|---|---|
| `event` | `module-inventory` — the inbox's discriminator |
| `deployment`, `instanceId`, `host` | `Hosting:Deployment`, `PluginCatalog:InstanceId`, `PluginCatalog:HomeUrl` |
| `platformVersion` | `PlatformBuildInfo.PlatformVersion` — the string Settings ▸ About shows and `Admin/PlatformVersion` records; never a third opinion |
| `commitSha` | `PlatformBuildInfo.CommitHash` — the core commit the image was built from |
| `frameworkIdentity` | the live framework identity (`PrebuiltAssemblySeeder.LiveFrameworkMvid`) — the key every prebuilt bundle is filed under ([ModuleVersioning](/Doc/Architecture/ModuleVersioning)) |
| `adoptedFrameworkIdentities` | distinct `CompiledFrameworkVersion` stamps from the instance's NodeType definitions, including older builds still adopted by modules |
| `adoptedFrameworkInventoryComplete` | `true` only after the adoption query completes and every returned definition is readable; `false` on failure, absent in legacy reports |
| `updatePolicy` | the `Admin/UpdatePolicy` node's policy, read as a children listing of the partition (a point read of an absent node trips the routing NotFound and its storm-breaker; a test mesh has no such node) |
| `sampledAt` | UTC, second precision |
| `modules[]` | per module: `id`, `origin` (`GitSync` or `Package`), `repository`, `ref`, `subdirectory`, `commitSha`, `lastSyncedAt`, `moduleVersion`. A module recorded under both shapes reports as `GitSync` carrying the install record's version — the receiver's fold keeps exactly that |
| `warnings[]` | an incomplete read (a source that could not be queried), an empty module list, a missing platform version or home url |
| `reporter` | `hosted` — distinguishes the service from the manual script in the record |

Fields the control instance's inbox does not read yet (`commitSha`, `frameworkIdentity`,
`updatePolicy`, `reporter`) ride along: an older control instance ignores them, a newer one shows them.

## Retention reads the adopted builds too

The running platform identity is not the whole consumer inventory. A portal running build Y can
still serve a module adopted from X. The report reads those adoption stamps from the local mesh and
includes X separately; the control instance's retention reference reader protects both identities.
The receiver support is in MeshWeaver.Plugins#1564 and must be available before remote reports can
preserve these fields.

An explicitly complete empty adoption list is valid. An absent, malformed, or explicitly incomplete
list blocks the retention pass instead of implying that no older build is used. The reporter still
sends the rest of its inventory on a failed adoption read, with the failure visible in its warnings.
Legacy reports block cleanup until replaced by a complete report from an upgraded reporter and
receiver.

These fields describe an individual report, not proof that every fleet member reported or that an
old report is current. Fleet coverage, freshness, and coordination with active publishers remain
requirements of the complete retention protocol in #3438 and #3842. No cleanup setting is enabled
by adding this report contract.

## Delivery

- **`Hosting:ReportTo` set:** `POST {ReportTo}/api/hooks/Hosting/Modules`, body as above, header
  `X-Hub-Signature-256: sha256=<lowercase hex HMAC-SHA256 of the raw body>` — GitHub's webhook
  signature shape, which the inbox verifies before it parses anything. The POST runs through the
  `Http` IO pool, never on a hub thread.
- **`Hosting:ReportTo` unset:** the same body is written to `{OperationalSpace}/Modules/{deployment}`
  as a `Hosting/ModuleInventory` node **with `$type: ModuleInventoryContent` stamped** — content
  written without the discriminator is stored perfectly and then materialises as nothing: the node
  keeps the whole document while every reader of the record type sees an empty record. The write runs
  as system: there is no principal on a clock tick, and the record is the platform's observation of
  itself.

## Reading the fleet

On the control instance, **Deployments** (the board) and **Fleet** (`Hosting/ModuleInventory`'s area)
show one column per instance and one row per module; a row whose builds differ across instances, or
that is missing on one, is drift. The `Hosting/Deployment` record is the **intent** (what an instance
should run, GitSynced from the deployments repo), `Hosting/DeploymentStatus` is what the cluster
**observes** (image, replicas), and this report is what the instance **says about itself** — three
sources, and a disagreement between any two is the finding.

## Failure modes, by design

| You see | It means |
|---|---|
| `[DeploymentReport] inert: Hosting:Deployment is not set` at boot | This instance is not in the fleet. Name it, or accept that it reports nothing. |
| `Hosting:ReportTo is set but no signing secret is configured` | Misconfiguration; nothing was sent. Provision the secret on both ends. |
| `the report for X was REJECTED by …: 401` | The secret differs from the receiver's `Hosting:ModuleReportSecret:X` (or the claimed deployment id does not match the key it was signed with). |
| `the report for X failed; the next one runs in 01:00:00` | A transient fault (network, a slow query). It is logged with the exception and retried on the next tick — never silently. |
| A `Modules/{deployment}` record whose `warnings` name an INCOMPLETE read | A module source could not be queried on that tick. The record is marked, not shortened. |

## Related

- [Instances](/Doc/Architecture/Instances) — what an instance is and how its version is read
- [ModuleVersioning](/Doc/Architecture/ModuleVersioning) — the framework identity a report carries
- [CandidateReleaseProtocol](/Doc/Architecture/CandidateReleaseProtocol) — the deploy gate that reads the same combo
- [ReleaseStrategy](/Doc/Architecture/ReleaseStrategy) — the update policy a report carries
