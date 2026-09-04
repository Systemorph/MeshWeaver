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

One exception, and it follows from the same mechanism rather than qualifying it: a key the LAST
deploy rendered and the CURRENT chart does not is in the release's own manifest, so helm owned it
and the merge deletes it. That is not a cluster-only setting surviving; it is a chart retirement
landing. The gate computes that case and reports it as a `PENDING DELETION` — the discriminator and
the mechanism are under *"`CLUSTER-ONLY` splits in two"* below.

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
| `CLUSTER-ONLY` | live, in no committed source | on a rebuild or restore, and at every review — **unless the release manifest still owns the key**, when the next upgrade deletes it and the finding reads `PENDING DELETION` |
| `CHART-ONLY` | rendered, never applied | nobody is getting it today |
| `DIFFERS` | both sides, values disagree | never resolves itself; needs a decision — except an `inline env`, which helm owns and an upgrade resets |

### Any envFrom source can be shadowed, and a secret is the silent version

`envFrom` carries **secrets**, the portal's own ConfigMap, and any further source added through
`.Values.extraEnvFrom` — the chart documents both `{secretRef: …}` and `{configMapRef: …}` as
entries there. An inline `env:` overrides all of them identically, so the checker enumerates the key
names of **every** envFrom source, not just the secrets. Enumerating one kind and not the other
would have left exactly the blind spot MeshWeaver#3201 exposed, one source-kind over: a checker that
can see half of the class it is named for reports "clean" for a reason its reader cannot guess.

A shadowed secret is the worst version of this: the shadowed copy is inert, and the value actually
in use sits in plaintext on the Deployment spec, in no committed source, readable by anything that
can `get deploy`.

That is live on `memex` today (MeshWeaver#3201): `PluginCatalog__RegistryToken` exists both in
`secret/memex-portal-secrets` and as an inline entry, and **the two values differ** — so the portal
authenticates to the plugin registry with the plaintext copy while the secret's copy goes unused. It
also means the obvious cleanup is wrong: deleting the inline entry does not restore the status quo.

### A credential shadow can be two PRINCIPALS, not two values

Re-measured on the live cluster 2026-09-04, that case was worse than "two values of one setting",
and in a way the checker cannot see — which is why the conclusion belongs here rather than in its
output:

- **Both sides are valid.** The live inline key and the shadowed copy each authenticate to
  `https://memex.meshweaver.cloud/api/plugins` — verified out of band from inside the cluster, with
  an anonymous call and a syntactically-valid bogus key as the two controls (both `401`).
- **They are different registered instances.** Hashing each credential *inside the cluster* and
  comparing against the `MeshWeaverInstance.keyHash` the registry stores identifies the inline key
  as instance `memex` — the portal's own identity, and the correct one. The shadowed copy matches
  no enumerable instance, and its catalog projection is `Plugins/*` **plus** `Crm/*`, a strict
  superset of what instance `memex` is granted.
- **So the reflex cleanup re-identifies the deployment.** Deleting the inline entry would not have
  "switched onto an unverified token"; it would have made the portal authenticate as a *different
  instance*, silently widening what it may pull from 44 packages to 45.
- **There was never a Key Vault copy.** `secret/memex-portal-secrets` is helm-rendered
  (`app.kubernetes.io/managed-by=Helm`), not CSI-provisioned. The CSI-backed secrets in those
  namespaces are `memex-kv-secrets` and `memexcloud-portal-ai-secrets`, neither of which carries
  this key, and the `Systemorph` vault holds no `PluginCatalog-RegistryToken` entry for either
  portal. `deployments/aks/secretproviderclass.reference.yaml` in `Systemorph/Memex` already
  templates that wiring; it was never applied to either live SecretProviderClass.

The generalisable rule: **for a credential, "which value is live?" is not the whole question — "which
identity does each side authenticate as?" is.** A name-only checker can report the shadow and must
say the two sides are unknown; it cannot say they are the same credential, and it must not narrate a
provenance it never observed. The comparator asserted a Key Vault origin here until 2026-09-04, and
the self-test now fails if that claim returns.

Neither namespace sets `PluginCatalog__InstanceId` and neither carries a `PluginCatalog__BootstrapKey`,
so nothing self-registers: **the token IS the identity**. memex's own ConfigMap asks for
`PluginCatalog__InstallByDefault__0=Plugins/*`, which is exactly instance `memex`'s grant and not the
shadowed copy's — the configuration and the live key agree, and only the shadowed copy is the odd one
out.

### Clearing it, and what a rotation does NOT need

The order is forced by the fact that the live key is the correct one:

1. **Vault the LIVE key of each portal** — `memex-PluginCatalog-RegistryToken` and
   `memexcloud-PluginCatalog-RegistryToken` in the `Systemorph` vault, then add the object plus its
   `secretObjects.data` mapping to the namespace's SecretProviderClass (`memex-kv`,
   `memexcloud-portal-ai-secrets`). Additive and inert: those CSI secrets sit *after*
   `memex-portal-secrets` in `envFrom`, and the inline entry outranks both until it is deleted.
2. **Drop the foreign copy from the source that renders it.** `secret/memex-portal-secrets` is
   helm-rendered, so deleting the live Secret key alone is undone by the next `helm upgrade` — the
   env's (uncommitted) values file must stop setting `secrets.memex_portal.PluginCatalog__RegistryToken`.
3. **Then delete the inline `env:` entries** — a Deployment-spec edit, so a rollout on both portals.
4. **Then rotate**, because both live keys sat in plaintext in a spec readable by anything that can
   `get deploy`.

🚨 **Rotating an instance key needs no re-granting.** `PluginGrant` nodes are keyed by
`instanceId` (`Admin/_PluginGrant/memex`, `…/memex-cloud`), and re-issuing a key replaces
`MeshWeaverInstance.KeyHash` on the *same* instance record — so entitlement, plan and install history
survive untouched. What must be updated is every **holder of the key value**, and measured on
2026-09-04 that is a short list: the Key Vault entry from step 1, and nothing else. No GitHub Actions
secret in `Systemorph/MeshWeaver` or the org holds either portal's instance key (the `ci-*` instances
used by node repos are separate records with separate keys), and `Plugins__Registry__PublishToken` is
a different credential entirely. **Re-registering a new instance instead of re-issuing the key is the
move that would need re-granting** — and would lose the install history keyed to the old id.

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

**`COLLIDES` and `SHADOWS` are immune to this**, which matters because they are the two classes
ranked first. Their agree/disagree verdict is computed against the **live** ConfigMap — the one the
pod actually mounts — and falls back to the render only for a key that is rendered but not yet live.
So the finding "the pod runs one value and the ConfigMap holds another" is a statement about two
live objects, and no values-layer question can touch it. On the 2026-09-03 run that is 38 of the 76
findings, of which the 8 disagreeing ones are the whole of the live-wrong list.

For the rest, the doubt is checkable per finding: look the key up in the overlay before acting on a
`CLUSTER-ONLY` or a `DIFFERS`. A key with **no chart key at all** is also immune — nothing can render
it whatever the capture says. The durable fix is that the capture carries
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
`Hosting/Deployment` record, rendered by the chart), **except for the `PENDING DELETION` findings
inside it, which are dated work: the next deploy removes them**; `DIFFERS` is a decision, not a
deploy.

## `CLUSTER-ONLY` splits in two, and the check now computes the split

There are **three** sides to chart drift, not two:

| side | what it is | how it is read |
|---|---|---|
| **D** | the chart as CI renders it | `helm template` |
| **L** | the live cluster objects | `kubectl get …` |
| **M** | the last-deployed release manifest | `helm get manifest <release> -n <ns>` |

Everything above this section is **D vs L**. `hosting-audit` (`deploy/aks/operator/bin`) computes
**M vs L**. The deletion hazard is in **neither**: it is **D vs M**.

"A `helm upgrade` does not delete cluster-only settings" is the measured rule, and it is right about
the *mechanism* — helm removes only what it **previously owned**. But a key can be cluster-only
*today* precisely **because the chart stopped rendering a key the last deploy did render**, and that
key IS in `M`. The three-way merge then deletes it, correctly. So the class has two mechanically
distinguishable halves, and `M` is the discriminator:

| sub-class | test | next `helm upgrade` |
|---|---|---|
| **owned-but-retired** | the key IS in `helm get manifest` | **deletes it** — helm owned it |
| **never-owned** | the key is NOT in `helm get manifest` | **leaves it** — the migration backlog |

**Since 2026-09-04 the gate computes this itself.** `check-chart-drift.sh` fetches `M` on both
transports and hands it to the comparator, which reports an owned-but-retired finding as a
**`PENDING DELETION`** — still inside the `CLUSTER-ONLY` class, because the class is "live and not
rendered" and both halves are that, but with the verdict written into the finding and a count in the
summary line. The never-owned half now says *helm never owned it* explicitly rather than leaving
"a `helm upgrade` PRESERVES it" to be taken on trust.

**The manifest is a required input, and an unreadable one is RED.** Both the script and the
comparator refuse to run without it. That is not defensiveness: with `M` missing, *every* live-only
key reads as never-owned — the harmless half — so the report would answer "nothing here is about to
be deleted" at exactly the moment it could not tell. That is the skip-trapdoor shape
[AGENTS.md](https://github.com/Systemorph/MeshWeaver/blob/main/AGENTS.md) forbids, expressed as a
default value rather than as an `if:`.

**Measured before shipping it, read-only, on the live cluster (2026-09-04).** A gate whose new
required input turns out to be unreadable in production goes red for a missing-input reason on its
first scheduled run, which is indistinguishable from finding drift. So the input was measured rather
than assumed: `helm version --short` inside `az aks command invoke` answers **v3.21.1+gc56dd00** —
the binary is there, on the transport that matters — and `helm get manifest` returns a manifest for
both production releases (`memex` in `memex`, 76 386 bytes; `memexcloud` in `memex-cloud`, 79 884
bytes). Every object the comparator hard-requires is in both: the `memex-portal-config` ConfigMap,
the `memex-portal-deployment` Deployment, and the `memex-portal` container inside it. None of the
three "the manifest parses but lacks an object" failure paths can fire on either namespace.

🚨 **Reading a release manifest by eye needs a parser, not `grep`.** `helm get manifest` emits
**quoted** scalars — `kind: "ConfigMap"`, `name: "memex-portal-config"` — so `grep '^kind: ConfigMap'`
returns nothing and reads as *"the object is absent"*. `yaml.safe_load_all` unquotes them, which is
why the comparator's `doc.get("kind") == "ConfigMap"` is correct and a quick shell check is not.

🚨 **The finding reports the VALUE's emptiness, never the value.** A pending deletion of a
zero-length value is a no-op; of a non-empty value it is a real removal. The report says which, and
for a ConfigMap key gives the length — reading only key names is what turns thirteen no-ops into a
false alarm.

The baseline observation, measured **by hand** on 2026-09-04 over all 36 `CLUSTER-ONLY` findings —
the last time anyone had to: **13 owned-but-retired** (`memex` 7, `memex-cloud` 6) and **23
never-owned** (`memex` 12, `memex-cloud` 11). Every one of the 13 was zero-length live, so all
thirteen deletions were no-ops — but that is a property of *those* keys on *that* day, not of the
sub-class, which is why the check reads the value on every run rather than recording the conclusion.
`Systemorph/Memex#152` asked for exactly this check in the opposite direction ("a key that is live
and rendered by neither chart nor overlay is about to be deleted, which is information no current
gate reports"); it is now information the nightly report carries, which is what turns a silent
deletion into a reviewed one.

The comparator's own regression test (`deploy/aks/scripts/test-chart-drift-compare.sh`, run by
`chart-gate.yml` on every pull request) drives all four shapes off committed fixtures — a non-empty
pending deletion, a zero-length one, a never-owned key that must NOT be reported as pending, and a
key that is live, helm-owned **and still rendered**, which must not be reported at all. That last
one is the control: without it the check could pass by flagging everything the manifest carries.

🚨 **And read the VALUE, not the key name, before calling such a deletion harmless.**
`FrameworkBroadcast__Subscribers__0..3` was reported here on 2026-09-03 as "the live keys feed
nothing", and separately as a subscriber list that a deploy would take "from 4 to 0". Both readings
assumed a value. Measured on 2026-09-04 by three methods — live ConfigMap `jsonpath`, live ConfigMap
YAML, and the deployed release manifest — **all four slots are `""` on BOTH namespaces**, and the
deployed release (`helm get values`) sets no value for any of them. The release wave has been
resolving to an empty subscriber set on both portals all along; the pending deletion removes four
empty strings and changes nothing. The hazard is real and already realised, which is a different
piece of work from the one "a deploy will break it" describes.

## The backlog, classified — run 33843264982, 2026-09-04

"91 divergences" is not a worklist. **81 today** (`memex` 35 across 188 compared fields,
`memex-cloud` 46 across 204), and they are seven groups, not eighty-one decisions. Counts are from
the run's own log; the owned/never-owned split and the value probes are read-only cluster reads.

| # | group | count | what it is | what clears it |
|---|---|---|---|---|
| 1 | **Disagreeing `SHADOWS`** | **8** | the pod runs a value the ConfigMap contradicts — `PreWarm__BatchBake`/`PrebuiltBundleRoot` and `PluginCatalog__RegistryUrl` (both namespaces), `PreWarm__GateReadiness` + `Features__Ai__Providers__AzureOpenAI` (memex) | chart value first, **then** delete the inline entry |
| 2 | **The registry credential** | **2** | `PluginCatalog__RegistryToken`: on memex a `SHADOWS` over `secret/memex-portal-secrets` whose two values differ **and belong to different registered instances**; on memex-cloud the inline entry is the ONLY copy, and neither portal has a Key Vault entry at all | MeshWeaver#3201 — the live inline key is the CORRECT one for each portal (hash-confirmed); vault *it*, then delete inline, then rotate. Never adopt the shadowed copy |
| 3 | **Agreeing `SHADOWS`** | **29** | not wrong today; the ConfigMap is simply not what the pod reads, so the next chart change to any of them silently fails — Kestrel endpoints, `PluginCatalog__Sources__*`, `WebhookInbox__Targets__0` | same two steps, no urgency |
| 4 | **Chart-retired, helm-owned** | **13** | `FrameworkBroadcast__Subscribers__0..3` (both) plus `Authentication__DevAdminUsers`/`__Google__ClientId` (both) and `__LinkedIn__ClientId` (memex). All 13 are in the release manifest and zero-length live | the next `helm upgrade` deletes them; verify each is still empty first |
| 5 | **Cluster-only, never owned** | **22** | live-edited settings the chart had no key for until MeshWeaver#3199 — AI providers, `LogWatch__*`, `Speech__*`, `Commerce__BaseUrl`, `Features__Ai__Clis__*`, `Portal__ReactAppUrl` | put them on the `Hosting/Deployment` record — `Systemorph/Memex#148` |
| 6 | **Committed, never deployed** | **5** | memex-cloud's overlay carries `PreWarm__{BatchBake,BuildProtocol,DynamicTypes,GateReadiness}: "true"` and `probes.startup.failureThreshold: 1080`; live runs the shipped defaults | a deploy, not a cleanup — this is `deploy-drift`'s class surfacing here |
| 7 | **Ruled inert** | **2** | the memex liveness/readiness `initialDelaySeconds` — see above; the chart is authoritative | nothing; do not "fix" it |

Zero `COLLIDES` and zero `CHART-ONLY` — as on 2026-09-03, which is the only other run since #3168
introduced those two classes, so "consecutive" is a two-run claim and nothing more. The `EMAIL__*`
collisions that crashed memex at boot on 2026-08-30, and the four blanked `ModelTier__*`, are gone
from both namespaces.

**76 → 81 in a day, and nothing drifted.** The five new findings are the `Authentication__*` keys in
group 4: the first-run-setup change made them conditional where they had been emitted unconditionally
with `| default ""`, so the render stopped carrying a key the cluster still holds. The live
ConfigMaps have not been written since 2026-08-30 / 2026-08-29 (`managedFields`; helm revisions 28
and 21). **A count that moves is not automatically drift** — check the chart's own history before
reading a rise as a regression.

**Group 6 carries the one coupled hazard on the list.** `PreWarm__GateReadiness` reaches the pod
through an inline `env:` that shadows the ConfigMap, so a `helm upgrade` of `memex-cloud` would raise
`probes.startup.failureThreshold` to 1080 — a pod-template field helm owns — while the gate it is
paired with stays off, because the inline entry still wins. `progressDeadlineSeconds` is derived as
`periodSeconds × failureThreshold + 600`, so a genuinely failed rollout would take **3 h 10 m** to be
declared failed instead of ~16 minutes. Deleting the inline entry and deploying are one change, not
two.

## What is NOT in these 81

The checker compares `memex-portal-config`, `memex-portal-deployment`, the PodDisruptionBudget and
the ScaledObject. Every other object in these namespaces is outside its scope, and two of them are
drifted right now: `portal-next-deployment` (both namespaces — `kubectl-client-side-apply` 2026-07-05
then `kubectl set` 2026-08-07, no helm annotations, 0/1 ready on an image tag ACR does not have;
`Systemorph/Memex#172`) and the `k8s-dashboard`/`memex-website`/`whisper-swiss-german` workloads. A
green Chart Drift would say nothing about any of them, which is worth knowing before this report is
read as "the namespace matches the chart".

See also [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) and
[Deployment.md](/Doc/Architecture/Deployment).
