# The database platform layer — installed once per cluster

A per-instance database is its **own Helm release** (`deploy/helm-db`, chart `memex-db`): a
CloudNativePG `Cluster` with a primary and a standby in two zones on the `db` node pool. The
design and the comparison with the alternatives are in
[`Doc/Architecture/InClusterDatabases`](../../../../src/MeshWeaver.Documentation/Data/Architecture/InClusterDatabases.md).
Four things must exist on the cluster before the first database release, and none of them belongs
to an instance:

| # | What | Why it is platform, not instance |
|---|---|---|
| 1 | **The `db` node pool** — `Standard_E4ds_v5`, 2 nodes fixed, zones 1 and 2, label `workload=db`, taint `workload=db:NoSchedule` | memory-optimised nodes the portals and CI never land on; one node per zone so primary and standby never share a failure domain |
| 2 | **The CloudNativePG operator** (namespace `cnpg-system`) | owns the `Cluster` CRD every database release renders |
| 3 | **The Barman Cloud plugin** (only where backups are on) | WAL archiving and base backups to Blob; it moved out of the operand image into a plugin |
| 4 | **The `memex-db-premiumv2` StorageClass** ([`storageclass-premiumv2.yaml`](storageclass-premiumv2.yaml)) | cluster-scoped: N instance releases cannot all own one object |

## Commands

Every line below changes a cluster. On the Systemorph estate that is the maintainer's, through the
approved lane (Memex `docs/aks-ops.md`); it is written out here so it is reviewed as code first.

```bash
# 1 — the node pool (ARM, not kubectl). The pool is created on the cluster resource, so it needs
#     Microsoft.ContainerService/managedClusters/agentPools/write on the cluster.
az aks nodepool add -g <rg> --cluster-name <cluster> -n db \
  --mode User --os-type Linux --os-sku AzureLinux \
  --node-vm-size Standard_E4ds_v5 --node-count 2 --zones 1 2 \
  --labels workload=db --node-taints workload=db:NoSchedule \
  --max-pods 30

# 2 — the operator (pin the chart version you reviewed)
helm repo add cnpg https://cloudnative-pg.github.io/charts
helm upgrade --install cnpg cnpg/cloudnative-pg -n cnpg-system --create-namespace \
  --version <reviewed chart version> \
  --set nodeSelector.workload=db \
  --set 'tolerations[0].key=workload' --set 'tolerations[0].operator=Equal' \
  --set 'tolerations[0].value=db' --set 'tolerations[0].effect=NoSchedule'

# 3 — the Barman Cloud plugin (needs cert-manager, which the fleet clusters run for ingress TLS)
kubectl apply -f https://github.com/cloudnative-pg/plugin-barman-cloud/releases/download/<reviewed version>/manifest.yaml

# 4 — the StorageClass
kubectl apply -f storageclass-premiumv2.yaml

# verify
kubectl get nodes -l workload=db -L topology.kubernetes.io/zone
kubectl get crd clusters.postgresql.cnpg.io
kubectl get storageclass memex-db-premiumv2
```

`hosting-db-release` (the operator step a Provision runs) checks 1, 2 and 4 before it installs
anything and refuses by name when one is missing — so a Provision on a cluster without the layer
stops at its first database step with nothing created.
