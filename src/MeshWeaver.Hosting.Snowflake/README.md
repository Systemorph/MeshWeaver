# MeshWeaver.Hosting.Snowflake

Snowflake storage backend for MeshWeaver. Persists mesh nodes in Snowflake, serves mesh
queries across schemas, and surfaces changes through a polling change feed.

## Features

- `SnowflakeEventLogStore` — the mesh event/version log on Snowflake
- `SnowflakeChangeFeedPoller` (+ hosted service) — change propagation into mesh streams
- `SnowflakeCrossSchemaQueryProvider` — mesh queries spanning partition schemas
- `SnowflakeAccessControl` / `SnowflakeAccessProjection` — access rules projected into queries
- `SnowflakeCapabilities` / `SnowflakeConnectionSource` — backend capability declaration and connections

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)
