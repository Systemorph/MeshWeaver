# MeshWeaver.Observability.Contract

Contracts and pure logic for MeshWeaver observability — shared by the ingest service and
anything that reads or reports incidents.

## Features

- `LogLineParser` / `LogSeverity` — log line model and parsing
- `ILogIncidentIdentity` with `StructuralLogIncidentIdentity` — fingerprinting that survives variable message parts
- `BurstAggregator` — collapses error bursts into single incident updates
- `LogIncidentReport` / `LogIncidentStatus` — the incident wire model
- `LokiQuery` — query model for the Loki backend

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
