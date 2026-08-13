# MeshWeaver.Observability

Log-incident control plane for MeshWeaver deployments. Watches the deployment's logs,
aggregates recurring errors by structural fingerprint, and files them as `LogIncident` nodes
on the mesh — so production red logs become tracked, assignable work items.

## Features

- `LogIncidentIngestService` — log watching and incident ingestion (Loki-backed)
- `LogIncidentNodeType` + `LogIncidentControlPlane` — incidents as mesh nodes with status transitions
- `LogIncidentFiler` — files/updates incidents, deduplicating by structural identity
- `LogWatchOptions` / `ObservabilityExtensions` — configuration and registration

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
