---
Name: Plugin Update on Green Build
Category: Architecture
Description: A plugin repo's CI going green reaches every installation that uses it, with nobody opening the catalog and no poll timer anywhere. An installation with GitHub access subscribes to a build node the webhook writes; an installation that installs from a registry reads that registry's own feed at startup. Both react per module, gated on content identity, so a build that changed nothing stays completely silent.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/><path d="m9 12 2 2 4-4"/></svg>
---

# Plugin Update on Green Build

[Plugins](/Doc/Architecture/Plugins) are installed from a git repo through the
[Plugin Registry](/Doc/Architecture/PluginRegistry). Before this, an installation only learned that a
new version existed when somebody **opened the catalog page** and the card compared versions. Nothing
pushed; nothing subscribed.

This page describes the path that closes that gap: **a plugin repo's CI going green reaches every
installation that uses it.**

## The shape

```
plugin repo CI goes green
   │
   │  workflow_run webhook  (GitSync — transport only)
   ▼
Admin/_Build/{owner}.{repo}          ← a BuildCompletion node: "repo X built green at sha Y"
   │
   │  GetMeshNodeStream(path)        ← the catalog SUBSCRIBES; nobody calls it
   ▼
per installed module:  ModuleVersion changed?
   ├── no  ──▶ silent. no notification, no fetch, nothing.
   └── yes ──▶ opted in ? install the delta : raise an "Update available" reminder
```

## 🚨 Which green runs count as a publish signal

`workflow_run` fires for far more than a repo's content CI, so the webhook applies **two independent
guards** before a delivery becomes a `BuildCompletion` (`GitHubWebhookProcessor.ProcessWorkflowRun`):

1. **The trigger must be on the allow-list** — `push`, `repository_dispatch`, `schedule`,
   `workflow_dispatch`. Each of those means *a build of the default branch's own tree*.
2. **`head_branch` must BE the repository's default branch.**

Both fail closed: an unknown trigger is refused, and a payload whose branch cannot be read records
nothing.

| Trigger | Admitted | Why |
|---|---|---|
| `push` | ✅ | The branch moved and its CI ran — the original case. |
| `repository_dispatch` | ✅ | GitHub only ever runs a dispatched workflow from the **default branch**, and `head_sha` is that branch's tip. This is how a platform release re-verifies every satellite repo: no commit to push, same tree, a genuine green verdict on it. |
| `schedule` | ✅ | Same — a cron run only ever exists on the default branch. |
| `workflow_dispatch` | ✅ | May target any ref, so guard 2 does the discriminating. On the default branch it is a manual re-verification of that tree, and the only recovery lever when a merge burst cancelled the push-triggered run. |
| `pull_request` / `pull_request_target` | ❌ | Green **unmerged** code. Note both can report `head_branch=main`, so guard 1 — not guard 2 — is what rejects them. |
| `dynamic` | ❌ | GitHub's Copilot reviewer. Completes green on the default branch and is not a build at all. |
| `merge_group` | ❌ | A merge-queue run's `head_branch` is the temporary `gh-readonly-queue/{base}/pr-{n}-{sha}` ref, so guard 2 already rejects it. Listing it would be unreachable. |
| anything else | ❌ | Fail closed. An allow-list means the next trigger GitHub invents does not publish by accident. |

**Widening the list cannot cause churn.** A sync source already sitting on the built sha is skipped
("already at this commit"), so a scheduled or dispatched re-verification of an unchanged default
branch triggers no import at all.

### 🚨 The single-value test that dropped real signals (2026-09-02)

Guard 1 was `event == "push"` — one value, not a set — and it discarded green builds that *were* the
default branch's tree. `Systemorph/MeshWeaver.Reinsurance`'s `main` built green three times at
`636ebd5` that day (11:17, 12:27, 12:55Z), every one with `event=repository_dispatch` from the
release-follow lane, which rebuilds every module against a new platform pin **without a commit to
push**. All three were dropped, and `Underwriting/_GitSync` on `memex.systemorph.com` sat 38 hours
behind a merged main — with the webhook armed, every delivery answering 200 OK, and nothing anywhere
reporting a problem. A dropped publish signal has no symptom except content that quietly stops
arriving; there is no scheduled poll behind it to paper over the gap, by design.

The decision table above is pinned by `GreenBuildPublishSignalTest`, in both directions — a test that
only listed the admitted triggers would go green against a gate that admits everything.

## 🚨 Two inputs, one decision — and which one your installation has

The path above needs a **GitHub webhook on the plugin repo**. An installation that installs from a
[registry](/Doc/Architecture/PluginRegistry) over HTTP deliberately holds no GitHub credential and
receives no webhooks, so for a long time it had **no input at all**: `BuildCompletion` is constructed
in exactly one place in the whole tree — the `workflow_run` webhook — and the watcher only opens a
subscription once a catalog node names a source repo. Such an installation had neither, so the
watcher was registered, live and completely inert, and `AutoUpdate` was a flag nothing ever fired.
Its plugins stayed at the version they were installed at until an administrator pressed **Provision**.

There are now two inputs, by deployment shape:

| | learns from | how | when |
|---|---|---|---|
| **Registry instance** (holds the GitHub credential) | its plugin repos | `PluginUpdateWatcher` subscribes to `Admin/_Build/{owner}.{repo}` | on every green build |
| **Consumer** (registry token, no GitHub) | its registry | `RegistryUpdateReconciler` **reads** `GET /api/plugins` | at startup |

Both hand the per-module verdict to the same `PackageUpdateReconciler`, so they can never disagree
about what "changed" means or about who opted into an unattended install. Both are registered
unconditionally and each is inert on the deployment shape it does not serve.

### Why the consumer READS instead of waiting to be told

There is nothing for a consumer to subscribe to. The registry is a **different deployment with a
different database**, so it shares no durable row — and the cross-process change feed the platform
does run (`PostgreSqlChangeListener`, live since #1816) is scoped to *this* deployment's database,
so no `NOTIFY` from the registry can reach it either. A signal would therefore mean a new push
protocol — a subscription registry, a shared secret, an inbox — kept in step with the feed that
already exists.

It does not need one. `GET /api/plugins` already returns every package's `ModuleVersion`, which is
the same content identity the install records carry, so **comparing the two answers the question
outright**. That is the shape `BuildProtocolDriver.FollowGo` arrived at for the cross-cluster case:
end on a fact you can read, not on a notification that cannot reach you.

### 🚨 Not a poll timer

The reconcile runs on an event the deployment already has — **this process starting** — and not on a
clock. A timer answers "how stale am I willing to be", which is a question nobody asked, and it turns
one misconfiguration into permanent background load.

On these deployments the restart *is* the fan-out: plugin content and the framework image are
published by the same CI, and the portals self-update onto each new image, so a green plugin build
and a pod roll already arrive together. The honest bound is therefore **a consumer learns on its next
boot** — plus immediately, on demand, whenever somebody opens the catalog page, which reads the same
feed and offers the same **Update**. An installation that never restarts also never picks up
framework fixes, which is a louder problem with its own alarm.

### 🚨 A boot read that fails past its budget is deferred, not dropped (#2888)

The boot read re-asks a *transient* answer — 503, 429, a gateway error, a connection that never
landed — within a small budget (four attempts, ~26 s). That budget exists to survive a hiccup, not
to wait out an outage, and it is **not** widened when an outage outlasts it: a registry that stays
down for a minute leaves the boot with nothing to reconcile against. What happens then used to be
one Error line on one pod, and the line's own promise — "the next chance is a human opening the
catalog page" — was false: the catalog page only *renders* the feed; it never ran the reconcile.
So the installation silently stopped noticing package and grant changes until its next restart.

Two things now happen instead, and neither is a clock:

1. **The skipped reconcile becomes a durable fact.** The reconciler owns one bookkeeping node,
   `Plugins/_RegistryReconcileLedger` (a `RegistryReconcileLedger`, one entry per configured
   registry), and the registry is recorded there as `Pending`, with the attempts spent and the
   registry's own last answer. Platform admins get **one** bell notification anchored under `Admin`
   — the same surface `StartupErrorNotifier` uses for a degraded boot — naming the registry and
   pointing at that ledger.
2. **The next successful feed read drains it.** Every contact this installation makes with a
   registry goes through one class, `RegistryPackageSource.ListPackages` — the catalog page, an
   install, the Store's package count, the boot reconcile itself. That method reports each
   successful read back to the reconciler, which checks whether that registry is pending at the
   ref that was read and, if so, **claims** the marker (one compare-and-swap, so two catalog opens
   in the same second drain it once) and runs the reconcile the boot skipped — from the packages
   that read already returned, so there is no second round-trip, and off the reader's thread, so
   the catalog render is never delayed. The ledger entry then records when and how (`feed-read`)
   the reconcile ran; a drain that faults re-marks the registry pending and says so.

The ledger is the reconciler's in-memory state projected onto a node: the running process is the
authority, the node is what an admin (or a test) reads. Its writes are serialised through one Rx
channel (`Subject` + `Concat`), so a drain completing while the boot pass is still recording another
registry can never land an older snapshot over a newer one — the same shape as every other
"one at a time" in the codebase, never a lock
([Removing Hand-Woven Gates](/Doc/Architecture/RemovingHandWovenGates)).

What this deliberately does **not** do: retry on a timer, widen the budget, or add a new caller
that has to remember to reconcile. The design decision above — no poll, the restart and the
catalog open are the events — stands; the change is that a catalog open now *is* a reconcile when
one is owed, which the log line had always claimed and the code had never done.

## Why a node and not a call

The producer is `MeshWeaver.GitSync` (it owns webhook signature verification, payload parsing and
repo resolution). The consumer is `MeshWeaver.PluginCatalog` (it owns what a module is and when one
has changed). They are connected by **a node, not an interface** — and that is a deliberate
constraint, not a stylistic preference:

- `MeshWeaver.PluginCatalog` **already references** `MeshWeaver.GitSync`. A handler interface
  declared in GitSync and implemented in PluginCatalog would close a reference cycle. An earlier
  draft did exactly that and had to be removed.
- The contract — `BuildCompletion` — therefore lives in `MeshWeaver.Graph`, which **both** already
  reference. Neither side owns it.
- A second consumer (a dashboard, a notifier, an audit trail) costs nothing: it subscribes to the
  same node. The producer never learns it exists.

This is the same read/write model as everything else in the mesh: state on a node, consumers
subscribe. See [Data Access Patterns](/Doc/Architecture/DataAccessPatterns) and
[CQRS](/Doc/Architecture/CqrsAndContentAccess).

## 🚨 A green build is NOT a change

The build node is rewritten on **every** green run — doc-only commits, reverts, re-runs of an
unchanged tree, a README typo. Acting on the emission itself would push an install to every
installation on every green run.

So the decision is made **per module, against content identity**, and it is made in exactly one
place. Every plugin module carries a CI-maintained `manifest.lock`
([ModuleManifest](/Doc/Architecture/PluginAuthoring)) whose `moduleVersion` is a hash over the
module's sorted *(path, file-hash)* pairs:

> **equal `moduleVersion` ⇒ identical file set ⇒ nothing to sync**

`CatalogLayoutAreas.InstallOrUpdate` already compares the catalog entry's `ModuleVersion` with the
install record's and no-ops when they match — costing one storage read and **not one fetched file**.
The subscriber calls that same method rather than re-implementing the comparison, which is what
stops the Update button and the automatic path from ever disagreeing about what "changed" means.

The file-level diff is available with **no extra fetch**: the installed side is already persisted on
the install record (`InstalledFiles`, written by `WriteInstalledRecord`), and the candidate side
rides in on the catalog entry (`ManifestFiles`, kept when the source parses `manifest.lock`).

## 🚨 Reminder by default; unattended on opt-in — seeded per deployment

The **platform default is explicit opt-in**: a changed module raises a `Notification` satellite on
the install record (the bell surfaces it, the catalog card offers **Update**) and nothing installs
until a human acts. A record whose `AutoUpdate` flag is set installs the delta unattended instead.

The opt-in is **stamped at install time from the deployment's policy**:
`PluginCatalog:AutoUpdateByDefault` (default `false`) seeds every fresh install record. A
deployment that wants plugins tracking their repos continuously sets it `true` — **our Helm
deployments do**, so a plugin repo's green build reaches those portals with nobody clicking
anything — while an installation that configures nothing stays review-first. Install-time seed
only: the record's own flag is the runtime authority thereafter, in both directions — an update
re-stamp carries it forward, and flipping the deployment default later changes nothing for
already-installed packages.

An opted-in, unattended update is still fenced three ways, none of which depend on a human being
present:

1. **Content identity** — an unchanged module is never touched, however many green builds land.
2. **Additive install** — only manifest-tracked nodes are ever written or pruned. A node the user
   *added* to the partition is structurally invisible to the update.
3. **Per-node claims** — a node the user *modified* and claimed (any non-`Include`
   [`SyncBehavior`](/Doc/Architecture/StaticRepoImport)) is skipped by both the upsert and the
   prune, exactly as the static-repo importer skips it. Claiming is the deliberate act that
   decouples one node from its package; an unclaimed local edit is overwritten, by design.

## Configuration

### 1. Register the webhook on the plugin repo

The registry already receives `push`, `issues` and `issue_comment`. Add **`workflow_run`** to the
same webhook — same URL, same secret, no new endpoint and no CI credential anywhere:

| Field | Value |
|---|---|
| Payload URL | the portal's existing GitHub webhook URL |
| Content type | `application/json` |
| Secret | the same shared secret (`GitHub:Webhook:Secret`) |
| Events | **Workflow runs**, in addition to Pushes / Issues / Issue comments |

Signature verification is unchanged — `GitHubWebhookProcessor.VerifySignature` rejects a forged
request before any work is scheduled.

### 2. Point a catalog at the repo

Nothing new. The catalog node's `SourceRepoPath` (see [Plugin Registry](/Doc/Architecture/PluginRegistry))
is what associates a repository with a catalog, and the same value resolves the incoming webhook —
a `workflow_run` payload carries the same `repository` object a `push` does. A catalog whose source
is a **local path** never matches a webhook, by construction.

### 3. Opting in to unattended updates

Per deployment: `PluginCatalog:AutoUpdateByDefault=true` seeds every FUTURE install record opted
in (the Helm chart sets this for our portals). Per package: set `AutoUpdate` on an installed
package's record. Both are edits to the record's own flag — the deployment key is only the
install-time seed.

### What you should see

- **Nothing changed** → no notification, no log line beyond the build record itself. This is the
  common case and it is supposed to be quiet.
- **A module changed** → an "Update available" notification on the install record, naming how many
  files changed and removed, and the short sha it was built from. Nothing installs — this is the
  platform default.
- **A module changed on an opted-in record** → the delta install runs, touching only the changed
  files — claimed (non-`Include` `SyncBehavior`) nodes excepted.

## Failure modes, and what they look like

| Symptom | Cause |
|---|---|
| Nothing happens on a green build | The webhook does not send **Workflow runs**; or no catalog's `SourceRepoPath` matches the repo; or the run's conclusion was not `success` — only completed+successful runs are recorded; or the run's **trigger is not on the allow-list** (`push`, `repository_dispatch`, `schedule`, `workflow_dispatch` record; `pull_request`, `dynamic` and anything unknown do not — see *Which green runs count as a publish signal* above); or the run was **not on the repository's default branch** — a green PR-branch build is unmerged code and is deliberately never recorded (fail-closed: a payload with no readable branch records nothing either). |
| A module never updates, and the log says it "has no module content identity" | The module's `manifest.lock` is missing or unparseable, so there is no `ModuleVersion` to compare and "has it changed" is unanswerable. A missing hash is the **absence of evidence**, not evidence of a change: treating it as changed would re-install the module on every green build of the repo *and* on every pod start, which is acting on the event rather than the content. It is refused, loudly, and the catalog card's manual **Update** stays available. Fix the module's CI to emit the sidecar. |
| Nothing happens on a green build, **and the log says the delivery "matched NONE of the N sync config(s)"** | No `_GitSync` targets that repository — usually because the repository was **renamed** and the configs still store its old name. The matcher falls back to GitHub's canonical `full_name` (which follows the rename redirect) and repoints the config when it finds one, so this line surviving means the lookup could not be made either: the repository is unreachable with the config creator's credential, or the hook really is installed on a repository this mesh does not sync. The Warning names both sides — the incoming repository and everything it was compared against. |
| Build node updates but no installation reacts | The package is not installed on that instance. A catalog lists far more packages than any instance installs; only packages with an install record are considered. |
| The boot log says the feed of a registry could not be read after N attempts and was **recorded as PENDING**, and admins got a bell notification pointing at `Plugins/_RegistryReconcileLedger` | The registry stayed unavailable for longer than the boot's retry budget (#2888). Nothing is lost: the ledger entry for that registry reads `Pending: true`, and the reconcile runs on the next successful feed read — open the catalog page (or install anything from that registry) once the registry is back, then check the entry reads `LastReconciledVia: feed-read`. If the registry answers a *definite* refusal (401/403) instead, the entry is pending too, but no catalog open will drain it until the key or grant is fixed — the `LastFault` names which. |

The webhook **never throws** on a write failure: GitHub retries a non-2xx delivery, so an unhandled
fault would turn one bad write into a delivery storm. Failures are logged and reported as "nothing
recorded" instead.

## Related

- [Plugin Registry](/Doc/Architecture/PluginRegistry) — where the catalog and its credential live
- [Plugin Manual](/Doc/Architecture/PluginAuthoring) — authoring a plugin and its `manifest.lock`
- [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) — the manual counterpart
- [GitHub Sync](/Doc/Architecture/GitHubSync) — the webhook transport this rides on
- [No Static State](/Doc/Architecture/NoStaticState) — why the subscriber is a per-mesh instance
