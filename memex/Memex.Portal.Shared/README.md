# Memex.Portal.Shared

## Overview
Memex.Portal.Shared is a Razor class library containing the shared configuration, authentication, UI pages, and domain types used by both the monolith and distributed Memex portal deployments. It centralizes all portal-level concerns so that deployment topology is the only difference between hosting modes.

## Features
- **Portal configuration** (`ConfigureMemexServices`, `ConfigureMemexMesh`, `ConfigureMemexPortal`) — wires up Blazor, AI providers, graph, documentation, persistence, and content collections
- **Authentication** — supports dev login, Microsoft Identity (Entra ID), Google, LinkedIn, Apple, and API token auth for MCP
- **Organization domain** — `Organization` content type with custom layout areas, access rules, partition provisioning, and post-creation handlers
- **UI pages** — Login, DevLogin, Onboarding, Welcome, Search, and the root `App.razor` / `Routes.razor`
- **AI integration** — Azure Foundry Claude, Azure OpenAI, Copilot, Claude Code, and web search plugin registration

## Integration
- Referenced by [Memex.Portal.Monolith](../Memex.Portal.Monolith/) and [Memex.Portal.Distributed](../aspire/Memex.Portal.Distributed/)
- Depends on core MeshWeaver libraries: Blazor, Graph, AI, Documentation, Hosting, and Kernel
- Uses [Microsoft.FluentUI](https://github.com/microsoft/fluentui-blazor) for the component framework

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for the overall architecture.
