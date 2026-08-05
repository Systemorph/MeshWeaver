---
Name: Plugin Update on Green Build
Category: Architecture
Description: A plugin repo's CI going green reaches every installation that uses it, without anyone polling a registry or opening the catalog. The webhook records the build as a mesh node; the catalog subscribes to that node and reacts per module, gated on content identity so a green run that changed nothing stays completely silent.
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
   └── yes ──▶ opted out ? raise an "Update available" reminder : install the delta
```

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

## 🚨 Unattended by default; reminder on opt-out

A changed module **installs itself** — the delta lands as soon as the green build is seen, with
nobody clicking anything. An install record that sets `AutoUpdateDisabled` opts out: it gets a
`Notification` satellite instead (the bell surfaces it, the catalog card offers **Update**) and
nothing installs until a human acts.

Default-on is safe because the unattended path is fenced three ways, none of which depend on a
human being present:

1. **Content identity** — an unchanged module is never touched, however many green builds land.
2. **Additive install** — only manifest-tracked nodes are ever written or pruned. A node the user
   *added* to the partition is structurally invisible to the update.
3. **Per-node claims** — a node the user *modified* and claimed (any non-`Include`
   [`SyncBehavior`](/Doc/Architecture/StaticRepoImport)) is skipped by both the upsert and the
   prune, exactly as the static-repo importer skips it. Claiming is the deliberate act that
   decouples one node from its package; an unclaimed local edit is overwritten, by design.

Opt out per package where even fenced changes need review — a regulated deployment, a plugin whose
CI you do not yet trust.

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

### 3. Opting a package out of unattended updates

On by default. Set `AutoUpdateDisabled` on the installed package's record to fall back to the
reminder-only flow for that package.

### What you should see

- **Nothing changed** → no notification, no log line beyond the build record itself. This is the
  common case and it is supposed to be quiet.
- **A module changed** → the delta install runs, touching only the changed files — claimed
  (non-`Include` `SyncBehavior`) nodes excepted.
- **A module changed on an opted-out record** → an "Update available" notification on the install
  record, naming how many files changed and removed, and the short sha it was built from. Nothing
  installs.

## Failure modes, and what they look like

| Symptom | Cause |
|---|---|
| Nothing happens on a green build | The webhook does not send **Workflow runs**; or no catalog's `SourceRepoPath` matches the repo; or the run's conclusion was not `success` — only completed+successful runs are recorded. |
| A notification per green run, nothing changed | A module's `manifest.lock` is missing or unparseable, so `ModuleVersion` is null and the legacy commit-sha comparison applies — every commit looks like a change. Fix the module's CI to emit the sidecar. |
| Build node updates but no installation reacts | The package is not installed on that instance. A catalog lists far more packages than any instance installs; only packages with an install record are considered. |

The webhook **never throws** on a write failure: GitHub retries a non-2xx delivery, so an unhandled
fault would turn one bad write into a delivery storm. Failures are logged and reported as "nothing
recorded" instead.

## Related

- [Plugin Registry](/Doc/Architecture/PluginRegistry) — where the catalog and its credential live
- [Plugin Manual](/Doc/Architecture/PluginAuthoring) — authoring a plugin and its `manifest.lock`
- [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) — the manual counterpart
- [GitHub Sync](/Doc/Architecture/GitHubSync) — the webhook transport this rides on
- [No Static State](/Doc/Architecture/NoStaticState) — why the subscriber is a per-mesh instance
