---
Name: Plugin Update on Green Build
Category: Architecture
Description: A plugin repo's CI going green reaches every installation that uses it, with nobody opening the catalog. An installation with GitHub access subscribes to a build node the webhook writes; an installation that installs from a registry reads that registry's own feed at startup, is told by the registry the moment a module is published, and reconciles on a half-hourly safety net so a lost notification is bounded. All react per module, gated on content identity, so a build that changed nothing stays completely silent.
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
| **Consumer** (registry token, no GitHub) | its registry | `RegistryUpdateReconciler` **reads** `GET /api/plugins` | at startup; on a **module-published broadcast** from the registry (that one package); and every **30 minutes** as a safety net (#3650) |

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

### 🚨 Three events and a floor — never "the next boot" (#3650)

The reconcile used to run on exactly one event, **this process starting**, on the reasoning that the
restart *is* the fan-out: plugin content and the framework image ship from the same CI, and a portal
rolls onto each image. That bound stopped being honest when the module lane became eager — rule R3
of the Module Adoption Policy (`Doc/Architecture/ModuleAdoptionPolicy`), maintainer 2026-09-07: *as soon
as a new module version ships, we start using it.* A module publish is not an image roll, so "the
next boot" could be days away; and an installation running its previous generation of a module
([the keep-the-old fallback](/Doc/Architecture/Modules), #3649) had no event at all that would ever
re-examine it.

The consumer now reconciles on three events, and everything a consumer learns still ends on a fact
it reads — never on what it was told:

| event | what runs | latency |
|---|---|---|
| **boot** | the full pass — content lane, module lane, module-set proposal — with its retry budget and its deferral (below) | one process start |
| **module-published broadcast** | the module lane for **that one package**: the registry POSTs a `ModulePublished` record to the consumer's webhook inbox the moment a bundle lands on its shelf | minutes |
| **safety net** (`PluginCatalog:ReconcileSafetyNetInterval`, default 30 min, `≤ 0` disables) | the full pass, against every configured registry | at most the interval |

Every pass runs on **one serialized lane** inside the reconciler (`Subject` + `Concat`): a landing
wave ends by proposing the mesh's module set from the activation record as it stands
([Module Set Convergence](/Doc/Architecture/ModuleSetConvergence)), and two waves interleaving would
let one propose the other's half-landed mix — the torn set the proposal exists to make unreachable.

#### The broadcast is a wake-up, never the truth

The registry does not know which installations installed a package, and it does not need to. It
tells **every registered instance** that recorded a `HomeUrl` (`PluginCatalog:HomeUrl` at
registration): one `ModulePublished` record — `event: module-published`, the registry's URL, the
package, the module, the version, the framework identity — POSTed to
`{HomeUrl}/api/hooks/Plugins/_RegistryReconcileLedger`. The target is the reconcile ledger node the
reconciler already **owns**, so it exists on every installation with a registry configured and
nothing new has to be seeded; the delivery lands as a durable `WebhookEvent` under its `_Inbox`, and
the reconciler drains it on its own thread. A delivery that lands while the process is down replays
into the first emission of the next boot's watch: at-least-once, and the decision behind it is
idempotent.

The consumer reads nothing off the record but *which registry* (matched by host against its
configured registries — an unknown registry's delivery is dropped) and *which package*. The drain
then reads the package's **own install record** (is it installed here, which module does it
declare), the registry's **own authenticated bundle index**, and runs the same `ModuleUpdateDecision`
the boot runs against the same activation record. A stale, duplicated or forged delivery therefore
costs one authenticated index read for one installed package — which is what lets the delivery be
**unsigned by default**. A consumer that allowlists the target *with* a `SecretConfigKey` is answered
with a matching `X-Hub-Signature-256` when the registry configures
`Plugins:Registry:BroadcastSecret`; a mismatch is a 401 the registry logs per consumer, never a
silent drop (#3312).

The fan-out is **reporter-class**: it runs detached from the publisher's response (a CI job must not
wait on a slow consumer), a 404 (target not allowlisted) or an unreachable instance is one logged
line per consumer, and nothing about it can fail the publish. Which is exactly why the floor exists.

#### The safety net is the floor, not the driver

A broadcast reaches an installation over a chain nobody re-verifies — the `HomeUrl` it registered,
the `WebhookInbox:Targets` slot, the ingress between — and every joint of it fails **silently**: an
installation whose channel is dead is byte-identical to one that is up to date. The safety net
bounds that, the way `SelfUpdate:SafetyNetCheckInterval` bounds a dead build-event channel (#2494):
it is not a poll that drives the update, it is the worst case a lost broadcast can cost. It cannot
change *what* lands — the same decision runs, and an unchanged module costs one feed read and one
index read, no download — only how late. Its default, 30 minutes, is half of
`SelfUpdate:MinRollInterval`, so a module that ships is landed before the next restart the roll
floor allows.

A safety-net read that finds the registry **pending** (the boot deferred it, see #2888 below) does
not run a second pass: the read itself reported to the reconciler and drained the deferral. A read
that fails is a Warning and the next tick; it never notifies admins or marks the ledger — that is the
boot deferral's job, and the boot's retry budget is what such a registry has already exhausted.

#### The restart happens

A landed generation still loads only at a restart — a module never swaps inside a running process.
What changes is who takes the restart: the self-updater, after the platform half of every check that
patched nothing, reads the activation record and, when `PendingRestart` is raised, rolls the
workloads **on the image they run** (`IDeploymentUpdater.RestartAsync`), paced by
`SelfUpdate:MinRollInterval` exactly like a roll. A wave this process ends
(`ModuleLandingService.ModuleSetProposed`) is itself a trigger, so the check does not wait for the
hourly safety net. See [Modules → Auto-update](/Doc/Architecture/Modules).

A human opening the catalog page still reads the same feed and offers the same **Update**.

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

What this deliberately does **not** do: widen the budget, or add a new caller that has to remember
to reconcile. The change is that a catalog open now *is* a reconcile when one is owed, which the log
line had always claimed and the code had never done. (The safety net above reads the feed too, and a
successful read from it drains a pending registry through this same path — one pass, not two.)

### 🚨 A package the registry does NOT offer is recorded as NOT DELIVERED (Plugins#1584)

Both lanes above iterate **the packages the registry serves** and intersect them with this
installation's install records. That is right for everything the registry carries — and it means a
package installed *here* that the registry does **not** carry is neither "up to date" nor "failed".
It is *absent from both loops*, and absence used to produce no log line, no ledger entry and no card
state anywhere.

The shape that made this expensive, measured on memex 2026-09-10:

| What was true | What every surface said |
|---|---|
| The registry answered its feed with **45 manifests**; `Mail` was not among them | — |
| `Mail/index.json` declares `tier: personal`; the instance's catalog grant covers the baseline plan, so the registry declined it (`the registry … does not offer a package 'Mail' to this instance`) | — |
| The package's **content** kept arriving through the instance's own git source, and the install record advanced to `1.5.0` an hour earlier | *installed, version 1.5.0, up to date* |
| The loaded assembly `MeshWeaver.Mail.MicrosoftGraph` was written **eleven days** earlier | *no module activation pending* — and correctly so: nothing had landed, so nothing was waiting on a restart |

A feature that ships as a module change in such a package therefore cannot reach the deployment by
merge + roll + restart. Only content moves. An Executive Assistant thread asked for the Teams tools
that shipped in that module's 1.5 and had none.

**The mechanism fix is to make the absence an answer.** After the content and module lanes, a full
feed pass lists this installation's install records once and records, on the same ledger entry, the
installed packages that declare a `module` and which *this registry did not offer*
(`RegistryReconcileEntry.UndeliveredModules`, one `UndeliveredModule` per package carrying the
module name and the content identity the record claims). One Warning line names them and points at
the ledger; `ModuleDelivery.NotDeliveredByAnyRegistry` intersects the entries so a surface can say
*not delivered* honestly on an installation with several registries — a package one registry serves
is delivered, whatever the others carry.

Three properties are load-bearing:

- **Absence is evidence only after a SUCCESSFUL, COMPLETE read.** The lane runs from
  `ReconcileFromFeed`, i.e. the boot pass, a drained deferral or the safety net — never from the
  per-package broadcast drain, whose "packages" is the one package the registry named and which
  would call every other installed module undelivered.
- **`null` is not an empty list.** A listing that fails records *not determined*, not *none*: an
  inventory that could not be read is not an empty inventory, and reading the first as the second is
  a gate that never ran painted the colour of one that passed.
- **It is not an entitlement verdict, and not a fault.** A consumer cannot tell "your grant does not
  cover this" from "this registry never carried it", and nothing here is broken — the package is
  simply not delivered from this registry. The remedy is the registry operator's: grant the
  instance the plan, tier the package differently, or point the installation at a registry that
  carries it.

What this deliberately does **not** do: fall back to the image's `modules/` seed or to an in-mesh
compile when the registry has no bundle. That changes what an installation *runs* and belongs with
the adoption policy ([Module Adoption Policy](/Doc/Architecture/ModuleAdoptionPolicy)), not with
the reconcile's bookkeeping; the honest first step is that the state stops being invisible.

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

### 🚨 A manifest-less package advertises its SNAPSHOT REF — so the ref must be content-derived (#3880)

The comparison above needs a `moduleVersion`, and a package that ships no `manifest.lock` has none.
Both the card and the install then fall back to `PackageManifest.Version`, which
`NodeRepoPackageSource.ListPackages` takes from the snapshot's `CommitSha`:

> no `manifest.lock` ⇒ the source's **snapshot ref** *is* the module's content identity

For a repo fetched over git that ref is a real commit sha, so content identity comes for free. A
**mounted working tree** has no commit to read — the local checkout a `memex-local` self-registry
serves, and the tree a `LocalCheckout` source reconciles on every boot — so
`PackageSources.LocalDirectoryFetch` synthesises the ref, and whatever it hashes *is* that identity.

It hashed each file's relative path and its `FileInfo.Length`. Any edit preserving the byte count —
`ABC` → `DEF` in a Markdown page, a PNG swapped for another of the same size — left the advertised
version identical, so the catalog card read "up to date" and the boot reconcile short-circuited.
The portal quietly stopped mirroring the tree it exists to mirror. Nothing was logged, because from
every consumer's point of view "nothing changed" was correctly derived from the evidence it was
given.

The fingerprint now hashes the bytes the snapshot actually returns (`RepoFile.Bytes` — already read
to build the payload, so there is no second filesystem read), keeping the `local-<sha>` ref format,
the dot-directory skip and the declared-release-version precedence.

Verified by reverting the fix under `LocalSourceContentVersionTest` (2026-09-10): with the
length-only hash restored, both the Markdown and the PNG case fail with

```text
Did not expect value to be "local-26ab574df588" because a mounted source version must
identify its content, including equal-length edits.
```

and both pass with the content hash, while the unchanged-tree and `.git`-only controls hold either
way — so the test discriminates rather than merely passing.

**The general rule: a fingerprint that gates adoption is computed over the bytes it certifies, never
over metadata that merely correlates with them.** Length and mtime are both preserved by an ordinary
edit, so neither is evidence of equal content. It is the same reason
[`PartitionSourceFingerprint`](/Doc/Architecture/StaticRepoImport) hashes serialised node content for
an unversioned partition instead of a version number, and why `ModuleLandingService.GenerationIdOf`
appends each file's bytes and not only its length.

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

## 🚨 A reminder is told ONCE — the gate the content-identity check cannot be (#3213)

The content-identity gate above is the whole story for the **unattended** path and no part of it
for the **reminder** path, and conflating the two shipped a real defect: on memex.meshweaver.cloud,
**124 of the newest 200 notification rows** were this one emitter, four rows for the same package
in a single day, two packages accounting for over half of everything the mesh had written in four
days.

The gate asks *is the candidate installed?*:

- On the `AutoUpdate` path that question is **self-silencing**. The apply advances the record's
  `ModuleVersion`, so the next reconcile compares equal and says nothing.
- On the reminder path it is **unsatisfiable**. Nothing is installed — by design, the user has not
  acted — so `ModuleVersion` never advances and the comparison evaluates false on every subsequent
  reconcile, forever. And the reconcile runs often: at boot, whenever a feed read drains a deferred
  registry, and on every build webhook.

The notify path needs the *other* question — *have I already told them about this candidate?* — so
it carries its own answer, and the fix is two independent halves. **Either one alone still
duplicates:**

1. **A satisfiable silence gate.** `PackageManifest.NotifiedModuleVersion` records the candidate
   the user was last reminded about, on the durable install record. Writing it is what makes the
   gate *become true* — which is exactly what the version comparison could never do here. It lives
   on the node rather than in a cache because a cache loses its memory on every pod start and
   re-notifies, which is the symptom itself.
2. **A deterministic notification identity.** `NotificationService.CreateNotification` takes an
   optional `identity`; when given, the node id is derived from *(main node, identity)* with the
   platform's content-addressing helper and the write becomes an atomic upsert. The reconciler
   keys it on *(record, kind, candidate version)*, so a repeat that does reach the notify path —
   two reconcile entry points overlapping, a marker write that failed — lands on the **same** node
   instead of adding a row.

The gate sits inside `Notify`, deliberately **not** in front of `Apply`: an unattended apply must
keep retrying every reconcile so a restored admin grant heals itself. It is only the *telling* that
is once per candidate. A refusal notification ("Update needs a Global Admin") is gated the same way
and by the same marker.

Order matters within the notify path: the bell is written **first**, the marker second. A marker
written first whose notification then failed would silence the reminder permanently; a bell written
first whose marker then failed costs one repeat, which the deterministic identity absorbs into the
same node.

A genuinely **new** candidate version changes both the marker comparison and the derived id, so it
gets its own unread bell — the suppression is per candidate, never per package.

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

### 4. Receiving the module-published broadcast (consumers, #3650)

On each **consuming** installation, allowlist the reconciler's inbox target and record the public
URL the registry should deliver to:

| key | value |
|---|---|
| `WebhookInbox:Targets:N` | `Plugins/_RegistryReconcileLedger` |
| `PluginCatalog:HomeUrl` | the installation's public base URL — sent at registration and stored on its instance record |
| `WebhookInbox:Targets:N:SecretConfigKey` | *optional* — the key holding a shared secret; then set the same value on the registry as `Plugins:Registry:BroadcastSecret` |
| `PluginCatalog:ReconcileSafetyNetInterval` | *optional* — default `00:30:00`; `00:00:00` disables the safety net (broadcast and boot only) |

Nothing is configured on the registry for the fan-out itself: its subscriber set is the instances
registered with it. An installation that configures none of this is reached by the safety net alone.

### What you should see

- **Nothing changed** → no notification, no log line beyond the build record itself. This is the
  common case and it is supposed to be quiet.
- **A module changed** → an "Update available" notification on the install record, naming how many
  files changed and removed, and the short sha it was built from. Nothing installs — this is the
  platform default. **Exactly one** notification per candidate version, however many times the
  reconcile runs (see *A reminder is told once* below).
- **The SAME module change, seen again** → nothing at all. No new row, no re-raised bell, not one
  file fetched.
- **A module changed on an opted-in record** → the delta install runs, touching only the changed
  files — claimed (non-`Include` `SyncBehavior`) nodes excepted.

## Failure modes, and what they look like

| Symptom | Cause |
|---|---|
| Nothing happens on a green build | The webhook does not send **Workflow runs**; or no catalog's `SourceRepoPath` matches the repo; or the run's conclusion was not `success` — only completed+successful runs are recorded; or the run's **trigger is not on the allow-list** (`push`, `repository_dispatch`, `schedule`, `workflow_dispatch` record; `pull_request`, `dynamic` and anything unknown do not — see *Which green runs count as a publish signal* above); or the run was **not on the repository's default branch** — a green PR-branch build is unmerged code and is deliberately never recorded (fail-closed: a payload with no readable branch records nothing either). |
| A module never updates, and the log says it "has no module content identity" | The module's `manifest.lock` is missing or unparseable, so there is no `ModuleVersion` to compare and "has it changed" is unanswerable. A missing hash is the **absence of evidence**, not evidence of a change: treating it as changed would re-install the module on every green build of the repo *and* on every pod start, which is acting on the event rather than the content. It is refused, loudly, and the catalog card's manual **Update** stays available. Fix the module's CI to emit the sidecar. |
| Nothing happens on a green build, **and the log says the delivery "matched NONE of the N sync config(s)"** | No `_GitSync` targets that repository — usually because the repository was **renamed** and the configs still store its old name. The matcher falls back to GitHub's canonical `full_name` (which follows the rename redirect) and repoints the config when it finds one, so this line surviving means the lookup could not be made either: the repository is unreachable with the config creator's credential, or the hook really is installed on a repository this mesh does not sync. The Warning names both sides — the incoming repository and everything it was compared against. |
| A green build produced nothing, and the log says the fact is **NOT recorded AND the sync did not run** | The `Admin/_Build/{owner}.{repo}` write failed. GitHub was answered 200, so there is no redelivery. The payload is kept at `Admin/_MissedBuild/{owner}.{repo}` (#3374) — read it with `MissedBuildFact.WatchQuery`, and compare it against the current build record to see whether a later green run of the SAME workflow has already superseded it. If a second Warning says the miss could not be recorded either, the log line is the only witness and the fact must be replayed from GitHub. |
| Build node updates but no installation reacts | The package is not installed on that instance. A catalog lists far more packages than any instance installs; only packages with an install record are considered. |
| The same "Update available" reminder keeps reappearing, or the bell re-lights on one you dismissed | Before #3213 this was the norm — a new row per reconcile, forever. The reminder is now told once per candidate version: the install record carries `NotifiedModuleVersion`, and the notification's id is derived from *(record, kind, candidate)*. Seeing it again means the candidate genuinely moved (`ModuleVersion` differs from the one on the record's marker) — read the record and compare the two, rather than assuming a duplicate. |
| The boot log says the feed of a registry could not be read after N attempts and was **recorded as PENDING**, and admins got a bell notification pointing at `Plugins/_RegistryReconcileLedger` | The registry stayed unavailable for longer than the boot's retry budget (#2888). Nothing is lost: the ledger entry for that registry reads `Pending: true`, and the reconcile runs on the next successful feed read — the safety net's, a catalog open, an install — once the registry is back; then check the entry reads `LastReconciledVia: feed-read`. If the registry answers a *definite* refusal (401/403) instead, the entry is pending too, but no read will drain it until the key or grant is fixed — the `LastFault` names which. |
| A module was published and the registry's log says `{Package}: 0/N consumer(s) told — … (404: …)` for this installation | The installation has not allowlisted the inbox target (`WebhookInbox:Targets:N = Plugins/_RegistryReconcileLedger`), or its instance record carries no `HomeUrl`, or the ledger node does not exist yet (no boot pass has run against a configured registry). The safety net reconciles it within `PluginCatalog:ReconcileSafetyNetInterval` regardless; the ledger's `LastReconciledVia` then reads `safety-net` instead of `broadcast`. |
| The registry's log says a consumer answered **401** to the broadcast | The consumer's target declares a `SecretConfigKey` and the registry's `Plugins:Registry:BroadcastSecret` does not match (or is unset). The consumer stores nothing — that is the fail-closed contract of #3312 — and is reached by its safety net meanwhile. |
| A module landed (`RESTART REQUIRED` in the log) and the self-update check reads `RestartUnavailable` | The install cannot roll its own workloads: it does not self-patch, or its updater predates `IDeploymentUpdater.RestartAsync` (the `MeshWeaver.SelfUpdate.Aks` module needs updating). Restart the portal workloads by hand (`kubectl rollout restart deployment/<portal>`) to load the landed generation. |
| The ledger reads `LastReconciledVia: broadcast` but `Pending: true` | Both are true: a broadcast reconciled one package on the module lane, while the boot's full pass against that registry is still owed and drains on the next successful feed read. |

The webhook **never throws** on a write failure: GitHub retries a non-2xx delivery, so an unhandled
fault would turn one bad write into a delivery storm. Failures are logged and reported as "nothing
recorded" instead.

### 🚨 What a failed build-record write costs, and where the fact goes now (#3374)

Answering 200 on a failed write is right — a non-2xx is the storm above — but on its own it made the
event **gone for good**: the build record kept its previous value, the sync the green build
authorises never ran (it hangs off the success branch), GitHub did not redeliver, and nothing else
retried. A transient infrastructure fault became permanent data loss. Measured on `memex-cloud`:
**154 of these in one week**, from two distinct inner causes — an owner that returned no verdict,
and initial state that never arrived within 30 s.

It was also **un-monitorable**, not merely broken. The *attempt* is logged at `Information`, and
`Information` is not emitted on that deployment, so the failure line has no denominator: 154
failures could be 5 % of deliveries or 100 % and nothing could tell them apart.

So the fact is now KEPT. On a failed write the webhook records a `MissedBuildFact` node at
`Admin/_MissedBuild/{owner}.{repo}` carrying the build payload **verbatim**, so the lost build can
be replayed rather than reconstructed from a log line. The delivery still answers 200 and still
reports "nothing recorded" — recording the miss must not change the delivery's outcome.

Two properties are deliberate:

- **There is no drain, no `Pending` flag and no timer.** A later green build of the same workflow
  writes the build record and runs the sync, superseding the lost one outright — so "does this
  record still matter?" is a **read** (`MissedBuildFact.IsSupersededBy`), comparing it against the
  build record that exists now. The happy path therefore writes no extra node, opens no query, and
  cannot fail in a new way. Same *end on a fact you can read* shape `RegistryUpdateReconciler`
  settled on.
- **Superseding is per WORKFLOW.** Run numbers are per-workflow counters, so a busy second workflow
  on the same repository sits far ahead; comparing across workflows would retire every real gap
  almost immediately and hand back the silent loss this record replaced.

The record shares a failure domain with the write that just failed. It is a **different node with a
different owning hub**, so it survives the per-node faults actually observed — but a cluster-wide
fault takes both, and when it does the log says so explicitly ("recorded NOWHERE — this log line is
the only remaining witness") rather than counting the miss as handled.

🚨 Read these with `MissedBuildFact.WatchQuery`, never a bare `nodeType:MissedBuildFact`. Like build
records, they live in the **Admin partition**, which an unscoped query does not reach — it answers
empty however many exist, so "nothing has been missed" would be indistinguishable from "the query
cannot see them".

## Related

- [Plugin Registry](/Doc/Architecture/PluginRegistry) — where the catalog and its credential live
- [Plugin Manual](/Doc/Architecture/PluginAuthoring) — authoring a plugin and its `manifest.lock`
- [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) — the manual counterpart
- [GitHub Sync](/Doc/Architecture/GitHubSync) — the webhook transport this rides on
- [No Static State](/Doc/Architecture/NoStaticState) — why the subscriber is a per-mesh instance
