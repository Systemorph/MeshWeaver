---
Name: MeshWeaver Architecture
Category: Documentation
Description: "Overview of MeshWeaver's distributed architecture: message-based communication, UI streaming, AI agents, and data management"
Icon: /static/DocContent/Architecture/icon.svg
---

MeshWeaver is a distributed platform for building data-driven applications with AI capabilities. This documentation covers the core architectural concepts.

# Featured Articles

| Article | Description |
|---------|-------------|
| [Specifying Software](SpecifyingSoftware) | Learn how to write iterative specifications closely aligned with implementation |
| [From Specification to Implementation](SpecificationToImplementation) | Transform your prototypes into production-ready code |

---

# What MeshWeaver Does

- Build Blazor Server portals with reactive layout areas (addressable UI surfaces that embed data + business logic)
- Route commands/events through the Message Hub for concurrency, activity tracking, and request/response
- Standardize CRUD: catalogs, details, data model visualization, import, and business rules
- Integrate AI: chat/command agents, autocomplete, and provider abstraction for Azure OpenAI/Foundry/Claude
- Deploy flexibly: single-process portal or Orleans-based distributed setup via .NET Aspire

# What MeshWeaver Is Not

- Not a static-site CMS; it assumes live hubs and services
- Not a mobile/native UI toolkit; primary UI is Blazor Server
- Cloud targets are flexible, but samples assume Azure services (Blob, OpenAI/Foundry, PostgreSQL/Cosmos options)
- No out-of-the-box multi-tenant SaaS provisioning; security hardening guidance is minimal today

# Architecture Overview

@@content:platform-overview.svg

# Core Concepts

## 1. Message-Based Communication

**MessageHubs** are the foundation of MeshWeaver. They:
- Manage concurrency through the actor model
- Handle messages for data, layouts, workflows, and more
- Route messages across the mesh
- Support horizontal and vertical cloud-native scaling

[Read more: Message-Based Communication](MessageBasedCommunication)

---

## 2. User Interface

UI is generated **where data lives**:
- Controls defined server-side in a declarative language
- Serialized to JSON and streamed to browsers
- Two-way data binding for real-time updates
- Click events delivered as messages

[Read more: User Interface Architecture](UserInterface)

---

## 3. Agentic AI

AI agents are **first-class citizens** in the mesh:
- Minimal system prompts - agents query for context
- Multi-agent collaboration (Planner, Researcher, Executor)
- MeshPlugin for mesh operations
- MCP integration for external AI services

[Read more: Agentic AI Architecture](AgenticAI)

---

## 4. Mesh Graph

**Data types are data elements**:
- Hierarchical namespaces (a/b/c/d pattern)
- Types attach at any level
- Built-in semantic versioning
- Dynamic hub configuration

[Read more: Mesh Graph Architecture](MeshGraph)

---

## 5. Data Versioning

Technology-specific versioning strategies:
- Snowflake: Time Travel (up to 90 days)
- SQL Server: Temporal tables
- Manual: Path-based versioning (@path@V1, V2)

[Read more: Data Versioning Strategies](DataVersioning)

---

## 6. Access Control

Flexible security through `IDataValidator`:
- Hierarchical: businessArea/department/deal
- Dimensional: geography, line of business
- Operation-specific: read vs. write permissions

[Read more: Access Control Architecture](AccessControl)

---

## 7. Project Templates

Bootstrap a new MeshWeaver portal with a single command:
- `dotnet new meshweaver-memex -o MyProject` scaffolds a complete solution
- Monolith and distributed deployment modes included
- Sample data, dev login users, and access control pre-configured

[Read more: Project Templates](ProjectTemplates)

---

## 8. Deployment

Deploy with **.NET Aspire** to Azure Container Apps:
- Multiple modes: `local`, `local-test`, `local-prod`, `test`, `prod`, `monolith`
- Aspire CLI for build, push, and provisioning
- PostgreSQL, Orleans, Blob Storage, Application Insights

[Read more: Deployment](Deployment)

---

# Key Principles

| Principle | Description |
|-----------|-------------|
| **Data Locality** | Process and render where data lives |
| **Message-Driven** | All operations as typed messages |
| **Type as Data** | Data types stored in mesh, not code |
| **Agent-Ready** | AI agents access everything through unified APIs |
| **Security-First** | Validation at every operation |

# Getting Started

Explore each architecture topic in depth through the linked articles above, or browse the `Doc/Architecture` namespace.

---

# Tech Stack

| Layer | Technologies |
|-------|--------------|
| **Language/Runtime** | C# on .NET 9 |
| **UI** | Blazor Server, SignalR, Radzen, ChartJS, Google Maps; Interactive Markdown for embedded areas |
| **Distributed/Hosting** | Orleans, .NET Aspire orchestration; Azure Blob/PostgreSQL/Cosmos options for storage |
| **AI** | Agent abstractions with Azure OpenAI/Foundry/Claude; Semantic-Kernel-style plugins; chat memory persistence |
| **Tooling** | Central package management (Directory.Packages.props), xUnit v3 + FluentAssertions for tests |

---

# Folder Structure

| Path | Description |
|------|-------------|
| `portal/` | Monolith portal (MeshWeaver.Portal) with Blazor UI and SignalR |
| `portal/aspire/` | Aspire AppHost + service projects for distributed runs (requires Docker for deps) |
| `loom/` | Alternate portal flavor added in 3.0 (monolith + Aspire + shared Razor UI and auth config) |
| `samples/` | Domain samples (Northwind, Todo, Graph) and documentation content under Documentation/ |
| `src/` | Core libraries (Messaging.Hub, Layout, AI, Hosting, Blazor controls, Graph, Charting, Import, BusinessRules) |
| `modules/` | Domain/feature modules (e.g., Documentation AI demos) to plug into hubs/layout |
| `test/` | xUnit v3 suites for core and modules |

---

# Feature-to-Component Matrix

| Feature | Components |
|---------|------------|
| Addressable UI (layout areas), catalogs/details, CRUD grids | Layout + Domain modules + Portal |
| Messaging, routing, activity tracking, request/response | Messaging.Hub + Hosting |
| AI chat, commands, autocomplete, provider abstraction | MeshWeaver.AI + AzureFoundry/OpenAI providers + Portal chat UI |
| Visualization (charts/maps/graph editors) | Blazor.ChartJs, Blazor.Radzen, Blazor.GoogleMaps, Graph modules |
| Content collections and documentation delivery | ContentCollections + Markdown system + Portal |
| Deployment elasticity | Orleans + Aspire (distributed) or ASP.NET/Blazor (monolith) |

---

# Extending MeshWeaver

| Extension Type | How To |
|---------------|--------|
| **Add a module** | Register hub handlers, layout areas, and content collections under an address (e.g., `@app/MyDomain`) |
| **Add a layout area** | Static class with data/view logic; expose via layout configuration; becomes addressable and embeddable |
| **Add AI capabilities** | Create a plugin/command handler, register with the command registry, configure provider in appsettings |
| **Choose hosting** | Start with monolith; move to Aspire/Orleans when scaling or integrating external services |

---

# Known Documentation Gaps

As of version 3.0:

- Security/auth hardening (OIDC, cookies, roles/claims) and production guidance
- Observability and ops (logging, tracing, health, metrics, DR/backup, scaling knobs)
- AI configuration details (models, rate limits, safety controls, command lifecycle examples)
- Performance/concurrency tuning (hub actor settings, Orleans silo sizing)
- Migration/upgrade notes and multi-tenancy/partitioning patterns beyond address-based routing
