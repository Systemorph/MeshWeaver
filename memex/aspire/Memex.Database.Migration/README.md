# Memex.Database.Migration

## Overview
Memex.Database.Migration is a worker service that runs PostgreSQL schema initialization and versioned data-repair migrations for the Memex database. It executes as a short-lived Aspire project that the portal waits for before starting.

## Features
- **Schema initialization** — idempotent `CREATE IF NOT EXISTS` for tables, indexes, triggers, and satellite tables via `PostgreSqlSchemaInitializer`
- **Versioned data repairs** — tracked migrations (v1-v8) that fix data written incorrectly by prior code versions; skipped on fresh databases
- **pgvector support** — vector column dimensions derived from the configured embedding model
- **Azure PostgreSQL** — automatic `azure_pg_admin` grants and managed identity authentication
- **Searchable schema registry** — populates `public.searchable_schemas` on every run for cross-partition search

## Usage
Normally launched by [Memex.AppHost](../Memex.AppHost/) via `WaitForCompletion(dbMigration)`. Can also run standalone:
```bash
dotnet run --project memex/aspire/Memex.Database.Migration
# Requires ConnectionStrings:memex and Embedding__Model in configuration
```

## Integration
- Uses [MeshWeaver.Hosting.PostgreSql](../../../src/MeshWeaver.Hosting.PostgreSql/) for schema initialization
- Uses [Memex.Portal.ServiceDefaults](../Memex.Portal.ServiceDefaults/) for health checks and telemetry
