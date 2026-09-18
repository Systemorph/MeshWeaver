---
Name: Rendering a chart you are not allowed to fully configure
Category: Architecture
Description: Chart Drift produced no verdict for 34 days because it may hold two of the deploy's three value sources and the chart correctly refuses that subset — the placeholder that unblocks the render, the two-render proof that keeps it honest, and what the first verdict found
Icon: BranchCompare
---

# Rendering a chart you are not allowed to fully configure

`Chart Drift` ran **39 times between 2026-08-15 and 2026-09-17 and failed 39 times**, never once
producing the verdict it exists for ([MeshWeaver#4640](https://github.com/Systemorph/MeshWeaver/issues/4640)).
Nothing in it was broken in the ordinary sense: it failed RED exactly as designed, named its cause
in the log every night, and nobody read it.

Two separate defects held it there, and they need different fixes. This page is about both,
because the second one is the reason the first survived for 34 days.

## The structural defect: two of three value sources

A record-driven deploy renders the chart from **three** sources:

| # | source | holds |
|---|---|---|
| 1 | `deploy/helm/values.yaml` | the chart's own defaults |
| 2 | `deployments/aks/<ns>/values.<release>.public.yaml` (private `Systemorph/Memex`) | the record's render — secret-free by construction |
| 3 | the Key Vault values half (`helm-values-<release>`, layered by `hosting-deploy --vault`) | **the connection strings and every other secret** |

A drift check that runs in this **public** repository may hold 1 and 2. It may never hold 3 — the
overlays were moved out of this repo precisely so the deployment's credentials are not published,
and `check-chart-drift.sh`'s own header says so.

From sources 1 + 2 alone the chart **refuses to render**:

```
Error: execution error at (memex/templates/memex-portal/secrets.yaml:101:33):
memex.orleansConnectionString: 'memex_portal' runs AdoNet clustering on an EXTERNAL database
(postgres.enabled is false) but names no database for cluster membership … Refusing to render.
```

**That refusal is correct and must stay.** It is [#3780](https://github.com/Systemorph/MeshWeaver/issues/3780)'s
guard: a `helm upgrade` fed the record's render *without* the vault half manufactured
`ConnectionStrings__orleans: Host=memex-postgres-service;…` from the chart's in-cluster default — a
Service the release does not run — and every new pod on the control instance died at silo start,
twice (revisions 44 and 55). `check-chart-invariants.sh` asserts on every pull request that the
chart still refuses that render, and nothing here changes that.

So the check was asking the chart a question the chart is right to refuse. The header's own
instruction — *"Render with the SAME `-f` list the deploy uses"* — **cannot be followed** by
anything running in this repository. It was not a rule that was being broken; it was a rule that
described an impossible act.

## The fix: supply the input, then prove it inert

`deploy/aks/scripts/chart-drift-render.py` supplies an obviously-fake placeholder for exactly that
input and then **demonstrates, every run, that nothing the comparison reads depends on it**.

### What is injected, and why only that

Only `secrets.<half>.ConnectionStrings__orleans`, and only when the chart's own precondition for
the #3780 refusal holds (external database, AdoNet clustering, neither connection string in
values). Its **host is not invented** — it is read from `config.<half>.MEMEX_HOST` /
`MEMEX_PORT`, the same committed, secret-free input the chart itself uses to decide what
`wait-for-postgres` waits for when the connection string arrives from outside the values
(`memex.meshProbeGroup` in `templates/_database.tpl`). Only the credential fields are placeholders.

Using the real host is not cosmetic. With it, `memex.dbProbeTargets` deduplicates the orleans
endpoint against the mesh one and renders **the same single probe target the deploy renders**; a
placeholder host would render two, so the render would differ from the deploy in shape rather than
only in a value nobody compares.

And when even the host is missing, the script **refuses** and names that input, rather than
inventing one. Manufacturing a database host is the #3780 defect itself; a checker that did it
would be committing the defect it guards.

### The proof, and why a search would not do

The check compares four objects: `ConfigMap/memex-portal-config`,
`Deployment/memex-portal-deployment`, the `PodDisruptionBudget` and the `ScaledObject`. A
placeholder is safe only if none of them is a function of it.

Searching the render for the placeholder string answers a **weaker** question. A value can reach an
object *derived* — hashed, base64'd, truncated — and the search comes back clean while the object
has still moved. So the script renders the chart **twice, with two different placeholders**, and
requires the four objects to be identical. Anything that is a function of the placeholder, by any
derivation, differs between the two renders and is reported by JSON path.

Measured 2026-09-18 against both production overlays, on helm v3.16.3 (the CI binary) and v4.2.4:
**exactly one path differs.**

```
Deployment/memex-portal-deployment.spec.template.metadata.annotations.checksum/secrets
```

That is a `sha256` of the rendered Secret whose only job is to roll pods when a secret changes. It
is a digest of values this check does not hold and never compares, so it can carry no drift
verdict — `chart-drift-compare.py` reads no pod-template annotation at all.

> **The exclusion list is default-deny, and that is the whole point.** The proof starts from *every
> byte of the four objects must match* and subtracts only what is named in
> `EXPECTED_PLACEHOLDER_DEPENDENCIES`, each with the reason it carries no verdict. A chart change
> that makes any other field a function of a secret reddens the check on the next run, naming the
> field. **Do not add an entry to silence a red**: a new dependency means the compared set now reads
> a secret, which is a finding about the check, not noise.

### The negative controls

`deploy/aks/scripts/test-chart-drift-render.sh` runs on every pull request through `chart-gate.yml`
— no cluster, no credentials, no network. Three of its four cases are negative:

| case | what it does | must |
|---|---|---|
| 1 (control) | the real chart, the record-driven shape, no vault half | render, and **state** the proof it made |
| 2 | copies the chart and templates the placeholder into a compared ConfigMap key | go **RED**, naming the leaking key |
| 3 | copies the chart so the placeholder decides whether a compared object exists at all | go **RED** on the object-set comparison |
| 4 | values with no `MEMEX_HOST` | **refuse**, and write no render |

Case 1 is the control for 2–4: without something that passes, a script that failed unconditionally
would satisfy every other assertion.

Case 3 exists because a field-by-field diff over the *intersection* would miss it, and it has its
own trap: the poisoned condition must match placeholder A **exactly**, or the object is absent from
both renders, the sets match, and the case passes having tested nothing.

Both negative controls were themselves verified by sabotage — the independence check and the
object-set check were each disabled in turn, and each time the corresponding case went red and no
other did.

## The second defect, and the one that actually cost 34 days

[#1709](https://github.com/Systemorph/MeshWeaver/issues/1709) tracked the *first* cause — a stored
PAT that authenticated as nothing — and was closed when that was fixed. The next cause took over
silently, and **with the ticket closed nothing pointed at it**.

A red on the Actions tab is an **event**. "The cluster does not match the chart" and "drift
detection is not running" are **standing states**, and a standing state needs an artefact that
survives somebody closing a ticket. `chart-drift.yml` now carries a `report` job on the same
pattern `Systemorph/Memex`'s `deploy-drift.yml` already uses (whose own header points at this
workflow for the cluster-vs-chart half): one tracking issue, found by a marker in its body, updated
in place, closed automatically when every environment matches, reopened on the next divergence.

It is a **reporter, not a gate** — it publishes the verdict the `drift` job produced and can never
turn a red run green. Three states, and the third is the one this incident was in:

- **green** — every environment matches; the issue is closed.
- **drift** — a completed comparison found divergences.
- **unknown** — the detector did not run. *This must be as loud as drift*, because an unrun detector
  reporting nothing is the original bug.

## What the first verdict found

Run 2026-09-18 with this workflow's exact command line against both namespaces:

| namespace | verdict |
|---|---|
| `memex` | 19 divergences across 209 compared fields — 15 `SHADOWS`, 1 `CHART-ONLY`, 3 `DIFFERS` |
| `memex-cloud` | 29 divergences across 233 compared fields — 29 `SHADOWS` |

Most are the known `SHADOWS` backlog triaged in [ChartDriftSemantics](../ChartDriftSemantics) — an
inline `env:` entry duplicating a ConfigMap key the chart also renders, agreeing today and dead
tomorrow. But the first verdict immediately surfaced something no committed source shows, on
**both** production namespaces at once:

> **The NodeType bake readiness gate — the one instrument that stops a bad roll — is not in force
> on either production namespace, for two different reasons.**

- **`memex-cloud`**: the ConfigMap carries `PreWarm__GateReadiness=true` and the pod carries an
  **inline `env` of `false`**. An inline env overrides `envFrom`, so the gate is **off on the pod**
  while the chart, the values files and the ConfigMap all say it is on. This is the
  [#2235](https://github.com/Systemorph/MeshWeaver/issues/2235) shape exactly: every signal green,
  the pod running something else.
- **`memex`**: the gate is armed on both sides, and the live `startupProbe` was hand-moved to
  `/ready` while the chart and the record say `/health` (`DIFFERS startupProbe`: chart
  `/health` timeout 30, live `/ready` timeout 5). `/ready` does not run the `nodetype_bake` check,
  so **the gate is armed and has no reader** — the chart's own comment beside the probe path warns
  that moving it costs exactly this.

Both are recorded on #4640. Neither is visible to `deploy-drift.yml` (which compares git against
the deploy record, not the cluster), to the fleet watch (`IssueKinds.ImageDrift` is explicitly
excluded from `EvaluatedKinds`), or to any `dotnet build`.

## Residual gaps, named rather than closed

Three things this change deliberately does **not** do. Each is a scope call, not an oversight.

1. **It does not prove the *withheld* Key Vault half has no influence on the compared objects.**
   The two-render proof bounds the *placeholder*; whether source 3 carries structure (`config`,
   `ingress`, `resources`, …) rather than secrets alone is a property of how the fleet splits its
   values, tracked as `Systemorph/Memex#295` and its PR `#352`. If the vault half does carry
   `config` leaves, those render as `CLUSTER-ONLY` findings — correctly classified but wrongly
   *caused*. The discriminator is cheap: a `CLUSTER-ONLY ConfigMap <key>` whose live value matches
   what the record renders is this, not hand-applied drift.
2. **The container image is in nobody's compared set.** `check-chart-drift.sh` compares the
   ConfigMap, the pod template's env / probes / lifecycle and the availability shape — `grep -n
   image` over it finds only prose. `deploy-drift.yml` compares git against the record, and the
   fleet watch excludes `ImageDrift`. So "the cluster runs a tag nobody committed" is detected by
   nothing. Whether it belongs here is #4640's open item 3.
3. **A completed run publishes its ConfigMap findings to a public Actions log.** The comparator
   never prints an inline-env or Secret value; it does print both sides of a ConfigMap finding, and
   a ConfigMap is non-secret by definition. Measured on the first real run the entire disclosure was
   one value, `'Job'` — but it is a live property now that runs complete, and it is a wider surface
   than the workflow's own *"that would publish the deployment inventory"* warning implies. The
   tracking issue carries **counts only** for this reason. Whether ConfigMap values should be
   withheld the way inline-env values already are is an open call on #4640.

## The generalisable rule

The instruction that failed here was *"render with the same inputs the deploy uses"*. It reads like
a rule and is actually an impossibility for any checker that is deliberately given less access than
the thing it checks — which is most of them.

The rule that replaces it is narrower, and the important half is the second clause:

> **Render from the sources you may hold, and prove the compared set does not depend on the ones you
> may not.** A source the comparison does not read cannot change the comparison — but that is a
> claim about *today's* chart, so re-establish it on every run instead of writing it down once.

Related: [Chart Drift — what a deploy actually does](../ChartDriftSemantics) ·
[Probe Semantics](../ProbeSemantics) ·
[A Probe Must Answer Inside Its Own Timeout](../AProbeMustAnswerInsideItsOwnTimeout) ·
[Verifying Chart Values](../VerifyingChartValues)
