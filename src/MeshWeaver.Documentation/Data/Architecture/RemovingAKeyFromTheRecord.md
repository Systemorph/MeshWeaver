---
Name: Removing a key from a Deployment record
Category: Architecture
Description: How hosting-deploy tells a key the record deliberately stopped rendering from a hand-applied live-only value — the record-owned manifest written into every release, and the HOSTING_RETIRE_VALUES bootstrap
Icon: Delete
---

# Removing a key from a Deployment record

`hosting-deploy` refuses an upgrade that would **drop** a value the live release carries but the
upgrade no longer supplies. helm replaces a release's user-supplied values, so such a key silently
falls back to the chart default — measured on Memex#376, where memex-cloud's `features.fleetops`
lived only in a hand-applied overlay and a record-driven render would have re-enabled fleet ops on
it with nothing in any log. That guard is right for **hand-applied, live-only** values.

It was wrong for the other kind of drop: a key the **record itself** used to render and no longer
does. Removing a key from a Deployment record is a reviewed, deliberate change, yet the guard could
not tell it from a hand-applied value, and the remedy it named — a governed `HelmRelease` removal —
exists for two instances only. So removing a key from a record bricked every later deploy of that
instance. Measured 2026-10-09/10: build's rolls (`selfupdate-roll-build-3-1-10352-88e8bc9b`) refused
on the four keys Memex#713 removed from its record, and memex-cloud earlier on
`WebhookInbox__Targets__1`.

## The record-owned manifest

The two kinds are told apart by a **declaration**, never a guess (policy `declared-not-derived`).
Every deploy writes the leaf paths the RECORD renders into the release itself:

```yaml
hostingDeploy:
  recordOwned: ["extraPortalConfig.A", "portal.image", …]
```

The list is the leaf paths of the record's render plus `hosting-deploy`'s own `--set` flags, merged
by helm through a one-template probe chart. It deliberately **excludes the Key Vault values half**:
that half was captured from whatever was live, so its leaves prove nothing about the record. The
chart reads nothing under `hostingDeploy`; it is bookkeeping, rewritten on every deploy and never
compared as a value.

The next deploy reads it back with `helm get values` and classifies each path it would drop:

| Class | Condition | Outcome |
|---|---|---|
| record-owned | the previous deploy's manifest lists exactly this leaf path (never a parent: a manifest leaf `a.b` says nothing about a hand-applied `a.b.c` that replaced it) | **dropped**, logged `values    DROPPING <path> — the record rendered it on the previous deploy …` and reported as `::hosting:: dropped_value=owned:<path>` |
| retired | the record declares `HOSTING_RETIRE_VALUES` naming the path or a parent of it | **dropped**, logged and reported as `retired:<path>` |
| live-only | neither | **refused** before helm, naming every path (Memex#376, unchanged) |

## A release with no manifest

A release last deployed before this change — or last applied by the config repository's
`helm-release` lane, which replaces the values and so drops the manifest — carries no manifest.
Then nothing can tell a removed record key from a hand-applied one, and the deploy falls back to
the old behaviour: every drop is refused. The refusal says so and names the bootstrap.

**The bootstrap is a declaration on the record:** add to the record's `operator.environment`

```
HOSTING_RETIRE_VALUES=extraPortalConfig.Hosting__Builds__Concurrency,extraPortalConfig.Hosting__Builds__PublicUrl
```

(comma-separated dotted values paths; each must match `[A-Za-z0-9_][A-Za-z0-9_./-]*` — `/` for keys
such as ingress annotations — anything else is refused). The next deploy drops those paths, logs each one, and writes the manifest — from then
on a removal from the record needs no declaration. An entry that matches nothing the upgrade drops
is named in the log as spent, so the declaration can leave the record once the release no longer
carries the value.

The path to name is the one the refusal printed (`would DROP n value(s) … : <path> <path>`), so the
refusal itself is the input to the declaration.

### Why not the history on the control instance

Inferring the bootstrap from the control instance's history — the record's earlier versions, or the
`renderedValues` of the last successful action for the deployment — was considered and measured
against the first two cases, and it does not cover them. On 2026-10-10 the pearl Provision
(`Ops/Actions/provision-pearl-20261010-values-half`) refused on
`config.memex_portal.OpenRouter__Models__0..3`. The control instance holds pearl's record from
version 1 (2026-10-08), and version 1 already renders the EU route instead of those keys; the only
`Done` action for pearl there is a secret-status read with no render. The keys were rendered by a
record version that predates the control instance's copy, so no control-side history names them,
while a declaration on the record covers that case and every other one. History that exists for
one instance and not another is a guess about provenance; the declaration is not.

## What is unchanged

- A value applied by hand and never rendered by the record is still refused — the negative control
  in `deploy/aks/operator/test/run-tests.sh` pins it with a manifest present.
- A first install reads no live values and is never asked.
- The operator logs paths only, never a value.

## Related

- [Deployment env layers](../DeploymentEnvLayers) — the layers a record must be able to hold, and
  the Key Vault values half.
- [Chart Drift — what a deploy actually does](../ChartDriftSemantics) — what a `helm upgrade` does
  and does not remove.
- [Declared, Not Derived](../DeclaredNotDerived) — the policy this follows.
