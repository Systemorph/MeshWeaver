# MeshWeaver.Tools.CosmosImport

## Overview
MeshWeaver.Tools.CosmosImport is a CLI tool that imports seed data from a local file-system directory into Azure Cosmos DB. It reads MeshNode and partition data, creates the required database and containers if they do not exist, and bulk-inserts the records.

## Usage
```bash
dotnet run --project tools/MeshWeaver.Tools.CosmosImport -- \
  --source-path samples/Graph/Data \
  --connection-string "AccountEndpoint=..." \
  --database memexdb
```

### Options
| Flag | Description |
|------|-------------|
| `--source-path` | Path to the seed data directory (required) |
| `--connection-string` | Cosmos DB connection string (required) |
| `--database` | Target database name (default: `memexdb`) |
| `--force` | Re-import even if data already exists |
| `--allow-insecure-ssl` | Accept self-signed certificates (local emulator) |

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about deployment and data seeding.
