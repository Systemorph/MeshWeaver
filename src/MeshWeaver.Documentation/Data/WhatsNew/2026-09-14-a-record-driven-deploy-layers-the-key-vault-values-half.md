---
Name: A record-driven deploy layers the Key Vault values half, and the chart refuses to invent a database host
Category: Fix
Description: Re-applying a Deployment record through the operator handed helm the record alone, so the chart re-rendered the portal's Secret from its in-cluster defaults and every new pod on the control instance died at silo start on a host that does not exist. The operator now layers the captured Key Vault values half first, exactly as the config repo's lane always did, and the chart refuses to render cluster membership against a host it does not deploy.
Icon: Database
Order: -20260914
---

# A record-driven deploy layers the Key Vault values half, and the chart refuses to invent a database host

A portal's configuration is assembled from layers, and one of them lives in no repository and on
no record: the **values half** captured into Key Vault (`helm-values-<release>`), which is where the
connection strings of an instance on an external database are kept. The config repo's deploy lane
has always layered it under the committed overlay. The operator path — a record-driven Provision or
Reconcile, the one an operator reaches for from the Fleet Console — did not. It handed helm the
record's render alone, which is secret-free by construction.

helm replaces a Secret wholesale on every upgrade. So the operator did not deploy *less*: it
rewrote the portal's Secret with the chart's in-cluster defaults, and the Orleans membership string
pointed at `memex-postgres-service` — a Service no cluster namespace renders. Every new pod then
failed at silo start:

    'MembershipTableManager' failed to start due to errors at stage 'RuntimeGrainServices (8000)'.
    Npgsql.NpgsqlException: Name or service not known

It happened twice on the control instance, both times from a Reconcile (helm revisions 44 and 55,
five days apart), and each time it was read as a resolver blip — because the init container that
waits for the database had passed, and because every Roll and Restart in between was healthy
(neither touches the Secret). The mesh connection string was not affected only because a second
Key Vault class shadows it later in the pod's environment; nothing shadows the orleans one.

Three things changed:

- **`hosting-deploy` takes `--vault <name>` and layers `helm-values-<release>` first**, then the
  record's render, so the reviewed record still wins on every key it declares. The half is read by
  the operator's own identity, never echoed (only its size is logged), and where the record declares
  it, it is required: a vault that holds none is a refusal before helm, naming the capture to run.
- **The plan passes `--vault` when the record declares `vaultValuesKeys`** — the record's own
  statement that the chart's Secret carries keys nothing in the record renders. A provisioned
  instance with no such declaration keeps its one-layer shape. (This half ships with the Hosting
  plugin.)
- **The chart refuses to invent the host.** On an external database with neither connection string
  supplied, `memex.orleansConnectionString` now fails the render naming the missing input instead
  of manufacturing an in-cluster default. The chart gate carries a refusal control — a fixture of
  exactly that render, which must fail to template — because the invariant checker could never
  have seen it: the Secret carried the key, naming a host, that simply did not exist.

The full measurement, and the one consequence deliberately left open, is in
[Deployment env layers](/Doc/Architecture/DeploymentEnvLayers) → "Layer 2 on the operator path".
