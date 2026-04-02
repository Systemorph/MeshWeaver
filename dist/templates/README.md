# Memex Portal

A MeshWeaver portal application with Graph support, AI integration, and sample ACME data.

## Projects

| Project | Description |
|---------|-------------|
| `Memex.Portal.Monolith` | Blazor Server portal (development, file-system storage) |
| `Memex.Portal.Shared` | Shared Razor class library (auth, config, UI) |
| `aspire/Memex.AppHost` | .NET Aspire orchestrator (distributed deployment) |
| `aspire/Memex.Portal.Distributed` | Orleans co-hosted portal (PostgreSQL, Azure) |
| `aspire/Memex.Database.Migration` | PostgreSQL schema migration worker |
| `aspire/Memex.Portal.ServiceDefaults` | Shared Aspire services (telemetry, health checks) |

## Getting Started

### Monolith (Recommended for Development)

```bash
dotnet run --project Memex.Portal.Monolith
```

Access at **https://localhost:7122**. Uses file-system storage with ACME sample data.

### Microservices (.NET Aspire)

```bash
dotnet run --project aspire/Memex.AppHost
```

Requires Docker for PostgreSQL and Azure Storage emulation.

## Sample Data

The `samples/Graph/Data/ACME/` directory contains sample organization data including:
- Todo task management with projects (CustomerOnboarding, ProductLaunch)
- Article content management
- User and access control definitions
- Markdown documentation
