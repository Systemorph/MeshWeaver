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
instance `memex.meshweaver.cloud`, GitSynced to the private `Systemorph/Memex` repo), and every
change to it is an **action node** (`Hosting/InstanceAction`) that the control instance's operator
executes **in-cluster** under its own service account. The operator's credential lives in the
cluster; the person or agent who asks holds none. That is the whole design: an operator who never
had `az`, `kubectl` or a Loki endpoint can still roll, restart, suspend, audit and reconcile an
instance — and can read what it is running.

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

## What does NOT exist yet — and what that means for you

Measured 2026-09-08 on the control instance: the `DeploymentStatus`, `LogEntry` and `Issue`
**types exist and have ZERO instances**. Nothing samples status in-cluster, nothing answers a log
query, and no replica reports which NodeType assemblies it can actually load. Until those writers
land (the work is in flight; its PRs reference this page), three questions still have only a
break-glass answer:

| Question | Today's break-glass read | Why it is break-glass |
|---|---|---|
| *What image / how many restarts / how old is each replica?* | `az aks command invoke … kubectl -n <ns> get pods -o wide` | needs a cluster credential the API is meant to make unnecessary; the Fleet Console shows the RUNNING version from the record side, not a pod sample |
| *What did the process log at time T?* | `az aks command invoke … curl loki.monitoring.svc.cluster.local:3100/loki/api/v1/query_range …` — and the invoke shell has `curl` but **no `sed`/`python3`** | same; see [MeasuringALivePortalReadOnly](/Doc/Architecture/MeasuringALivePortalReadOnly) for the safe, read-only shapes |
| *Can THIS replica load NodeType X?* | `kubectl exec … ls /tmp/MeshWeaver/.mesh-cache/<Type>*` on EACH replica | a live compile is pod-local (`local` = `FileSystemAssemblyStore`); the record's pointer can be dead on one replica and live on another — see [NodeTypeCompilation](/Doc/Architecture/NodeTypeCompilation) |

A break-glass read is fine; a break-glass **write** is half an operation. The Hosting manual's
"Break glass — when the control plane cannot act" section says exactly which half the cluster
command leaves undone (paywall, backup question, the record's stamp, the audit) and how to
reconcile it in the same session. Do not re-derive that list here — read it there.

## How to read a cluster recipe in this doc tree

1. **Is there an action kind for it?** `set image` + rollout → `Roll`; `rollout restart` →
   `Restart`; `scale --replicas=0` → `Suspend`; `helm upgrade` → `HelmRelease deploy` (today) /
   the record pin + `Roll`; "make the cluster match the record" → `Reconcile`; "what is drifting?"
   → `Audit`. Use the action. The kubectl line is what the operator runs for you.
2. **Is it a read the API does not answer yet?** (the three above) Take it read-only, name it as
   break-glass in what you write down, and file the gap against the Hosting package rather than
   leaving the recipe as the procedure.
3. **Is it a measurement in a war story?** Leave it — it is the reason the rule on the page exists.
   Do not run it to "check"; ask the API question the page's rule now points at.

## Related rules decided the same day

- **A platform roll must not need every satellite re-baked first:** `Modules:VersionStrictness`
  (`Exact` / `Family` / `Minimum`, dev = `Minimum`) and "a sealed publication syncs its own sources"
  — [ModuleVersioning](/Doc/Architecture/ModuleVersioning),
  [SealedPublicationReads](/Doc/Architecture/SealedPublicationReads).
- **Self-update takes clean releases by default;** a `-ci.<n>` build is opted into by a version
  pattern on `Admin/UpdatePolicy` — [ReleaseProcess](/Doc/Architecture/ReleaseProcess). The version
  scheme is `X.Y.Z-ci.<n>` (temporary) → clean `X.Y.Z` (final); `rc` is retired.
- **A cancelled delivery run with zero jobs is a superseded queue entry**, not a lost seal —
  [ReadingCiSignals](/Doc/Architecture/ReadingCiSignals).
- **Steady state is self-update and CD**, never a hand roll —
  [ReleaseStrategy](/Doc/Architecture/ReleaseStrategy), [DeploymentAKS](/Doc/Architecture/DeploymentAKS)
  (the bootstrap runbook, now read under rule 1 above).
