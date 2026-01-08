---
Title: "MeshWeaver Architecture Overview"
Abstract: >
  High-level architecture of MeshWeaver: what it is for, how the pieces fit,
  the tech stack, deployment options, and how to extend it.
Published: "2026-01-08"
Tags:
  - "Architecture"
  - "Overview"
  - "Documentation"
Thumbnail: "thumbnails/Calculator.png"
---

MeshWeaver is a modular framework for building data-driven portals with real-time
UI, addressable layout areas, and AI-assisted interactions. It combines an actor-like
message hub, a reusable layout system, and pluggable hosting (monolith or distributed)
to deliver interactive CRUD, reporting, and automation.

## What MeshWeaver Does
- Build Blazor Server portals with reactive layout areas (addressable UI surfaces that embed data + business logic).
- Route commands/events through the Message Hub for concurrency, activity tracking, and request/response.
- Standardize CRUD: catalogs, details, data model visualization, import, and business rules.
- Integrate AI: chat/command agents, autocomplete, and provider abstraction for Azure OpenAI/Foundry/Claude.
- Deploy flexibly: single-process portal or Orleans-based distributed setup via .NET Aspire.

## What MeshWeaver Is Not
- Not a static-site CMS; it assumes live hubs and services.
- Not a mobile/native UI toolkit; primary UI is Blazor Server.
- Cloud targets are flexible, but samples assume Azure services (Blob, OpenAI/Foundry, PostgreSQL/Cosmos options).
- No out-of-the-box multi-tenant SaaS provisioning; security hardening guidance is minimal today.

## Core Components (C3-style: Containers)
- Portal (Blazor Server)
  - Renders layout areas, hosts Interactive Markdown, uses SignalR for live updates.
  - Talks to the Message Hub for queries/commands and surfaces AI chat/autocomplete.
- Message Hub
  - Actor-like routing with address spaces (e.g., `@app/Northwind`).
  - Request/response (`AwaitResponse`) and fire-and-forget (`Post`), DI wiring, activity tracking.
- Domain Modules (Northwind, Todo, Documentation, Graph, etc.)
  - Register hubs, layout areas, business rules, import pipelines, and content collections.
- AI Services
  - Agent chat factories, command registry, chat memory persistence; provider abstraction for model backends.
- Hosting Layer
  - Monolith: ASP.NET/Blazor Server.
  - Distributed: Orleans silos orchestrated by .NET Aspire; storage bindings for data and content.
- External Services
  - Azure OpenAI/Foundry/Claude, Azure Blob (content), PostgreSQL/Cosmos (data), SignalR, Radzen/ChartJS/Google Maps.

### Runtime Collaboration (C3 Component View)
- Browser → Portal: Blazor renders controls; SignalR keeps views live; markdown can embed layout areas.
- Portal → Message Hub: sends commands/queries to addressable handlers; receives view models/streams.
- Hub → Domain Modules: executes business logic, import/business rules, and graph/content operations.
- Hub → Layout System: produces layout areas (catalogs, details, dashboards); embeds into Portal UI.
- Portal → AI Services: agent chat/commands invoke domain plugins via hub; autocomplete uses chat factories.
- Hub/Modules → Storage/External: persistence for content collections; calls AI endpoints; uses mapping/visual libs.

## Folder Map (high level)
- portal/: Monolith portal (MeshWeaver.Portal) with Blazor UI and SignalR.
- portal/aspire/: Aspire AppHost + service projects for distributed runs (requires Docker for deps).
- loom/: Alternate portal flavor added in 3.0 (monolith + Aspire + shared Razor UI and auth config).
- samples/: Domain samples (Northwind, Todo, Graph) and documentation content under Documentation/.
- src/: Core libraries (Messaging.Hub, Layout, AI, Hosting, Blazor controls, Graph, Charting, Import, BusinessRules).
- modules/: Domain/feature modules (e.g., Documentation AI demos) to plug into hubs/layout.
- test/: xUnit v3 suites for core and modules.

## Feature-to-Component Matrix
- Addressable UI (layout areas), catalogs/details, CRUD grids → Layout + Domain modules + Portal.
- Messaging, routing, activity tracking, request/response → Messaging.Hub + Hosting.
- AI chat, commands, autocomplete, provider abstraction → MeshWeaver.AI + AzureFoundry/OpenAI providers + Portal chat UI.
- Visualization (charts/maps/graph editors) → Blazor.ChartJs, Blazor.Radzen, Blazor.GoogleMaps, Graph modules.
- Content collections and documentation delivery → ContentCollections + Markdown system + Portal.
- Deployment elasticity → Orleans + Aspire (distributed) or ASP.NET/Blazor (monolith).

## Tech Stack Quick Reference
- Language/Runtime: C# on .NET 9.
- UI: Blazor Server, SignalR, Radzen, ChartJS, Google Maps; Interactive Markdown for embedded areas.
- Distributed/Hosting: Orleans, .NET Aspire orchestration; Azure Blob/PostgreSQL/Cosmos options for storage.
- AI: Agent abstractions with Azure OpenAI/Foundry/Claude; Semantic-Kernel-style plugins; chat memory persistence.
- Tooling: Central package management (Directory.Packages.props), xUnit v3 + FluentAssertions for tests.

## Extending MeshWeaver (summary)
- Add a module: register hub handlers, layout areas, and content collections under an address (e.g., `@app/MyDomain`).
- Add a layout area: static class with data/view logic; expose via layout configuration; becomes addressable and embeddable.
- Add AI capabilities: create a plugin/command handler, register with the command registry, configure provider in appsettings.
- Choose hosting: start with monolith; move to Aspire/Orleans when scaling or integrating external services.

## Known Documentation Gaps (as of 3.0)
- Security/auth hardening (OIDC, cookies, roles/claims) and production guidance.
- Observability and ops (logging, tracing, health, metrics, DR/backup, scaling knobs).
- AI configuration details (models, rate limits, safety controls, command lifecycle examples).
- Performance/concurrency tuning (hub actor settings, Orleans silo sizing).
- Migration/upgrade notes and multi-tenancy/partitioning patterns beyond address-based routing.
