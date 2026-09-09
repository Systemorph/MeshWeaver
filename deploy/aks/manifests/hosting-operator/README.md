# hosting-operator — the identity an instance lifecycle run uses

Applied on the **control instance's** cluster only. Three principals, deliberately separate:

| Principal | Can | Cannot |
|---|---|---|
| `memex-portal-sa` (the portal, unchanged) | `get`/`patch` its own Deployment — what the self-updater needs | anything in this directory |
| `hosting-jobrunner` (mounted into the portal as a token) | create/get/delete Jobs and read pod logs **in `memex-ops` only** | touch a namespace, a database, or any cloud resource |
| `hosting-operator` (the Job's own SA) | namespaces, Helm releases, ingresses cluster-wide, and — via Workload Identity — the Azure control plane | exist outside the seconds a run takes |

The portal never holds the powerful credential. It holds a token whose entire power is *"start an
operator job and read what it said"*, which is what makes a prompt injection into an in-pod AI CLI
a nuisance rather than a cloud compromise.

## Apply

```bash
kubectl apply -f namespace.yaml
kubectl apply -f operator-serviceaccount.yaml      # edit AZURE_CLIENT_ID annotation first
kubectl apply -f operator-rbac.yaml                # FIRST TIME ONLY — from then on the config repo's
                                                   # helm-release lane (adopt/deploy) re-applies it from
                                                   # this repo at the chart pin; see the note below
kubectl apply -f jobrunner.yaml                    # 🚨 edit the SA/Secret namespace to your CONTROL
                                                   # PORTAL's namespace first — a pod can only mount
                                                   # Secrets from its own namespace, so they live
                                                   # there, and the RoleBinding in memex-ops names
                                                   # the foreign SA (verified with a
                                                   # SubjectAccessReview: create-jobs true,
                                                   # delete-namespaces false)
# Mount the jobrunner token into the portal Deployment (portal-patch.json in the env folder):
#   volume  : secret hosting-jobrunner-token   (same namespace as the portal)
#   mountPath: /var/run/secrets/hosting-operator
```

Then configure the portal:

```yaml
config:
  memex_portal:
    Hosting__Operator__Enabled: "true"
    Hosting__Operator__Namespace: "memex-ops"
    Hosting__Operator__ServiceAccount: "hosting-operator"
    Hosting__Operator__Image: "meshweaver.azurecr.io/hosting-operator:<tag>"
    Hosting__Operator__Environment__0: "AZ_RESOURCE_GROUP=<rg>"
    Hosting__Operator__Environment__1: "AZ_PORTAL_IDENTITY=<portal-identity>"
    Hosting__Operator__Environment__2: "AZURE_CLIENT_ID=<operatorIdentityClientId>"
    Hosting__Operator__Environment__3: "PAYWALL_URL=https://<control-host>/Deployments/{instance}/area/Suspended"
```

🚨 **Only on the control instance.** `Hosting:Operator:Enabled` on a tenant portal would give that
tenant's pod the ability to start a job that can delete any namespace on the cluster.

## The image is published on every push to `main`

`meshweaver.azurecr.io/hosting-operator:<short sha>` (immutable — what a deployment record pins)
and `:main` (moving) are built and pushed by `.github/workflows/hosting-operator.yml`'s `publish`
job, on `push` to `main` only, behind a preflight that fails red naming a missing OIDC secret.
Until MeshWeaver#3353 the image was built by hand (last on 2026-08-22) and nothing republished it,
so every script fix shipped to the repository and never to a Job.

## `hosting-pull-secret` — the platform images come from the mirror

Every installation **except** the one serving the read-through mirror (`cr.meshweaver.cloud`,
[A Container Registry in Memex](../../../src/MeshWeaver.Documentation/Data/Architecture/ContainerRegistryInMemex.md))
pulls `memex-portal-ai` and `memex-migration` from that mirror instead of ACR, and lists tags there
when it self-updates. Its pods therefore need a pull credential for the mirror host — and the
credential is the instance's **own plugin-registry key**, the vault object `hosting-kv-ensure`
lists as REQUIRED. Nothing new is minted.

```
hosting-pull-secret --namespace <ns> --registry cr.meshweaver.cloud \
                    --vault <vault> --secret <prefix>PluginCatalog-RegistryToken \
                    [--name registry-pull] [--username instance]
```

In order, each step idempotent: **ensures the namespace exists** (the plan runs this *before*
`hosting-deploy` on Provision, and first on Roll and Reconcile, so the namespace may not exist
yet); reads the key from Key Vault; creates-or-updates a `kubernetes.io/dockerconfigjson` Secret
named `--name` for `--registry`; **reads it back** and refuses a Secret of another type or one
naming another registry. It reports `::hosting:: pull_secret=<name>` — the value the deployment
record's `portal.imagePullSecret` must equal — and `pull_secret_registry=<host>`, which
`selfUpdate.registry` must equal. 🚨 **It never prints the key**: not in a command echo (the key
travels on stdin, never argv), not on failure, not in a fact; `test/run-tests.sh` asserts it.

The chart's half: `portal.imagePullSecret` renders `imagePullSecrets` on the portal Deployment
**and** the migration Job; `selfUpdate.registry` renders `SelfUpdate__Registry`, which makes the
self-updater list tags over the OCI Distribution API with the same key. Neither is set on the
mirror instance itself — it cannot serve the image that boots it.

## The ClusterRole follows the chart

`operator-rbac.yaml` is re-applied by `Systemorph/Memex` `helm-release.yml` on every `adopt` and
`deploy`, read from THIS repository at the same pin the lane renders the chart from. So a script
that needs a new grant lands with the grant, and the next deploy carries both to the cluster —
no laptop in the loop. Measured before this existed (2026-09-09): the `storageclasses` rule had
been on main since the volume-capacity step, the cluster's ClusterRole predated it, and the first
record-driven Reconcile through the fixed operator stopped at step 1/6 with `Forbidden`.
`deploy/aks/operator/test/check-rbac-coverage.sh` is the other half: every `kubectl <verb>
<resource>` a `bin/` script names must be granted here, or the operator test suite is red.
`kubectl apply` prints `configured` / `unchanged` per resource in the lane's log — that line is
the change report.

## The Azure grants a Provision needs — and who applies them

`backups.bicep` gives the operator identity Blob Data Contributor on the backup account and
Contributor on the PostgreSQL server. A Provision needs two more, both scoped to ONE resource:

| Step | Command | Grant | Where |
|---|---|---|---|
| 3 · Federate namespace identity | `hosting-federate` → `az identity federated-credential create` on the PORTAL identity | **Managed Identity Contributor** on `memexaks-portal-mi` | `backups.bicep`, param `portalIdentityId` |
| 4 · Create DNS record | `hosting-dns upsert` → `az network dns record-set a add-record` | **DNS Zone Contributor** on the zone | `dns-zone-operator-role.bicep` (the zone's resource group), or `backups.bicep` param `dnsZoneId` |

Measured 2026-09-09 (`Deployments/pearl-provision-20260909-b`, the first Provision ever run
through the lane): step 3 failed with `AuthorizationFailed … federatedIdentityCredentials/write`
over `…/memexaks-portal-mi/federatedIdentityCredentials/hosting-pearl` — the identity had never
been granted anything on the portal identity.

🚨 **No lane deploys bicep.** Applying these is the documented break-glass, run by a person with
Owner on the resource group (the operator identity itself deliberately cannot assign roles):

```bash
# from deploy/aks/infra/modules — parameters as your environment names them
az deployment group create -g memex-aks-rg -f backups.bicep \
  -p oidcIssuerUrl=<AZ_OIDC_ISSUER> postgresServerId=<memexaks-pg resource id> \
     portalIdentityId=<memexaks-portal-mi resource id> \
     dnsZoneId=/subscriptions/<sub>/resourceGroups/dns/providers/Microsoft.Network/dnsZones/meshweaver.cloud
```

After it: re-request the Provision — the plan is idempotent from the top (the database and the
vault secrets the first run created are found, not re-created).
