---
name: new-deployment
description: "Bring up a NEW MeshWeaver instance the ONLY sanctioned way: a Hosting/Deployment record in the private Systemorph/Memex repo, provisioned by the control instance's Hosting plugin (a Provision InstanceAction on memex.systemorph.com) — never kubectl, never deploy.sh, never the ACR image: images come from the fleet's own registry cr.meshweaver.cloud. Use when standing up a customer/internal portal, when a Provision refuses or fails, or when auditing whether an instance is wired the way its record says. Covers the human-done prerequisites (Entra app, registry instance key, db-connection secret, a tag present in cr), what the operator's Provision plan does in what order, the refusals that are ANSWERS not errors, and the traps that read as success."
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Edit
  - Grep
---

# /new-deployment — a new instance is a record, provisioned by memex

A deployment is **one Hosting/Deployment record** — `mesh/Deployments/<id>.json` in the PRIVATE
`Systemorph/Memex` repo — that the **control instance** (`memex.systemorph.com`, record `memex`,
`operator.enabled: true`) turns into a namespace, a database, DNS, a Helm release and a certificate
when a global admin files a **Provision** action against it. Everything else — cluster, ingress,
Postgres server, Key Vault, the registry, observability — already exists and is shared.

> 🔒 **The inventory is private.** Hosts, namespaces and database names live in `Systemorph/Memex`
> (`mesh/Deployments/*.json`, `docs/inventory.md`, the Deployments tab). The public MeshWeaver repo
> carries the *mechanism* (chart, operator scripts, the Hosting plugin's sources) and must never
> carry *who runs what*.

🚨 **The maintained runbook is `docs/new-deployment.md` in `Systemorph/Memex`.** It is authoritative
wherever it and this page disagree; this page is the map, not a copy of the commands.

## Where the work happens — and where it does not

| | who | how |
|---|---|---|
| the record | a human, by PR | `mesh/Deployments/<id>.json` (GitSynced to both portals; a mesh-side edit is written back as a PR by `DeploymentGitSync`) |
| secrets an instance cannot mint for itself | a human, once | Key Vault `Systemorph`, names `<keyVaultSecretPrefix><Section>-<Key>` |
| namespace, database, identity, DNS, pull secret, release, TLS | **the operator**, from the Provision plan | a `Hosting/InstanceAction` node on the control instance |
| the cluster | nobody by hand | `kubectl` is not part of this procedure; a step that needs it is a defect in the lane, file it |

## 1. What a human does first

Every one of these is a **prerequisite the Provision REFUSES without**, so do them before filing it.
`<prefix>` is the record's `keyVaultSecretPrefix`.

1. **The sign-in app** (multi-tenant; invitation-only is the gate, not the audience):
   `az ad app create --display-name "<Name> Portal (<host>)" --sign-in-audience AzureADMultipleOrgs --web-redirect-uris https://<host>/signin-microsoft`,
   then `az ad sp create --id <appId>`, then the client secret into the vault as
   `<prefix>Authentication-Microsoft-ClientSecret`. The appId goes on the record as
   `signIn.microsoftClientId`. 🚨 `az … credential reset` PRINTS the secret on stderr:
   `--query password -o tsv > "$S/x" 2>/dev/null`, then `az keyvault secret set --file "$S/x"`,
   then `rm`. Never `cat` a stderr capture from a minting verb.
2. **The registry instance key** — ONE key, two jobs (plugin catalog AND image pull):
   `POST https://memex.meshweaver.cloud/api/instances/register` with
   `{"bootstrapKey":"","instanceId":"<id>","displayName":"…","homeUrl":"https://<host>"}` (open
   registration = the `free` plan; an admin raises it under Admin ▸ Instance grants). Pipe
   `.instanceKey` straight into the vault as `<prefix>PluginCatalog-RegistryToken`.
   `hosting-kv-ensure` lists this object as REQUIRED and refuses the whole Provision without it —
   "minting a random value here would produce an instance that boots, registers nothing, and shows
   an empty Store with no error".
3. **The database connection string** — `<prefix>db-connection`
   (`InstanceSpec.DatabaseSecretName`), composed from the shared admin password in vault object
   `memex-postgres-password`: `Host=memexaks-pg.postgres.database.azure.com;Port=5432;Username=memexadmin;Password=…;Database=<db>;SslMode=Require;Trust Server Certificate=true`.
   **FQDN, never an IP** — Azure moves the backing instance and DNS follows it. Mapped on the
   record's `keyVaultSecrets.secrets` as `ConnectionStrings__memex`.
4. **An image tag that EXISTS in `cr.meshweaver.cloud`.** `imageRepository:
   cr.meshweaver.cloud/memex-portal-ai`, `pinnedImageTag: <tag>` — a first install with no pin is
   REFUSED by `hosting-deploy` ("Pin an image tag on the Deployment record for the first install").
   If the tag is not in cr yet (CD pushes to ACR), copy it there as the registry's `publisher` account:
   `crane copy meshweaver.azurecr.io/memex-portal-ai:<tag> cr.meshweaver.cloud/memex-portal-ai:<tag>`
   and the same for `memex-migration` (the migration image is DERIVED by replacing
   `memex-portal-ai` → `memex-migration`; both must be present).
5. **The record itself**, by PR, with (a sibling record is the worked example): `imagePullSecret:
   registry-pull`; `volumes[]` for `data`/`content`/`attachments`/`users` with `claimName`,
   `mountPath`, `size`, `storageClass: azurefile-memex`, `accessMode: ReadWriteMany` and
   **`create: true`** — the chart creates the claims; without `create` a new instance gets
   emptyDir and every restart wipes it; `keyVaultSecrets` naming the
   three vault objects above plus `<prefix>Ai-KeyProtection-MasterKey` (GENERATED by
   `hosting-kv-ensure`, never regenerated — it seals every stored `enc:` provider key);
   `pluginRepos[0] = { name: "Plugins", url: "https://memex.meshweaver.cloud" }` (the name is the
   REGISTRY's name, never the instance's — a bare `preInstall` id is qualified against it and the
   catalog FAILS CLOSED on a mismatch); `preInstall: ["Essentials"]`; `updatePolicy: Stable` for a
   customer; `extraPortalConfig` with `Hosting__Deployment: <id>` and `Hosting__ReportTo:
   https://memex.systemorph.com`.
   Gates that read it: `check-record-renders-overlay.py` (record ↔ `values.<release>.public.yaml`,
   `extraPortalConfig` one way, `keyVaultSecrets` both ways), `check-image-pins.py` (resolves every
   pin — `cr.meshweaver.cloud` pins need `MW_REGISTRY_KEY`), `config-key-coverage`.

## 2. What the operator does — the Provision plan, in order

Composed by `InstanceActionPlan.ProvisionSteps` (MeshWeaver.Plugins, in-mesh — it compiles in the
portal, no CI type-checks it), run by `deploy/aks/operator/bin/run.sh` in a Job in `memex-ops`,
fail-fast, the failing STEP named on the node:

1. `az postgres flexible-server db create` — control plane, workload identity. (Memex#132 —
   `PGPASSWORD` has no secret channel — blocks Backup/Suspend/Teardown/Restore, **not** this.)
2. `hosting-kv-ensure` — generates the master key, REQUIRES the registry token.
3. `hosting-federate` — federated credential, subject **exactly**
   `system:serviceaccount:<ns>:memex-portal-sa`; a mismatch does not error, it silently stops
   self-update tag discovery.
4. `hosting-dns upsert` — the A record at the shared ingress IP. DNS BEFORE TLS: HTTP-01 needs it.
5. `hosting-pull-secret` — ensures the namespace, turns the vault's registry token into the
   `kubernetes.io/dockerconfigjson` Secret named by `imagePullSecret`, reads it back, never prints
   the key.
6. `hosting-deploy --image <imageRepository>:<pinnedImageTag>` with values **rendered from the
   record** (`HelmValues`) — the chart is BAKED INTO THE OPERATOR IMAGE, so the control record's
   `operator.image` (`meshweaver.azurecr.io/hosting-operator:<short sha>`, published on every push
   to MeshWeaver main) decides which chart deploys. A stale operator image deploys a stale chart.
7. rollout wait → 8. `hosting-tls` (waits for the Secret to carry a key pair) → 9.
   `hosting-verify` → 10. `hosting-verify-catalog` (the expected mounts and packages, so an empty
   Store cannot read as healthy).

## 3. Filing it

On the control instance (MCP server for memex.systemorph.com), create under `Deployments/`:

```json
{ "id": "<id>-provision", "namespace": "Deployments", "nodeType": "Hosting/InstanceAction",
  "name": "Provision <id>", "content": { "$type": "InstanceActionContent",
  "deployment": "Deployments/<id>", "requestedAction": "Provision",
  "confirmation": "<id>", "dryRun": true, "reason": "…" } }
```

**Dry run first** — Authorize → Read record → Plan → "Dry run — nothing changed" prints the composed
step list on the node's `log`; a refusal here (`state: Refused`, `error` naming the field) is the
ANSWER, not an error to retry. Then the real one (`dryRun: false`). Read `state`/`phase`/`message`
and the `log` on the node; a failed step names itself and the plan is idempotent from the top, so
fix the cause and file again.

## 4. After it answers

- **A cold instance recompiles every dynamic NodeType.** The startup budget on the record
  (`startupProbe.budgetSeconds`, 3 h on the siblings) exists for this; a pod `0/1` during the bake
  is the gate doing its job. Do not cycle pods while it warms.
- Register the GitHub Environment and a deployments entry on `Systemorph/Memex`, add the
  `docs/inventory.md` row, set `Admin/UpdatePolicy` (**Stable** for a customer). A deployment nobody
  recorded is a deployment nobody will remember to patch.

## 🚨 Traps that read as success

- **HTTP 200 proves nothing.** The Blazor shell answers 200 for an error page or a paywall redirect;
  `hosting-verify` checks rendered content — so should you.
- **Nobody can sign in** is a perfectly healthy instance: every `Authentication__*__ClientId`
  empty and dev login off means it boots, passes probes, serves, and has nothing to authenticate
  against. Step 1 is not optional.
- **The chart ConfigMap emits only the keys it templates**; a record `extraPortalConfig` key the
  chart has no line for reaches NO container, with no error. `config-key-coverage` is the guard.
- **`kubectl set env` is not a deployment.** Helm's three-way merge never removes an inline entry it
  did not own; the record's `inlineEnv` exists to make such drift visible, not to bless it.
- **Never emit `""` for an int/bool key** — it fails binding, DI throws, the landing page dies.
- **A refused Provision with `hosting-kv-ensure` naming a missing vault object** wants the object
  ISSUED (step 1.2), never a random value.
- **`az` prints minted secrets on stderr** — see step 1.1; a leaked value is rotated in the same
  turn.

## Related

- `/plugins` — the registry the instance consumes packages from
- `/release` — how images get built; a tag missing from cr is copied there by hand (step 1.4)
- `/deployment` · `/delete-user` · `/storm` · `/debug` — operating a live instance
- Design: [ContainerRegistryInMemex.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ContainerRegistryInMemex.md)
  (the registry as a separate service) · Memex#132 (the operator's PGPASSWORD gap)
