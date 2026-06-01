---
Name: MeshWeaver Architecture
Category: Documentation
Description: How the platform works under the hood — message-based communication, the actor model, partitioned persistence, reactive UI streaming, and AI agents
Icon: /static/DocContent/Architecture/icon.svg
---

<div style="background: linear-gradient(135deg, #0d47a1 0%, #1976d2 100%); border-radius: 18px; padding: 40px 34px; margin: 4px 0 30px 0; color: #fff;">
  <div style="font-size: 2.1rem; font-weight: 800; letter-spacing: -0.02em; line-height: 1.15;">Architecture</div>
  <div style="font-size: 1.05rem; opacity: 0.92; margin-top: 10px; max-width: 720px; line-height: 1.55;">
    The backbone of MeshWeaver: message-based communication, the actor model, partitioned persistence, access control, and UI streaming. Start here to understand how the platform works under the hood.
  </div>
</div>

MeshWeaver is a distributed platform for building data-driven applications with AI capabilities. A handful of principles hold the whole system together:

| Principle | What it means |
|---|---|
| **Data locality** | Process and render *where the data lives* — no unnecessary round-trips. |
| **Message-driven** | Every operation is a typed message routed through the hub; no direct object calls across boundaries. |
| **Type as data** | Node types live in the mesh, not only in compiled code — they can be authored, versioned, and released at runtime. |
| **Agent-ready** | AI agents reach everything through the same unified APIs as users — no special back-channels. |
| **Security-first** | Access control is validated at every read and write, not bolted on after the fact. |

## Platform overview

@@content:platform-overview.svg

## Core concepts

<div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; margin: 20px 0;">
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Message-based communication</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Message hubs manage concurrency through the actor model and route messages across the mesh.</div>
    <div style="margin-top: 10px;"><a href="MessageBasedCommunication">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">User interface</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">UI is generated where data lives, serialized to JSON, and streamed to the browser with two-way binding.</div>
    <div style="margin-top: 10px;"><a href="UserInterface">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Agentic AI</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">AI agents are first-class citizens that query the mesh for context and collaborate through messages.</div>
    <div style="margin-top: 10px;"><a href="AgenticAI">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Mesh graph</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Hierarchical namespaces where data types attach at any level, with built-in semantic versioning.</div>
    <div style="margin-top: 10px;"><a href="MeshGraph">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Access control</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Hierarchical, dimensional, and operation-specific permissions enforced on every read and write.</div>
    <div style="margin-top: 10px;"><a href="AccessControl">Read more →</a></div>
  </div>
  <div style="border: 1px solid var(--neutral-stroke-divider); border-radius: 12px; padding: 18px;">
    <div style="font-weight: 700; font-size: 1.05rem;">Deployment</div>
    <div style="color: var(--neutral-foreground-hint); margin-top: 6px; font-size: 0.9rem;">Run as a single-process monolith or an Orleans-based distributed mesh orchestrated by .NET Aspire.</div>
    <div style="margin-top: 10px;"><a href="Deployment">Read more →</a></div>
  </div>
</div>

## Getting started

New to the platform? Read [Specifying Software](SpecifyingSoftware) to learn how to write iterative specifications closely aligned with implementation, then explore the full catalog of architecture topics above.
