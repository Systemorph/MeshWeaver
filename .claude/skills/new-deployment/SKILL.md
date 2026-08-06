---
name: new-deployment
description: Ramp up a NEW MeshWeaver portal deployment (its own domain, database, sign-in, plugins) on the shared AKS cluster, and register it so it is tracked. Use when standing up a new customer/internal portal, adding a namespace to the cluster, or auditing whether an existing deployment is wired correctly (self-update, plugins, TLS, update policy). Covers the order that matters (federated identity BEFORE deploy, DNS BEFORE TLS), the silent-failure traps (federated-credential subject mismatch, un-templated chart keys, non-idempotent portal-patch, CSI envFrom order), and where the deployment inventory lives — the PRIVATE Systemorph/Memex repo, never the public MeshWeaver repo.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Edit
  - Grep
---

# /new-deployment — stand up a new portal deployment

A deployment is **one namespace** on the shared AKS cluster with its own domain, database and
sign-in. Everything else — cluster, ingress, Postgres server, Key Vault, ACR, observability — already
exists and is shared.

> 🔒 **The inventory is private.** Customer domains, namespaces and database names go in
> [`Systemorph/Memex`](https://github.com/Systemorph/Memex) → `docs/deployments.md` and its
> **Deployments** tab. The public MeshWeaver repo carries the *mechanism* (chart, scripts, generic
> docs) and must never carry *who runs what*. Deployment records were deleted from the public repo on
> 2026-08-06 for this reason — don't recreate them there.

## Ground truth first

Never write a runbook step from memory — read the live cluster:

```bash
# What exists, and what each namespace actually runs
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
  "for ns in memex memex-cloud atioz; do echo -n \"\$ns|\"; \
   kubectl -n \$ns get deploy memex-portal-deployment -o jsonpath='{.spec.template.spec.containers[0].image}'; \
   echo -n '|'; kubectl -n \$ns get ingress -o jsonpath='{range .items[*]}{range .spec.rules[*]}{.host} {end}{end}'; echo; done" \
  --query logs -o tsv
```

The cluster is **private** — every `kubectl` goes through `az aks command invoke`. There is no direct
kubeconfig path without the P2S VPN.

## The order that matters

Two orderings are not negotiable, and both fail *silently* when you get them wrong:

1. **Federated workload identity BEFORE the first deploy.** Subject must be exactly
   `system:serviceaccount:<env>:memex-portal-sa`. A mismatch does **not** error — the in-pod
   Deployment patch still works, only ACR *tag discovery* is blocked, so the deployment simply never
   self-updates and looks fine.
2. **DNS BEFORE TLS.** cert-manager uses an HTTP-01 challenge; without a publicly resolving A-record
   it fails and retries with backoff that gets slow enough to look like a hang.

## The steps

Full runbook with commands: **`docs/new-deployment.md` in `Systemorph/Memex`** (private). Generic
mechanism: [OnboardingNewEnvironment.md](../../../src/MeshWeaver.Documentation/Data/Architecture/OnboardingNewEnvironment.md)
and [DEPLOY-RUNBOOK.md](../../../deploy/aks/DEPLOY-RUNBOOK.md).

| # | Step | Gets it wrong how |
|---|---|---|
| 1 | Namespace → `portalNamespaces` in `deploy/aks/infra/main.bicep`, re-run infra deploy | subject mismatch → never self-updates |
| 2 | `az postgres flexible-server db create -d <env>` on `memexaks-pg` | migrating? **reuse the source master key** or every `enc:` provider key is undecryptable |
| 3 | DNS A-record → the **shared** ingress IP | pointing at a per-env IP that doesn't exist |
| 4 | Entra app (multi-tenant) + Key Vault secrets `<env>-*` | invitation-only is the gate, not the audience |
| 5 | `deploy/aks/envs/<env>/` scaffold + `deploy.sh` | see traps below |
| 6 | **Plugins** → `/plugins` skill | new deployment starts with none |
| 7 | `tls.sh` | needs step 3 resolving publicly |
| 8 | Register the GitHub Environment on `Systemorph/Memex` | otherwise it's untracked |
| 9 | `Admin/UpdatePolicy`: Continuous internal, **Stable** for a customer | |

`values.<env>.yaml` and `secretproviderclass.yaml` are **git-ignored** — they carry hosts, database
names, Entra ids and KV references. Managed out-of-band on disk. That is deliberate.

## 🚨 Silent-failure traps

Every one of these presents as "it deployed fine" while something is broken.

- **The chart configMap only emits keys it templates.** `deploy/helm/templates/memex-portal/config.yaml`
  has a fixed key list; anything else in your overlay is **dropped without a warning**. Symptom: the
  Microsoft button never renders, or the plugin catalog reads "not configured", with the values file
  looking correct. **If you add a config key, add it to the template.** Verify what actually landed:
  ```bash
  helm template t deploy/helm -f <your-values>.yaml | grep -A0 '<YourKey>' || echo "DROPPED"
  ```
- **`kubectl set env` is not a deployment.** Config applied by hand lives only on the live
  Deployment; the next `helm upgrade` reverts it and nothing in git says it existed. If you find
  yourself patching env vars to make something work, the fix is a chart key, not a patch.
- **`portal-patch.json` is not idempotent and replaces volumes BY INDEX.** Re-applying can fail on a
  duplicate volume add, and because the patch is atomic that rejects the *whole* patch — silently
  dropping the CSI `envFrom`. Adding or reordering a chart volume misaligns every environment.
- **The Key Vault (CSI) secret must be LAST in `envFrom`.** The chart's `memex-portal-secrets` has an
  *empty* client secret; the CSI-synced one has the real value; later `envFrom` wins. Symptom:
  `AADSTS7000218`.
- **Never emit `""` for an int/bool key.** `Anthropic__Order: ""` fails `Int32` binding → DI throws →
  the post-onboarding landing page dies.
- **HTTP 200 proves nothing.** The Blazor shell returns 200 for an error page or a paywall redirect.
  Verify rendered content, in a loop.
- **KEDA overrides `kubectl scale`.** `minReplicaCount: 2` silently restores replicas, so the
  documented scale-to-zero heal never runs. `kubectl get scaledobject -n <env>` first.

## Verifying a fresh deployment

A cold deployment recompiles **every** dynamic NodeType. Expect a window of *"No response received …
`SubscribeRequest`"*. That is cold-compile, not a bad image.

- **Do not cycle pods while it warms** — that restarts the work from cold.
- A pod sitting `0/1` during a bake is the readiness gate doing its job.
- A *steady* ~50% error ratio across many minutes is one failing replica, not warm-up. Warm-up
  converges toward 100%.

```bash
NEW=$(kubectl -n <env> get pods -l app.kubernetes.io/component=memex-portal \
  --sort-by=.metadata.creationTimestamp -o jsonpath='{.items[-1:].metadata.name}')
kubectl -n <env> logs "$NEW" | grep "DynamicTypePreWarmer: warm-up complete"
```

## Register it

```bash
gh api -X PUT repos/Systemorph/Memex/environments/<env>
jq -n '{ref:"main", environment:"<env>", task:"deploy", auto_merge:false, required_contexts:[],
        production_environment:true, description:"<purpose>",
        payload:{namespace:"<env>", cluster:"memexaks-cluster", database:"<env>",
                 image:"meshweaver.azurecr.io/memex-portal-ai:<tag>"}}' \
  | gh api -X POST repos/Systemorph/Memex/deployments --input -
# then a status carrying the live URL
```

`required_contexts` must be a real JSON array — `-F required_contexts='[]'` is rejected with a 422,
which is why the body goes through `jq` and `--input -`.

Then add the row to `docs/deployments.md` in that repo. A deployment nobody recorded is a deployment
nobody will remember to patch.

## Related

- `/plugins` — wire the deployment to the plugin registry
- `/release` — how images get built and rolled
- `/delete-user` · `/storm` · `/debug` — operating a live deployment
