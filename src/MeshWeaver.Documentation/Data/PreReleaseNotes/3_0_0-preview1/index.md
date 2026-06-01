---
Name: MeshWeaver 3.0.0-preview1
Category: Release Notes
Description: Preview release notes for MeshWeaver 3.0 — a ground-up rewrite of the distributed data mesh platform
Icon: Rocket
---

# MeshWeaver 3.0.0-preview1

**MeshWeaver** is a distributed data mesh platform for building data-driven applications with first-class AI support. Version 3.0 is a ground-up rewrite that unifies messaging, reactive UI, content management, and AI agents into a single coherent framework on .NET 10.

> **Preview notice.** This is an early preview release. APIs are stabilising; breaking changes may occur before the final 3.0 release.

---

## What's New at a Glance

| Capability | What it gives you |
|---|---|
| **Message Hub / Actor Model** | Lightweight actors that scale from a single process to an Orleans cluster — no code changes |
| **Content Graph** | Every piece of data is a typed, versioned, queryable `MeshNode` |
| **Reactive UI** | Server-side C# layout areas that push updates via observable streams into Blazor Server |
| **Interactive Markdown** | Live code execution, Mermaid diagrams, MathJax, and cross-references — all inside markdown |
| **AI Agent Framework** | Built-in agents (Orchestrator, Researcher, Worker, Navigator) on Microsoft.Extensions.AI |
| **Access Control** | Row-level, hierarchical, dimensional security — fully data-driven |
| **Three deployment modes** | Monolith → Aspire → Orleans, progressively |

---

## Message Hub and Actor Model

All communication flows through **MessageHubs** — lightweight actors that serialize access, route messages by address, and scale from a single process to a distributed Orleans cluster without any code changes. Request/response, fire-and-forget, and streaming patterns are all built in.

Addressing is hierarchical: every hub has a typed address, and routing resolves across process boundaries transparently. You write the same hub code whether you are running in a monolith or a sharded Orleans deployment.

---

## Content Graph and Data Management

Every piece of data is a **MeshNode** in a hierarchical content graph. Nodes carry typed content, support semantic versioning, and are queryable through a unified query syntax.

What makes the model especially powerful is that **node types are data too** — define a type once and the platform automatically provides CRUD operations, full-text and vector search, and AI tool access. There is no code generation step and no schema migration ceremony.

---

## Reactive UI and Layout System

UI is defined server-side in C# as a tree of immutable **controls** — stacks, tabs, grids, editors, toolbars, and more. Layout areas are addressable surfaces that subscribe to observable data streams and re-render in Blazor Server the moment the underlying data changes.

No frontend framework knowledge is required. The reactive graph flows from your data model straight to the browser.

---

## Interactive Markdown

Markdown nodes are full citizens of the platform. They support:

- **Embedded layout areas** — live controls rendered inside documentation
- **Live code execution** — C# cells that run against the mesh and display their output
- **Mermaid diagrams and MathJax** — rendered natively
- **Cross-references** — unified `@/path` references resolved at render time

Documentation and application content live side by side in the same content graph, so your documentation is always in sync with your data.

---

## AI-First Agent Framework

AI agents are first-class citizens built on **Microsoft.Extensions.AI**. The built-in agent roster — Orchestrator, Researcher, Worker, and Navigator — collaborates through the message hub using the same messaging primitives as the rest of the platform.

**MeshPlugin** gives agents typed access to the full mesh: Get, Search, Create, Update, Delete, and NavigateTo operations across every node type, scoped to the calling user's permissions.

---

## Access Control

Row-level security is driven by **AccessAssignment** and **PartitionAccessPolicy** nodes. Permissions are:

- **Hierarchical** — policies set at a namespace automatically apply to all child nodes
- **Dimensional** — scope by geography, line of business, or any custom dimension
- **Operation-specific** — read, write, create, delete, and comment are controlled independently

Because policies are data, access rules can be adjusted at runtime with no code changes or redeployment.

---

## Deployment

MeshWeaver supports three deployment modes that you can graduate through as your needs grow:

| Mode | Description |
|---|---|
| **Monolith** | Single-process Blazor Server app — ideal for development and small teams |
| **Aspire** | .NET Aspire orchestration with separate services, PostgreSQL, and Azure Container Apps |
| **Orleans** | Full distributed clustering for horizontal scale |

> **Important.** Never run bare `aspire deploy`. The Aspire 13.2 tooling reports success even when the db-migration container crashes. Always use the project deploy scripts:
>
> ```bash
> tools/deploy.sh prod   # production
> tools/deploy.sh test   # test environment
> ```

---

## Developer Experience

3.0 is designed to be pleasant to work with from day one:

- **Templates and samples** — Northwind, Graph, and Todo samples demonstrate common patterns end to end
- **Testing** — xUnit v3 with `MonolithMeshTestBase` for full integration tests; no mocking of core services is needed or encouraged
- **Embedded documentation** — Platform docs ship inside the framework and are browsable directly in the running portal
- **Central package management** — `Directory.Packages.props` keeps dependencies consistent across all 50+ projects; update it once, not per-csproj
