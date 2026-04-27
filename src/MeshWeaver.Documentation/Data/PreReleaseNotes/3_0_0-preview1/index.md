---
Name: MeshWeaver 3.0.0-preview1
Category: Release Notes
Description: Preview release notes for MeshWeaver 3.0 — a ground-up rewrite of the distributed data mesh platform
Icon: Rocket
---

# MeshWeaver 3.0.0-preview1

**MeshWeaver** is a distributed data mesh platform for building data-driven applications with first-class AI support. Version 3.0 is a ground-up rewrite that unifies messaging, reactive UI, content management, and AI agents into a single coherent framework on .NET 10.

---

## Message Hub and Actor Model

All communication flows through **MessageHubs** — lightweight actors that serialize access, route messages by address, and scale from a single process to a distributed Orleans cluster without code changes. Request/response, fire-and-forget, and streaming patterns are all built in.

## Content Graph and Data Management

Every piece of data is a **MeshNode** in a hierarchical content graph. Nodes carry typed content, support semantic versioning, and are queryable through a unified query syntax. Node types themselves are data — define a type once and the platform provides CRUD, search, and AI tool access automatically.

## Reactive UI and Layout System

UI is defined server-side in C# as a tree of immutable **controls** (stacks, tabs, grids, editors, toolbars). Layout areas are addressable surfaces that react to data changes via observable streams and render in Blazor Server. No frontend framework knowledge is required.

## Interactive Markdown

Markdown nodes support embedded layout areas, mermaid diagrams, MathJax, code blocks with live execution, and cross-references via unified paths. Documentation and application content live side by side in the same content graph.

## AI-First Agent Framework

AI agents are first-class citizens built on **Microsoft.Extensions.AI**. The built-in agent roster (Planner, Researcher, Navigator, Executor) collaborates through the message hub. **MeshPlugin** gives agents typed access to Get, Search, Create, Update, Delete, and NavigateTo operations across the entire mesh.

## Access Control

Row-level security is driven by **AccessAssignment** and **PartitionAccessPolicy** nodes. Permissions are hierarchical (namespace → node), dimensional (geography, line of business), and operation-specific (read, write, create, delete, comment). Policies are data — no code changes needed to adjust access.

## Deployment

MeshWeaver supports three deployment modes:

| Mode | Description |
|------|-------------|
| **Monolith** | Single-process Blazor Server app — ideal for development and small teams |
| **Aspire** | .NET Aspire orchestration with separate services, PostgreSQL, and Azure Container Apps |
| **Orleans** | Full distributed clustering for horizontal scale |

Deploy with a single command: `aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode prod`

## Developer Experience

- **Templates and samples** — Northwind, Graph, and Todo samples demonstrate common patterns
- **Testing** — xUnit v3 with `MonolithMeshTestBase` for integration tests; no mocking of core services
- **Embedded documentation** — Platform docs ship inside the framework and are browsable in the running portal
- **Central package management** — `Directory.Packages.props` keeps dependencies consistent across 50+ projects
