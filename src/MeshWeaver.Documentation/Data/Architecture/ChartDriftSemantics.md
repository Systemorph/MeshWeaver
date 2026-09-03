---
Name: Chart Drift — what a deploy actually does
Category: Architecture
Description: The measured semantics behind the Chart Drift report — which divergence classes are live-wrong, which are hygiene, and why a helm upgrade resolves neither
Icon: BranchCompare
---

# Chart Drift: what a deploy actually does

`Chart Drift` compares what the chart *describes* against what the cluster *runs*
(`deploy/aks/scripts/check-chart-drift.sh`, scheduled daily by `.github/workflows/chart-drift.yml`).
It has been red every day since it first completed on 2026-08-26, and its report was ranked by a
model of `helm upgrade` that **measurement does not support**. This page records the measurement, so
the ranking is not re-derived from intuition every time somebody re-reads the backlog.

## The claim that was wrong

The script's legend, and [MeshWeaver#2355](https://github.com/Systemorph/MeshWeaver/issues/2355)
which was triaged against it for a week, said:

| class | claimed consequence |
|---|---|
| `CLUSTER-ONLY` | *"the next `helm upgrade` **deletes** it"* |
| `DIFFERS` | *"the render would **overwrite** the running value"* |

That framing ranks the backlog by **what a deploy would destroy**. Every re-measurement of #2355
therefore led with "these are one deploy away from being gone", and the probes were called
*"a loaded gun"* and *"the highest-value thing on the list"*.

## What actually happens — measured 2026-09-03

A throwaway release was installed on a local k3s cluster, drifted in the four shapes the detector
reports, and then upgraded with a chart that **genuinely changed** — a new image, a new ConfigMap
value, and a new `periodSeconds` on the same probe object the drift had touched.

| drift applied to the live objects | after `helm upgrade` |
|---|---|
| live-only ConfigMap key | **survives** |
| live-only inline `env:` entry | **survives** |
| inline `env:` duplicating a chart ConfigMap key | **survives** |
| `initialDelaySeconds` added to a live probe | **survives** |

The chart's own three changes all landed — that is the positive control, without which the test
could not have failed. Run on **helm v3.21.1**, the exact binary `az aks command invoke` executes,
and repeated on helm v4.2.4 with an identical result.

The mechanism is Helm's three-way strategic merge: a patch removes only what Helm **previously
owned**. Anything applied out-of-band is in neither the old nor the new manifest, so no patch
touches it. This is also why `Systemorph/Memex#148` records section B as *"a helm upgrade PRESERVES
them"* — that inventory was right and the detector's legend was wrong.

> **A deploy does not clean up drift, and it does not destroy it either.** Both halves matter: the
> urgency the old legend created was false, *and* the reassurance "it will sort itself out on the
> next deploy" is equally false. Drift is only ever cleared deliberately.

## The hazard that is real, and was invisible

An inline `env:` entry **overrides `envFrom`**. So an inline entry whose name matches a key the
ConfigMap supplies does not merely duplicate it — it makes the ConfigMap value **dead** while it
still renders, still reads correctly in `values.*.yaml`, and still shows in `kubectl get configmap`.

This is not hypothetical on this fleet:

- **#2235** — the ConfigMap said `Hosting/PlatformBuilds`, the pod ran `Store/Payments`, every
  signal was green, and release broadcast 404'd for eleven days.
- **memex boot crash, 2026-08-30** — `EMAIL__*` patched onto the Deployment beside the chart's own
  `Email__*` ConfigMap keys. Linux env is case-**sensitive** so the pod carried both; .NET's
  configuration provider is case-**insensitive** and the last enumerated wins, so the effective
  value was a coin toss per pod start. It died on `EmailConfigurationGuard` with SIGABRT, and the
  restart on identical config came up fine.

The detector's header had described this override from the day it was written — but nothing in it
ever **checked** for it. Every such entry was reported as a plain `CLUSTER-ONLY`, sorted in among
dozens of harmless ones, which is precisely why #2355 read as an undifferentiated backlog nobody
owned.

## The classes now, worst first

| class | meaning | when it bites |
|---|---|---|
| `COLLIDES` | inline `env:` + a ConfigMap key differing **only in case** | **already**, at random, every pod start |
| `SHADOWS` | inline `env:` + the **same** key from **any** envFrom source | **already** — the shadowed value is dead |
| `CLUSTER-ONLY` | live, in no committed source | on a rebuild or restore, and at every review |
| `CHART-ONLY` | rendered, never applied | nobody is getting it today |
| `DIFFERS` | both sides, values disagree | never resolves itself; needs a decision — except an `inline env`, which helm owns and an upgrade resets |

### Any envFrom source can be shadowed, and a secret is the silent version

`envFrom` carries **secrets**, the portal's own ConfigMap, and any further source added through
`.Values.extraEnvFrom` — the chart documents both `{secretRef: …}` and `{configMapRef: …}` as
entries there. An inline `env:` overrides all of them identically, so the checker enumerates the key
names of **every** envFrom source, not just the secrets. Enumerating one kind and not the other
would have left exactly the blind spot MeshWeaver#3201 exposed, one source-kind over: a checker that
can see half of the class it is named for reports "clean" for a reason its reader cannot guess.

A shadowed secret is the worst version of this: the credential the platform provisioned through Key
Vault is inert, and the value actually in use sits in plaintext on the Deployment spec, in no
committed source, readable by anything that can `get deploy`.

That is live on `memex` today (MeshWeaver#3201): `PluginCatalog__RegistryToken` exists both in
`secret/memex-portal-secrets` and as an inline entry, and **the two values differ** — so the portal
authenticates to the plugin registry with the plaintext copy while the managed credential goes
unused. It also means the obvious cleanup is wrong: deleting the inline entry does not restore the
status quo, it switches the portal onto a different token.

The checker reads only the KEY NAMES of every envFrom source other than `memex-portal-config`,
projected inside the cluster — a drift checker must not become the thing that copies the credentials
it audits onto a CI runner's disk. So a `SHADOWS` over one of those sources carries **no**
agree/disagree verdict: unknown is reported as unknown rather than guessed. A `SHADOWS` over
`memex-portal-config` — the one source fetched in full — does report whether the two values **agree
today**. Agreeing is not safe: it means the next change to the chart will silently fail to take
effect. Disagreeing means somebody is already reading a setting no pod uses.

> Fixing a `SHADOWS` or `COLLIDES` takes **both** steps, in order: put the intended value in the
> chart, *then* delete the inline entry (`kubectl set env deploy/… KEY-`). Either step alone leaves
> the pod on the inline value — so a plan that reads "add these to the chart and the drift clears"
> leaves the cluster exactly where it was.

## The probes are inert, and the chart is authoritative

`DIFFERS livenessProbe` / `readinessProbe` on `memex` — live carries `initialDelaySeconds` 60 and
20, this chart carries none — was read three times as *"live is authoritative, add them to the
chart"*. It is the opposite:

- Kubernetes **suspends liveness and readiness entirely until the `startupProbe` succeeds**, and
  the live pod carries that startup probe byte-identically (`/health`, `periodSeconds: 5`,
  `failureThreshold: 60` — a 300 s window). A delay measured from container start can therefore
  never be the thing protecting a slow boot.
- `memex-cloud` runs this exact probe block with **no `initialDelaySeconds` at all**, and is
  healthy — the "loaded gun" already fired there, harmlessly.

So the divergence is real but **inert**, and adding `initialDelaySeconds` to the chart would
enshrine dead config. The reasoning now lives beside the probes in
`deploy/helm/templates/memex-portal/deployment.yaml`, so the next reader of the drift report does
not re-derive the wrong answer.

## The one drift shape the checker could not see at all

Until 2026-09-03 an inline `env:` entry present on **both** sides was matched by NAME and skipped —
after incrementing the comparison counter, so the run reported it as a field it had compared. The
chart renders five such entries (the `DOTNET_Dbg*` crash-dump block and the self-updater's
`AZURE_CLIENT_ID`), and a `kubectl set env` over any of them produced **no finding whatsoever**: a
wrong `AZURE_CLIENT_ID` breaks the ACR credential the self-updater pulls with, silently, and this
report would have said the namespace's inline env was clean.

Both entries are now compared in full — values compared, never printed, because an inline env can
hold a token. It reports `DIFFERS inline env <name>`, and that class carries the one exception to
the measurement above: **helm owns an entry it renders**, so an upgrade *does* reset that one. The
finding says so, rather than repeating the generic "a deploy does not resolve this".

## What this report does NOT prove

🚨 **The left-hand side is not the chart a deploy renders.** `chart-drift.yml` renders
`deploy/helm/values.yaml + deployments/aks/<ns>/values.<release>.public.yaml`. `helm-release.yml`
(mode `deploy`) renders `<the Key Vault capture> + <that same committed overlay>`, where the capture
is a `helm get values` of the live release — the whole user-supplied values document, `config:`
included. The overlay is layered LAST, so:

- for a key the committed overlay **carries**, the two renders agree and the finding is sound;
- for a key it does **not** carry, the "chart" column is the chart *default*, and a deploy may
  render something else entirely.

That confines the doubt rather than removing it, and the confinement is checkable per finding: look
the key up in the overlay before acting on a `DIFFERS`. The durable fix is that the capture carries
**no `config:` section at all** — the overlay is already generated from the `Hosting/Deployment`
record (`HelmValues.Render`), so the vault half only needs to hold the secret sections it was
created for. Asserting that belongs beside the capture, in `Systemorph/Memex`'s `helm-release.yml`;
until it exists, this report's `DIFFERS` column is an input to a decision and not the decision.

The same boundary in one line: **this check answers "does the cluster match the chart as CI renders
it". It does not answer "is every committed value live"** — that is `Systemorph/Memex`'s
`deploy-drift.yml`, which compares git against the deploy record. Neither substitutes for the other,
and a finding can be produced by an un-run deploy rather than by cluster drift.

## Reading the report

Findings are printed worst-first and the summary counts each class. Rank the work that way:
`COLLIDES` and `SHADOWS` are live-wrong now; `CLUSTER-ONLY` is the migration tracked by
`Systemorph/Memex#148` (*nothing may live only on the cluster* — every value belongs on the
`Hosting/Deployment` record, rendered by the chart); `DIFFERS` is a decision, not a deploy.

## The backlog, classified — run 33755512452, 2026-09-03

"91 divergences" is not a worklist. **76 today** (`memex` 32 across 188 compared fields,
`memex-cloud` 44 across 204), and they are seven groups, not seventy-six decisions. Counts are from
the run's own log; no cluster was touched to produce them.

| # | group | count | what it is | what clears it |
|---|---|---|---|---|
| 1 | **Disagreeing `SHADOWS`** | **8** | the pod runs a value the ConfigMap contradicts — `PreWarm__BatchBake`/`PrebuiltBundleRoot` (both), `PluginCatalog__RegistryUrl` (both), `PreWarm__GateReadiness` + `Features__Ai__Providers__AzureOpenAI` (memex) | chart value first, **then** delete the inline entry |
| 2 | **The registry credential** | **2** | `PluginCatalog__RegistryToken`: on memex a `SHADOWS` over `secret/memex-portal-secrets` whose two values differ; on memex-cloud the inline entry is the ONLY copy | MeshWeaver#3201 — establish the live token, vault it, delete inline, rotate |
| 3 | **Agreeing `SHADOWS`** | **29** | not wrong today; the ConfigMap is simply not what the pod reads, so the next chart change to any of them silently fails — Kestrel endpoints, `PluginCatalog__Sources__*`, `WebhookInbox__Targets__0` | same two steps, no urgency |
| 4 | **Chart-retired, inert** | **8** | `FrameworkBroadcast__Subscribers__0..3` on both namespaces. The chart stopped rendering these on 2026-09-03 — the subscriber set is mesh data now — so the live keys feed nothing | delete the keys; drop them from the memex-cloud overlay, which still sets them |
| 5 | **Cluster-only, now expressible** | **22** | live-edited settings the chart had no key for until MeshWeaver#3199 — AI providers, `LogWatch__*`, `Speech__*`, `Commerce__BaseUrl`, `Features__Ai__Clis__*`, `Portal__ReactAppUrl` | put them on the `Hosting/Deployment` record — `Systemorph/Memex#148` |
| 6 | **Committed, never deployed** | **5** | memex-cloud's overlay carries `PreWarm__{BatchBake,BuildProtocol,DynamicTypes,GateReadiness}: "true"` and `probes.startup.failureThreshold: 1080`; live runs the shipped defaults | a deploy, not a cleanup — this is `deploy-drift`'s class surfacing here |
| 7 | **Ruled inert** | **2** | the memex liveness/readiness `initialDelaySeconds` — see above; the chart is authoritative | nothing; do not "fix" it |

Zero `COLLIDES` and zero `CHART-ONLY`. The `EMAIL__*` collisions that crashed memex at boot on
2026-08-30, and the four blanked `ModelTier__*`, are gone from both namespaces.

**Groups 1–5 all need a Deployment-spec edit, and that is blocked**: MeshWeaver#3207 — the portal
image is ahead of its schema, so any pod-template change spawns a ReplicaSet that lands on
`DbVersionGate` and crash-loops, and both portals are pinned under a freeze. The classification is
the deliverable until the schema is current; nothing here is urgent enough to lift a freeze for.

See also [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) and
[Deployment.md](/Doc/Architecture/Deployment).
