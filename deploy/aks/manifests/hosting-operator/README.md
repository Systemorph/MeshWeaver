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
kubectl apply -f operator-rbac.yaml
kubectl apply -f jobrunner.yaml
# Mount the jobrunner token into the portal Deployment (portal-patch.json in the env folder):
#   volume  : secret hosting-jobrunner-token
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
