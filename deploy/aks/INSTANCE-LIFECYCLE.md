# Instance lifecycle — provision, suspend, tear down

How an instance is created, taken offline and destroyed on this cluster, **from the mesh**, by any
platform developer, with the database backed up and verified before anything is deleted.

The mesh half lives in the `Hosting` plugin (MeshWeaver.Plugins): `Hosting/Deployment` records the
instance, `Hosting/InstanceAction` runs the lifecycle, `Hosting/Backup` records the dumps and
`Hosting/BackupStore` says where they go. This directory is the half that runs on the cluster.

## Why a Job and not the portal process

The portal must not hold a credential that can delete a namespace. It runs AI CLIs in-pod, so a
prompt injection that reaches the control plane is a cloud compromise, not a bad afternoon.

So there are three principals:

| Principal | Power | Lifetime |
|---|---|---|
| `memex-portal-sa` | `get`/`patch` its own Deployment — the self-updater's needs, unchanged | the pod |
| `hosting-jobrunner` (a token mounted into the portal) | create a Job in `memex-ops`, read its log | the pod |
| `hosting-operator` (the Job's SA) | namespaces, releases, ingresses, and via Workload Identity the Azure control plane | the seconds a run takes |

The mesh decides and orders; the Job executes. `manifests/hosting-operator/` has the RBAC,
`operator/` has the image, and `infra/modules/backups.bicep` has the storage and the identity.

## The lifecycle

```
Planned ──provision──▶ Live ──suspend──▶ Suspended ──teardown──▶ Decommissioned
                        ▲                    │
                        └────reactivate──────┘
```

**Suspension is the notice period.** It stops the portal, takes a verified dump, and re-points the
host at a paywall page — nothing is deleted, and reactivating is a two-command undo. Teardown
refuses on a `Live` instance and waits out a grace period measured from the suspension.

### The redirect is an Ingress patch

Every instance on this cluster shares one ingress controller and one public IP, and each owns only
an `Ingress` in its own namespace. So suspending re-points a host with a single annotation:

- **no DNS change** — the A-record already points at the shared ingress IP, so nothing propagates;
- **no certificate change** — the namespace's TLS secret keeps serving, so the customer meets a
  page rather than a browser warning;
- **reversible** — the previous backend is stashed as an annotation, and the redirect is a **302**,
  never a 301. A permanent redirect would be cached by browsers and outlive the reactivation.

## Deploy it

```bash
# 1. Storage + the operator identity.
az deployment group create -g "$RG" \
  --template-file infra/modules/backups.bicep \
  --parameters location=swedencentral backupAccountName=<globally-unique> \
               oidcIssuerUrl="$(az aks show -g "$RG" -n "$CLUSTER" --query oidcIssuerProfile.issuerURL -o tsv)" \
               postgresServerId="$(az postgres flexible-server show -g "$RG" -n "$PG" --query id -o tsv)"

# 2. The operator identity needs two more grants, on resources this module does not own:
#    DNS Zone Contributor on the zone, and Key Vault Secrets Officer on the vault.
az role assignment create --assignee "<operatorIdentityPrincipalId>" \
  --role "DNS Zone Contributor" --scope "$(az network dns zone show -g dns -n "$ZONE" --query id -o tsv)"
az role assignment create --assignee "<operatorIdentityPrincipalId>" \
  --role "Key Vault Secrets Officer" --scope "$(az keyvault show -n "$VAULT" --query id -o tsv)"

# 3. RBAC (edit the client-id annotation first — see manifests/hosting-operator/README.md).
kubectl apply -f manifests/hosting-operator/

# 4. Build and push the operator image.
docker build -t "$ACR/hosting-operator:1" operator/ && docker push "$ACR/hosting-operator:1"

# 5. Mount the jobrunner token into the portal and set Hosting:Operator:* — README.md in
#    manifests/hosting-operator/ has the exact values.
```

Then record the store in the mesh, once, as a `Hosting/BackupStore` node — `account` and
`container` from this module's outputs, `credentialRef` = `operatorIdentityClientId`, and
`retentionDays` matching the module's `retentionDays` so a restore can be told its archive expired
before it tries to download it.

## What the operator scripts guarantee

`operator/bin/run.sh` executes the mesh's plan and contains no policy of its own — every ordering
rule is in the mesh's pure, unit-tested plan. What the scripts themselves guarantee:

- **`hosting-backup` never claims verification.** It dumps, checks the archive is non-empty,
  uploads with `--overwrite false`, and reports size and digest. That is all.
- **`hosting-verify-backup` is the only thing that emits `verified=true`**, and only after
  downloading the archive and parsing a non-zero number of table entries out of its table of
  contents. A truncated upload exits zero; an empty database dumps perfectly. This step is what
  every destructive phase waits behind.
- **`run.sh` stops at the first failure**, and the Job's `backoffLimit` is `0`. A half-finished
  teardown is never retried from the top, where it would re-dump over a verified archive.
- **`hosting-kv-ensure` never overwrites an existing master key.** Regenerating it would make every
  stored `enc:` value in that instance's database permanently unreadable.
- **`hosting-export` never prints the URL it mints.** A user-delegation SAS is a bearer credential;
  it goes into a one-shot Secret the mesh reads once and deletes.
- **`hosting-verify-catalog` proves the plugin mounts took.** An instance with green pods and an
  empty Store is the failure worth catching, and it is invisible in a rollout status.

## Two traps worth knowing

**An apostrophe inside `${VAR:?message}` is a bash parse error** — the shell opens a single-quoted
string inside the expansion and the script dies at load with an EOF error pointing at the last
line. Reword the message; do not escape it.

**`pg_dump`'s major version must be ≥ the server's**, or it refuses outright. The version is pinned
in `operator/Dockerfile`; bump it when the fleet's PostgreSQL server moves.
