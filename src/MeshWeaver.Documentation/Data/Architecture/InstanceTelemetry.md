---
Name: Instance Telemetry
Category: Architecture
Description: Every registered instance leaves a record at its registry of which platform it runs and when it was last online, and reports its error signatures there so they reach the fleet's bug-triage dispatch — what is collected, by whom, how often, where it lands, who reads it, what is deliberately not collected, and the trust model
Icon: Pulse
---

# Instance Telemetry — who runs what, when they were last online, and what fails

**Every instance registered at a plugin registry leaves two things behind there, without anyone
operating it: a heartbeat with its platform version, and a digest of the errors it logs.** The
registry (for the fleet, the public instance) stamps the heartbeat onto the instance's own record and
turns each error signature into a triage submission, which the existing feedback relay carries to the
control instance's bug-triage intake — the same "dispatch" every person's feedback already reaches.

The question this answers is the one [Deployment Inventory](../DeploymentInventory) cannot: that
report is sent only by an instance with a `Hosting:Deployment` id — the installations the operator
runs — so a client's own installation, a developer's local mesh or a build host that merely *pulls*
from the registry was invisible. Every one of those authenticates against the registry to pull, and
that authentication is the one channel they all share. Telemetry rides it.

## What is collected

| Reading | Carried by | Lands as |
|---|---|---|
| **Last online** | every authenticated call that exchanges the instance key for a token (`POST /api/instances/token`), and every consumer inventory report (`POST /api/instances/inventory`) | `MeshWeaverInstance.LastSeenAt` on the instance record at `{owner}/MeshWeaverInstance/{instanceId}` |
| **Platform version + commit** | the `telemetry` field of the token request — `PlatformBuildInfo.PlatformVersion` and `.CommitHash`, the same identity `/api/version` answers | `LastSeenVersion`, `LastSeenCommit` |
| **Home URL** | the same field — `PluginCatalog:HomeUrl`, what the running instance says about itself (the registrant typed `HomeUrl` once at registration; this one refreshes) | `LastSeenHomeUrl` |
| **Package summary** | the same field — the package COUNT and an eight-digit digest over the sorted `id@version` set (`87 packages · 3f9a1c2e`), never the list | `LastSeenSummary` |
| **Error signatures** | `POST /api/instances/errors`, a bounded digest of NEW or ESCALATED signatures since the last accepted digest | one `Feedback/Feedback` submission per (instance, signature) at `Feedback/_Submissions/fb-err-…` on the registry, relayed to the control instance's `Hosting/Triage/feedback/…` |
| **Plan** | nothing new — it is the licence already on the record | shown beside the readings |

The full module list of an instance that reports one stays where it is:
`{Ops}/ConsumerReports/{instanceId}` on the registry and `{Ops}/Modules/{deployment}` on the control
instance ([Deployment Inventory](../DeploymentInventory)). Telemetry is deliberately the SMALL reading
every instance leaves, not a second inventory.

## What is deliberately NOT collected

- **No content.** No node, no path of a node, no file, no message body, no search term.
- **No people beyond the registration.** The record already names the registering user (owner id,
  name, e-mail — given at registration, by the owner). Telemetry adds nobody: an error digest carries
  no user id, and every text field is run through `TelemetryRedaction.Mask`, which removes e-mail
  addresses, bearer/basic credentials, JWTs, known token prefixes, query-string and `key=value`
  secrets and URL user info — on the sender before it leaves, and again on the registry.
- **No log lines.** A signature is a logger category, an event id, the message TEMPLATE (the
  `{OriginalFormat}` of the structured log call — placeholders, no values), the exception TYPE and the
  first application FRAME (`Type.Method`, no arguments, no file, no line), with a count and first/last
  seen. There is no field for a formatted message, a stack trace or a sample, so a digest cannot leak
  what a line might carry. A record logged without a template is first normalised
  (`TelemetryRedaction.Normalize`): GUIDs, hex runs, quoted literals, paths and numbers become
  placeholders.
- **No package names** in the summary — a count and a digest.
- **Nothing from an instance that opted out** (below), and nothing from an instance that is not
  registered: an unregistered mesh has no key, so it has no channel.

## Who collects, how often

**The instance collects about itself; the registry stamps what it authenticated.** Both halves live in
`MeshWeaver.PluginCatalog` on the consumer side and in the portal's registry endpoints
(`Memex.Portal.Shared/Api`, MeshWeaver.Plugins) on the registry side.

- **Heartbeat.** `RegistryTokenResolver` adds the `telemetry` field to every token exchange. The
  exchange is lazy — it happens whenever any registry call needs a token and the cached one is
  within a minute of expiry (15 minutes by default) — and the update reconciler's safety net
  (`PluginCatalog:ReconcileSafetyNetInterval`, 30 minutes) guarantees one at least that often while
  the instance runs. So a running instance is seen at least every half hour, and an instance that has
  not been seen for a day is **offline**. The package summary is read through `InstanceComboReader`,
  the one reader of what an instance carries, memoised for 30 minutes and bounded to five seconds:
  a token exchange is never held hostage by the inventory, and when the read does not answer the
  telemetry goes out with the previous summary.
- **Error digest.** One more `ILoggerProvider` (`ErrorSignatureLoggerProvider`) folds every
  `Error`/`Critical` record into the in-process `ErrorSignatureCollector`: at most 200 distinct
  signatures; when full, the oldest ALREADY-REPORTED signature is evicted, and when every held
  signature is still unreported the newcomer is dropped and COUNTED — a flood of distinct new errors
  is itself the finding. `InstanceErrorReporter` runs two minutes after the default install settled
  and then every `Telemetry:ErrorReports:Interval` (15 minutes), sending what is **new** (never
  reported) or **escalated** (the count doubled, or grew by a hundred) since the last digest a
  registry ACCEPTED — a refused or unreachable delivery marks nothing, so it costs a retry and
  nothing else. A digest carries at most 50 signatures, most frequent first, and at most 24 KB on
  the wire (`ErrorDigestPayloads.Bound`); the registry refuses a larger body before parsing it.

Both are presented with the same credential the instance pulls bundles with, so the registry files
everything under the instance it **authenticated**, never one the body claims — exactly as the
consumer inventory does.

## Where it lands, who can read it

**The heartbeat** is four fields on the `MeshWeaverInstance` node, in the registering user's
partition. Its readers are the record's readers: the owner, and a global admin through the registry's
**Instance grants** admin tab, whose registered-installations grid now shows **Plan**, **Version** and
**Last online** beside the instance and its owner, most recently seen first. Last online renders in
the VIEWER's time zone with its age (`2026-10-27 11:15 (12 min ago)`); an instance unseen for a day
or more reads **offline since …**; a record that was never stamped reads **never**
(`LastSeenWording`). The stamp is throttled and single-flight on the registry — one write per
instance per ten minutes unless the reported version, commit, home URL or summary CHANGED, in which
case it is written at once — so a busy instance never turns its own record into the hottest node on
the mesh (the lesson of `ApiToken.LastUsedAt`).

**The error digest** lands on the registry as `Feedback/Feedback` submissions in
`Feedback/_Submissions`, one per (instance, signature), id `fb-err-{sha256(instance|signature)[..24]}`,
category `bug`, submitted by `instance:{instanceId}`, with the origin (instance id, reported version
and commit, home URL), the signature and its counts in the message — and the masked template, never a
line. From there the Feedback module's own hand-over delivers it to the control instance's triage
intake (`Hosting:ControlInbox:*`), where it becomes a `Hosting/Triage/feedback/…` item like any
person's feedback: the triage agent records a verdict and, for a defect, files the GitHub issue with
a severity. Whether anything then *fixes* it is the control instance's `Hosting:BugFix:Enabled`
and the bug pool's mode — triage classifies regardless; fixing stays off unless it is on.

**Dedupe on the registry.** A signature the instance reports again — a flapping error — does not
file a second submission: while the earlier one is still open (`New`/`Triaged`) the registry UPDATES
it (count, last seen, the version it was last seen on); only once it was `Resolved` or `WontFix` does
a recurrence file anew, with a timestamp suffix, because a defect that came back after a fix IS a new
finding. The same signature from two instances is two submissions: the instance is part of the
identity, and "the same error on three instances" is what triage should see.

### Retention

The heartbeat is four fields that are overwritten on every stamp — nothing accumulates. Error
submissions follow the Feedback module's lifecycle and the triage items on the control instance
follow triage's. The in-process ledger is bounded (200 signatures) and dies with the process.

## The trust model

- **Authenticated by the instance key, never by the body.** Both routes authenticate through
  `InstanceRegistryAuthenticator` exactly as a bundle pull does; a disabled instance (`IsDisabled`),
  a revoked key and an unknown key are refused with the same 401 before anything is read or written.
  Nothing in a telemetry body is used for authentication, authorization, routing or licensing — the
  version an instance claims decides nothing (floors are decided from the registry's own records,
  [Module Versioning](../ModuleVersioning)).
- **The registry executes nothing from a report.** A digest is data: it is masked, bounded, and
  written as a submission whose only consumer is a triage agent reading it as untrusted text. No
  field of it becomes a command, a path that is dereferenced, or a query.
- **Bounded on both sides.** Field lengths, signature count and body size are clipped by the sender
  and enforced by the registry (`BoundedBody`), so a misbehaving instance cannot grow a record or a
  partition.
- **Opt-out, never opt-in.** `Telemetry:ErrorReports:Enabled=false` switches the digest off on an
  instance; the default for a registered instance is on, because an installation nobody watches from a
  cluster console has no other way to say it is failing. The heartbeat has no switch: an instance that
  authenticates IS online, and the version it runs is what its registry needs to serve it correctly.
  The registry side has the mirror switch `PluginCatalog:ErrorReports:Enabled=false`, which refuses
  every digest with a 403 that says so.
- **Trusted-instance gating stays where it is.** The relayed submission reaches the control instance
  as feedback from the REGISTRY (a trusted feedback instance), so the origin instance's trust is not
  what admits it; what admits it is that the registry authenticated the sender and wrote the
  submission itself. The submission names the origin instance, so a reviewer can tell a client
  installation from an estate instance.

## Operating it

| You see | It means |
|---|---|
| **Last online: never** on an instance that is running | It has never exchanged a token since this shipped — it runs a platform older than the field, or pulls from no registry. Its consumer report (if it sends one) is at `{Ops}/ConsumerReports/{id}`. |
| **Version** blank, **Last online** fresh | An older consumer: it exchanges tokens but sends no `telemetry`. It will fill in when it rolls. |
| `[InstanceErrorReport] switched off (Telemetry:ErrorReports:Enabled=false)` at boot | Opted out; nothing is sent. |
| `[InstanceErrorReport] Skipped — no credential for this registry` | The instance holds no registry credential; register it (or set `PluginCatalog:Registries:N:Token`). |
| `[InstanceErrorReport] Refused — …/api/instances/errors: refused 403 …` | The registry has `PluginCatalog:ErrorReports:Enabled=false`, or the instance is disabled. Everything stays pending. |
| `[InstanceErrorReport] Sent — …: accepted {"received":3,"filed":1,"updated":2,"ignored":0}` | Three signatures went out; one is a new submission, two updated open ones. |
| A `Feedback/_Submissions/fb-err-…` whose message counts keep rising | The error is flapping and still open; the submission is being updated, not re-filed. |

## Related

- [Deployment Inventory](../DeploymentInventory) — the full module report of an operator-run instance; telemetry is the small reading EVERY instance leaves.
- [Plugin Registry](../PluginRegistry) — the token exchange telemetry rides on, and the instance record it is stamped onto.
- [Instance Identity and Setup](../InstanceIdentityAndSetup) — what an instance is to its registry.
- [Incidental Findings](../IncidentalFindings) — the bug-triage intake ("dispatch") the error digests reach, and how a finding is filed by hand.
