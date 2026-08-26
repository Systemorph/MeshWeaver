---
Name: Repository Topology
Category: Architecture
Description: Three repositories, one rule each — MeshWeaver (public) is the framework and the Helm chart, MeshWeaver.Plugins (private) is all common code and the plugin catalog, and Memex (private) is config only. Access to the private code catalog is mediated by the registry instance, never by handing out GitHub credentials.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7V5a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v2"/><path d="M3 7h18v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M8 13h8"/></svg>
---

# Repository Topology

Three repositories carry a MeshWeaver deployment, and each has exactly one job. Getting a change into
the right one is not tidiness — it decides whether the change is public, whether it ships on merge or
on a rebuild, and who needs a credential to consume it.

| Repository | Visibility | Owns | Never holds |
|---|---|---|---|
| **`Systemorph/MeshWeaver`** | **public** | The framework, and the **Helm chart** (`deploy/helm`). | — |
| **`Systemorph/MeshWeaver.Plugins`** | **private** | **All common, reusable code** — the plugin catalog *and* shared scripts (CI gates, generators). | Deployment config. |
| **`Systemorph/Memex`** | **private** | **Config only** — the deployment overlays and thin CI glue. | Reusable code or logic. |

## The framework repo is public, and the chart is read at deploy time

`MeshWeaver` is public. One consequence is easy to miss and load-bearing: the deploy
(`helm-release.yml`, which lives in the `Memex` repo) checks the chart out of `MeshWeaver` with **no
credential and no pinned `ref:`** — it takes the default branch **at deploy time**. The ConfigMap and
every rendered resource come from **helm reading the repo**, never from the running portal image.

So **a chart-only change is effective the moment it merges.** There is no image rebuild, no roll, no
release cycle standing between a merged chart fix and the next `helm upgrade`. This was proven the
hard way: a chart template that named every config key explicitly had simply omitted
`OpenRouter__Models__*`, so twelve declared models reached no container — and the fix (rendering
those keys) was live for the next deploy as soon as it merged, with no image involved. The corollary
is the warning: because the chart is read live, a *bad* chart change is also live on merge — the
chart is not gated behind an image the way application code is.

## Code goes in Plugins; config goes in Memex

> **Common and necessary code lives in `MeshWeaver.Plugins`. `Memex` is config only.**

The test for which repo a file belongs in is a single question: **would this same file be correct,
unchanged, for a second deployment?**

- **Yes → it is code, and it goes in `MeshWeaver.Plugins`.** A CI gate that checks any deployment's
  overlays, a script that generates manifests, a plugin's node types — none of these are specific to
  one environment. They are the shared catalog, and duplicating them into a config repo is how two
  copies drift and the next reader follows whichever they happened to open.
- **No → it is config, and it goes in `Memex`.** The per-environment overlays
  (`deployments/aks/{env}/values.{env}.public.yaml` for `memex` and `memex-cloud`, plus the
  layered `gate`/`ha`/`replica` values and the Key-Vault "vault half" of secrets merged at deploy)
  are the *only* thing that differs between one instance and the next.

A workflow file sits on the line, and the line is drawn by content, not by file type: **a workflow
that merely invokes external code is acceptable glue in `Memex`; a workflow (or script) that carries
logic is not** — that logic belongs in `MeshWeaver.Plugins`, with the config repo calling it.

## Access to the private catalog is mediated by the registry

`MeshWeaver.Plugins` is **private**. The rule that follows is absolute:

> **Access to private catalog content is granted *via* the registry instance
> (`memex.meshweaver.cloud`) — never by handing a consumer direct GitHub credentials to the private
> repo.**

This is not a new mechanism; it is the [Plugin Registry](/Doc/Architecture/PluginRegistry) model
applied as a general principle rather than only to plugin installs. **One** instance —
`memex.meshweaver.cloud` — holds the source GitHub credential, reads the private
[`MeshWeaver.Plugins`](/Doc/Architecture/Plugins) repo, and **re-serves** its contents over an
authenticated HTTP surface. Every other installation consumes through the registry, presenting an
instance key, and never touches git. The credential is **encapsulated in the registry**, exactly like
npm or NuGet: the registry has source access, and clients just speak HTTP.

So onboarding a new consumer is *"point it at the registry,"* not *"provision it a GitHub App for a
private repo."* A GitHub credential scattered onto each consumer is the anti-pattern this topology
exists to prevent — it multiplies the blast radius of a leaked token by the number of installations,
and it is exactly what the registry's credential encapsulation removes.

## A known gap — CI-time access to private scripts

One consumer does not yet fit the registry model cleanly: **CI**. A gate script is *code*, so it
belongs in `MeshWeaver.Plugins` — but a check that compares an environment's overlays against the
chart has to run where the overlays live, in `Memex` CI, and it needs the script. The registry serves
**plugin nodes**, not arbitrary CI scripts, so there is no registry-mediated way to hand a CI runner a
script from the private repo today.

The honest state: this is a gap, not a solved case. The clean long-term answer is
registry-mediated — the same credential encapsulation, extended to reusable build tooling. Until then
a scoped, auditable read is the pragmatic interim, and it should be called out as debt wherever it is
used rather than quietly normalised into "every CI runner gets a token to the private repo," which is
the pattern this page forbids everywhere else.

## Related docs

- @../PluginRegistry — the credential-encapsulation mechanism in full: how the registry re-serves the private catalog over a token-gated HTTP surface so consumers need no GitHub access.
- @../Plugins — the node-native plugin model: a plugin is a repo of mesh nodes, compiled live, with no package format.
- @../DeployingPluginChanges — how a merged change in the plugins repo reaches a running mesh.
- @../CiContentBake — how the plugin repos' CI bakes and publishes content, and why the bake follows the release.
