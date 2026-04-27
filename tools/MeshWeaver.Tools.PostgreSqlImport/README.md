# MeshWeaver.Tools.PostgreSqlImport

## Overview
MeshWeaver.Tools.PostgreSqlImport is a CLI tool that imports seed data from a local file-system directory into a PostgreSQL database. It initializes the required schema (with pgvector support), then bulk-inserts MeshNode and partition records.

## Usage
```bash
dotnet run --project tools/MeshWeaver.Tools.PostgreSqlImport -- \
  --source-path samples/Graph/Data \
  --connection-string "Host=localhost;Database=memexdb;Username=postgres;Password=..."
```

### Options
| Flag | Description |
|------|-------------|
| `--source-path` | Path to the seed data directory (required) |
| `--connection-string` | PostgreSQL connection string (required) |
| `--force` | Re-import even if data already exists |

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about deployment and data seeding.
