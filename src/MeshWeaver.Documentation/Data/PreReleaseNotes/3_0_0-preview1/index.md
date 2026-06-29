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

<svg viewBox="0 0 760 370" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="370" rx="12" fill="none" stroke="currentColor" stroke-opacity=".12" stroke-width="1"/>
  <text x="380" y="28" text-anchor="middle" font-size="15" font-weight="bold" fill="currentColor" fill-opacity=".85">MeshWeaver 3.0 — Platform Architecture</text>
  <rect x="30" y="310" width="700" height="44" rx="10" fill="#37474f" fill-opacity=".75"/>
  <text x="380" y="327" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Deployment Modes</text>
  <text x="176" y="343" text-anchor="middle" font-size="11" fill="#b0bec5">Monolith</text>
  <text x="380" y="343" text-anchor="middle" font-size="11" fill="#b0bec5">Aspire + PostgreSQL</text>
  <text x="584" y="343" text-anchor="middle" font-size="11" fill="#b0bec5">Orleans Cluster</text>
  <line x1="277" y1="316" x2="277" y2="348" stroke="#b0bec5" stroke-opacity=".4" stroke-width="1"/>
  <line x1="483" y1="316" x2="483" y2="348" stroke="#b0bec5" stroke-opacity=".4" stroke-width="1"/>
  <rect x="30" y="240" width="700" height="56" rx="10" fill="#1565c0" fill-opacity=".85"/>
  <text x="380" y="263" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Message Hub / Actor Model</text>
  <text x="380" y="281" text-anchor="middle" font-size="11" fill="#bbdefb">Typed addressing · Request/Response · Fire-and-forget · Streaming · Cross-process routing</text>
  <rect x="30" y="170" width="340" height="56" rx="10" fill="#2e7d32" fill-opacity=".85"/>
  <text x="200" y="193" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Content Graph</text>
  <text x="200" y="211" text-anchor="middle" font-size="11" fill="#c8e6c9">MeshNode · Versioned · Typed · Queryable</text>
  <rect x="390" y="170" width="340" height="56" rx="10" fill="#6a1b9a" fill-opacity=".85"/>
  <text x="560" y="193" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Reactive UI</text>
  <text x="560" y="211" text-anchor="middle" font-size="11" fill="#e1bee7">C# Layout Areas · Observable Streams · Blazor Server</text>
  <rect x="100" y="95" width="220" height="56" rx="10" fill="#1565c0" fill-opacity=".65"/>
  <text x="210" y="118" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Interactive Markdown</text>
  <text x="210" y="136" text-anchor="middle" font-size="11" fill="#bbdefb">Live C# · Mermaid · MathJax · @/ refs</text>
  <rect x="440" y="95" width="220" height="56" rx="10" fill="#e65100" fill-opacity=".85"/>
  <text x="550" y="118" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">AI Agent Framework</text>
  <text x="550" y="136" text-anchor="middle" font-size="11" fill="#ffe0b2">Orchestrator · Researcher · Worker · Navigator</text>
  <rect x="270" y="44" width="220" height="36" rx="10" fill="#00695c" fill-opacity=".85"/>
  <text x="380" y="58" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Access Control</text>
  <text x="380" y="72" text-anchor="middle" font-size="11" fill="#b2dfdb">Hierarchical · Dimensional · Data-driven</text>
  <line x1="210" y1="151" x2="200" y2="168" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="550" y1="151" x2="560" y2="168" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="200" y1="226" x2="280" y2="239" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="560" y1="226" x2="480" y2="239" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="80" x2="280" y2="93" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="80" x2="480" y2="93" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="296" x2="380" y2="308" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>

*MeshWeaver 3.0 layers: Access Control governs all tiers; the Message Hub is the universal communication backbone; Content Graph and Reactive UI build directly on it; Interactive Markdown and AI Agents compose on top.*

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
