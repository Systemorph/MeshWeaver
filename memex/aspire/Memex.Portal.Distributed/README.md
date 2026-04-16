# Memex.Portal.Distributed

## Overview
Memex.Portal.Distributed is the production-grade Memex portal that co-hosts an Orleans silo with the Blazor Server web application. It connects to PostgreSQL for partitioned persistence and Azure Blob Storage for content, enabling horizontal scaling across multiple replicas.

## Features
- **Orleans co-hosted silo** — clustering via Azure Table Storage, configured with `UseOrleansMeshServer`
- **Partitioned PostgreSQL persistence** — per-organization schemas with pgvector embeddings and managed identity authentication
- **Azure Blob Storage** — content collections served via Aspire-injected blob clients
- **Full Memex stack** — Graph, AI providers, documentation, row-level security, and activity tracking via shared `ConfigureMemexMesh`
- **Embedding support** — Azure Foundry embeddings for semantic search

## Usage
Launched by [Memex.AppHost](../Memex.AppHost/) as part of the Aspire orchestration. Not typically run standalone.

## Integration
- Uses [Memex.Portal.Shared](../../Memex.Portal.Shared/) for portal configuration, authentication, and UI
- Uses [Memex.Portal.ServiceDefaults](../Memex.Portal.ServiceDefaults/) for health checks and OpenTelemetry
- Depends on [MeshWeaver.Hosting.Orleans](../../../src/MeshWeaver.Hosting.Orleans/) and [MeshWeaver.Connection.Orleans](../../../src/MeshWeaver.Connection.Orleans/) for distributed messaging
- Depends on [MeshWeaver.Hosting.PostgreSql](../../../src/MeshWeaver.Hosting.PostgreSql/) for partitioned storage
