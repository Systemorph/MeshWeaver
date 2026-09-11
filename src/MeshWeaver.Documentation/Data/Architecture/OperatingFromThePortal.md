---
Name: Operating from the portal, not the cluster
Category: Architecture
Description: The operating policy for every deployment — operations and diagnostics go through the Hosting surfaces of the control instance; az aks command invoke, kubectl, Loki-by-curl and a hand helm-release dispatch are break-glass. What exists today, what does not yet, and how each cluster recipe in this doc tree is to be read.
Icon: Cloud
---

# Operating from the portal, not the cluster

**Maintainer directive, 2026-09-08:** *"do all the operations through the memex api"* · *"no direct
access of aks etc"* · *"build the api in a way that you don't need any az access"*.

An instance is a **record** (`Deployments/<name>`, a `Hosting/Deployment` node on the control
instance **`memex.systemorph.com`**, GitSynced to the private `Systemorph/Memex` repo), and every
change to it is an **action node** (`Hosting/InstanceAction`) that the control instance's operator
executes **in-cluster** under its own service account. The operator's credential lives in the
cluster; the person or agent who asks holds none. That is the whole design: an operator who never
had `az`, `kubectl` or a Loki endpoint can still roll, restart, suspend, audit and reconcile an
instance — and can read what it is running.

### 🚨 Which portal am I talking to? The MCP server named `memex` is NOT the instance named `memex`

Measured 2026-09-10, and it cost two sessions an hour on the same morning:

| MCP server | Host | What it is |
|---|---|---|
| `systemorph` | `memex.systemorph.com` | the **control instance** — the live, GitSynced `Deployments` space |
| `memex` | `memex.meshweaver.cloud` | the public portal and the **plugin registry** |

The instance *named* `memex` is `memex.systemorph.com`, so the server named after it is the *other*
one. Both portals hold nodes at `Deployments/<name>`, and **only the control instance's copy is
authoritative**: on 2026-09-10 the control instance's `Deployments/memex-cloud` was version 60,
`createdDate` 2026-08-10, `lastModifiedBy` `system-security`, written 64 s after the config-repo
merge; `memex.meshweaver.cloud`'s was version 9, `createdDate` 2026-08-30, and 40 versions behind.

That second copy is **not unsynced** — an earlier revision of this page called it "not a sync
target at all", and that was wrong. `memex.meshweaver.cloud` carries its own `Deployments/_GitSync`,
a `GitHubSyncConfig` for the SAME folder (`Systemorph/Memex`, `mesh/Deployments`, `twoWay: true`,
created 2026-08-30). Measured 2026-09-11:

| Field | Reading |
|---|---|
| `lastSyncOutcome` | `Imported` |
| `lastSyncCommitSha` · `lastSyncedAt` | one commit · 2026-09-09 |
| `lastAttemptedCommitSha` · `lastSyncAttemptAt` | a DIFFERENT commit · 2026-09-11 |
| `lastAttemptWasFinal` | `true` |

`Imported` beside a `lastSyncCommitSha` that does not move is **not by itself a stalled sync**.
`GitHubSyncService.MayAdvanceBaseline` deliberately holds that baseline whenever an import preserved
server-newer nodes or failed any, and **`lastAttemptWasFinal` is the discriminator**: `true` means
the verdict at `lastAttemptedCommitSha` is settled — a preserved node or a content-verdict refusal,
which re-attempting the same commit cannot change — while `false` means a transient failure the next
delivery retries. Here it is `true`: a SETTLED divergence. Either this portal's two-way import kept
its own server-newer `Deployments/*` nodes over the repository's, or it refused some of the
repository's content; the sync's activity log says which. So the copy is synced AND disagrees with
the record, by design — which is exactly why it is not the record. It is scheduled for retirement
with the GitHub App split; until then read `Deployments/*` on the control instance only. Three facts settle which portal is which without guessing: the `Deployments/memex`
record's own `host` and `purpose`, and every other
instance's `Hosting__ReportTo`, which points at `https://memex.systemorph.com`.

**So confirm the portal before drawing any conclusion from a read of it** — `/api/version`, or the
MCP server's configured URL. This is the same class as the `namespace: memex` confusion in #3883,
where the word named a Kubernetes namespace rather than an instance.

The rest of this doc tree still carries `az aks command invoke …` / `kubectl …` recipes. **They are
evidence, not procedure**: each one is either a measurement that was taken through the cluster
before the API surface existed (kept because the war story is the reason the rule exists), or a
break-glass read for the case where the control plane itself cannot act. Read them that way.

## What exists today (measured on the control instance, 2026-09-08)

The `Hosting` package (MeshWeaver.Plugins, `Hosting/**`; its own manual is the in-mesh page
`Hosting/Guide`, source
[Hosting/Guide.md](https://github.com/Systemorph/MeshWeaver.Plugins/blob/main/Hosting/Guide.md))
gives you:

| Surface | What it answers / does |
|---|---|
| **`/Hosting/Console`** — the Fleet Console | every instance at a glance: recorded state, the version it is RUNNING, sampled health with the sample's age, per-instance log links. It reports; it does not command. |
| **`Deployments/<name>`** (`Hosting/Deployment`) | the record — host, namespace, cluster, database, image repository, key-vault classes, env precedence, operator settings. The record's image pin **is** the roll. |
| **`Hosting/InstanceAction`** kinds | `Provision`, `Teardown`, `Backup`, `Restore`, `Suspend`, `Reactivate`, `HelmRelease` (`helmAction: capture\|adopt\|deploy` — dispatches the config repo's `helm-release.yml` and follows it), `Roll` (set image to `imageTag`, else the record's pin, then WAIT for the rollout), `Restart` (rolling restart, then WAIT), `InstallAddOn`, `Audit`, `RotateRegistryKey`, `Reconcile` (converge a drifted instance back onto its record — the reconcile loop). Each run carries phases, a log, the invoker's identity and the name-the-instance confirmation. |
| **`Hosting/DeploymentStatus`**, **`Hosting/LogEntry`**, **`Hosting/Issue`** (types) | the designed observation surfaces — one status sample per deployment (ready/desired replicas, restarts, health, RUNNING image, last activity), structured log records pulled from the logging backend, filed issues. |
| **`Sample`** and **`Logs`** (`Hosting/InstanceAction` kinds — Systemorph/MeshWeaver.Plugins#1521, the delivery of the paragraphs below) | READ-ONLY, no operator job, no confirmation: `Sample` writes `Ops/Status/<id>` with `replicas[]` — per pod the image, ready, restarts, started, phase, terminating, generation, and what the pod's OWN `/health` says (verdict, detail, `version`, `frameworkIdentity`, `pluginCount`) — plus `generations`, `converged` and `warnings[]` (unknown is never zero); `Logs` + `query`/`sinceMinutes`/`limit`/`pod` lands a Loki window as `Hosting/LogEntry` nodes under `Ops/Logs` with the exact LogQL, the count and whether it was CUT on the run. Both read Prometheus and Loki from the control instance's own pod, where they are credential-free. |

An action is one node:

```json
{ "id": "roll-memex", "namespace": "Ops/Actions", "name": "Roll memex",
  "nodeType": "Hosting/InstanceAction",
  "content": { "$type": "InstanceActionContent",
    "deployment": "memex", "requestedAction": "Roll", "imageTag": "3.0.0-ci.8079",
    "confirmation": "memex", "reason": "…", "dryRun": true } }
```

Start with `dryRun: true` — it renders the exact commands and changes nothing. Then watch the same
node: `state` goes `Requested → Running → Done`, or `Failed` naming the phase, or `Refused` with
the question you did not answer. Through MCP that is `create` / `get`; through the portal it is the
node's own page.

## What did NOT exist on 2026-09-08 — and what delivers it

Measured 2026-09-08 10:40Z on the control instance: the `DeploymentStatus`, `LogEntry` and
`Issue` **types existed and had ZERO instances**, `Ops` itself did not exist, and the maintainer's
own identity was refused `Create` on `Ops/Actions/…`. Nothing sampled status in-cluster, nothing
answered a log query, and no replica reported what it could load. **The delivery is
Systemorph/MeshWeaver.Plugins#1521** (Hosting module) — read this table as "before / after it
lands on the control instance":

| Question | Before (break-glass read) | After #1521 (one node, no credential) |
|---|---|---|
| *What image / how many restarts / how old is each replica — and is an old process still a cluster member?* | `az aks command invoke … kubectl -n <ns> get pods -o wide` | `{ "requestedAction": "Sample" }` → `Ops/Status/<id>`: `replicas[]` with image, ready, restarts, started, phase, `terminating`, `generation`; `generations` and `converged` on the node. A roll (`Roll`/`Restart`/`Reconcile`/`Reactivate`) now ends with **Verify one generation** and refuses Done while a previous-generation pod is still a member — the 8059-after-8079 measurement. |
| *What did the process log at time T?* | `az aks command invoke … curl loki.monitoring.svc.cluster.local:3100/loki/api/v1/query_range …` — the invoke shell has `curl` but **no `sed`/`python3`** | `{ "requestedAction": "Logs", "query": "…", "sinceMinutes": 60, "limit": 300 }` → `logQl`, `entryCount`, `truncated` on the run; lines under `Ops/Logs`, the Deployment page's Logs area. 🚨 Zero entries with a `logQl` is an answer only once a **same-text positive control** passes — measured 2026-09-11, three in-window zeros for lines LogWatch held samples of from the same namespace under three hours earlier ([Log entries are a query result](/Doc/Architecture/LogEntriesAreAQueryResult)). |
| *Can THIS replica load NodeType X?* | `kubectl exec … ls /tmp/MeshWeaver/.mesh-cache/<Type>*` on EACH replica — a live compile is pod-local (`local` = `FileSystemAssemblyStore`), see [NodeTypeCompilation](/Doc/Architecture/NodeTypeCompilation) | **the core half has landed.** `/health` is a system-side census, public and past RLS, and the `Sample` keeps each pod's whole body: `content-types` names every type this replica could not TYPE; `bake-report` (census-tagged, so it prints CLEAN too) carries `total`/`baked`/`pending`, the per-state breakdown and `ClassifiedFromLocalAdoption`; `source-discovery` carries the fold's chunk count and largest inter-chunk gap. **Still partial**, and say so: `content-types` records a degradation only when a read degrades, so a type nobody has opened on this replica is in neither list — for that, the boot log remains the only source. A Ready process reporting **0 plugins** already degrades the sample, which is the shape that outage wore. |
| *Who may create the first action?* | nobody — `Ops` did not exist and no grant path existed | the Hosting module provisions `Ops` and mirrors every `Admin` on `Admin/_Access` as `Admin` on `Ops/_Access` at start (`OperationalSpaceProvisioning`, on the always-activated `Hosting/PlatformBuilds` hub). |

Until #1521 is on the control instance, the "before" column is what you have; see
[MeasuringALivePortalReadOnly](/Doc/Architecture/MeasuringALivePortalReadOnly) for the safe,
read-only shapes. A break-glass read is fine; a break-glass **write** is half an operation. The
Hosting manual's "Break glass — when the control plane cannot act" section says exactly which
half the cluster command leaves undone (paywall, backup question, the record's stamp, the audit)
and how to reconcile it in the same session. Do not re-derive that list here — read it there.

## How to read a cluster recipe in this doc tree

1. **Is there an action kind for it?** `set image` + rollout → `Roll`; `rollout restart` →
   `Restart`; `scale --replicas=0` → `Suspend`; `helm upgrade` → `HelmRelease deploy` (today) /
   the record pin + `Roll`; "make the cluster match the record" → `Reconcile`; "what is drifting?"
   → `Audit`; `patch pvc … storage` → `volumes[].size` on the record + `Reconcile` (the operator's
   `hosting-pv-resize`, which never shrinks and reads the capacity back); `set env deploy/<name> <KEY>-` → `retiredBy` on the record's
   `inlineEnv` entry + `Reconcile` (the operator's `hosting-inline-env-retire`). Use the action.
   The kubectl line is what the operator runs for you.
2. **Is it a read the API does not answer yet?** (the three above) Take it read-only, name it as
   break-glass in what you write down, and file the gap against the Hosting package rather than
   leaving the recipe as the procedure.
3. **Is it a measurement in a war story?** Leave it — it is the reason the rule on the page exists.
   Do not run it to "check"; ask the API question the page's rule now points at.

### Retiring an inline `env:` entry — `Reconcile`, driven by the record's `retiredBy`

Until 2026-09-11 this was the one routine WRITE with no action kind. No repository change and no
`InstanceAction` could remove an inline `env:` entry from a Deployment; only a break-glass
`kubectl set env deploy/<name> <KEY>-` could. The chart never rendered one (it emits four
unconditional portal entries — the `DOTNET_Dbg*` crash-dump set — plus `AZURE_CLIENT_ID` when
`selfUpdate.azureClientId` is set, and two per gate sidecar; every name is fixed and no values key
extends the list), the record's `inlineEnv` is declarative by contract — *dropping an entry does not
delete one* — and `ReapplyRecord` is a `helm upgrade`, whose three-way merge removes only what helm
previously owned. `Audit` detected the drift precisely, under `envLiveOnly` and
`plainSecretEntries`, and no remedy could act on it.

**The lane is now `Reconcile`** ([MeshWeaver.Plugins#1593](https://github.com/Systemorph/MeshWeaver.Plugins/issues/1593)). Its `RetireInlineEnv` remedy reads the one statement the
record could always make and nothing read — `inlineEnv[].retiredBy` — and removes exactly the
entries that carry it:

1. **Mark the entry retired on the record**: `retiredBy` names the issue that ends it, `shadows`
   names the `envFrom` source it falls through to, and a credential also needs
   `agreesWithShadowed: true`. The plan REFUSES, naming the entry, a sole source (`shadows` blank —
   removal would blank the key), a credential whose equality is not recorded `true`, and any entry
   the record says disagrees with its shadow. One refused entry refuses the whole `Reconcile`.
2. **File a `Reconcile`.** The retirement runs after `ReapplyRecord` — so the key's declared home is
   on the pod first — and before any roll. The operator's `hosting-inline-env-retire` refuses while
   a rollout is in progress; walks the portal container's `envFrom` in order and requires the LAST
   source carrying the key to be the one the record names; re-measures in-cluster that the inline
   value and that source are **EQUAL** (lengths and a verdict, never a value); and only then removes
   the key from **every** container that carries it, the gate sidecars included, in one patch
   guarded on the Deployment's `resourceVersion`. The run waits for the rollout and ends in the
   re-audit and **Verify one generation**, like every `Reconcile`.
3. **Drop the entry from the record** once the closing audit no longer lists it. Until then the
   record still describes a shadow, and `RotateRegistryKey` refuses while the record lists an inline
   entry for any key the registry key lands as; `hosting-kv-rotate` refuses the same thing
   in-cluster, before it mints.

🚨 **It works only once the control instance has BOTH halves**: an operator image that carries
`hosting-inline-env-retire` (the operator image is control-instance configuration, moved by a
`helm-release deploy`, not by any record) and the Hosting module version that plans the remedy
(1.17). On an older operator image the step fails by name (`command not found`) after the re-apply
and nothing is removed. And it retires a shadow; it does **not** remediate a disclosure — a
credential that sat in plaintext in a Deployment spec still needs rotating at its issuer, which is a
separate act (MeshWeaver#3201 is the worked example).

## Related rules decided the same day

- **The Deployment record is the ONE input.** Aspire and Helm render from it; the image receives
  it as configuration (`Deployment:Record`); Aspire emits a record, never a chart. The record is
  built fluently (`AddMemex("memex").WithImage(…).WithPluginRepo(…)`), the adapter's own copy of
  it (`MemexOptions`) is gone, and generating the chart from Aspire (#3646) is retired —
  [ConfiguringAnInstanceFromAspire](/Doc/Architecture/ConfiguringAnInstanceFromAspire).
- **Volume capacity is a record property:** `volumes[].size` on the Deployment record is what a
  claim holds. `Provision` and `Reconcile` grow every declared claim to it through
  `hosting-pv-resize` (grow-only, read back from the claim's status, refuses a class that cannot
  expand); `Audit` reports a claim that has fallen below its record. The 16Gi `/data` share that
  measured FULL on `memex.systemorph.com` at 13:51Z that day is the case —
  [DeploymentAKS](/Doc/Architecture/DeploymentAKS) → "Volume capacity is a record property".
- **A platform roll must not need every satellite re-baked first:** `Modules:VersionStrictness`
  (`Exact` / `Family` / `Minimum`, dev = `Minimum`) and "a sealed publication syncs its own sources"
  — [ModuleVersioning](/Doc/Architecture/ModuleVersioning),
  [SealedPublicationReads](/Doc/Architecture/SealedPublicationReads).
- **Self-update takes clean releases by default;** a `-ci.<n>` build is opted into by a version
  pattern on `Admin/UpdatePolicy` — [ReleaseProcess](/Doc/Architecture/ReleaseProcess). The version
  scheme is `X.Y.Z-ci.<n>` (temporary) → clean `X.Y.Z` (final); `rc` is retired.
- **A cancelled delivery run with zero jobs is a superseded queue entry**, not a lost seal —
  [ReadingCiSignals](/Doc/Architecture/ReadingCiSignals).
- **The operator's ClusterRole follows the chart:** `deploy/aks/manifests/hosting-operator/operator-rbac.yaml`
  is applied by the config repository's helm-release lane on every `adopt` / `deploy`, from the
  chart at the pin; the operator test suite refuses a script that names a `kubectl` verb+resource
  the role does not grant (`check-rbac-coverage.sh`). Measured 2026-09-09: the first Reconcile
  through the fixed operator stopped at step 1/6 with `storageclasses … is forbidden` — the grant
  had been on `main` for hours, the cluster's role predated it, and only a laptop could have moved
  it. A control-plane input with no lane is a defect, whichever file it lives in.
- **`/api/version` names the delivery run's RESOLVED target commit, not the set's queue tip.**
  `3.0.0-ci.8131` is CD run 34260408777 with head `e1039813e`; its "Resolve the target commit"
  job picked `3853374f7` (the merge one second earlier in the same queue group), and that is what
  the image answers. Compare a portal's commit against the run's resolve job, never against the
  run's head or `main` — read as "not 8131", it cost an hour on 2026-09-09.
- **Steady state is self-update and CD**, never a hand roll —
  [ReleaseStrategy](/Doc/Architecture/ReleaseStrategy), [DeploymentAKS](/Doc/Architecture/DeploymentAKS)
  (the bootstrap runbook, now read under rule 1 above).
